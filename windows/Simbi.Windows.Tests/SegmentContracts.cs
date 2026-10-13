using System.Net;
using System.Net.Http;
using System.Text.Json;
using Simbi.Windows.Core;

internal static class SegmentContracts
{
    public static async Task RunAsync(string root)
    {
        var note = new NoteStore(root).Create("Deterministic speech segments");
        var segmenter = new SpeechSegmenter("Microphone"); var notifications = 0;
        for (var i = 0; i < 100; i++) segmenter.Process(new short[960], note.Folder, () => notifications++);
        if (TranscriptQueue.Count(note.Folder) != 0) throw new Exception("Silence created speech uploads.");
        var frame = Enumerable.Range(0, 960).Select(i => (short)(10000 * Math.Sin(i * 2 * Math.PI * 440 / 48000))).ToArray();
        for (var i = 0; i < 20; i++) segmenter.Process(frame, note.Folder, () => notifications++);
        for (var i = 0; i < 40; i++) segmenter.Process(new short[960], note.Folder, () => notifications++);
        if (notifications != 1 || TranscriptQueue.Count(note.Folder) != 1) throw new Exception("Speech did not flush exactly one durable segment.");
        var manifest = Directory.GetFiles(PendingSegments.DirectoryFor(note.Folder), "*.json").Single();
        var item = JsonSerializer.Deserialize<PendingSegment>(File.ReadAllText(manifest), AtomicFile.Json)!;
        if (Math.Abs(item.Start - 1.9) > 0.001 || item.End < 3.1 || item.Speaker != "Microphone") throw new Exception("Speech source/timestamps are invalid.");
        if (OpusWebM.Decode(Path.ChangeExtension(manifest, ".webm")).All(v => Math.Abs((int)v) < 1000)) throw new Exception("Speech payload is silent.");
        using var client = new TranscriptionClient(new Success(), () => new("synthetic", "synthetic"));
        await new TranscriptQueue(client).DrainAsync(note.Folder);
        var cue = Vtt.Read(note.Folder).Single();
        if (cue.Speaker != item.Speaker || Math.Abs(cue.Start - item.Start) > 0.001 || cue.Text != "Verified speech") throw new Exception("Captured speech did not become a matching cue.");
        Console.WriteLine("PASS: silence filtering, speech segmentation, source/timing metadata and end-to-end queued cue commit");

        var orphanNote = new NoteStore(root).Create("Orphaned queue metadata");
        var orphanQueue = PendingSegments.DirectoryFor(orphanNote.Folder); Directory.CreateDirectory(orphanQueue);
        AtomicFile.WriteJson(Path.Combine(orphanQueue, "orphan.json"), new PendingSegment("orphan", 0, 1, "Microphone"));
        AtomicFile.WriteJson(Path.Combine(orphanQueue, "later.json"), new PendingSegment("later", 1, 2, "Microphone"));
        using (var output = new OpusWebM(Path.Combine(orphanQueue, "later.webm"))) output.Write(Enumerable.Repeat(frame, 20).SelectMany(f => f).ToArray());
        await new TranscriptQueue(client).DrainAsync(orphanNote.Folder);
        if (Vtt.Read(orphanNote.Folder).SingleOrDefault()?.Id != "later" || !File.Exists(Path.Combine(orphanQueue, TranscriptQueue.FailedFolder, "orphan.json")))
            throw new Exception("Orphaned metadata blocked later segments or was not quarantined.");
        Console.WriteLine("PASS: orphaned queue metadata is quarantined and later segments still transcribe");

        var staged = new PendingSegment("interrupted", 0, 1, "System audio");
        var pending = PendingSegments.DirectoryFor(note.Folder);
        AtomicFile.WriteJson(Path.Combine(pending, "interrupted.pending"), staged);
        using (var output = new OpusWebM(Path.Combine(pending, "interrupted.partial"))) output.Write(Enumerable.Repeat(frame, 50).SelectMany(f => f).ToArray());
        using (var tail = new FileStream(Path.Combine(pending, "interrupted.partial"), FileMode.Append)) tail.Write([0x1f, 0x43]);
        PendingSegments.Recover(note.Folder);
        if (!File.Exists(Path.Combine(pending, "interrupted.json")) || !File.Exists(Path.Combine(pending, "interrupted.webm"))) throw new Exception("Staged audio recovery failed.");
        Console.WriteLine("PASS: interrupted segment encoding repairs trailing bytes and recovers durable source/timestamps");
        var beforePacket = new NoteStore(root).Create("Interrupted before first packet");
        using (var mixed = new PcmRecording(Path.Combine(beforePacket.Folder, ".simbi", "audio.wav"))) mixed.Write(Enumerable.Repeat(frame, 50).SelectMany(f => f).ToArray());
        var early = PendingSegments.DirectoryFor(beforePacket.Folder); Directory.CreateDirectory(early);
        AtomicFile.WriteJson(Path.Combine(early, "early.pending"), new PendingSegment("early", 0, 1, "Microphone"));
        File.WriteAllBytes(Path.Combine(early, "early.partial"), [0x1a, 0x45]);
        PendingSegments.Recover(beforePacket.Folder);
        var recovered = JsonSerializer.Deserialize<PendingSegment>(File.ReadAllText(Path.Combine(early, "early.json")), AtomicFile.Json)!;
        if (recovered.Speaker != "Recovered audio (mixed)" || OpusWebM.Decode(Path.Combine(early, "early.webm")).All(v => Math.Abs((int)v) < 1000))
            throw new Exception("Speech was lost before its first encoded packet.");
        Console.WriteLine("PASS: a crash before the first Opus packet recovers saved PCM with an honest mixed-source label");

        var failedStart = new NoteStore(root).Create("Failed capture startup");
        await using (var failedCapture = new AudioCapture())
        {
            var rejected = false;
            try { failedCapture.Start(failedStart, true, false, "simbi-missing-test-device"); }
            catch (Exception) { rejected = true; }
            if (!rejected) throw new Exception("Missing capture device was accepted.");
        }
        var state = JsonSerializer.Deserialize<RecordingStamp>(File.ReadAllText(RecordingStamp.PathFor(failedStart.Folder)), AtomicFile.Json)!;
        if (state.Active) throw new Exception("Failed capture startup left a false active marker.");
        Console.WriteLine("PASS: failed capture startup closes its files and leaves an inactive marker");

        var audio = Path.Combine(note.Folder, "audio.webm");
        using (var recording = new OpusWebM(audio)) recording.Write(frame);
        RecordingStamp.Save(note.Folder, false, 0.02);
        if (!RecordingStamp.Matches(note.Folder)) throw new Exception("Own recording stamp did not match.");
        File.Delete(audio); using (var replacement = new OpusWebM(audio)) replacement.Write(frame);
        if (RecordingStamp.Matches(note.Folder)) throw new Exception("An externally replaced recording matched stale state.");
        await using var capture = new AudioCapture();
        try { capture.Start(note, false, true); throw new Exception("Stale Windows state permitted replacing external audio."); }
        catch (IOException) { }
        Console.WriteLine("PASS: stale Windows state cannot authorize appending to externally replaced audio");

        PendingSegments.Discard(note.Folder);
        if (TranscriptQueue.Count(note.Folder) != 0 || !File.Exists(audio) || Vtt.Read(note.Folder).Count != 1) throw new Exception("Queue discard changed the full recording or transcript.");
        Console.WriteLine("PASS: pending-audio discard preserves the full recording and transcript");
    }
    private sealed class Success : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Verified speech\"}") });
    }
}
