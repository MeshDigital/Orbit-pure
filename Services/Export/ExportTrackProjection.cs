using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

public enum ExportTargetKind
{
    Rekordbox,
    Traktor,
    VirtualDj,
    Serato
}

/// <summary>Target-neutral cue/loop entry with assigned hot-cue slot (0..7) or memory cue (-1).</summary>
public sealed record ExportCueProjection(
    int Slot,
    string Name,
    double StartSeconds,
    double? LoopEndSeconds,
    bool IsLoop,
    string ColorHex,
    bool IsHotCue);

/// <summary>
/// Target-neutral track projection built once from the library and serialized by Rekordbox,
/// Traktor, VirtualDJ, and Serato writers (DX0 #555).
/// </summary>
public sealed record ExportTrackProjection(
    string TrackUniqueHash,
    string FilePath,
    string Title,
    string Artist,
    string Album,
    string Genre,
    string Label,
    string Comment,
    double Bpm,
    double? BeatgridAnchorSeconds,
    string? CanonicalKey,
    double? Energy,
    int Rating,
    double DurationSeconds,
    int Bitrate,
    IReadOnlyList<ExportCueProjection> Cues);

public sealed record ExportPreflightReport(
    ExportTargetKind Target,
    int TrackCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Per-target fixed field mapping and slot limits (DX0 #555).
/// </summary>
public static class ExportFieldMapping
{
    public static int MaxHotCueSlots(ExportTargetKind target) => target switch
    {
        ExportTargetKind.Rekordbox => 8,
        ExportTargetKind.Traktor => 8,
        ExportTargetKind.Serato => 8,
        ExportTargetKind.VirtualDj => 8,
        _ => 8
    };

    /// <summary>
    /// Formats the comment field for targets without a dedicated Energy column
    /// (prepends <c>E7</c> when Energy is known and not already present).
    /// </summary>
    public static string FormatComment(ExportTrackProjection track, ExportTargetKind target)
    {
        var baseComment = (track.Comment ?? string.Empty).Trim();
        if (!track.Energy.HasValue || track.Energy.Value <= 0)
            return baseComment;

        var energy1To10 = Math.Clamp((int)Math.Round(track.Energy.Value <= 1.0 ? track.Energy.Value * 10.0 : track.Energy.Value), 1, 10);
        var tag = $"E{energy1To10}";
        if (baseComment.Contains(tag, StringComparison.OrdinalIgnoreCase))
            return baseComment;

        return string.IsNullOrEmpty(baseComment) ? tag : $"{tag} · {baseComment}";
    }

    public static string FormatKey(ExportTrackProjection track, ExportTargetKind target) => target switch
    {
        ExportTargetKind.Traktor => KeyNotation.ToOpenKey(track.CanonicalKey) ?? track.CanonicalKey ?? string.Empty,
        ExportTargetKind.VirtualDj => KeyNotation.ToCamelot(track.CanonicalKey) ?? track.CanonicalKey ?? string.Empty,
        ExportTargetKind.Serato => KeyNotation.ToCamelot(track.CanonicalKey) ?? track.CanonicalKey ?? string.Empty,
        ExportTargetKind.Rekordbox => KeyNotation.ToMusical(track.CanonicalKey) ?? track.CanonicalKey ?? string.Empty,
        _ => track.CanonicalKey ?? string.Empty
    };

    public static ExportPreflightReport Preflight(IReadOnlyList<ExportTrackProjection> tracks, ExportTargetKind target)
    {
        var warnings = new List<string>();
        var slotLimit = MaxHotCueSlots(target);
        var missingPathCount = tracks.Count(t => string.IsNullOrWhiteSpace(t.FilePath));
        if (missingPathCount > 0)
            warnings.Add($"{missingPathCount} track(s) have no local file path and will be skipped.");

        var overflowTracks = tracks.Count(t => t.Cues.Count(c => c.IsHotCue) > slotLimit);
        if (overflowTracks > 0)
            warnings.Add($"{overflowTracks} track(s) have more than {slotLimit} hot cues; extra hot cues will be truncated for {target}.");

        return new ExportPreflightReport(target, tracks.Count, warnings);
    }
}

/// <summary>Builds <see cref="ExportTrackProjection"/> from library models and stored cues.</summary>
public static class ExportTrackProjectionBuilder
{
    public static ExportTrackProjection Build(
        PlaylistTrack track,
        IReadOnlyList<CuePointEntity>? cues = null,
        AudioFeaturesEntity? features = null,
        string? effectivePath = null)
    {
        var path = effectivePath ?? track.ResolvedFilePath ?? string.Empty;
        var bpm = features?.Bpm > 0 ? features.Bpm : (track.BPM ?? 0);
        var key = !string.IsNullOrWhiteSpace(features?.Key) ? features.Key : track.MusicalKey;
        var energy = features?.Energy > 0 ? features.Energy : track.Energy;
        var durationSec = track.CanonicalDuration.HasValue && track.CanonicalDuration.Value > 0
            ? track.CanonicalDuration.Value / 1000.0
            : 0;

        var projectedCues = new List<ExportCueProjection>();
        if (cues != null)
        {
            var usedHotSlots = new HashSet<int>();
            foreach (var c in cues.OrderBy(x => x.TimestampInSeconds))
            {
                var isLoop = c.IsLoop && c.LoopEndSeconds > c.TimestampInSeconds + 0.05;
                double? loopEnd = isLoop ? c.LoopEndSeconds : null;

                var slot = -1;
                if (c.SlotIndex >= 0 && c.SlotIndex < 8 && usedHotSlots.Add(c.SlotIndex))
                {
                    slot = c.SlotIndex;
                }
                else
                {
                    for (int s = 0; s < 8; s++)
                    {
                        if (usedHotSlots.Add(s))
                        {
                            slot = s;
                            break;
                        }
                    }
                }

                projectedCues.Add(new ExportCueProjection(
                    Slot: slot,
                    Name: string.IsNullOrWhiteSpace(c.Label) ? $"Cue {projectedCues.Count + 1}" : c.Label.Trim(),
                    StartSeconds: Math.Max(0, c.TimestampInSeconds),
                    LoopEndSeconds: loopEnd,
                    IsLoop: isLoop,
                    ColorHex: string.IsNullOrWhiteSpace(c.Color) ? "#27AE60" : c.Color,
                    IsHotCue: slot >= 0));
            }
        }

        double? anchor = features != null && features.DownbeatOffsetSeconds >= 0 && bpm > 0
            ? features.DownbeatOffsetSeconds
            : null;

        return new ExportTrackProjection(
            TrackUniqueHash: track.TrackUniqueHash ?? string.Empty,
            FilePath: path,
            Title: track.Title ?? string.Empty,
            Artist: track.Artist ?? string.Empty,
            Album: track.Album ?? string.Empty,
            Genre: track.PrimaryGenre ?? string.Empty,
            Label: string.Empty,
            Comment: string.Empty,
            Bpm: bpm,
            BeatgridAnchorSeconds: anchor,
            CanonicalKey: key,
            Energy: energy,
            Rating: track.Rating,
            DurationSeconds: durationSec,
            Bitrate: track.Bitrate ?? 0,
            Cues: projectedCues);
    }
}
