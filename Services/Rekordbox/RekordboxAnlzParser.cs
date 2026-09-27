using System;
using System.Collections.Generic;
using System.Text;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// One raw phrase entry from a Rekordbox ANLZ "PSSI" (song structure) tag, before conversion
/// to seconds. <see cref="Beat"/> is a 1-indexed beat number, not a timestamp — Rekordbox stores
/// phrase boundaries relative to the beat grid, so converting to seconds requires the track's BPM
/// and downbeat anchor (see <see cref="RekordboxPssiService"/>).
/// </summary>
public readonly record struct RekordboxPhraseEntry(int Index, int Beat, int Kind);

/// <summary>
/// One memory or hot cue point as actually saved by a human (or Rekordbox's own auto-cue) in
/// Rekordbox, from a PCOB/PCO2 tag — this is the real, user-facing cue data, distinct from the
/// PSSI phrase-structure analysis <see cref="RekordboxPhraseEntry"/> represents. Comment/ColorId
/// are only ever populated from PCO2 (the nxs2-era extended tag); PCOB-only files leave them null.
/// </summary>
public readonly record struct RekordboxCuePoint(
    bool IsHotCue,
    int HotCueNumber,
    bool IsLoop,
    double TimeSeconds,
    double? LoopEndSeconds,
    string? Comment,
    int? ColorId);

/// <summary>
/// Result of parsing a single Rekordbox ANLZ (.DAT/.EXT/.2EX) file: whichever of the PPTH (source
/// audio path), PSSI (song structure), and PCOB/PCO2 (saved cue points) tags were present. Any may
/// be empty/null — most ANLZ files are .DAT (waveforms/beatgrid, no PSSI) or lack PSSI when
/// Rekordbox hasn't performed phrase analysis on that track, and most tracks have no manually
/// saved cues at all (confirmed against a real ~1,250-track library: 0 had any).
/// </summary>
public sealed class RekordboxAnlzResult
{
    public string? SourcePath { get; init; }
    public int Mood { get; init; }
    public IReadOnlyList<RekordboxPhraseEntry> Phrases { get; init; } = Array.Empty<RekordboxPhraseEntry>();
    public IReadOnlyList<RekordboxCuePoint> CuePoints { get; init; } = Array.Empty<RekordboxCuePoint>();
}

