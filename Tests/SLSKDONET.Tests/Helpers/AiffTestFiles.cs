using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SLSKDONET.Tests.Helpers;

/// <summary>
/// Writes small, valid AIFF / AIFF-C files (a 440 Hz sine) for format tests: plain big-endian
/// AIFF at 16 or 24 bit (tags added with TagLib#, as Rekordbox writes them: an "ID3 " chunk), and
/// AIFF-C "sowt" (little-endian, what Logic/GarageBand export) with a hand-built ID3 chunk, which
/// TagLib# can't open.
/// </summary>
public static class AiffTestFiles
{
    public sealed record Tags(string Title, string Artist, uint Bpm = 0, string? Key = null);

    public static string WriteAiff(string path, int bits, int sampleRate, double seconds, Tags? tags = null)
    {
        WriteFile(path, aifc: false, bits, sampleRate, seconds, id3: null);
        if (tags != null)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Title = tags.Title;
            file.Tag.Performers = new[] { tags.Artist };
            if (tags.Bpm > 0) file.Tag.BeatsPerMinute = tags.Bpm;
            if (tags.Key != null) file.Tag.InitialKey = tags.Key;
            file.Save();
        }
        return path;
    }

    public static string WriteAifcSowt(string path, int sampleRate, double seconds, Tags? tags = null)
    {
        byte[]? id3 = null;
        if (tags != null)
        {
            var tag = new TagLib.Id3v2.Tag { Title = tags.Title, Performers = new[] { tags.Artist } };
            if (tags.Bpm > 0) tag.BeatsPerMinute = tags.Bpm;
            if (tags.Key != null) tag.InitialKey = tags.Key;
            id3 = tag.Render().Data;
        }
        WriteFile(path, aifc: true, bits: 16, sampleRate, seconds, id3);
        return path;
    }

    private static void WriteFile(string path, bool aifc, int bits, int sampleRate, double seconds, byte[]? id3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int channels = 2;
        int frames = (int)(sampleRate * seconds);
        int bytesPerSample = bits / 8;
        var pcm = new byte[frames * channels * bytesPerSample];
        double peak = bits == 24 ? 8_388_607 * 0.5 : 32_767 * 0.5;
        for (int f = 0; f < frames; f++)
        {
            int value = (int)Math.Round(Math.Sin(2 * Math.PI * 440 * f / sampleRate) * peak);
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytesPerSample;
                if (bits == 24)
                {
                    pcm[o] = (byte)(value >> 16); pcm[o + 1] = (byte)(value >> 8); pcm[o + 2] = (byte)value;
                }
                else if (aifc) // sowt = little-endian
                {
                    pcm[o] = (byte)value; pcm[o + 1] = (byte)(value >> 8);
                }
                else
                {
                    pcm[o] = (byte)(value >> 8); pcm[o + 1] = (byte)value;
                }
            }
        }

        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(aifc ? "AIFC" : "AIFF"));
        if (aifc) WriteChunk(body, "FVER", new byte[] { 0xA2, 0x80, 0x51, 0x40 });

        var comm = new MemoryStream();
        WriteBe16(comm, channels);
        WriteBe32(comm, (uint)frames);
        WriteBe16(comm, bits);
        comm.Write(Extended(sampleRate));
        if (aifc)
        {
            comm.Write(Encoding.ASCII.GetBytes("sowt"));
            comm.Write(new byte[] { 0, 0 }); // empty Pascal-string compression name, padded
        }
        WriteChunk(body, "COMM", comm.ToArray());

        var ssnd = new byte[8 + pcm.Length]; // offset + block size, then samples
        Buffer.BlockCopy(pcm, 0, ssnd, 8, pcm.Length);
        WriteChunk(body, "SSND", ssnd);
        if (id3 != null) WriteChunk(body, "ID3 ", id3);

        using var file = new FileStream(path, FileMode.Create);
        file.Write(Encoding.ASCII.GetBytes("FORM"));
        WriteBe32(file, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(file);
    }

    private static void WriteChunk(Stream s, string id, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(id));
        WriteBe32(s, (uint)data.Length);
        s.Write(data);
        if ((data.Length & 1) == 1) s.WriteByte(0);
    }

    private static void WriteBe16(Stream s, int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); s.Write(b); }
    private static void WriteBe32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }

    /// <summary>80-bit IEEE extended, big-endian (AIFF's sample-rate encoding) for a positive integer rate.</summary>
    private static byte[] Extended(int rate)
    {
        ulong mantissa = (ulong)rate;
        int exponent = 16383 + 63;
        while ((mantissa & 0x8000_0000_0000_0000UL) == 0) { mantissa <<= 1; exponent--; }
        var b = new byte[10];
        b[0] = (byte)(exponent >> 8);
        b[1] = (byte)exponent;
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(2), mantissa);
        return b;
    }
}
