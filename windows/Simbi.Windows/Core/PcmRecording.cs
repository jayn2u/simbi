using System.Buffers.Binary;

namespace Simbi.Windows.Core;

// A fixed PCM header makes an interrupted capture recoverable from file length.
public sealed class PcmRecording : IDisposable
{
    private readonly FileStream stream;
    public long Samples => Math.Max(0, (stream.Length - 44) / 2);
    public PcmRecording(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length is > 0 and < 44) { stream.Dispose(); throw new IOException("Invalid recording header."); }
        if (stream.Length == 0) stream.SetLength(44);
        else
        {
            Span<byte> prefix = stackalloc byte[12];
            stream.ReadExactly(prefix);
            if (!prefix[..4].SequenceEqual("RIFF"u8) || !prefix[8..].SequenceEqual("WAVE"u8))
            { stream.Dispose(); throw new IOException("Recording is not a PCM WAV file."); }
        }
        Checkpoint();
        stream.Position = stream.Length;
    }
    public void Write(short[] samples)
    {
        if (stream.Length + samples.Length * 2 >= uint.MaxValue) throw new IOException("Recording reached the WAV size limit. Stop and create a new note.");
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        stream.Write(bytes);
    }
    public void Checkpoint()
    {
        var position = stream.Position;
        stream.Position = 0;
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        writer.Write("RIFF"u8); writer.Write((uint)(stream.Length - 8)); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(48000); writer.Write(96000);
        writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write((uint)(stream.Length - 44));
        stream.Position = Math.Max(44, position);
        stream.Flush(true);
    }
    public void Dispose() { try { Checkpoint(); } finally { stream.Dispose(); } }
}
