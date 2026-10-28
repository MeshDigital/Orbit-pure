using System;
using System.Buffers.Binary;
using System.IO;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Minimal AIFF / AIFF-C header reader (FORM → COMM, SSND, "ID3 " chunks). TagLib# 2.3 only
/// accepts the AIFF form type, so AIFF-C files (Logic/GarageBand exports, little-endian "sowt")
/// were reported corrupt by the importer; their sample rate, length and embedded ID3 tag are read
/// here instead. It is also used to tell whether NAudio's AiffFileReader can decode a file
/// (uncompressed AIFF only) or it must take the ffmpeg-decoded path.
/// </summary>
public sealed record AiffHeader(
    bool IsAifc,
    string CompressionType,
    int Channels,
    long SampleFrames,
    int BitsPerSample,
    double SampleRate,
    long AudioDataLength,
    byte[]? Id3Tag)
{
    /// <summary>Plain uncompressed big-endian AIFF, the only kind NAudio decodes itself.</summary>
    public bool IsPlainAiff => !IsAifc;

    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(SampleFrames / SampleRate) : TimeSpan.Zero;

    /// <summary>kbps of the PCM stream (rate × bits × channels), 0 when unknown.</summary>
    public int BitrateKbps => SampleRate > 0 && BitsPerSample > 0 && Channels > 0
        ? (int)Math.Round(SampleRate * BitsPerSample * Channels / 1000.0)
        : 0;

    /// <summary>Reads the header of <paramref name="path"/>. False for missing files, I/O errors and non-AIFF data.</summary>
    public static bool TryRead(string path, out AiffHeader header)
    {
        header = null!;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryRead(stream, out header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Serilog.Log.Debug(ex, "[AiffHeader] Can't read {File}", path);
            return false;
        }
    }

    public static bool TryRead(Stream stream, out AiffHeader header)
    {
        header = null!;
        Span<byte> head = stackalloc byte[12];
        if (stream.Read(head) != 12) return false;
        if (head[0] != 'F' || head[1] != 'O' || head[2] != 'R' || head[3] != 'M') return false;
        var form = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4));
        bool aifc = form == "AIFC";
        if (!aifc && form != "AIFF") return false;

        int channels = 0, bits = 0;
        long frames = 0, dataLength = 0;
        double rate = 0;
        string compression = aifc ? "NONE" : "";
        byte[]? id3 = null;
        bool sawComm = false;

        Span<byte> chunkHead = stackalloc byte[8];
        while (stream.Read(chunkHead) == 8)
        {
            var id = System.Text.Encoding.ASCII.GetString(chunkHead[..4]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(chunkHead.Slice(4, 4));
            long next = stream.Position + size + (size & 1); // chunks are padded to an even length

            if (id == "COMM" && size >= 18)
            {
                var comm = new byte[Math.Min(size, 256)];
                if (stream.Read(comm, 0, comm.Length) < 18) return false;
                channels = BinaryPrimitives.ReadInt16BigEndian(comm.AsSpan(0, 2));
                frames = BinaryPrimitives.ReadUInt32BigEndian(comm.AsSpan(2, 4));
                bits = BinaryPrimitives.ReadInt16BigEndian(comm.AsSpan(6, 2));
                rate = ReadExtended(comm.AsSpan(8, 10));
                if (aifc && comm.Length >= 22) compression = System.Text.Encoding.ASCII.GetString(comm, 18, 4);
                sawComm = true;
            }
            else if (id == "SSND")
            {
                dataLength = Math.Max(0, size - 8);
            }
            else if ((id == "ID3 " || id == "id3 ") && size is > 10 and < 64 * 1024 * 1024)
            {
                id3 = new byte[size];
                if (stream.Read(id3, 0, id3.Length) != id3.Length) id3 = null;
            }

            if (next > stream.Length) break;
            stream.Position = next;
        }

        if (!sawComm) return false;
        header = new AiffHeader(aifc, compression.Trim(), channels, frames, bits, rate, dataLength, id3);
        return true;
    }

    /// <summary>80-bit IEEE 754 extended (big-endian), as AIFF stores the sample rate.</summary>
    private static double ReadExtended(ReadOnlySpan<byte> b)
    {
        int exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(2, 8));
        if (exponent == 0 && mantissa == 0) return 0;
        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (b[0] & 0x80) != 0 ? -value : value;
    }
}
