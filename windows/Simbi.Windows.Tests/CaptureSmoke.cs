using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Simbi.Windows.Core;

internal static class CaptureSmoke
{
    public static async Task RunAsync(string root)
    {
        Console.WriteLine("Windows WASAPI synthetic loopback smoke (no microphone, no network)");
        var note = new NoteStore(root).Create("Synthetic capture");
        await using var capture = new AudioCapture();
        Exception? failure = null;
        capture.Failed += ex => failure = ex;
        capture.Start(note, microphone: false, systemAudio: true);
        using var output = new WaveOutEvent();
        var tone = new SignalGenerator(48000, 2) { Gain = 0.025, Frequency = 440, Type = SignalGeneratorType.Sin };
        output.Init(tone.Take(TimeSpan.FromSeconds(1.5))); output.Play();
        await Task.Delay(2200);
        await capture.StopAsync();
        if (failure != null) throw failure;
        var samples = OpusWebM.Decode(Path.Combine(note.Folder, "audio.webm"));
        var peak = samples.Max(s => Math.Abs((int)s));
        if (samples.Length < 48000 || peak < 100) throw new Exception($"Loopback capture produced no audible samples: {samples.Length}, peak {peak}.");
        if (TranscriptQueue.Count(note.Folder) < 1 || !RecordingStamp.Matches(note.Folder)) throw new Exception("Capture did not produce durable speech or a valid resume stamp.");
        capture.Start(note, microphone: false, systemAudio: true);
        await Task.Delay(100); await capture.StopAsync();
        if (!RecordingStamp.Matches(note.Folder)) throw new Exception("Resumed capture stamp is invalid.");
        using var reader = new WebMPlayback(Path.Combine(note.Folder, "audio.webm"));
        reader.CurrentTime = TimeSpan.FromSeconds(0.5);
        if (reader.Read(new byte[960 * 2], 0, 960 * 2) == 0) throw new Exception("Playback seek failed.");
        Console.WriteLine($"PASS: real WASAPI capture; {samples.Length} decoded samples; peak {peak}; seekable native playback; {TranscriptQueue.Count(note.Folder)} durable segments");
    }
}
