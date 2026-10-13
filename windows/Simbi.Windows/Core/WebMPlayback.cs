using System.Buffers.Binary;
using Concentus;
using NAudio.Wave;

namespace Simbi.Windows.Core;

// Packet index keeps long meetings seekable without decoding the whole file into RAM.
public sealed class WebMPlayback : WaveStream
{
    private sealed record Packet(long Offset, int Bytes, long Sample, int Samples);
    private readonly FileStream source;
    private readonly List<Packet> packets = [];
    private readonly IOpusDecoder decoder;
    private readonly int channels;
    private readonly long length;
    private byte[] decoded = [];
    private int decodedOffset;
    private int nextPacket;
    private long position;
    public override WaveFormat WaveFormat { get; }
    public override long Length => length;
    public override long Position
    {
        get => position;
        set
        {
            position = Math.Clamp(value, 0, Length) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
            var sample = position / WaveFormat.BlockAlign;
            var index = packets.FindIndex(p => p.Sample + p.Samples > sample);
            if (index < 0) { nextPacket = packets.Count; decoded = []; decodedOffset = 0; return; }
            decoder.ResetState();
            for (var i = Math.Max(0, index - 4); i < index; i++) Decode(packets[i]);
            decoded = Decode(packets[index]);
            decodedOffset = (int)(sample - packets[index].Sample) * WaveFormat.BlockAlign;
            nextPacket = index + 1;
        }
    }
    public WebMPlayback(string path)
    {
        source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var locations = new List<(long Offset, int Bytes)>();
        var detectedChannels = 1;
        try
        {
            Scan(source.Length, locations, ref detectedChannels);
            channels = detectedChannels;
            WaveFormat = new WaveFormat(48000, 16, channels);
            decoder = OpusCodecFactory.CreateDecoder(48000, channels);
            var samples = 0L;
            foreach (var (offset, bytes) in locations)
            {
                source.Position = offset; var packet = new byte[bytes]; source.ReadExactly(packet);
                var count = Concentus.Structs.OpusPacketInfo.GetNumSamples(packet.AsSpan(), 48000);
                packets.Add(new(offset, bytes, samples, count)); samples += count;
            }
            length = samples * WaveFormat.BlockAlign;
            if (packets.Count == 0) throw new InvalidDataException("No Opus audio packets found.");
            Position = 0;
        }
        catch { source.Dispose(); throw; }
    }
    private byte[] Decode(Packet packet)
    {
        source.Position = packet.Offset; var bytes = new byte[packet.Bytes]; source.ReadExactly(bytes);
        var pcm = new short[5760 * channels];
        var count = decoder.Decode(bytes, pcm, 5760, false);
        var result = new byte[count * channels * 2];
        for (var i = 0; i < count * channels; i++) BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), pcm[i]);
        return result;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count && position < Length)
        {
            if (decodedOffset >= decoded.Length)
            {
                if (nextPacket >= packets.Count) break;
                decoded = Decode(packets[nextPacket++]); decodedOffset = 0;
            }
            var take = Math.Min(count - total, decoded.Length - decodedOffset);
            Array.Copy(decoded, decodedOffset, buffer, offset + total, take);
            total += take; decodedOffset += take; position += take;
        }
        return total;
    }
    private void Scan(long end, List<(long, int)> locations, ref int detectedChannels)
    {
        while (source.Position < end)
        {
            var id = Vint(false); var size = Vint(true);
            var stop = size == ulong.MaxValue ? end : Math.Min(end, source.Position + checked((long)size));
            if (id is 0x18538067 or 0x1F43B675 or 0x1654AE6B or 0xAE or 0xA0) Scan(stop, locations, ref detectedChannels);
            else if (id == 0x63A2 && stop - source.Position >= 19)
            {
                var head = new byte[19]; source.ReadExactly(head);
                if (head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)) detectedChannels = head[9];
                if (detectedChannels is < 1 or > 2) throw new InvalidDataException("Only mono or stereo Opus recordings are supported.");
            }
            else if (id is 0xA3 or 0xA1)
            {
                var track = Vint(true);
                if (track != 1) { source.Position = stop; continue; }
                source.Position += 2; var flags = source.ReadByte();
                if ((flags & 6) != 0) throw new InvalidDataException("Laced Opus recordings are not supported.");
                var bytes = checked((int)(stop - source.Position));
                if (bytes > 65024) throw new InvalidDataException("An Opus packet exceeds the supported size.");
                if (bytes > 0) locations.Add((source.Position, bytes));
            }
            source.Position = stop;
        }
    }
    private ulong Vint(bool strip)
    {
        var first = source.ReadByte(); if (first <= 0) throw new InvalidDataException("Incomplete WebM element.");
        var width = 1; var mask = 128;
        while ((first & mask) == 0) { width++; mask >>= 1; }
        var value = (ulong)(strip ? first & (mask - 1) : first);
        for (var n = 1; n < width; n++) { var b = source.ReadByte(); if (b < 0) throw new InvalidDataException("Incomplete WebM element."); value = (value << 8) | (byte)b; }
        return strip && value == (1UL << (width * 7)) - 1 ? ulong.MaxValue : value;
    }
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}
