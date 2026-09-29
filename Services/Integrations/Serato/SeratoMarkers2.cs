using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SLSKDONET.Services.Integrations.Serato;

/// <summary>One entry of a Serato Markers2 tag ("CUE", "LOOP", "COLOR", "BPMLOCK", "FLIP", …).
/// Entries ORBIT doesn't edit are carried through byte for byte.</summary>
public sealed record SeratoEntry(string Type, byte[] Data);

/// <summary>A hot cue: slot 0–7, position in milliseconds, RGB colour, name.</summary>
public sealed record SeratoCue(int Index, uint PositionMs, byte R, byte G, byte B, string Name);

/// <summary>A saved loop: slot 0–7, start/end in milliseconds, RGB colour, locked flag, name.</summary>
public sealed record SeratoLoop(int Index, uint StartMs, uint EndMs, byte R, byte G, byte B, bool Locked, string Name);

/// <summary>
/// Reads and writes Serato DJ's "Serato Markers2" tag — hot cues, loops, track colour and BPM
/// lock — as documented by the serato-tags reverse-engineering project (Jan Holthuis) and
/// implemented in Mixxx (src/track/serato/markers2.cpp):
///
/// <code>
/// ID3 (MP3/AIFF/WAV): GEOB frame, description "Serato Markers2", mime application/octet-stream
///     body   = 01 01 | base64(payload), no '=' padding, '\n' after every 72 chars | zero-padded to ≥ 470 bytes
/// Vorbis comment (FLAC/Ogg): SERATO_MARKERS_V2 =
///     base64("application/octet-stream\0" "\0" "Serato Markers2\0" body) wrapped at 72 chars
/// payload = 01 01 | entries | 00
/// entry   = type (ASCII) 00 | length (uint32 BE) | data
/// CUE     = 00 | index | position ms (u32) | 00 | R G B | 00 00 | name (UTF-8) 00
/// LOOP    = 00 | index | start ms (u32) | end ms (u32) | FF FF FF FF | 00 R G B | 00 | locked | name 00
/// </code>
/// Parsing is lenient (padding, line breaks, trailing zeros), writing follows Serato's own layout.
/// </summary>
public static class SeratoMarkers2
{
    public const string Id3Description = "Serato Markers2";
    public const string VorbisField = "SERATO_MARKERS_V2";
    public const string MimeType = "application/octet-stream";
    private const int Id3MinimumLength = 470;

    // ── Payload ─────────────────────────────────────────────────────────────