/// <summary>
/// Binary reader for Rekordbox's ANLZ analysis file format (.DAT/.EXT/.2EX) — specifically the
/// PPTH (source file path), PSSI (song structure / phrase), and PCOB/PCO2 (saved memory/hot cue
/// points) tags.
///
/// Format reverse-engineered by the pyrekordbox (github.com/dylanljones/pyrekordbox) and
/// Deep Symmetry crate-digger (github.com/Deep-Symmetry/crate-digger) projects; this is an
/// independent C# port of their documented struct layout, cross-checked against both sources.
/// Not affiliated with, endorsed by, or tested against Pioneer/AlphaTheta's own tooling.
///
/// File envelope (all multi-byte fields big-endian):
///   Header:  "PMAI" (4) + len_header (u32) + len_file (u32), then tags start at len_header.
///   Tag:     fourcc (4) + len_header (u32) + len_tag (u32), tag-specific payload follows,
///            next tag starts at (tag start + len_tag).
///
/// PSSI payload (tag len_header = 32): len_entry_bytes (u32, always 24) + len_entries (u16),
/// then a "body" of mood (u16) + reserved (6) + end_beat (u16) + reserved (2) + bank (u8) +
/// reserved (1), followed by len_entries × 24-byte phrase entries.
///
/// The body is XOR-masked ONLY when its raw (pre-unmask) mood field reads &gt; 20 — confirmed
/// against ~60 real Rekordbox 6 analysis files, all of which store the body as plain big-endian
/// integers (mood always 1 or 2, first phrase always index=1/beat=1/kind=1 "Intro") with no
/// masking applied at all. The crate-digger Kaitai spec's own `is_masked: raw_mood > 20` guard
/// confirms this is by design, not a fluke of this sample: unmasking unconditionally (the
/// previous behaviour here, before real files were available to test against) silently turns
/// valid plaintext into garbage, since XOR-ing a small valid mood value with the mask still
/// produces *a* number — just not the right one — with no way to detect the corruption
/// downstream. When masked, the per-byte key is (MaskBase[i % 19] + len_entries) &amp; 0xFF,
/// cycling from the first byte of the body (the mood field) — this branch is implemented per
/// the documented spec but, unlike the plaintext path, has not been observed in a real file in
/// this environment (a Rekordbox version/workflow that actually masks phrase data may exist
/// elsewhere).
///
/// PPTH payload: fourcc+len_header+len_tag (12 bytes) + len_path (u32, at tagStart+12) + UTF-16BE
/// path string starting at tagStart+len_header, running for (len_path - 2) bytes — len_path
/// counts a trailing 2-byte null terminator that isn't part of the string content.
///
/// PCOB payload (tag len_header = 24, confirmed against real files): type (u32; 0=memory_cues,
/// 1=hot_cues) + reserved (2) + num_cues (u16) + memory_count (u32), then num_cues self-describing
/// cue_entry records starting at tagStart+24 (each has its own magic "PCPT" + len_header + len_tag
/// header, so entries are walked by their own length rather than a hardcoded stride). A file
/// carries two PCOB tags — one type=0, one type=1 — even when both are empty (num_cues=0), which
/// is the case for every track sampled from a real ~1,250-track library; the entry field layout
/// (hot_cue/type/time/loop_time) is implemented per the documented spec but has only been verified
/// against real EMPTY entries, not a populated one.
///
/// PCO2 payload (the nxs2-era extended tag; tag len_header = 20 in the confirmed-empty case): type
/// (u32) + num_cues (u16, at tagStart+16 — NOT the same relative offset as PCOB's num_cues, despite
/// the superficially similar header) + reserved (2), then num_cues self-describing cue_extended_entry
/// records (magic "PCP2") starting at tagStart+tagHeaderLen, each optionally carrying a UTF-16BE
/// comment and RGB colour beyond the fixed 40-byte prefix, gated by the entry's own len_entry per
/// the spec. Preferred over PCOB wholesale when present, since it's a strict superset.
/// </summary>
public static class RekordboxAnlzParser
{
    private const int PssiEntrySize = 24;

    // XOR mask base pattern (19 bytes) — actual per-byte mask value is (base[i] + numEntries) & 0xFF,
    // repeated cyclically across the masked region. Source: Deep Symmetry crate-digger's
    // rekordbox_anlz.ksy Kaitai spec for the PSSI tag.
    private static readonly byte[] MaskBase =
    {
        0xCB, 0xE1, 0xEE, 0xFA, 0xE5, 0xEE, 0xAD, 0xEE, 0xE9, 0xD2,
        0xE9, 0xEB, 0xE1, 0xE9, 0xF3, 0xE8, 0xE9, 0xF4, 0xE1,
    };

    /// <summary>
    /// Parses an ANLZ file's bytes for its PPTH and/or PSSI tags. Returns null if the file isn't
    /// a recognizable ANLZ file (wrong magic) or contains neither tag. Never throws — any
    /// unexpected structure (truncated file, unknown tag layout, future format revision) is
    /// treated as "nothing usable here" rather than propagated, since this is an optional,
    /// best-effort enrichment source that must never break cue generation if Rekordbox's format
    /// changes or a file is malformed.
    /// </summary>
    public static RekordboxAnlzResult? TryParse(byte[] data)
    {
        try
        {
            return ParseInternal(data);
        }
        catch
        {
            return null;
        }
    }

