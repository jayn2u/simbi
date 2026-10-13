using Simbi.Windows.Core;

if (args.Length > 0 && args[0] == "app-server") { await FakeAppServer.RunAsync(); return; }

var root = Path.Combine(Path.GetTempPath(), "Simbi-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new NoteStore(root);
    var note = store.Create("회의 / planning");
    Assert(Path.GetDirectoryName(note.Folder) == root, "note path stays inside root");
    Assert(store.Create("회의 / planning").Folder != note.Folder, "duplicate titles do not overwrite");
    Assert(!Path.GetFileName(store.Create("CON").Folder).Equals("CON", StringComparison.OrdinalIgnoreCase), "reserved Windows names handled");
    store.SaveText(note, "note.md", "# 회의\nUnicode content ✓");
    Assert(store.ReadText(note, "note.md").Contains("✓"), "Unicode notes survive reload");
    var attachment = Path.Combine(root, "reference.txt"); File.WriteAllText(attachment, "Original attachment ✓");
    var imported = store.Import(note, attachment); var importedAgain = store.Import(note, attachment);
    Assert(File.ReadAllText(imported) == "Original attachment ✓" && importedAgain != imported,
        "attachment originals and duplicate names are preserved");
    Assert(File.ReadAllText(Path.Combine(note.Folder, "context", "reference.txt.md")) == "Original attachment ✓",
        "text attachment content is supplied as Markdown context");
    var cue = new Cue("segment-1", 1.25, 3.5, "Microphone", "Hello <world> & everyone");
    Vtt.Append(note.Folder, cue);
    Vtt.Append(note.Folder, cue);
    var cues = Vtt.Read(note.Folder);
    Assert(cues.Count == 1 && cues[0].Text == cue.Text, "queue retries do not duplicate or corrupt cues");
    Assert(cues[0].Start == 1.25 && cues[0].Speaker == "Microphone", "timestamps and voice tags round trip");
    var legacy = Path.Combine(note.Folder, ".simbi", "state.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
    File.WriteAllText(legacy, "{\"totalSamples\":123,\"futureField\":true}");
    store.SaveText(note, "summary.md", "summary");
    Assert(File.ReadAllText(legacy).Contains("futureField"), "macOS state stays intact");
    using (var wave = new PcmRecording(Path.Combine(root, "test.wav"))) wave.Write(new short[960]);
    using (var wave = new PcmRecording(Path.Combine(root, "test.wav")))
    {
        Assert(wave.Samples == 960, "recording resumes without losing samples");
        wave.Write(new short[960]);
    }
    Assert(new FileInfo(Path.Combine(root, "test.wav")).Length == 44 + 1920 * 2, "resumed WAV has correct length");
    var pcm = Enumerable.Range(0, 48000).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 10000)).ToArray();
    var webm = Path.Combine(root, "test.webm");
    using (var writer = new OpusWebM(webm)) writer.Write(pcm);
    var decoded = OpusWebM.Decode(webm);
    Assert(decoded.Length >= 47000 && decoded.Any(s => Math.Abs(s) > 2000), "WebM Opus encodes and decodes audible PCM");
    using (var writer = new OpusWebM(webm, 1)) writer.Write(pcm);
    Assert(OpusWebM.Decode(webm).Length >= 95000, "WebM appends a second session");
    if (args.Contains("--ffmpeg")) await ExternalCodec.VerifyAsync(webm);
    await QueueContracts.RunAsync(root);
    await SegmentContracts.RunAsync(root);
    await FakeAppServer.VerifyClientAsync(root);
    if (args.Contains("--codex"))
    {
        var executable = CodexLocator.Find() ?? throw new Exception("Installed Codex executable was not discovered.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await CodexClient.ConnectAsync(executable, root, timeout.Token);
        var account = await client.Request("account/read", new { refreshToken = false }, timeout.Token);
        Console.WriteLine("PASS: installed Windows Codex discovery, initialize handshake and account/read (no AI turn)");
    }
    Console.WriteLine("PASS: all storage, WebVTT, recording and codec contracts");
    if (args.Contains("--capture")) await CaptureSmoke.RunAsync(root);
    UiSmoke.Run(root, args);
}
finally { Directory.Delete(root, true); }

static void Assert(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
