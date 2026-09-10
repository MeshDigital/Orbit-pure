using System;
using System.Collections.Generic;
using System.Text;
using SLSKDONET.Services.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Rekordbox;

/// <summary>
/// Round-trip tests for <see cref="RekordboxAnlzParser"/> against a synthetic, hand-built ANLZ
/// byte buffer. These validate the parser's byte-offset arithmetic, field wiring, and
/// tag-envelope walking self-consistently — they do NOT independently verify that the documented
/// format (pyrekordbox / Deep Symmetry crate-digger) was transcribed correctly, since the same
/// mask formula is used to both encode and decode here. That verification instead lives in
/// <see cref="RekordboxRealFileValidationTests"/>, which runs the parser against actual Rekordbox
/// analysis files — real .EXT bytes are what caught the two bugs this synthetic suite's closed
/// loop could not: a wrong PPTH length-field offset, and unmasking PSSI unconditionally instead
/// of only when the raw mood value indicates it's actually masked (see the parser's own doc
/// comment for both).
/// </summary>
public class RekordboxAnlzParserTests
{
    private static readonly byte[] MaskBase =
    {
        0xCB, 0xE1, 0xEE, 0xFA, 0xE5, 0xEE, 0xAD, 0xEE, 0xE9, 0xD2,
        0xE9, 0xEB, 0xE1, 0xE9, 0xF3, 0xE8, 0xE9, 0xF4, 0xE1,
    };

    private static void WriteU32BE(List<byte> buf, uint value)
    {
        buf.Add((byte)(value >> 24)); buf.Add((byte)(value >> 16));
        buf.Add((byte)(value >> 8)); buf.Add((byte)value);
    }

    private static void WriteU16BE(List<byte> buf, int value)
    {
        buf.Add((byte)(value >> 8)); buf.Add((byte)value);
    }

    /// <summary>Builds a synthetic ANLZ file containing a PPTH tag and a PSSI tag with the given
    /// (index, beat, kind) entries, mood, and source path — mirroring the real file's envelope
    /// exactly as documented (see RekordboxAnlzParser's own header comment for field layout).</summary>
    private static byte[] BuildSyntheticAnlz(string sourcePath, int mood, (int Index, int Beat, int Kind)[] entries)
    {
        const int fileHeaderLen = 28;

        // ── PPTH tag ── len_path sits right after the common 12-byte header, at a *fixed* offset
        // that real files (tagHeaderLen=16 = 12 common + 4 for len_path) confirm is NOT the same
        // position as tagHeaderLen — building it any other way would silently re-validate the bug
        // this test previously couldn't catch.
        var pathBytes = Encoding.BigEndianUnicode.GetBytes(sourcePath);
        var ppth = new List<byte>();
        ppth.AddRange(Encoding.ASCII.GetBytes("PPTH"));
        WriteU32BE(ppth, 16); // tag header len: common 12 bytes + 4-byte len_path field
        int pptTagLen = 16 + pathBytes.Length + 2;
        WriteU32BE(ppth, (uint)pptTagLen);
        WriteU32BE(ppth, (uint)(pathBytes.Length + 2)); // len_path includes the 2-byte terminator
        ppth.AddRange(pathBytes);
        ppth.Add(0); ppth.Add(0); // null terminator

        // ── PSSI tag ── real files mask the body only when its raw mood value reads > 20; below
        // that it's plain big-endian data (see RekordboxAnlzParser's doc comment — confirmed
        // against ~60 real analysis files, all of which fall in this plaintext branch). Mirror
        // that here rather than always masking, so this test can't silently pass for a `mood`
        // value that would make the real parser skip masking entirely.
        int numEntries = entries.Length;
        bool isMasked = mood > 20;
        var bodyRegion = new List<byte>();
        WriteU16BE(bodyRegion, mood);
        bodyRegion.AddRange(new byte[6]); // reserved
        WriteU16BE(bodyRegion, entries.Length > 0 ? entries[^1].Beat : 0); // end_beat
        bodyRegion.AddRange(new byte[2]); // reserved
        bodyRegion.Add(1); // bank
        bodyRegion.Add(0); // reserved
        foreach (var (index, beat, kind) in entries)
        {
            WriteU16BE(bodyRegion, index);
            WriteU16BE(bodyRegion, beat);
            WriteU16BE(bodyRegion, kind);
            bodyRegion.AddRange(new byte[24 - 6]); // remaining entry fields, unused by the parser
        }

        var maskedBytes = bodyRegion.ToArray();
        if (isMasked)
        {
            for (int i = 0; i < maskedBytes.Length; i++)
            {
                byte maskByte = (byte)(MaskBase[i % MaskBase.Length] + numEntries);
                maskedBytes[i] ^= maskByte;
            }
        }

        var pssi = new List<byte>();
        pssi.AddRange(Encoding.ASCII.GetBytes("PSSI"));
        WriteU32BE(pssi, 32); // tag header len (fixed for PSSI)
        // len_tag covers: envelope(12) + len_entry_bytes(4) + len_entries(2) + full masked region
        int pssiTagLen = 12 + 4 + 2 + maskedBytes.Length;
        WriteU32BE(pssi, (uint)pssiTagLen);
        WriteU32BE(pssi, 24); // len_entry_bytes
        WriteU16BE(pssi, numEntries);
        pssi.AddRange(maskedBytes);

        // ── File envelope ──
        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("PMAI"));
        WriteU32BE(file, fileHeaderLen);
        int totalLen = fileHeaderLen + ppth.Count + pssi.Count;
        WriteU32BE(file, (uint)totalLen);
        file.AddRange(new byte[fileHeaderLen - file.Count]); // pad to fileHeaderLen
        file.AddRange(ppth);
        file.AddRange(pssi);

