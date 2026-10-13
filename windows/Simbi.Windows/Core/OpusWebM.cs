using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;

namespace Simbi.Windows.Core;

// Streaming, unknown-sized Segment, one mono Opus track, 20 ms SimpleBlocks.
// Each complete Cluster can survive a process interruption independently.
public sealed class OpusWebM : IDisposable
{
    private readonly FileStream stream;
    private readonly IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
    private readonly double baseSeconds;
    private long frames;
    public OpusWebM(string path, double baseSeconds = 0)
    {
        this.baseSeconds = baseSeconds;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
        encoder.Bitrate = 32000;
        if (stream.Length == 0) WriteHeader();
        else
        {
            // Drop only an incomplete trailing element left by a crashed writer.
            var complete = CompleteEnd(stream);
            stream.SetLength(complete);
        }
        stream.Position = stream.Length;
        }
        catch { stream.Dispose(); throw; }
    }

    private void WriteHeader()
    {
        stream.Write(Element(0x1A45DFA3, Join(
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Text(0x4282, "webm"), UInt(0x4287, 4), UInt(0x4285, 2))));
        stream.Write(Id(0x18538067)); stream.Write([0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]);
        stream.Write(Element(0x1549A966, Join(UInt(0x2AD7B1, 1000000), Text(0x4D80, "Simbi Windows"), Text(0x5741, "Simbi Windows"))));
        var head = new byte[19]; Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; head[9] = 1; BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        var rate = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(rate, BitConverter.DoubleToInt64Bits(48000));
        stream.Write(Element(0x1654AE6B, Element(0xAE, Join(
            UInt(0xD7, 1), UInt(0x73C5, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1UL), UInt(0x83, 2), Text(0x86, "A_OPUS"), Element(0x63A2, head),
            UInt(0x56AA, 6500000), UInt(0x56BB, 80000000),
            Element(0xE1, Join(Element(0xB5, rate), UInt(0x9F, 1)))))));
        stream.Flush(true);
    }
    public void Write(short[] samples)
    {
        var packet = new byte[1275];
        for (var offset = 0; offset < samples.Length; offset += 960)
        {
            var frame = new short[960]; Array.Copy(samples, offset, frame, 0, Math.Min(960, samples.Length - offset));
            var count = encoder.Encode(frame, 960, packet, packet.Length);
            var block = new byte[count + 4]; block[0] = 0x81; block[3] = 0x80;
            Array.Copy(packet, 0, block, 4, count);
            var time = (ulong)Math.Round(baseSeconds * 1000) + (ulong)(frames++ * 20);
            stream.Write(Element(0x1F43B675, Join(UInt(0xE7, time), Element(0xA3, block))));
        }
    }
    public void Flush() => stream.Flush(true);
    public void Dispose() => stream.Dispose();

    public static string HeaderFingerprint(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[128]; input.ReadExactly(header);
        if (!Encoding.UTF8.GetString(header).Contains("Simbi Windows", StringComparison.Ordinal))
            throw new InvalidDataException("This recording was created by another application.");
        return Convert.ToHexString(SHA256.HashData(header));
    }

    public static short[] Decode(string path)
    {
        using var source = File.OpenRead(path);
        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var result = new List<short>();
        Walk(source, source.Length, (id, body) =>
        {
            if (id != 0xA3 || body.Length < 5) return;
            if ((body[3] & 0x06) != 0) throw new InvalidDataException("Laced WebM audio is not supported by this decoder.");
            var pcm = new short[5760];
            var count = decoder.Decode(body.AsSpan(4), pcm, 5760, false);
            result.AddRange(pcm.Take(count));
        });
        return result.ToArray();
    }

    private static void Walk(Stream source, long end, Action<ulong, byte[]> visit)
    {
        while (source.Position < end)
        {
            if (!ReadVint(source, false, out var id) || !ReadVint(source, true, out var length)) break;
            var stop = length == ulong.MaxValue ? end : Math.Min(end, source.Position + (long)length);
            if (id is 0x18538067 or 0x1F43B675) Walk(source, stop, visit);
            else if (id == 0xA3)
            {
                var bytes = new byte[checked((int)(stop - source.Position))]; source.ReadExactly(bytes); visit(id, bytes);
            }
            source.Position = stop;
        }
    }
    private static long CompleteEnd(Stream source)
    {
        source.Position = 0;
        var complete = 0L;
        while (source.Position < source.Length)
        {
            if (!ReadVint(source, false, out var id) || !ReadVint(source, true, out var size)) break;
            if (id == 0x18538067 && size == ulong.MaxValue) { complete = source.Position; continue; }
            if (size == ulong.MaxValue || size > (ulong)(source.Length - source.Position)) break;
            source.Position += (long)size; complete = source.Position;
        }
        if (complete == 0) throw new InvalidDataException("Cannot resume an invalid WebM recording.");
        return complete;
    }
    private static bool ReadVint(Stream source, bool removeMarker, out ulong value)
    {
        value = 0; var first = source.ReadByte(); if (first <= 0) return false;
        var width = 1; var mask = 0x80;
        while ((first & mask) == 0) { width++; mask >>= 1; }
        value = (ulong)(removeMarker ? first & (mask - 1) : first);
        for (var n = 1; n < width; n++) { var b = source.ReadByte(); if (b < 0) return false; value = (value << 8) | (byte)b; }
        if (removeMarker && value == (1UL << (width * 7)) - 1) value = ulong.MaxValue;
        return true;
    }
    private static byte[] Id(uint id)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
        return bytes.SkipWhile(b => b == 0).ToArray();
    }
    private static byte[] Element(uint id, byte[] content)
    {
        var width = 1; while ((ulong)content.Length >= (1UL << (7 * width)) - 1) width++;
        var size = new byte[width]; var value = (ulong)(uint)content.Length | (1UL << (width * 7));
        for (var n = width - 1; n >= 0; n--) { size[n] = (byte)value; value >>= 8; }
        return Join(Id(id), size, content);
    }
    private static byte[] UInt(uint id, ulong value)
    {
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var trimmed = bytes.SkipWhile(b => b == 0).ToArray();
        return Element(id, trimmed.Length == 0 ? [0] : trimmed);
    }
    private static byte[] Text(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));
    private static byte[] Join(params byte[][] bytes) => bytes.SelectMany(b => b).ToArray();
}