    private static RekordboxAnlzResult? ParseInternal(byte[] data)
    {
        if (data.Length < 12 || data[0] != 'P' || data[1] != 'M' || data[2] != 'A' || data[3] != 'I')
            return null;

        uint fileHeaderLen = ReadU32BE(data, 4);
        if (fileHeaderLen < 12 || fileHeaderLen > data.Length) return null;

        string? sourcePath = null;
        int mood = 0;
        List<RekordboxPhraseEntry>? phrases = null;
        List<RekordboxCuePoint>? pcobCues = null;
        List<RekordboxCuePoint>? pco2Cues = null;

        long offset = fileHeaderLen;
        while (offset + 12 <= data.Length)
        {
            string fourcc = Encoding.ASCII.GetString(data, (int)offset, 4);
            uint tagHeaderLen = ReadU32BE(data, offset + 4);
            uint tagLen = ReadU32BE(data, offset + 8);

            // Sanity bounds — a corrupt/unrecognized length must not send us out of range or into
            // an infinite loop (tagLen == 0 would never advance).
            if (tagLen < 12 || offset + tagLen > data.Length) break;

            if (fourcc == "PPTH" && tagHeaderLen >= 12 && offset + 16 <= data.Length)
            {
                sourcePath = TryReadPpth(data, offset, tagHeaderLen, tagLen);
            }
            else if (fourcc == "PSSI" && tagHeaderLen == 32 && offset + 32 <= data.Length)
            {
                (mood, phrases) = TryReadPssi(data, offset, tagLen);
            }
            else if (fourcc == "PCOB" && tagHeaderLen == 24 && offset + 24 <= data.Length)
            {
                (pcobCues ??= new List<RekordboxCuePoint>()).AddRange(TryReadCueTag(data, offset, tagHeaderLen, tagLen));
            }
            else if (fourcc == "PCO2" && tagHeaderLen >= 18 && offset + tagHeaderLen <= data.Length)
            {
                (pco2Cues ??= new List<RekordboxCuePoint>()).AddRange(TryReadExtendedCueTag(data, offset, tagHeaderLen, tagLen));
            }

            offset += tagLen;
        }

        if (sourcePath == null && phrases == null && pcobCues == null && pco2Cues == null) return null;

        // PCO2 (nxs2-era) carries everything PCOB does plus comment/color, so prefer it wholesale
        // when present rather than merging — a file either has the extended tags or it doesn't.
        var cuePoints = (IReadOnlyList<RekordboxCuePoint>?)pco2Cues ?? (IReadOnlyList<RekordboxCuePoint>?)pcobCues
            ?? Array.Empty<RekordboxCuePoint>();

        return new RekordboxAnlzResult
        {
            SourcePath = sourcePath,
            Mood = mood,
            Phrases = (IReadOnlyList<RekordboxPhraseEntry>?)phrases ?? Array.Empty<RekordboxPhraseEntry>(),
            CuePoints = cuePoints,
        };
    }

    private static string? TryReadPpth(byte[] data, long tagStart, uint tagHeaderLen, uint tagLen)
    {
        // len_path sits at a fixed offset right after the common 12-byte tag header (fourcc +
        // tagHeaderLen + tagLen) — it's part of what tagHeaderLen itself counts (confirmed against
        // real files: tagHeaderLen=16 = 12 common + 4 for len_path), NOT a field found at
        // tagStart+tagHeaderLen. The actual string payload starts there instead.
        long lenPathPos = tagStart + 12;
        if (lenPathPos + 4 > data.Length) return null;

        uint lenPath = ReadU32BE(data, lenPathPos);
        long strStart = tagStart + tagHeaderLen;
        // lenPath includes a 2-byte null terminator not part of the actual path text.
        long strLen = lenPath - 2;
        if (strLen <= 0 || strStart + strLen > data.Length || strStart + strLen > tagStart + tagLen)
            return null;

        try
        {
            return Encoding.BigEndianUnicode.GetString(data, (int)strStart, (int)strLen);
        }
        catch
        {
            return null;
        }
    }

