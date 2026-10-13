using System.Net;
using System.Net.Http;
using Simbi.Windows.Core;

internal static class QueueContracts
{
    public static async Task RunAsync(string root)
    {
        var note = new NoteStore(root).Create("Queue contracts");
        var folder = Path.Combine(note.Folder, ".simbi", "windows-pending"); Directory.CreateDirectory(folder);
        var metadata = Path.Combine(folder, "queued-1.json"); var audio = Path.ChangeExtension(metadata, ".webm");
        AtomicFile.WriteJson(metadata, new PendingSegment("queued-1", 0, 1, "System audio"));
        File.WriteAllBytes(audio, [1, 2, 3]);
        using var handler = new Responses();
        using var client = new TranscriptionClient(handler, () => new("test-token", "test-account"));
        var queue = new TranscriptQueue(client);
        try { await queue.DrainAsync(note.Folder); throw new Exception("Failed upload was accepted."); }
        catch (HttpRequestException) { }
        if (!File.Exists(metadata) || !File.Exists(audio)) throw new Exception("Offline audio was discarded.");
        Console.WriteLine("PASS: failed transcription retains both queue files");
        await queue.DrainAsync(note.Folder);
        if (Vtt.Read(note.Folder).Count != 1 || File.Exists(metadata) || File.Exists(audio)) throw new Exception("Retry did not commit and remove its queue entry.");
        AtomicFile.WriteJson(metadata, new PendingSegment("queued-1", 0, 1, "System audio")); File.WriteAllBytes(audio, [1, 2, 3]);
        await queue.DrainAsync(note.Folder);
        if (handler.Calls != 2 || Vtt.Read(note.Folder).Count != 1) throw new Exception("Crash recovery duplicated a committed cue or uploaded it again.");
        Console.WriteLine("PASS: retry commits exactly once; post-commit crash recovery does not re-upload");
        AtomicFile.WriteJson(metadata, new PendingSegment("../../escape", 0, 1, "System audio")); File.WriteAllBytes(audio, [1, 2, 3]);
        try { await queue.DrainAsync(note.Folder); throw new Exception("Invalid queue metadata was accepted."); }
        catch (InvalidDataException) { }
        if (!File.Exists(metadata) || handler.Calls != 2) throw new Exception("Invalid metadata modified or transmitted data.");
        Console.WriteLine("PASS: invalid queue metadata is retained and rejected before upload");

        var store = new NoteStore(root);
        store.SaveText(note, "note.md", "external edit");
        try { store.SaveText(note, "note.md", "my edit", "older content"); throw new Exception("External edit was overwritten."); }
        catch (IOException) { }
        if (store.ReadText(note, "note.md") != "external edit" || !Directory.GetFiles(Path.Combine(note.Folder, ".simbi", "conflicts")).Any())
            throw new Exception("Edit conflict was not preserved.");
        Console.WriteLine("PASS: external note edits and local conflict copy are both preserved");
        try { AtomicFile.WriteChecked(Path.Combine(note.Folder, "note.md"), "racing local save", "stale version"); throw new Exception("Compare/replace race was not reported."); }
        catch (NoteConflictException ex)
        {
            if (File.ReadAllText(ex.RecoveryPath) != "external edit") throw new Exception("Exact externally replaced version was lost.");
        }
        Console.WriteLine("PASS: atomic replacement backs up the exact external version during an edit race");

        var macNote = store.Create("macOS compatibility");
        File.WriteAllBytes(Path.Combine(macNote.Folder, "audio.webm"), [1, 2, 3]);
        await using var capture = new AudioCapture();
        try { capture.Start(macNote, false, true); throw new Exception("Existing macOS audio was replaced."); }
        catch (IOException) { }
        if (!File.ReadAllBytes(Path.Combine(macNote.Folder, "audio.webm")).SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("macOS audio changed.");
        Console.WriteLine("PASS: existing macOS recording is protected from overwrite");
        var busyNote = store.Create("Busy queue"); var busyFolder = PendingSegments.DirectoryFor(busyNote.Folder); Directory.CreateDirectory(busyFolder);
        AtomicFile.WriteJson(Path.Combine(busyFolder, "busy.json"), new PendingSegment("busy", 0, 1, "Microphone"));
        File.WriteAllBytes(Path.Combine(busyFolder, "busy.webm"), [1, 2, 3]);
        using var delayed = new Delayed(); using var busyClient = new TranscriptionClient(delayed, () => new("synthetic", "synthetic"));
        var busyQueue = new TranscriptQueue(busyClient); var uploading = busyQueue.DrainAsync(busyNote.Folder);
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await busyQueue.DiscardAsync(busyNote.Folder); throw new Exception("Discard raced an in-flight upload."); }
        catch (InvalidOperationException) { }
        if (!File.Exists(Path.Combine(busyFolder, "busy.json")) || !File.Exists(Path.Combine(busyFolder, "busy.webm"))) throw new Exception("Active upload files were discarded.");
        delayed.Completed.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Busy upload completed\"}") });
        await uploading;
        Console.WriteLine("PASS: pending-audio discard refuses to race an active upload");
    }
    private sealed class Delayed : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Started.TrySetResult(); return Completed.Task.WaitAsync(token); }
    }
    private sealed class Responses : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization?.Parameter != "test-token") throw new Exception("Authentication header missing.");
            if (!(await request.Content!.ReadAsStringAsync(token)).Contains("codex.webm")) throw new Exception("WebM multipart missing.");
            return ++Calls == 1 ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Synthetic transcript ✓\"}") };
        }
    }
}
