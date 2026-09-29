using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.IO;

namespace SLSKDONET.Services.Integrations.Serato;

public enum SeratoWriteMode
{
    /// <summary>Keep every cue/loop already in the file (set in Serato); ORBIT's go in free slots.</summary>
    KeepExisting,
    /// <summary>Replace all cues and loops with ORBIT's. Track colour, BPM lock and flips are kept.</summary>
    Replace,
}

public sealed record SeratoExportResult(bool Success, int CuesWritten, int LoopsWritten, int Skipped, string? Error = null);

/// <summary>
/// Writes a track's ORBIT cues into the audio file as Serato Markers2, so they show up in Serato
/// DJ (and Mixxx, which reads the same tag) without an XML import. MP3/AIFF/WAV get the ID3 GEOB
/// frame, FLAC/Ogg/Opus the SERATO_MARKERS_V2 Vorbis comment. Every other Serato entry (track
/// colour, BPM lock, flips, anything unknown) is preserved byte for byte, and the write goes
/// through SafeWriteService (temp copy → tag → verify → atomic replace).
/// </summary>
public sealed class SeratoCueExportService
{
    public const int Slots = 8;
    private static readonly HashSet<string> Id3Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".aif", ".aiff", ".wav" };
    private static readonly HashSet<string> VorbisExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".ogg", ".opus" };

    private readonly ICuePointService _cues;
    private readonly IFileWriteService _fileWrite;
    private readonly ILogger<SeratoCueExportService> _logger;

    public SeratoCueExportService(ICuePointService cues, IFileWriteService fileWrite, ILogger<SeratoCueExportService> logger)
    {
        _cues = cues;
        _fileWrite = fileWrite;
        _logger = logger;
    }

    public static bool IsSupported(string path) =>
        Id3Extensions.Contains(Path.GetExtension(path)) || VorbisExtensions.Contains(Path.GetExtension(path));

    public async Task<SeratoExportResult> WriteAsync(string filePath, string trackHash, SeratoWriteMode mode, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return new(false, 0, 0, 0, "File not found");
        if (!IsSupported(filePath)) return new(false, 0, 0, 0, $"{Path.GetExtension(filePath)} files can't carry Serato cues here");
        var cues = await _cues.GetByTrackIdAsync(trackHash).ConfigureAwait(false);
        if (cues.Count == 0) return new(false, 0, 0, 0, "No cues to write");

        SeratoExportResult? result = null;
        bool ok = await _fileWrite.WriteAtomicAsync(filePath, async temp =>
        {
            File.Copy(filePath, temp, overwrite: true);
            await Task.Run(() => result = WriteTags(temp, filePath, cues, mode), ct).ConfigureAwait(false);
        }, cancellationToken: ct).ConfigureAwait(false);

        if (!ok || result == null) return new(false, 0, 0, 0, result?.Error ?? "Writing the file failed");
        _logger.LogInformation("[Serato] {File}: {Cues} cues, {Loops} loops written ({Mode}), {Skipped} skipped",
            Path.GetFileName(filePath), result.CuesWritten, result.LoopsWritten, mode, result.Skipped);
        return result;
    }

    /// <summary>Tags <paramref name="tempPath"/> in place (format from <paramref name="originalPath"/>). Internal for tests.</summary>
    internal static SeratoExportResult WriteTags(string tempPath, string originalPath, IReadOnlyList<CuePointEntity> cues, SeratoWriteMode mode)
    {
        bool id3 = Id3Extensions.Contains(Path.GetExtension(originalPath));
        using var file = TagLib.File.Create(new TagLib.File.LocalFileAbstraction(tempPath), MimeFor(originalPath), TagLib.ReadStyle.Average);

        List<SeratoEntry> existing;
        if (id3)
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            var frame = FindGeob(tag);
            existing = frame == null ? new() : SeratoMarkers2.ParseId3Body(frame.Data.Data);
            var (entries, c, l, s) = Merge(existing, cues, mode);
            if (frame == null)
            {
                frame = new TagLib.Id3v2.AttachmentFrame { Type = TagLib.PictureType.NotAPicture, Description = SeratoMarkers2.Id3Description };
                tag.AddFrame(frame);
            }
            frame.MimeType = SeratoMarkers2.MimeType;
            frame.Filename = "";
            frame.Data = new TagLib.ByteVector(SeratoMarkers2.BuildId3Body(entries));
            file.Save();
            return new(true, c, l, s);
        }
        else
        {
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, create: true);
            var value = xiph.GetFirstField(SeratoMarkers2.VorbisField);
            existing = string.IsNullOrEmpty(value) ? new() : SeratoMarkers2.ParseVorbisValue(value);
            var (entries, c, l, s) = Merge(existing, cues, mode);
            xiph.SetField(SeratoMarkers2.VorbisField, SeratoMarkers2.BuildVorbisValue(entries));
            file.Save();
            return new(true, c, l, s);
        }
    }

    internal static TagLib.Id3v2.AttachmentFrame? FindGeob(TagLib.Id3v2.Tag tag) =>
        tag.GetFrames<TagLib.Id3v2.AttachmentFrame>()
           .FirstOrDefault(f => f.Type == TagLib.PictureType.NotAPicture && f.Description == SeratoMarkers2.Id3Description);

    /// <summary>
    /// Combines the file's existing entries with ORBIT's cues. Hot cues keep their ORBIT slot when
    /// it is free, other cues fill the remaining slots in time order; a cue within 50 ms of one
    /// already in the file is not duplicated. Public for tests.
    /// </summary>
    public static (List<SeratoEntry> Entries, int Cues, int Loops, int Skipped) Merge(
        IReadOnlyList<SeratoEntry> existing, IReadOnlyList<CuePointEntity> orbitCues, SeratoWriteMode mode)
    {
        var kept = mode == SeratoWriteMode.Replace
            ? existing.Where(e => e.Type is not ("CUE" or "LOOP")).ToList()
            : existing.ToList();

        var cueSlots = kept.Select(SeratoMarkers2.ReadCue).Where(c => c != null).Select(c => c!.Index).ToHashSet();
        var loopSlots = kept.Select(SeratoMarkers2.ReadLoop).Where(l => l != null).Select(l => l!.Index).ToHashSet();
        var takenTimes = kept.Select(SeratoMarkers2.ReadCue).Where(c => c != null).Select(c => (double)c!.PositionMs)
            .Concat(kept.Select(SeratoMarkers2.ReadLoop).Where(l => l != null).Select(l => (double)l!.StartMs)).ToList();

        int cuesWritten = 0, loopsWritten = 0, skipped = 0;
        var added = new List<SeratoEntry>();
        // Slot owners first (in slot order), then memory cues by time.
        var ordered = orbitCues.Where(c => c.TimestampInSeconds >= 0)
            .OrderBy(c => c.SlotIndex is >= 0 and < Slots ? 0 : 1)
            .ThenBy(c => c.SlotIndex is >= 0 and < Slots ? c.SlotIndex : 0)
            .ThenBy(c => c.TimestampInSeconds);

        foreach (var cue in ordered)
        {
            uint ms = (uint)Math.Round(cue.TimestampInSeconds * 1000);
            if (takenTimes.Any(t => Math.Abs(t - ms) <= 50)) { skipped++; continue; }
            // A loop without a usable end is written as a plain cue.
            bool asLoop = cue.IsLoop && cue.LoopEndSeconds > cue.TimestampInSeconds;
            var slots = asLoop ? loopSlots : cueSlots;
            int slot = cue.SlotIndex is >= 0 and < Slots && !slots.Contains(cue.SlotIndex)
                ? cue.SlotIndex
                : Enumerable.Range(0, Slots).FirstOrDefault(s => !slots.Contains(s), -1);
            if (slot < 0) { skipped++; continue; }

            var (r, g, b) = ParseColour(cue.Color, asLoop);
            string name = string.IsNullOrWhiteSpace(cue.Label) ? "" : cue.Label.Trim();
            if (asLoop)
            {
                added.Add(SeratoMarkers2.WriteLoop(new SeratoLoop(slot, ms, (uint)Math.Round(cue.LoopEndSeconds * 1000), r, g, b, false, name)));
                loopsWritten++;
            }
            else
            {
                added.Add(SeratoMarkers2.WriteCue(new SeratoCue(slot, ms, r, g, b, name)));
                cuesWritten++;
            }
            slots.Add(slot);
            takenTimes.Add(ms);
        }

        // Serato writes COLOR and BPMLOCK first, then cues/loops; keep that order.
        var head = kept.Where(e => e.Type is "COLOR" or "BPMLOCK").ToList();
        var rest = kept.Where(e => e.Type is not ("COLOR" or "BPMLOCK")).ToList();
        return (head.Concat(rest).Concat(added).ToList(), cuesWritten, loopsWritten, skipped);
    }

    private static (byte, byte, byte) ParseColour(string? hex, bool loop)
    {
        // "#FFFFFF" is CuePointEntity's "no colour" default → Serato's own default colour instead.
        if (hex is { Length: 7 } && hex[0] == '#' && !hex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return loop ? ((byte)0x27, (byte)0xAA, (byte)0xE1) : ((byte)0xCC, (byte)0x00, (byte)0x00); // Serato defaults
    }

    // TagLib# registers every format under "taglib/<extension>".
    private static string MimeFor(string path) => "taglib/" + Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
}
