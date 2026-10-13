using System.Text.Json;
using System.Buffers.Binary;
using NAudio.Wave;

namespace Simbi.Windows.Core;

public sealed class SpeechSegmenter(string label, double initialSeconds = 0)
{
    public Action? BeforeCommit { get; set; }
    private readonly List<short> segment = [];
    private readonly Queue<short> preroll = new();
    private double time = initialSeconds;
    private double segmentStart;
    private int silenceFrames;
    public void Process(short[] pcm, string folder, Action ready)
    {
        if (pcm.Length != 960) throw new ArgumentException("Speech segmentation requires one 20-ms mono frame.", nameof(pcm));
        var energy = Math.Sqrt(pcm.Average(v => (double)v * v)) / 32768;
        if (energy > 0.006)
        {
            if (segment.Count == 0)
            {
                segmentStart = Math.Max(0, time - preroll.Count / 48000.0);
                segment.AddRange(preroll); preroll.Clear();
            }
            silenceFrames = 0;
        }
        else silenceFrames++;
        if (segment.Count > 0 || energy > 0.006) segment.AddRange(pcm);
        else
        {
            foreach (var value in pcm) preroll.Enqueue(value);
            while (preroll.Count > 4800) preroll.Dequeue();
        }
        time += 0.02;
        if (segment.Count >= 48000 * 20 || segment.Count > 0 && silenceFrames >= 40) Flush(folder, ready);
    }
    public void Flush(string folder, Action ready)
    {
        if (segment.Count >= 4800)
        {
            BeforeCommit?.Invoke();
            var id = "win-" + Guid.NewGuid().ToString("N");
            PendingSegments.Commit(folder, new(id, segmentStart, segmentStart + segment.Count / 48000.0, label), segment.ToArray());
            ready();
        }
        segment.Clear(); silenceFrames = 0;
    }
}

public static class PendingSegments
{
    private static readonly object Gate = new();
    public static string DirectoryFor(string folder) => Path.Combine(folder, ".simbi", "windows-pending");
    public static void Commit(string folder, PendingSegment item, short[] samples)
    {
        lock (Gate)
        {
            var pending = DirectoryFor(folder); Directory.CreateDirectory(pending);
            var manifest = Path.Combine(pending, item.Id + ".pending");
            var partial = Path.Combine(pending, item.Id + ".partial");
            // Write durable timestamps/source BEFORE audio. Recovery never has to guess them.
            AtomicFile.WriteJson(manifest, item);
            using (var output = new OpusWebM(partial)) { output.Write(samples); output.Flush(); }
            File.Move(partial, Path.Combine(pending, item.Id + ".webm"));
            File.Move(manifest, Path.Combine(pending, item.Id + ".json"));
        }
    }
    public static void Recover(string folder)
    {
        lock (Gate)
        {
            var pending = DirectoryFor(folder);
            if (!Directory.Exists(pending)) return;
            foreach (var manifest in Directory.GetFiles(pending, "*.pending"))
            {
                var item = JsonSerializer.Deserialize<PendingSegment>(File.ReadAllText(manifest), AtomicFile.Json);
                if (item == null || item.Id != Path.GetFileNameWithoutExtension(manifest)) throw new InvalidDataException("Invalid staged speech metadata.");
                var audio = Path.ChangeExtension(manifest, ".webm");
                var partial = Path.ChangeExtension(manifest, ".partial");
                if (!File.Exists(audio))
                {
                    var complete = false;
                    if (File.Exists(partial))
                    {
                        try
                        {
                            using (var repair = new OpusWebM(partial)) { }
                            using var decoded = new WebMPlayback(partial);
                            complete = decoded.TotalTime.TotalSeconds >= item.End - item.Start - 0.02;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
                    }
                    if (complete) File.Move(partial, audio);
                    else if (File.Exists(Path.Combine(folder, ".simbi", "audio.wav")))
                    {
                        // The capture checkpoints mixed PCM before staging a manifest. It can
                        // recover speech even if the process died before the first Opus packet.
                        item = RecoverFromPcm(folder, item, audio);
                        if (File.Exists(partial)) File.Delete(partial);
                    }
                    else if (File.Exists(partial))
                    {
                        using (var repair = new OpusWebM(partial)) { }
                        // Dispose the reader before File.Move, or Windows reports a sharing violation.
                        using (var decoded = new WebMPlayback(partial))
                            item = item with { End = Math.Min(item.End, item.Start + decoded.TotalTime.TotalSeconds) };
                        File.Move(partial, audio);
                    }
                }
                if (!File.Exists(audio) && !File.Exists(partial))
                    throw new IOException("An interrupted speech segment has metadata but no audio payload. The manifest is preserved: " + manifest);
                if (File.Exists(audio))
                {
                    AtomicFile.WriteJson(manifest, item);
                    AtomicFile.WriteJson(Path.ChangeExtension(manifest, ".json"), item);
                    File.Delete(manifest);
                }
            }
        }
    }
    private static PendingSegment RecoverFromPcm(string folder, PendingSegment item, string audio)
    {
        var wav = Path.Combine(folder, ".simbi", "audio.wav");
        using (var repair = new PcmRecording(wav)) { }
        using var input = new WaveFileReader(wav);
        var start = (long)Math.Round(item.Start * 48000);
        var count = (int)Math.Min(Math.Round((item.End - item.Start) * 48000), input.Length / 2 - start);
        if (count <= 0) throw new IOException("Interrupted speech segment is outside the saved recording timeline.");
        input.Position = start * 2;
        var bytes = new byte[count * 2]; input.ReadExactly(bytes);
        var samples = new short[count];
        for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
        var recovered = audio + ".recovered";
        // Repeated interrupted recoveries start a fresh output, never append another copy.
        if (File.Exists(recovered)) File.Delete(recovered);
        using (var output = new OpusWebM(recovered)) { output.Write(samples); output.Flush(); }
        File.Move(recovered, audio);
        return item with { End = item.Start + count / 48000.0, Speaker = "Recovered audio (mixed)" };
    }
    public static void Discard(string folder)
    {
        lock (Gate)
        {
            var pending = DirectoryFor(folder);
            if (!Directory.Exists(pending)) return;
            foreach (var file in Directory.GetFiles(pending))
                if (new[] { ".json", ".webm", ".pending", ".partial", ".recovered" }.Contains(Path.GetExtension(file))) File.Delete(file);
        }
    }
}
