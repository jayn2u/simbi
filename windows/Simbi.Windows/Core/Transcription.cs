using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Simbi.Windows.Core;

public sealed record PendingSegment(string Id, double Start, double End, string Speaker);
public sealed record CodexCredentials(string AccessToken, string AccountId);

public static class CodexLogin
{
    public static string Home => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public static CodexCredentials Load()
    {
        var path = Path.Combine(Home, "auth.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Sign in to Codex with your ChatGPT account first. Pending recordings remain on disk.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (!root.TryGetProperty("auth_mode", out var mode) || mode.GetString() != "chatgpt" ||
            !root.TryGetProperty("tokens", out var tokens) ||
            !tokens.TryGetProperty("access_token", out var token) ||
            !tokens.TryGetProperty("account_id", out var account) ||
            string.IsNullOrWhiteSpace(token.GetString()) || string.IsNullOrWhiteSpace(account.GetString()))
            throw new InvalidOperationException("ChatGPT account credentials are unavailable. Sign in to Codex, then retry.");
        return new(token.GetString()!, account.GetString()!);
    }
    public static bool Available() { try { Load(); return true; } catch { return false; } }
}

public sealed class TranscriptionClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Func<CodexCredentials> credentials;
    public TranscriptionClient(HttpMessageHandler? handler = null, Func<CodexCredentials>? credentials = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(60);
        this.credentials = credentials ?? CodexLogin.Load;
    }
    public async Task<string> TranscribeAsync(string webm, CancellationToken token)
    {
        var auth = credentials();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/transcribe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Add("originator", "Codex Desktop");
        request.Headers.UserAgent.ParseAdd("Simbi-Windows/0.1.0");
        using var form = new MultipartFormDataContent("simbi-" + Guid.NewGuid().ToString("N"));
        using var stream = File.OpenRead(webm);
        var audio = new StreamContent(stream);
        audio.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/webm;codecs=opus");
        form.Add(audio, "file", "codex.webm"); request.Content = form;
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("ChatGPT rejected the session. Sign in again and retry; audio is preserved.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Transcription returned HTTP {(int)response.StatusCode}. Audio is preserved for retry.");
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!data.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Transcription returned an invalid response. Audio is preserved.");
        return text.GetString()!;
    }
    public void Dispose() => client.Dispose();
}

public sealed class TranscriptQueue(TranscriptionClient client)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task DiscardAsync(string folder, CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("Wait for the active transcript upload to finish before discarding pending speech.");
        try { PendingSegments.Discard(folder); }
        finally { gate.Release(); }
    }
    public static int Count(string folder)
    {
        var pending = Path.Combine(folder, ".simbi", "windows-pending");
        return Directory.Exists(pending) ? Directory.EnumerateFiles(pending).Count(p => p.EndsWith(".json") || p.EndsWith(".pending")) : 0;
    }
    public async Task DrainAsync(string folder, Action<string>? status = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var pending = Path.Combine(folder, ".simbi", "windows-pending");
            if (!Directory.Exists(pending)) return;
            Exception? deferred = null;
            foreach (var path in Directory.GetFiles(pending, "*.json").OrderBy(File.GetCreationTimeUtc))
            {
                token.ThrowIfCancellationRequested();
                var audio = Path.ChangeExtension(path, ".webm");
                PendingSegment? item;
                try
                {
                    item = JsonSerializer.Deserialize<PendingSegment>(File.ReadAllText(path), AtomicFile.Json);
                }
                catch (JsonException) { item = null; }
                // Never accept a path from queue metadata: the payload is paired by filename.
                if (item == null || item.Id != Path.GetFileNameWithoutExtension(path))
                {
                    Quarantine(pending, path, audio, "invalid metadata");
                    status?.Invoke("Skipped invalid pending segment " + Path.GetFileName(path));
                    deferred ??= new InvalidDataException("Invalid pending-segment metadata was moved to " + FailedFolder + ": " + Path.GetFileName(path));
                    continue;
                }
                var transcribed = Vtt.Read(folder).Any(c => c.Id == item.Id);
                if (!transcribed && !File.Exists(audio))
                {
                    // Orphaned metadata would otherwise block every later segment forever.
                    Quarantine(pending, path, audio, "missing audio");
                    status?.Invoke("Skipped pending segment with missing audio");
                    continue;
                }
                if (!transcribed)
                {
                    try
                    {
                        status?.Invoke("Transcribing " + item.Speaker + "…");
                        var text = await client.TranscribeAsync(audio, token);
                        Vtt.Append(folder, new(item.Id, item.Start, item.End, item.Speaker, text));
                    }
                    // Session/auth problems affect every item; stop and surface them without counting attempts.
                    catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
                    {
                        if (RecordFailure(path) >= MaxAttempts)
                        {
                            Quarantine(pending, path, audio, ex.Message);
                            status?.Invoke("Moved a repeatedly failing segment to " + FailedFolder);
                        }
                        // Keep draining later segments; report the first failure afterwards.
                        deferred ??= ex;
                        continue;
                    }
                }
                // Commit transcript before queue removal. A crash here is idempotent on retry.
                File.Delete(audio);
                File.Delete(path);
                File.Delete(AttemptsPath(path));
                status?.Invoke("Transcript saved");
            }
            if (deferred != null) throw deferred;
        }
        finally { gate.Release(); }
    }
    public const int MaxAttempts = 3;
    public const string FailedFolder = "failed";
    private static string AttemptsPath(string manifest) => Path.ChangeExtension(manifest, ".attempts");
    private static int RecordFailure(string manifest)
    {
        var file = AttemptsPath(manifest);
        var count = File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var previous) ? previous + 1 : 1;
        File.WriteAllText(file, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return count;
    }
    // Quarantined items are kept (never deleted) under windows-pending/failed for manual recovery.
    private static void Quarantine(string pending, string manifest, string audio, string reason)
    {
        var failed = Path.Combine(pending, FailedFolder);
        Directory.CreateDirectory(failed);
        foreach (var file in new[] { manifest, audio })
            if (File.Exists(file)) File.Move(file, Path.Combine(failed, Path.GetFileName(file)), overwrite: true);
        File.WriteAllText(Path.Combine(failed, Path.GetFileNameWithoutExtension(manifest) + ".reason.txt"), reason);
        if (File.Exists(AttemptsPath(manifest))) File.Delete(AttemptsPath(manifest));
    }
}
