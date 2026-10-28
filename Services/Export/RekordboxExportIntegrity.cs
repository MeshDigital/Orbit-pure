using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Non-destructive inspection of the data that crosses the Rekordbox XML bridge.
/// Blockers mean export would lose or corrupt saved work; warnings describe an intentional,
/// explicit fallback that the user can accept.
/// </summary>
public sealed record RekordboxExportPreflightResult(
    int SourceTrackCount,
    int UniqueTrackCount,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    public int RepeatedTrackEntryCount => Math.Max(0, SourceTrackCount - UniqueTrackCount);
    public bool CanExport => Blockers.Count == 0;
}

/// <summary>Raised when continuing would silently lose saved preparation data or audio files.</summary>
public sealed class RekordboxExportIntegrityException : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public RekordboxExportIntegrityException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems;
    }

    private static string BuildMessage(IReadOnlyList<string> problems)
    {
        var details = problems.Take(12).Select(problem => $"• {problem}");
        var remainder = problems.Count > 12
            ? $"{Environment.NewLine}• …and {problems.Count - 12} more."
            : string.Empty;
        return $"Rekordbox export blocked: {problems.Count} integrity issue(s). Nothing was exported."
            + Environment.NewLine + string.Join(Environment.NewLine, details) + remainder;
    }
}

/// <summary>Pure validation shared by the UI preflight and the final export-service guard.</summary>
public static class RekordboxExportIntegrity
{
    public static RekordboxExportPreflightResult Validate(
        IReadOnlyList<PlaylistTrack> sourceTracks,
        IReadOnlyList<PlaylistTrack> uniqueTracks,
        IReadOnlyDictionary<string, List<CuePointEntity>> cuesByHash,
        IReadOnlyDictionary<string, AudioFeaturesEntity> beatDataByHash,
        Func<PlaylistTrack, string?> pathSelector)
    {
        ArgumentNullException.ThrowIfNull(sourceTracks);
        ArgumentNullException.ThrowIfNull(uniqueTracks);
        ArgumentNullException.ThrowIfNull(cuesByHash);
        ArgumentNullException.ThrowIfNull(beatDataByHash);
        ArgumentNullException.ThrowIfNull(pathSelector);

        var blockers = new List<string>();
        var warnings = new List<string>();

        if (sourceTracks.Count == 0)
            blockers.Add("The selected playlist is empty.");

        var repeatedEntries = sourceTracks.Count - uniqueTracks.Count;
        if (repeatedEntries > 0)
        {
            warnings.Add(
                $"{repeatedEntries} repeated playlist entr{(repeatedEntries == 1 ? "y" : "ies")}: " +
                "each audio file appears once in the collection; every playlist position is preserved.");
        }

        foreach (var track in uniqueTracks)
        {
            var label = Label(track);
            var path = pathSelector(track);
            if (string.IsNullOrWhiteSpace(path))
            {
                blockers.Add($"{label}: no usable export file location.");
            }
            else if (!File.Exists(path))
            {
                blockers.Add($"{label}: audio file not found at '{path}'.");
            }

            var hash = track.TrackUniqueHash ?? string.Empty;
            beatDataByHash.TryGetValue(hash, out var beatData);
            cuesByHash.TryGetValue(hash, out var dbCues);
            dbCues ??= new List<CuePointEntity>();

            ValidateBeatGrid(track, beatData, label, blockers, warnings);
            ValidateCues(track, beatData, dbCues, label, blockers, warnings);
        }

        return new RekordboxExportPreflightResult(
            sourceTracks.Count,
            uniqueTracks.Count,
            blockers,
            warnings);
    }