    private static (int Mood, List<RekordboxPhraseEntry>) TryReadPssi(byte[] data, long tagStart, uint tagLen)
    {
        // Fields relative to tagStart: len_entry_bytes(4) len_entries(2) | mood(2) u1(6) end_beat(2) u2(2) bank(1) u3(1)
        uint entryBytes = ReadU32BE(data, tagStart + 12);
        int numEntries = ReadU16BE(data, tagStart + 16);
        if (entryBytes != PssiEntrySize || numEntries < 0 || numEntries > 512)
            return (0, new List<RekordboxPhraseEntry>());

        long bodyStart = tagStart + 18; // right after len_entry_bytes + len_entries
        long entriesEnd = tagStart + 32 + (long)numEntries * PssiEntrySize;
        long bodyLen = entriesEnd - bodyStart;
        if (bodyLen <= 0 || entriesEnd > data.Length || entriesEnd > tagStart + tagLen)
            return (0, new List<RekordboxPhraseEntry>());

        // The body is masked only when its raw (pre-unmask) mood value reads implausibly large —
        // confirmed against real files, see the class doc comment. Peeking this way, rather than
        // unmasking unconditionally, is the difference between reading real phrase data and
        // silently turning it into noise.
        int rawMood = (data[bodyStart] << 8) | data[bodyStart + 1];
        byte[] body;
        if (rawMood > 20)
        {
            body = new byte[bodyLen];
            for (int i = 0; i < bodyLen; i++)
            {
                byte maskByte = (byte)(MaskBase[i % MaskBase.Length] + numEntries);
                body[i] = (byte)(data[bodyStart + i] ^ maskByte);
            }
        }
        else
        {
            body = new byte[bodyLen];
            Array.Copy(data, bodyStart, body, 0, bodyLen);
        }
        var unmasked = body;

        // unmasked[0..1] = mood, [2..7] = reserved, [8..9] = end_beat, [10..11] = reserved,
        // [12] = bank, [13] = reserved, [14..] = entries.
        int mood = (unmasked[0] << 8) | unmasked[1];

        var entries = new List<RekordboxPhraseEntry>(numEntries);
        int entriesStart = 14;
        for (int i = 0; i < numEntries; i++)
        {
            int e = entriesStart + i * PssiEntrySize;
            if (e + PssiEntrySize > unmasked.Length) break;

            int index = (unmasked[e] << 8) | unmasked[e + 1];
            int beat = (unmasked[e + 2] << 8) | unmasked[e + 3];
            int kind = (unmasked[e + 4] << 8) | unmasked[e + 5];
            entries.Add(new RekordboxPhraseEntry(index, beat, kind));
        }

        return (mood, entries);
    }

    /// <summary>
    /// PCOB ("cue_tag"): type(u4) + pad(2) + num_cues(u2) + memory_count(u4) — all within the
    /// 24-byte tagHeaderLen, mirroring PSSI's pattern of sub-header fields living inside
    /// tagHeaderLen rather than at tagStart+tagHeaderLen. Confirmed against real (empty) PCOB tags:
    /// type read as 0 (memory_cues) / 1 (hot_cues) exactly matching which of the file's two PCOB
    /// tags it was. Entries start at tagStart+24 and are walked using each entry's own len_entry
    /// (self-describing, like the top-level tag loop) rather than a hardcoded stride, since no real
    /// non-empty example was available in this environment to confirm the fixed 56-byte size the
    /// spec implies.
    /// </summary>
    private static List<RekordboxCuePoint> TryReadCueTag(byte[] data, long tagStart, uint tagHeaderLen, uint tagLen)
    {
        var result = new List<RekordboxCuePoint>();
        int numCues = ReadU16BE(data, tagStart + 18);
        if (numCues <= 0 || numCues > 999) return result;

        long entryOffset = tagStart + tagHeaderLen;
        long tagEnd = tagStart + tagLen;
        for (int i = 0; i < numCues && entryOffset + 12 <= tagEnd; i++)
        {
            uint entryLen = ReadU32BE(data, entryOffset + 8);
            if (entryLen < 40 || entryOffset + entryLen > data.Length || entryOffset + entryLen > tagEnd) break;

            uint hotCue = ReadU32BE(data, entryOffset + 12);
            byte type = data[entryOffset + 28]; // cue_entry_type: 1=memory_cue, 2=loop
            uint timeMs = ReadU32BE(data, entryOffset + 32);
            uint loopTimeMs = ReadU32BE(data, entryOffset + 36);
            bool isLoop = type == 2;

            result.Add(new RekordboxCuePoint(
                IsHotCue: hotCue != 0,
                HotCueNumber: (int)hotCue,
                IsLoop: isLoop,
                TimeSeconds: timeMs / 1000.0,
                LoopEndSeconds: isLoop ? loopTimeMs / 1000.0 : null,
                Comment: null,
                ColorId: null));

            entryOffset += entryLen;
        }
        return result;
    }

