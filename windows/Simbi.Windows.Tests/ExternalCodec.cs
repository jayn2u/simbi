using System.Diagnostics;
using System.Text.Json;
using Simbi.Windows.Core;

internal static class ExternalCodec
{
    public static async Task VerifyAsync(string path)
    {
        var probe = await Run("ffprobe.exe", ["-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", path]);
        using var json = JsonDocument.Parse(probe);
        var stream = json.RootElement.GetProperty("streams")[0];
        if (stream.GetProperty("codec_name").GetString() != "opus" || stream.GetProperty("channels").GetInt32() != 1 || stream.GetProperty("sample_rate").GetString() != "48000")
            throw new Exception("Independent ffprobe found an invalid WebM/Opus track.");
        await Run("ffmpeg.exe", ["-v", "error", "-i", path, "-f", "null", "-"]);
        Console.WriteLine("PASS: independent Windows ffprobe recognizes mono 48-kHz Opus; ffmpeg decodes both appended sessions without errors");
        foreach (var channels in new[] { 1, 2 })
        {
            var fixture = Path.Combine(Path.GetDirectoryName(path)!, $"independent-{channels}.webm");
            await Run("ffmpeg.exe", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=1", "-ac", channels.ToString(), "-ar", "16000", "-c:a", "libopus", "-b:a", "24k", "-frame_duration", "20", "-y", fixture]);
            using var reader = new WebMPlayback(fixture);
            if (reader.WaveFormat.Channels != channels || reader.TotalTime.TotalSeconds < 1) throw new Exception("Independent Opus fixture format was not decoded.");
            reader.Position = reader.Length / 2;
            var bytes = new byte[960 * channels * 2];
            if (reader.Read(bytes, 0, bytes.Length) != bytes.Length || bytes.All(b => b == 0)) throw new Exception("Independent fixture seeking/decoding failed.");
        }
        Console.WriteLine("PASS: independent mono/stereo WebM fixtures with 16-kHz input and 20-ms Opus frames decode and seek correctly");
    }
    private static async Task<string> Run(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors)) throw new Exception("Independent codec validation failed: " + await errors);
        return await output;
    }
}