    private static void ValidateBeatGrid(
        PlaylistTrack track,
        AudioFeaturesEntity? beatData,
        string label,
        List<string> blockers,
        List<string> warnings)
    {
        var hasUsableBpm = track.BPM is > 0 and < 1000 && double.IsFinite(track.BPM.Value);
        var hasInvalidBpm = track.BPM.HasValue && track.BPM.Value != 0 && !hasUsableBpm;
        if (hasInvalidBpm)
        {
            blockers.Add($"{label}: saved BPM '{track.BPM}' is invalid; use a finite value greater than 0 and less than 1000, or clear it before export.");
        }

        List<double>? beats = null;

        if (beatData != null && !string.IsNullOrWhiteSpace(beatData.BeatGridJson))
        {
            try
            {
                beats = JsonSerializer.Deserialize<List<double>>(beatData.BeatGridJson);
                if (beats == null)
                {
                    blockers.Add($"{label}: saved beat grid is null; repair or regenerate it before export.");
                }
            }
            catch (JsonException ex)
            {
                blockers.Add($"{label}: saved beat grid is malformed ({ex.Message}); repair or regenerate it before export.");
            }
        }

        if (beats != null)
        {
            var invalidValue = beats.Any(value => !double.IsFinite(value) || value < 0);
            var nonMonotonic = beats.Zip(beats.Skip(1), (left, right) => right <= left).Any(invalid => invalid);
            if (invalidValue || nonMonotonic)
            {
                blockers.Add($"{label}: saved beat grid contains negative, non-finite, or non-increasing beat times.");
            }
        }

        var durationSeconds = GetDurationSeconds(track, beatData);
        if (beatData != null)
        {
            if (!double.IsFinite(beatData.DownbeatOffsetSeconds) || beatData.DownbeatOffsetSeconds < 0)
            {
                blockers.Add($"{label}: saved downbeat offset is invalid.");
            }
            else if (durationSeconds > 0 && beatData.DownbeatOffsetSeconds > durationSeconds + 0.25)
            {
                blockers.Add($"{label}: saved downbeat offset lies beyond the track duration.");
            }
        }

        if (beats is { Count: > 0 } && !hasUsableBpm && !hasInvalidBpm)
        {
            blockers.Add($"{label}: saved beat grid has no usable average BPM; export cannot produce valid TEMPO anchors.");
        }
        else if ((beats == null || beats.Count == 0) && !hasInvalidBpm)
        {
            warnings.Add(hasUsableBpm
                ? $"{label}: no detailed beat-tick grid; export will use one TEMPO anchor at the saved downbeat offset."
                : $"{label}: no usable BPM or beat grid; export will omit TEMPO data.");
        }

        if (beats is { Count: > 0 } && durationSeconds > 0 && beats[^1] > durationSeconds + 0.25)
        {
            blockers.Add($"{label}: saved beat grid extends beyond the track duration.");
        }
    }