    public static List<SeratoEntry> ParsePayload(ReadOnlySpan<byte> payload)
    {
        var entries = new List<SeratoEntry>();
        if (payload.Length < 2 || payload[0] != 1 || payload[1] != 1) return entries;
        int pos = 2;
        while (pos < payload.Length)
        {
            int nul = payload[pos..].IndexOf((byte)0);
            if (nul <= 0) break; // empty name = terminator
            string type = Encoding.ASCII.GetString(payload.Slice(pos, nul));
            pos += nul + 1;
            if (pos + 4 > payload.Length) break;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[pos..]);
            pos += 4;
            if (length > payload.Length - pos) break;
            entries.Add(new SeratoEntry(type, payload.Slice(pos, (int)length).ToArray()));
            pos += (int)length;
        }
        return entries;
    }

    public static byte[] BuildPayload(IEnumerable<SeratoEntry> entries)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(1); ms.WriteByte(1);
        Span<byte> len = stackalloc byte[4];
        foreach (var e in entries)
        {
            ms.Write(Encoding.ASCII.GetBytes(e.Type));
            ms.WriteByte(0);
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)e.Data.Length);
            ms.Write(len);
            ms.Write(e.Data);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    // ── ID3 GEOB body ───────────────────────────────────────────────────────

    public static List<SeratoEntry> ParseId3Body(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2 || body[0] != 1 || body[1] != 1) return new List<SeratoEntry>();
        return ParsePayload(DecodeSeratoBase64(Encoding.ASCII.GetString(body[2..])));
    }

    public static byte[] BuildId3Body(IEnumerable<SeratoEntry> entries)
    {
        var text = EncodeSeratoBase64(BuildPayload(entries));
        var body = new byte[Math.Max(Id3MinimumLength, 2 + text.Length)];
        body[0] = 1; body[1] = 1;
        Encoding.ASCII.GetBytes(text, body.AsSpan(2));
        return body;
    }

    // ── Vorbis comment value ────────────────────────────────────────────────

    public static List<SeratoEntry> ParseVorbisValue(string value)
    {
        var envelope = DecodeSeratoBase64(value);
        // mime \0 filename \0 description \0 body
        int pos = 0;
        for (int field = 0; field < 3; field++)
        {
            int nul = Array.IndexOf(envelope, (byte)0, pos);
            if (nul < 0) return new List<SeratoEntry>();
            pos = nul + 1;
        }
        return ParseId3Body(envelope.AsSpan(pos));
    }

    public static string BuildVorbisValue(IEnumerable<SeratoEntry> entries)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(MimeType)); ms.WriteByte(0);
        ms.WriteByte(0); // no filename
        ms.Write(Encoding.ASCII.GetBytes(Id3Description)); ms.WriteByte(0);
        ms.WriteByte(1); ms.WriteByte(1);
        ms.Write(Encoding.ASCII.GetBytes(EncodeSeratoBase64(BuildPayload(entries))));
        return Wrap(Convert.ToBase64String(ms.ToArray()));
    }

    // ── CUE / LOOP entries ──────────────────────────────────────────────────

    public static SeratoCue? ReadCue(SeratoEntry e)
    {
        if (e.Type != "CUE" || e.Data.Length < 13) return null;
        var d = e.Data;
        return new SeratoCue(d[1], BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(2)), d[7], d[8], d[9], ReadName(d, 12));
    }

    public static SeratoEntry WriteCue(SeratoCue cue)
    {
        var name = Encoding.UTF8.GetBytes(cue.Name);
        var d = new byte[12 + name.Length + 1];
        d[1] = (byte)cue.Index;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2), cue.PositionMs);
        d[7] = cue.R; d[8] = cue.G; d[9] = cue.B;
        name.CopyTo(d, 12);
        return new SeratoEntry("CUE", d);
    }

    public static SeratoLoop? ReadLoop(SeratoEntry e)
    {
        if (e.Type != "LOOP" || e.Data.Length < 21) return null;
        var d = e.Data;
        return new SeratoLoop(d[1], BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(2)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(6)),
            d[15], d[16], d[17], d[19] != 0, ReadName(d, 20));
    }

    public static SeratoEntry WriteLoop(SeratoLoop loop)
    {
        var name = Encoding.UTF8.GetBytes(loop.Name);
        var d = new byte[20 + name.Length + 1];
        d[1] = (byte)loop.Index;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2), loop.StartMs);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(6), loop.EndMs);
        d[10] = d[11] = d[12] = d[13] = 0xFF;
        d[15] = loop.R; d[16] = loop.G; d[17] = loop.B;
        d[19] = loop.Locked ? (byte)1 : (byte)0;
        name.CopyTo(d, 20);
        return new SeratoEntry("LOOP", d);
    }

    private static string ReadName(byte[] d, int start)
    {
        int end = Array.IndexOf(d, (byte)0, start);
        return Encoding.UTF8.GetString(d, start, (end < 0 ? d.Length : end) - start);
    }

    // ── Serato's base64 flavour ─────────────────────────────────────────────

    /// <summary>Standard base64 without '=' padding, '\n' after every 72 characters.</summary>
    internal static string EncodeSeratoBase64(byte[] data) => Wrap(Convert.ToBase64String(data).TrimEnd('='));

    /// <summary>Tolerates line breaks, trailing zero bytes, missing padding, and the dangling
    /// single character Serato sometimes leaves (length ≡ 1 mod 4).</summary>
    internal static byte[] DecodeSeratoBase64(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/') clean.Append(c);
        if (clean.Length % 4 == 1) clean.Length--;
        while (clean.Length % 4 != 0) clean.Append('=');
        try { return Convert.FromBase64String(clean.ToString()); }
        catch (FormatException) { return Array.Empty<byte>(); }
    }

    private static string Wrap(string s)
    {
        var sb = new StringBuilder(s.Length + s.Length / 72);
        for (int i = 0; i < s.Length; i += 72)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(s, i, Math.Min(72, s.Length - i));
        }
        return sb.ToString();
    }
}