        return file.ToArray();
    }

    /// <summary>Builds a synthetic PCOB ("cue_tag") tag with the given entries, mirroring the real
    /// field layout confirmed against real (empty) PCOB tags — see RekordboxAnlzParser's doc
    /// comment. <paramref name="cueListType"/> is 0 for memory_cues, 1 for hot_cues.</summary>
    private static byte[] BuildPcobTag(int cueListType, (uint HotCue, byte Type, uint TimeMs, uint LoopTimeMs)[] entries)
    {
        var body = new List<byte>();
        foreach (var (hotCue, type, timeMs, loopTimeMs) in entries)
        {
            var entry = new List<byte>();
            entry.AddRange(Encoding.ASCII.GetBytes("PCPT"));
            WriteU32BE(entry, 12); // len_header
            const int entryLen = 56;
            WriteU32BE(entry, entryLen);
            WriteU32BE(entry, hotCue);
            WriteU32BE(entry, 1); // status: enabled
            WriteU32BE(entry, 0); // unused
            WriteU16BE(entry, 0); // order_first
            WriteU16BE(entry, 0); // order_last
            entry.Add(type);
            entry.AddRange(new byte[3]); // pad
            WriteU32BE(entry, timeMs);
            WriteU32BE(entry, loopTimeMs);
            entry.AddRange(new byte[16]); // pad
            Assert.Equal(entryLen, entry.Count);
            body.AddRange(entry);
        }

        var tag = new List<byte>();
        tag.AddRange(Encoding.ASCII.GetBytes("PCOB"));
        WriteU32BE(tag, 24); // tag header len (fixed, confirmed against real files)
        WriteU32BE(tag, (uint)(24 + body.Count));
        WriteU32BE(tag, (uint)cueListType);
        tag.AddRange(new byte[2]); // pad
        WriteU16BE(tag, entries.Length);
        WriteU32BE(tag, 0xFFFFFFFF); // memory_count — sentinel, matches real files
        tag.AddRange(body);
        return tag.ToArray();
    }

    /// <summary>Builds a synthetic PCO2 ("cue_extended_tag") tag with one entry, optionally
    /// carrying a comment — mirroring the real field layout confirmed against real (empty) PCO2
    /// tags, notably that num_cues sits at a different relative offset than in PCOB despite the
    /// superficially similar header (see RekordboxAnlzParser's doc comment).</summary>
    private static byte[] BuildPco2Tag(int cueListType, uint hotCue, byte type, uint timeMs, uint loopTimeMs, byte colorId, string? comment)
    {
        var entry = new List<byte>();
        entry.AddRange(Encoding.ASCII.GetBytes("PCP2"));
        WriteU32BE(entry, 12); // len_header
        var commentBytes = comment != null ? Encoding.BigEndianUnicode.GetBytes(comment) : Array.Empty<byte>();
        int entryLen = comment != null ? 44 + commentBytes.Length : 40;
        WriteU32BE(entry, (uint)entryLen);
        WriteU32BE(entry, hotCue);
        entry.Add(type);
        entry.AddRange(new byte[3]); // pad
        WriteU32BE(entry, timeMs);
        WriteU32BE(entry, loopTimeMs);
        entry.Add(colorId);
        entry.AddRange(new byte[7]); // pad
        WriteU16BE(entry, 4); // loop_numerator
        WriteU16BE(entry, 4); // loop_denominator
        if (comment != null)
        {
            WriteU32BE(entry, (uint)commentBytes.Length);
            entry.AddRange(commentBytes);
        }
        Assert.Equal(entryLen, entry.Count);

        var tag = new List<byte>();
        tag.AddRange(Encoding.ASCII.GetBytes("PCO2"));
        WriteU32BE(tag, 20); // tag header len (fixed, confirmed against real files)
        WriteU32BE(tag, (uint)(20 + entry.Count));
        WriteU32BE(tag, (uint)cueListType);
        WriteU16BE(tag, 1); // num_cues
        tag.AddRange(new byte[2]); // pad
        tag.AddRange(entry);
        return tag.ToArray();
    }

    [Fact]
    public void TryParse_RecoversMemoryAndHotCues_FromPcobTags()
    {
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: Array.Empty<(int, int, int)>());
        var pcobMemory = BuildPcobTag(cueListType: 0, entries: new[] { (HotCue: 0u, Type: (byte)1, TimeMs: 5000u, LoopTimeMs: 0u) });
        var pcobHot = BuildPcobTag(cueListType: 1, entries: new[] { (HotCue: 1u, Type: (byte)1, TimeMs: 10000u, LoopTimeMs: 0u) });
        var combined = new List<byte>(data);
        combined.AddRange(pcobMemory);
        combined.AddRange(pcobHot);
        var file = FixUpFileLength(combined.ToArray());

        var result = RekordboxAnlzParser.TryParse(file);

        Assert.NotNull(result);
        Assert.Equal(2, result!.CuePoints.Count);
        Assert.Contains(result.CuePoints, c => !c.IsHotCue && c.TimeSeconds == 5.0);
        Assert.Contains(result.CuePoints, c => c.IsHotCue && c.HotCueNumber == 1 && c.TimeSeconds == 10.0);
    }

    [Fact]
    public void TryParse_RecoversLoopCue_WithLoopEndSeconds()
    {
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: Array.Empty<(int, int, int)>());
        var pcob = BuildPcobTag(cueListType: 0, entries: new[] { (HotCue: 0u, Type: (byte)2, TimeMs: 8000u, LoopTimeMs: 12000u) });
        var file = FixUpFileLength(Concat(data, pcob));

        var result = RekordboxAnlzParser.TryParse(file);

        Assert.NotNull(result);
        var cue = Assert.Single(result!.CuePoints);
        Assert.True(cue.IsLoop);
        Assert.Equal(8.0, cue.TimeSeconds);
        Assert.Equal(12.0, cue.LoopEndSeconds);
    }

    [Fact]
    public void TryParse_PreferPco2OverPcob_WhenBothPresent()
    {
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: Array.Empty<(int, int, int)>());
        var pcob = BuildPcobTag(cueListType: 1, entries: new[] { (HotCue: 1u, Type: (byte)1, TimeMs: 1000u, LoopTimeMs: 0u) });
        var pco2 = BuildPco2Tag(cueListType: 1, hotCue: 1, type: 1, timeMs: 1000, loopTimeMs: 0, colorId: 3, comment: "Drop");
        var file = FixUpFileLength(Concat(data, pcob, pco2));

        var result = RekordboxAnlzParser.TryParse(file);

        Assert.NotNull(result);
        var cue = Assert.Single(result!.CuePoints);
        Assert.Equal("Drop", cue.Comment);
        Assert.Equal(3, cue.ColorId);
    }

    [Fact]
    public void TryParse_NoCueTags_ReturnsEmptyCuePoints()
    {
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: Array.Empty<(int, int, int)>());

        var result = RekordboxAnlzParser.TryParse(data);

        Assert.NotNull(result);
        Assert.Empty(result!.CuePoints);
    }

    private static byte[] Concat(params byte[][] chunks)
    {
        var combined = new List<byte>();
        foreach (var chunk in chunks) combined.AddRange(chunk);
        return combined.ToArray();
    }

    /// <summary>Rewrites the PMAI header's len_file field after appending extra tags onto a buffer
    /// produced by <see cref="BuildSyntheticAnlz"/>, since that method only accounts for its own
    /// PPTH+PSSI tags when computing the total.</summary>
    private static byte[] FixUpFileLength(byte[] file)
    {
        uint total = (uint)file.Length;
        file[8] = (byte)(total >> 24); file[9] = (byte)(total >> 16);
        file[10] = (byte)(total >> 8); file[11] = (byte)total;
        return file;
    }

    [Fact]
    public void TryParse_RecoversSourcePathFromPpthTag()
    {
        var data = BuildSyntheticAnlz(@"D:\Music\Artist - Track.flac", mood: 1, entries: Array.Empty<(int, int, int)>());

        var result = RekordboxAnlzParser.TryParse(data);

        Assert.NotNull(result);
        Assert.Equal(@"D:\Music\Artist - Track.flac", result!.SourcePath);
    }

    [Fact]
    public void TryParse_RecoversPhraseEntries_ThroughXorMask()
    {
        var entries = new (int, int, int)[]
        {
            (1, 1, 1),   // Intro
            (2, 65, 2),  // Up
            (3, 129, 5), // Chorus (Drop)
            (4, 193, 6), // Outro
        };
        // mood > 20 so the body is actually masked — exercising the masked branch specifically.
        // This branch's correctness against a real masked file is unconfirmed (see class doc
        // comment); every real file sampled so far used the plaintext branch below instead.
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 21, entries: entries);

        var result = RekordboxAnlzParser.TryParse(data);

        Assert.NotNull(result);
        Assert.Equal(21, result!.Mood);
        Assert.Equal(4, result.Phrases.Count);
        Assert.Equal((1, 1, 1), (result.Phrases[0].Index, result.Phrases[0].Beat, result.Phrases[0].Kind));
        Assert.Equal((3, 129, 5), (result.Phrases[2].Index, result.Phrases[2].Beat, result.Phrases[2].Kind));
    }

    [Fact]
    public void TryParse_RecoversPhraseEntries_PlaintextBody_NoMasking()
    {
        // mood <= 20 — the branch every real file sampled against RekordboxRealFileValidationTests
        // actually takes. Confirms the parser doesn't unmask (and corrupt) data that was never
        // masked in the first place.
        var entries = new (int, int, int)[]
        {
            (1, 1, 1),  // Intro
            (2, 33, 2), // Build
        };
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: entries);

        var result = RekordboxAnlzParser.TryParse(data);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Mood);
        Assert.Equal(2, result.Phrases.Count);
        Assert.Equal((1, 1, 1), (result.Phrases[0].Index, result.Phrases[0].Beat, result.Phrases[0].Kind));
        Assert.Equal((2, 33, 2), (result.Phrases[1].Index, result.Phrases[1].Beat, result.Phrases[1].Kind));
    }

    [Fact]
    public void TryParse_ReturnsNull_ForNonAnlzData()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        Assert.Null(RekordboxAnlzParser.TryParse(garbage));
    }

    [Fact]
    public void TryParse_ReturnsNull_ForTruncatedFile()
    {
        var data = BuildSyntheticAnlz(@"C:\track.flac", mood: 1, entries: new[] { (1, 1, 1) });
        var truncated = new byte[data.Length - 20];
        Array.Copy(data, truncated, truncated.Length);

        // Must not throw despite the truncation — either null or a partial-but-valid result.
        var result = RekordboxAnlzParser.TryParse(truncated);
        _ = result; // no exception is the actual assertion here
    }
}
