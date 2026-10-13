using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Simbi.Windows.Core;

public static class CodexLocator
{
    public static string? Find(string configured = "")
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configured : null;
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            // Relative PATH entries would resolve against the working directory; never launch from there.
            .Where(Path.IsPathFullyQualified)
            .Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
        if (path != null) return path;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "resources"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "codex", "resources"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "bin")
        };
        foreach (var root in roots)
            { var candidate = Path.Combine(root, "codex.exe"); if (File.Exists(candidate)) return candidate; }
        // The installed package location is Windows-managed metadata, not a hardcoded version.
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "OpenAI.Codex*"))
            {
                var candidate = Path.Combine(package, "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        try
        {
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            using var query = new Process { StartInfo = new(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            query.StartInfo.ArgumentList.Add("-NoProfile"); query.StartInfo.ArgumentList.Add("-NonInteractive"); query.StartInfo.ArgumentList.Add("-Command");
            query.StartInfo.ArgumentList.Add("Get-AppxPackage OpenAI.Codex | Select-Object -ExpandProperty InstallLocation");
            query.Start();
            var stdout = query.StandardOutput.ReadToEndAsync();
            var stderr = query.StandardError.ReadToEndAsync();
            if (!query.WaitForExit(10000)) { query.Kill(true); return null; }
            foreach (var location in stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(location.Trim(), "app", "resources", "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        return null;
    }
}

// Each action owns a fresh child. EOF / cancellation / disposal always stops it.
public sealed class CodexClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Task reader;
    private readonly Task stderr;
    private int nextId;
    private Action<string, JsonElement>? notification;
    private CodexClient(string executable, string cwd)
    {
        process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cwd,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.Environment["CODEX_HOME"] = CodexLogin.Home;
        process.Start();
        reader = Task.Run(ReadLoop);
        // Drain stderr without recording any user content or credentials.
        stderr = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync(lifetime.Token) != null) { } } catch (OperationCanceledException) { } });
    }
    public static async Task<CodexClient> ConnectAsync(string executable, string cwd, CancellationToken token)
    {
        var client = new CodexClient(executable, cwd);
        try
        {
            await client.Request("initialize", new { clientInfo = new { name = "simbi_windows", title = "Simbi Windows", version = "0.1.0" } }, token);
            await client.Write(new { method = "initialized" }, token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> Request(string method, object parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        try
        {
            await Write(new { id, method, @params = parameters }, token);
            return await response.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task Write(object message, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token); await process.StandardInput.FlushAsync(token); }
        finally { writes.Release(); }
    }
    private async Task ReadLoop()
    {
        Exception failure = new IOException("Codex app-server disconnected.");
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("id", out var id) && !root.TryGetProperty("method", out _))
                {
                    if (!id.TryGetInt32(out var number) || !pending.TryRemove(number, out var response)) continue;
                    if (root.TryGetProperty("error", out var error))
                        response.TrySetException(new InvalidOperationException(
                            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                                ? text.GetString() : "Codex returned an error: " + error.GetRawText()));
                    else response.TrySetResult(root.GetProperty("result").Clone());
                }
                else if (root.TryGetProperty("method", out var method))
                {
                    if (root.TryGetProperty("id", out var requestId))
                        await Write(new { id = requestId.Clone(), error = new { code = -32601, message = "Simbi does not grant tool approvals." } }, lifetime.Token);
                    else if (root.TryGetProperty("params", out var parameters)) notification?.Invoke(method.GetString()!, parameters.Clone());
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var item in pending.Values) item.TrySetException(failure);
            notification?.Invoke("simbi/disconnected", default);
        }
    }
    public async Task<string> AskAsync(string cwd, string prompt, CancellationToken token)
    {
        var thread = await Request("thread/start", new { cwd, approvalPolicy = "never", sandbox = "read-only" }, token);
        var id = thread.GetProperty("thread").GetProperty("id").GetString()!;
        await Request("thread/name/set", new { threadId = id, name = "[simbi windows] " + Path.GetFileName(cwd) }, token);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = "";
        notification = (method, parameters) =>
        {
            if (method == "simbi/disconnected") { completion.TrySetException(new IOException("Codex disconnected during the turn.")); return; }
            if (!parameters.TryGetProperty("threadId", out var threadId) || threadId.GetString() != id) return;
            if (method == "item/completed" && parameters.TryGetProperty("item", out var item) &&
                item.GetProperty("type").GetString() == "agentMessage") message = item.GetProperty("text").GetString() ?? "";
            if (method == "turn/completed")
            {
                var turn = parameters.GetProperty("turn");
                if (turn.GetProperty("status").GetString() == "completed") completion.TrySetResult(message);
                else completion.TrySetException(new InvalidOperationException("Codex turn failed or was interrupted. Your files were not changed."));
            }
        };
        try
        {
            await Request("turn/start", new
            {
                threadId = id, input = new[] { new { type = "text", text = prompt, text_elements = Array.Empty<string>() } },
                approvalPolicy = "never", sandboxPolicy = new { type = "readOnly" }
            }, token);
            var answer = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), token);
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Codex returned no text.");
            return answer;
        }
        finally
        {
            notification = null;
            try { await Request("thread/archive", new { threadId = id }, CancellationToken.None); } catch { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await Task.WhenAll(reader, stderr).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        process.Dispose(); lifetime.Dispose(); writes.Dispose();
    }
}