    /// <summary>
    /// PCO2 ("cue_extended_tag") — the nxs2-era tag adding comment text and colour. Fixed fields
    /// (through loop_denominator) always present; comment and colour are only present when the
    /// entry's own len_entry indicates enough trailing bytes, per the spec's length-gated optional
    /// fields. Like <see cref="TryReadCueTag"/>, entries are walked via each entry's own len_entry
    /// rather than a hardcoded stride, and this has only been confirmed against real EMPTY PCO2
    /// tags (num_cues=0) in this environment — the comment/colour field offsets are implemented
    /// per the documented spec but not yet round-tripped against a real populated entry.
    /// </summary>
    private static List<RekordboxCuePoint> TryReadExtendedCueTag(byte[] data, long tagStart, uint tagHeaderLen, uint tagLen)
    {
        var result = new List<RekordboxCuePoint>();
        if (tagStart + tagHeaderLen > data.Length) return result;
        // type(u4) @+12, num_cues(u2) @+16 — confirmed against a real (empty) PCO2 tag.
        int numCues = ReadU16BE(data, tagStart + 16);
        if (numCues <= 0 || numCues > 999) return result;

        long entryOffset = tagStart + tagHeaderLen;
        long tagEnd = tagStart + tagLen;
        for (int i = 0; i < numCues && entryOffset + 12 <= tagEnd; i++)
        {
            uint entryLen = ReadU32BE(data, entryOffset + 8);
            if (entryLen < 40 || entryOffset + entryLen > data.Length || entryOffset + entryLen > tagEnd) break;

            uint hotCue = ReadU32BE(data, entryOffset + 12);
            byte type = data[entryOffset + 16]; // cue_entry_type: 1=memory_cue, 2=loop
            uint timeMs = ReadU32BE(data, entryOffset + 20);
            uint loopTimeMs = ReadU32BE(data, entryOffset + 24);
            byte colorId = data[entryOffset + 28];
            bool isLoop = type == 2;

            string? comment = null;
            if (entryLen > 43)
            {
                uint lenComment = ReadU32BE(data, entryOffset + 40);
                long commentStart = entryOffset + 44;
                long commentLen = lenComment; // includes any trailing null(s); read as-is, trim below
                if (lenComment > 0 && commentStart + commentLen <= entryOffset + entryLen && commentStart + commentLen <= data.Length)
                {
                    try
                    {
                        comment = Encoding.BigEndianUnicode.GetString(data, (int)commentStart, (int)commentLen).TrimEnd('\0');
                    }
                    catch { /* leave comment null on malformed text */ }
                }
            }

            result.Add(new RekordboxCuePoint(
                IsHotCue: hotCue != 0,
                HotCueNumber: (int)hotCue,
                IsLoop: isLoop,
                TimeSeconds: timeMs / 1000.0,
                LoopEndSeconds: isLoop ? loopTimeMs / 1000.0 : null,
                Comment: string.IsNullOrEmpty(comment) ? null : comment,
                ColorId: colorId));

            entryOffset += entryLen;
        }
        return result;
    }

    private static uint ReadU32BE(byte[] data, long offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static int ReadU16BE(byte[] data, long offset) =>
        (data[offset] << 8) | data[offset + 1];
}
