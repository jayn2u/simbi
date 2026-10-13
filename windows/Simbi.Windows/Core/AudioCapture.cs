using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Simbi.Windows.Core;

public sealed record InputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class AudioCapture : IAsyncDisposable
{
    private readonly List<Source> sources = [];
    private CancellationTokenSource? cancellation;
    private Task? pump;
    private PcmRecording? wav;
    private OpusWebM? webm;
    private string folder = "";
    public double Seconds { get; private set; }
    public float Peak { get; private set; }
    public event Action<string>? Status;
    public event Action? SegmentReady;
    public event Action<Exception>? Failed;

    public static IReadOnlyList<InputDevice> Microphones()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => { var item = new InputDevice(d.ID, d.FriendlyName); d.Dispose(); return item; }).ToList();
    }
    public void Start(Note note, bool microphone, bool systemAudio, string microphoneId = "")
    {
        if (pump != null) throw new InvalidOperationException("Already recording.");
        if (!microphone && !systemAudio) throw new InvalidOperationException("Select microphone or system audio first.");
        folder = note.Folder;
        var mainAudio = Path.Combine(folder, "audio.webm");
        if (File.Exists(mainAudio) && !RecordingStamp.Matches(folder))
            throw new IOException("This note contains an existing or externally changed recording. Create a new note for Windows recording; the original audio will be preserved.");
        try
        {
            wav = new PcmRecording(Path.Combine(folder, ".simbi", "audio.wav"));
            Seconds = wav.Samples / 48000.0;
            webm = new OpusWebM(mainAudio, Seconds);
            RecordingStamp.Save(folder, true, Seconds);
            if (microphone)
            {
                using var devices = new MMDeviceEnumerator();
                var device = string.IsNullOrWhiteSpace(microphoneId)
                    ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(microphoneId);
                sources.Add(new Source(new WasapiCapture(device), "Microphone", Seconds, SignalFailure, Checkpoint, device));
            }
            if (systemAudio) sources.Add(new Source(new WasapiLoopbackCapture(), "System audio", Seconds, SignalFailure, Checkpoint));
            cancellation = new();
            foreach (var source in sources)
            {
                source.Capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) SignalFailure(e.Exception);
                };
                source.Capture.StartRecording();
            }
            pump = Task.Run(() => PumpAsync(cancellation.Token));
        }
        catch
        {
            var opened = webm != null;
            Cleanup();
            if (opened) RecordingStamp.Save(folder, false, Seconds);
            throw;
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var frames = 0L;
        var initial = Seconds;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                // Use elapsed time, not timer ticks: late scheduling must not shorten the timeline.
                var target = (long)(clock.Elapsed.TotalSeconds * 50);
                while (frames < target)
                {
                    var mixed = new short[960];
                    var inputFrames = sources.Select(source => (Source: source, Pcm: source.ReadFrame())).ToArray();
                    foreach (var input in inputFrames)
                    {
                        for (var i = 0; i < 960; i++) mixed[i] = (short)Math.Clamp(mixed[i] + input.Pcm[i], short.MinValue, short.MaxValue);
                    }
                    wav!.Write(mixed); webm!.Write(mixed);
                    foreach (var input in inputFrames) input.Source.Segment(input.Pcm, folder, () => SegmentReady?.Invoke());
                    Peak = mixed.Max(v => Math.Abs((int)v)) / 32768f;
                    Seconds = initial + ++frames * 0.02;
                    if (frames % 50 == 0) { wav.Checkpoint(); webm.Flush(); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally
        {
            foreach (var source in sources)
            {
                try { source.FlushSegment(folder, () => SegmentReady?.Invoke()); }
                catch (Exception ex) { Failed?.Invoke(ex); }
            }
            wav?.Checkpoint(); webm?.Flush();
            Status?.Invoke("Recording saved");
        }
    }
    public async Task StopAsync()
    {
        if (pump == null) return;
        cancellation!.Cancel();
        try { await pump; }
        finally { Cleanup(); RecordingStamp.Save(folder, false, Seconds); }
    }
    private void Checkpoint() { wav!.Checkpoint(); webm!.Flush(); }
    private void Cleanup()
    {
        Exception? failure = null;
        foreach (var source in sources) { try { source.Dispose(); } catch (Exception ex) { failure ??= ex; } }
        sources.Clear();
        try { wav?.Dispose(); } catch (Exception ex) { failure ??= ex; } finally { wav = null; }
        try { webm?.Dispose(); } catch (Exception ex) { failure ??= ex; } finally { webm = null; }
        cancellation?.Dispose(); cancellation = null; pump = null;
        if (failure != null) throw failure;
    }
    private void SignalFailure(Exception error)
    {
        var active = cancellation; if (active == null) return;
        try { active.Cancel(); } catch (ObjectDisposedException) { return; }
        Failed?.Invoke(error);
    }
    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class Source : IDisposable
    {
        public WasapiCapture Capture { get; }
        private readonly BufferedWaveProvider buffer;
        private readonly ISampleProvider samples;
        private readonly MMDevice? device;
        private readonly SpeechSegmenter segmenter;
        public Source(WasapiCapture capture, string label, double initial, Action<Exception> fail, Action beforeCommit, MMDevice? device = null)
        {
            Capture = capture; this.device = device; segmenter = new(label, initial);
            segmenter.BeforeCommit = beforeCommit;
            buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(5), DiscardOnBufferOverflow = false, ReadFully = true };
            capture.DataAvailable += (_, e) =>
            {
                try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
                catch (InvalidOperationException) { fail(new IOException("Audio capture fell behind. Recording stopped to avoid silently dropping sound; recorded audio is preserved.")); }
            };
            ISampleProvider mono = new Downmix(buffer.ToSampleProvider());
            samples = mono.WaveFormat.SampleRate == 48000 ? mono : new WdlResamplingSampleProvider(mono, 48000);
        }
        public short[] ReadFrame()
        {
            var floats = new float[960]; samples.Read(floats, 0, floats.Length);
            return floats.Select(v => (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue)).ToArray();
        }
        public void Segment(short[] pcm, string folder, Action ready)
        {
            segmenter.Process(pcm, folder, ready);
        }
        public void FlushSegment(string folder, Action ready)
        {
            segmenter.Flush(folder, ready);
        }
        public void Dispose() { try { Capture.StopRecording(); } finally { try { Capture.Dispose(); } finally { device?.Dispose(); } } }
    }
    private sealed class Downmix(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var data = new float[count * channels];
            var read = source.Read(data, 0, data.Length) / channels;
            for (var i = 0; i < read; i++)
            {
                var sum = 0f; for (var c = 0; c < channels; c++) sum += data[i * channels + c];
                buffer[offset + i] = sum / channels;
            }
            return read;
        }
    }
}