    private static void ValidateCues(
        PlaylistTrack track,
        AudioFeaturesEntity? beatData,
        List<CuePointEntity> dbCues,
        string label,
        List<string> blockers,
        List<string> warnings)
    {
        var userCues = new List<OrbitCue>();
        if (!string.IsNullOrWhiteSpace(track.CuePointsJson))
        {
            try
            {
                userCues = JsonSerializer.Deserialize<List<OrbitCue>>(track.CuePointsJson)
                    ?? throw new JsonException("the cue list is null");
            }
            catch (JsonException ex)
            {
                blockers.Add($"{label}: saved user cues are malformed ({ex.Message}); repair them before export.");
            }
        }

        var durationSeconds = GetDurationSeconds(track, beatData);
        foreach (var cue in dbCues)
        {
            ValidateCuePad(cue.SlotIndex, label, cue.Label, blockers);
            ValidateCuePosition(cue.TimestampInSeconds, cue.IsLoop, cue.LoopEndSeconds,
                durationSeconds, label, cue.Label, blockers);
        }
        foreach (var cue in userCues)
        {
            ValidateCuePad(cue.SlotIndex, label, cue.Name, blockers);
            ValidateCuePosition(cue.Timestamp, cue.IsLoop, cue.LoopEndSeconds,
                durationSeconds, label, cue.Name, blockers);
        }

        // Mirror the exporter's 50 ms merge so deliberately omitted duplicates are visible to the user.
        var acceptedCues = new List<OrbitCue>();
        acceptedCues.AddRange(dbCues.Select(cue => new OrbitCue
        {
            Timestamp = cue.TimestampInSeconds,
            Name = cue.Label,
            Color = cue.Color,
            IsLoop = cue.IsLoop,
            LoopEndSeconds = cue.LoopEndSeconds,
            SlotIndex = cue.SlotIndex,
        }));

        var mergedUserCueCount = 0;
        foreach (var cue in userCues.OrderBy(cue => cue.Timestamp))
        {
            if (acceptedCues.Any(existing => Math.Abs(existing.Timestamp - cue.Timestamp) < 0.05))
            {
                mergedUserCueCount++;
                continue;
            }
            acceptedCues.Add(cue);
        }

        if (mergedUserCueCount > 0)
        {
            warnings.Add($"{label}: {mergedUserCueCount} user cue(s) within 50 ms of another cue will be merged.");
        }

        if (acceptedCues.Count == 0)
        {
            warnings.Add($"{label}: no saved hot cues, memory cues, or loops; audio and metadata only.");
        }

        var duplicatePads = acceptedCues
            .Where(cue => cue.SlotIndex is >= 0 and <= 7)
            .GroupBy(cue => cue.SlotIndex)
            .Where(group => group.Count() > 1);
        foreach (var group in duplicatePads)
        {
            blockers.Add($"{label}: hot-cue pad {group.Key + 1} is assigned to {group.Count()} cues or loops; resolve the collision before export.");
        }

        var invalidColorCount = acceptedCues.Count(cue => !IsValidHexColor(cue.Color));
        if (invalidColorCount > 0)
        {
            warnings.Add($"{label}: {invalidColorCount} invalid cue colour(s) will be exported as white.");
        }
    }

    private static void ValidateCuePad(
        int slotIndex,
        string trackLabel,
        string cueName,
        List<string> blockers)
    {
        if (slotIndex is < -1 or > 7)
        {
            var cueLabel = string.IsNullOrWhiteSpace(cueName) ? "unnamed cue" : $"cue '{cueName}'";
            blockers.Add($"{trackLabel}: {cueLabel} has invalid hot-cue pad index {slotIndex}; valid indices are -1 for memory cues and 0-7 for hot pads.");
        }
    }

    private static void ValidateCuePosition(
        double timestamp,
        bool isLoop,
        double loopEnd,
        double durationSeconds,
        string trackLabel,
        string cueName,
        List<string> blockers)
    {
        var cueLabel = string.IsNullOrWhiteSpace(cueName) ? "unnamed cue" : $"cue '{cueName}'";
        if (!double.IsFinite(timestamp) || timestamp < 0)
        {
            blockers.Add($"{trackLabel}: {cueLabel} has an invalid position.");
        }
        else if (durationSeconds > 0 && timestamp > durationSeconds + 0.25)
        {
            blockers.Add($"{trackLabel}: {cueLabel} lies beyond the track duration.");
        }

        if (isLoop && (!double.IsFinite(loopEnd) || loopEnd <= timestamp))
        {
            blockers.Add($"{trackLabel}: loop '{cueName}' has an invalid or non-positive loop range.");
        }
        else if (isLoop && durationSeconds > 0 && loopEnd > durationSeconds + 0.25)
        {
            blockers.Add($"{trackLabel}: loop '{cueName}' ends beyond the track duration.");
        }
    }

    private static double GetDurationSeconds(PlaylistTrack track, AudioFeaturesEntity? beatData)
    {
        if (track.CanonicalDuration is > 0)
            return track.CanonicalDuration.Value / 1000.0;
        return beatData?.TrackDuration > 0 ? beatData.TrackDuration : 0;
    }

    private static bool IsValidHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        var value = color.TrimStart('#');
        return value.Length == 6 && value.All(Uri.IsHexDigit);
    }

    private static string Label(PlaylistTrack track)
    {
        var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
        var title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown Title" : track.Title;
        return $"{artist} - {title}";
    }
}
