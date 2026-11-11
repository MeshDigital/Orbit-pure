using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>The outcome of a beat-grid edit. <see cref="Success"/> is false when nothing was written.</summary>
public sealed record GridEditResult(bool Success, string? Reason = null)
{
    public static GridEditResult Ok() => new(true);
    public static GridEditResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// The single write path for beat-grid edits (set BPM, ×2/÷2, slip, set Bar 1, reset). Every grid
/// change is keyed on the track hash and, unlike the old <c>TrackRepository.UpdateBpmAsync</c> path,
/// updates <see cref="AudioFeaturesEntity.Bpm"/>, <see cref="AudioFeaturesEntity.BeatGridJson"/> and
/// <see cref="AudioFeaturesEntity.DownbeatOffsetSeconds"/> together with the library BPM/ManualBPM, so
/// Cue Forge, transition planning, cue generation and the Rekordbox export all read the same tempo.
/// The beat list is always changed with the number — it must never disagree with <c>Bpm</c>.
/// </summary>
public interface IBeatGridEditService
{
    /// <summary>Typed/tap BPM: regenerates a constant grid from Bar 1 at the new tempo.</summary>
    Task<GridEditResult> SetBpmAsync(string hash, double bpm, CancellationToken ct);

    /// <summary>×2 (factor 2.0) or ÷2 (factor 0.5). ×2 inserts midpoints; ÷2 keeps every other beat, keeping the Bar 1 beat.</summary>
    Task<GridEditResult> ScaleTempoAsync(string hash, double factor, CancellationToken ct);

    /// <summary>Shifts every beat (and Bar 1) by <paramref name="deltaSeconds"/>; tempo is unchanged.</summary>
    Task<GridEditResult> SlipAsync(string hash, double deltaSeconds, CancellationToken ct);

    /// <summary>Moves Bar 1 to the nearest beat; tempo and the beat list are unchanged.</summary>
    Task<GridEditResult> SetDownbeatAsync(string hash, double barOneSeconds, CancellationToken ct);

    /// <summary>Restores the analysed BPM and downbeat, regenerating the constant grid.</summary>
    Task<GridEditResult> ResetToAnalysedAsync(string hash, double analysedBpm, double analysedDownbeat, CancellationToken ct);

    /// <summary>Marks the current grid as user-verified without changing Bpm, the beat list or Bar 1.</summary>
    Task<GridEditResult> MarkGridCorrectAsync(string hash, CancellationToken ct);

    /// <summary>Applies the grid kept in AnalysedGridJson as a suggestion (issue #221).</summary>
    Task<GridEditResult> UseSuggestionAsync(string hash, CancellationToken ct);

    /// <summary>Discards the grid kept in AnalysedGridJson (issue #221).</summary>
    Task<GridEditResult> DismissSuggestionAsync(string hash, CancellationToken ct);

    /// <summary>The track's full grid state, for undo (issue #222). Null when the track has no analysis.</summary>
    Task<GridSnapshot?> GetSnapshotAsync(string hash, CancellationToken ct) => Task.FromResult<GridSnapshot?>(null);

    /// <summary>Writes a snapshot back exactly — including its provenance, so undoing the first edit
    /// returns the grid to "analysis" (issue #222).</summary>
    Task<GridEditResult> RestoreSnapshotAsync(string hash, GridSnapshot snapshot, CancellationToken ct) =>
        Task.FromResult(GridEditResult.Failed("Restoring a grid is not supported here."));

    /// <summary>
    /// Variable tempo (issue #226): from the beat nearest <paramref name="atSeconds"/> up to the next tempo
    /// change (or the end), beats are re-spaced at <paramref name="bpm"/>; later sections keep their own
    /// tempo. Used for "Add tempo change here" and for editing the BPM of the section under the playhead.
    /// </summary>
    Task<GridEditResult> SetTempoFromAsync(string hash, double atSeconds, double bpm, CancellationToken ct) =>
        Task.FromResult(GridEditResult.Failed("Tempo sections are not supported here."));

    /// <summary>Removes the tempo change nearest <paramref name="atSeconds"/>: that section takes the
    /// previous section's tempo (issue #226).</summary>
    Task<GridEditResult> RemoveTempoChangeAsync(string hash, double atSeconds, CancellationToken ct) =>
        Task.FromResult(GridEditResult.Failed("Tempo sections are not supported here."));
}

/// <summary>A track's complete grid state (issue #222 undo).</summary>
public sealed record GridSnapshot(
    double Bpm, double[] Beats, double Downbeat,
    string? Source, bool Verified, float? Confidence, string? AnalysedGridJson, bool IsVariableTempo);

public sealed class BeatGridEditService : IBeatGridEditService
{
    private const double MinBpm = 40;
    private const double MaxBpm = 400;

    private readonly DatabaseService _database;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<BeatGridEditService> _logger;

    public BeatGridEditService(DatabaseService database, IEventBus? eventBus, ILogger<BeatGridEditService> logger)
    {
        _database = database;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<GridEditResult> SetBpmAsync(string hash, double bpm, CancellationToken ct)
    {
        if (bpm < MinBpm || bpm > MaxBpm)
            return GridEditResult.Failed($"BPM {bpm:0.#} is outside the supported range ({MinBpm:0}–{MaxBpm:0}).");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double downbeat = features.DownbeatOffsetSeconds;
        double[] beats = ConstantGrid(downbeat, bpm, features.TrackDuration);
        return await WriteGridAsync(hash, features, bpm, beats, downbeat, ct, PhraseFollow.KeepSeconds).ConfigureAwait(false);
    }

    public async Task<GridEditResult> ScaleTempoAsync(string hash, double factor, CancellationToken ct)
    {
        if (factor != 2.0 && factor != 0.5)
            return GridEditResult.Failed("Only ×2 and ÷2 tempo scaling is supported.");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] oldBeats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        if (oldBeats.Length < 2)
            return GridEditResult.Failed("This track has no beat grid to rescale.");

        double oldBpm = features.Bpm > 0 ? features.Bpm : MedianIntervalBpm(oldBeats);
        double newBpm = oldBpm * factor;
        if (newBpm < MinBpm || newBpm > MaxBpm)
            return GridEditResult.Failed($"The result ({newBpm:0.#} BPM) is outside the supported range ({MinBpm:0}–{MaxBpm:0}).");

        double period = MedianInterval(oldBeats);
        double downbeat = features.DownbeatOffsetSeconds;
        double[] newBeats = factor > 1.0
            ? InsertMidpoints(oldBeats, period)
            : KeepEveryOtherBeat(oldBeats, downbeat);

        return await WriteGridAsync(hash, features, newBpm, newBeats, downbeat, ct, PhraseFollow.KeepSeconds).ConfigureAwait(false);
    }

    public async Task<GridEditResult> SlipAsync(string hash, double deltaSeconds, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        double bpm = features.Bpm > 0 ? features.Bpm : (beats.Length >= 2 ? MedianIntervalBpm(beats) : 0);

        double downbeat = features.DownbeatOffsetSeconds + deltaSeconds;
        double shift = deltaSeconds;
        if (downbeat < 0 && bpm > 0)
        {
            // Wrap by one bar: the grid is periodic at the bar, so this preserves phase while keeping
            // every beat non-negative and Bar 1 a beat.
            double bar = 240.0 / bpm;
            downbeat += bar;
            shift += bar;
        }

        double[] newBeats = beats.Select(b => Math.Round(b + shift, 4)).ToArray();
        return await WriteGridAsync(hash, features, bpm, newBeats, downbeat, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> SetDownbeatAsync(string hash, double barOneSeconds, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        double bpm = features.Bpm > 0 ? features.Bpm : (beats.Length >= 2 ? MedianIntervalBpm(beats) : 0);
        double snapped = SnapToBeat(beats, barOneSeconds);

        return await WriteGridAsync(hash, features, bpm, beats, snapped, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> ResetToAnalysedAsync(string hash, double analysedBpm, double analysedDownbeat, CancellationToken ct)
    {
        if (analysedBpm < MinBpm || analysedBpm > MaxBpm)
            return GridEditResult.Failed($"The analysed BPM {analysedBpm:0.#} is outside the supported range.");

        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");

        double[] beats = ConstantGrid(analysedDownbeat, analysedBpm, features.TrackDuration);
        // Reset can change the tempo as well as the phase: keep phrases where they are in the music.
        return await WriteGridAsync(hash, features, analysedBpm, beats, analysedDownbeat, ct, PhraseFollow.KeepSeconds).ConfigureAwait(false);
    }

    public async Task<GridEditResult> MarkGridCorrectAsync(string hash, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to mark correct. Analyse it first.");

        features.GridVerified = true;
        features.GridEditedAt = DateTime.UtcNow.ToString("O");
        // A verification never changes the grid itself: Bpm, BeatGridJson, DownbeatOffsetSeconds and
        // GridSource stay exactly as they were.

        try
        {
            await _database.UpdateAudioFeaturesAsync(features).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: failed to mark grid correct for {Hash}", hash);
            return GridEditResult.Failed($"Couldn't mark the grid correct: {ex.Message}");
        }

        try
        {
            _eventBus?.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: publishing the update event failed for {Hash}", hash);
        }

        return GridEditResult.Ok();
    }

    public async Task<GridEditResult> UseSuggestionAsync(string hash, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to apply a suggestion to.");

        var suggestion = GridWriteRules.ParseSuggestion(features.AnalysedGridJson);
        if (suggestion is null)
            return GridEditResult.Failed("There is no stored grid suggestion to apply.");
        if (suggestion.Bpm < MinBpm || suggestion.Bpm > MaxBpm)
            return GridEditResult.Failed($"The suggested BPM {suggestion.Bpm:0.#} is outside the supported range.");

        return await WriteGridAsync(hash, features, suggestion.Bpm, suggestion.Beats, suggestion.Downbeat, ct, PhraseFollow.KeepSeconds).ConfigureAwait(false);
    }

    public async Task<GridSnapshot?> GetSnapshotAsync(string hash, CancellationToken ct)
    {
        var f = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        return f is null ? null : new GridSnapshot(
            f.Bpm, BeatGridFitter.ParseTicks(f.BeatGridJson), f.DownbeatOffsetSeconds,
            f.GridSource, f.GridVerified, f.GridConfidence, f.AnalysedGridJson, f.IsVariableTempo);
    }

    public async Task<GridEditResult> RestoreSnapshotAsync(string hash, GridSnapshot snapshot, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to restore a grid into.");
        var follow = Math.Abs(features.Bpm - snapshot.Bpm) > 0.005 ? PhraseFollow.KeepSeconds : PhraseFollow.KeepBars;
        return await WriteGridAsync(hash, features, snapshot.Bpm, snapshot.Beats, snapshot.Downbeat, ct, follow, snapshot).ConfigureAwait(false);
    }

    public async Task<GridEditResult> SetTempoFromAsync(string hash, double atSeconds, double bpm, CancellationToken ct)
    {
        if (bpm < MinBpm || bpm > MaxBpm)
            return GridEditResult.Failed($"BPM {bpm:0.#} is outside the supported range ({MinBpm:0}–{MaxBpm:0}).");
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to store a beat grid in. Analyse it first.");
        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        if (beats.Length < 8)
            return GridEditResult.Failed("This track has no beat grid to add a tempo change to.");

        // A user/Rekordbox grid is a clean piecewise grid; an analysis grid is made clean first so a
        // tempo change never inherits the beat tracker's jitter.
        if (!IsCleanSource(features.GridSource))
            beats = ConstantGrid(features.DownbeatOffsetSeconds, features.Bpm > 0 ? features.Bpm : MedianIntervalBpm(beats), features.TrackDuration);

        int start = NearestBeatIndex(beats, atSeconds);
        var sections = BeatGridFitter.ExactSections(beats);
        int end = sections.Select(s => s.StartBeatIndex).Where(i => i > start).DefaultIfEmpty(beats.Length).Min();
        double[] newBeats = Respace(beats, start, end, 60.0 / bpm, features.TrackDuration);
        return await WriteSectionedGridAsync(hash, features, beats, newBeats, ct).ConfigureAwait(false);
    }

    public async Task<GridEditResult> RemoveTempoChangeAsync(string hash, double atSeconds, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis.");
        double[] beats = BeatGridFitter.ParseTicks(features.BeatGridJson);
        var sections = BeatGridFitter.ExactSections(beats);
        if (sections.Count < 2)
            return GridEditResult.Failed("This grid has no tempo change to remove.");

        // The change nearest the playhead (never the first section's start, which is not a change).
        int k = Enumerable.Range(1, sections.Count - 1)
            .OrderBy(i => Math.Abs(sections[i].StartSeconds - atSeconds)).First();
        int start = sections[k].StartBeatIndex;
        int end = k + 1 < sections.Count ? sections[k + 1].StartBeatIndex : beats.Length;
        double[] newBeats = Respace(beats, start, end, 60.0 / sections[k - 1].Bpm, features.TrackDuration);
        return await WriteSectionedGridAsync(hash, features, beats, newBeats, ct).ConfigureAwait(false);
    }

    private static bool IsCleanSource(string? source) =>
        string.Equals(source, GridWriteRules.SourceUser, StringComparison.Ordinal) ||
        string.Equals(source, GridWriteRules.SourceRekordbox, StringComparison.Ordinal);

    /// <summary>
    /// Re-spaces beats [start, end) at <paramref name="period"/> from beats[start]; the beats after the
    /// range keep their own spacing, shifted to follow. The list is trimmed to / extended up to the track
    /// end at the last section's spacing.
    /// </summary>
    internal static double[] Respace(double[] beats, int start, int end, double period, double duration)
    {
        var result = new List<double>(beats.Length + 16);
        for (int i = 0; i <= start && i < beats.Length; i++) result.Add(beats[i]);
        double t = beats[Math.Min(start, beats.Length - 1)];
        for (int i = start + 1; i < end; i++) { t += period; result.Add(Math.Round(t, 4)); }
        double lastPeriod = period;
        for (int i = Math.Max(end, start + 1); i < beats.Length; i++)
        {
            lastPeriod = beats[i] - beats[i - 1];
            t += lastPeriod;
            result.Add(Math.Round(t, 4));
        }
        double limit = duration > 0 ? duration : double.MaxValue;
        while (result.Count > 1 && result[^1] > limit) result.RemoveAt(result.Count - 1);
        if (duration > 0 && lastPeriod > 0)
            while (result[^1] + lastPeriod <= duration) result.Add(Math.Round(result[^1] + lastPeriod, 4));
        return result.ToArray();
    }

    private async Task<GridEditResult> WriteSectionedGridAsync(
        string hash, Data.Entities.AudioFeaturesEntity features, double[] oldBeats, double[] newBeats, CancellationToken ct)
    {
        // Bar 1 stays on the same beat number; the displayed BPM is that of the longest section.
        int downbeatIndex = NearestBeatIndex(oldBeats, features.DownbeatOffsetSeconds);
        double downbeat = newBeats[Math.Clamp(downbeatIndex, 0, newBeats.Length - 1)];
        var sections = BeatGridFitter.ExactSections(newBeats);
        double bpm = sections.Count == 0 ? features.Bpm
            : sections.Select((s, i) => (s.Bpm, Length: (i + 1 < sections.Count ? sections[i + 1].StartBeatIndex : newBeats.Length) - s.StartBeatIndex))
                .OrderByDescending(x => x.Length).First().Bpm;
        return await WriteGridAsync(hash, features, bpm, newBeats, downbeat, ct, PhraseFollow.KeepSeconds).ConfigureAwait(false);
    }

    public async Task<GridEditResult> DismissSuggestionAsync(string hash, CancellationToken ct)
    {
        var features = await _database.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(false);
        if (features is null)
            return GridEditResult.Failed("This track has no analysis to dismiss a suggestion from.");

        features.AnalysedGridJson = null;
        try
        {
            await _database.UpdateAudioFeaturesAsync(features).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: failed to dismiss the grid suggestion for {Hash}", hash);
            return GridEditResult.Failed($"Couldn't dismiss the grid suggestion: {ex.Message}");
        }
        return GridEditResult.Ok();
    }

    /// <summary>
    /// How bar-indexed phrases (issue #225) follow a grid edit. A <b>phase</b> edit (slip, Bar 1) keeps
    /// each phrase on its bar, so the boundaries move with the bar lines. A <b>tempo</b> edit (×2/÷2,
    /// typed BPM, reset, suggestion) keeps each phrase where it is in the music and re-numbers its bars:
    /// keeping the bar number made every boundary land 1/factor as far into the track (2026-11-11:
    /// correcting a half-time 87.5 → 175 analysis moved "intro ends 1:27" to 0:44).
    /// </summary>
    internal enum PhraseFollow { KeepBars, KeepSeconds }

    private async Task<GridEditResult> WriteGridAsync(
        string hash, AudioFeaturesEntity features, double bpm, double[] beats, double downbeat, CancellationToken ct,
        PhraseFollow phraseFollow = PhraseFollow.KeepBars, GridSnapshot? restore = null)
    {
        features.Bpm = (float)Math.Round(bpm, 2);
        features.BeatGridJson = JsonSerializer.Serialize(beats);
        features.DownbeatOffsetSeconds = Math.Round(downbeat, 4);
        if (restore is not null)
        {
            // Undo/redo: put the provenance back exactly as it was.
            features.GridSource = restore.Source;
            features.GridVerified = restore.Verified;
            features.GridConfidence = restore.Confidence;
            features.AnalysedGridJson = restore.AnalysedGridJson;
            features.IsVariableTempo = restore.IsVariableTempo;
            features.GridEditedAt = DateTime.UtcNow.ToString("O");
        }
        else
        {
            // A grid the user has touched is user-owned and verified, so re-analysis and Rekordbox
            // adoption can never silently overwrite it (see GridWriteRules).
            features.GridSource = GridWriteRules.SourceUser;
            features.GridVerified = true;
            features.GridEditedAt = DateTime.UtcNow.ToString("O");
            features.GridConfidence = 1.0f;
            // A user grid is a clean piecewise grid, so its tempo sections are exact (issue #226).
            features.IsVariableTempo = BeatGridFitter.ExactSections(beats).Count > 1;
        }

        try
        {
            bool wrote = await _database.ApplyGridEditAsync(features, ct).ConfigureAwait(false);
            if (!wrote)
                return GridEditResult.Failed("Couldn't save the beat grid: the track has no analysis row.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: failed to persist grid for {Hash}", hash);
            return GridEditResult.Failed($"Couldn't save the beat grid change: {ex.Message}");
        }

        // Bar-indexed phrases follow the grid (issue #225) — see PhraseFollow for which way.
        string? phraseWarning = null;
        try
        {
            if (phraseFollow == PhraseFollow.KeepSeconds)
                await _database.ReindexPhraseBarsFromSecondsAsync(hash, bpm, downbeat, ct).ConfigureAwait(false);
            else
                await _database.ReindexPhraseSecondsFromBarsAsync(hash, bpm, downbeat, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The grid itself was saved; tell the user the phrase map didn't follow it.
            _logger.LogError(ex, "BeatGridEdit: could not re-index phrases for {Hash}", hash);
            phraseWarning = "The beat grid was saved, but the phrase map couldn't be updated to match. Re-analyse the track to rebuild it.";
        }

        try
        {
            _eventBus?.Publish(new TrackAnalysisUpdatedEvent(hash, DateTime.UtcNow));
            _eventBus?.Publish(new TrackMetadataUpdatedEvent(hash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeatGridEdit: publishing the update event failed for {Hash}", hash);
        }

        if (phraseWarning is not null)
        {
            try
            {
                _eventBus?.Publish(new ToastRequestedEvent("Phrase map not updated", phraseWarning,
                    SLSKDONET.Views.NotificationType.Warning, TimeSpan.FromSeconds(10)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BeatGridEdit: publishing the phrase warning failed for {Hash}", hash);
            }
        }

        return GridEditResult.Ok();
    }

    /// <summary>
    /// A constant beat grid at <paramref name="bpm"/> whose Bar 1 (<paramref name="downbeat"/>) is a
    /// beat, covering the whole track. The earliest beat sits at or after t = 0 and pickups before
    /// Bar 1 are included (negative beat indices relative to Bar 1).
    /// </summary>
    internal static double[] ConstantGrid(double downbeat, double bpm, double duration)
    {
        if (bpm <= 0 || duration <= 0)
            return new[] { Math.Round(Math.Max(0, downbeat), 4) };

        double period = 60.0 / bpm;
        int n0 = (int)-Math.Floor(downbeat / period);
        int n1 = (int)Math.Ceiling((duration - downbeat) / period);
        if (n1 < n0) return new[] { Math.Round(downbeat, 4) };

        var beats = new double[n1 - n0 + 1];
        for (int n = n0; n <= n1; n++) beats[n - n0] = Math.Round(downbeat + n * period, 4);
        return beats;
    }

    /// <summary>×2: every beat plus the midpoint to the next beat (so a variable grid doubles section
    /// by section); the last beat uses <paramref name="period"/> (or its own last interval).</summary>
    internal static double[] InsertMidpoints(double[] beats, double period)
    {
        var result = new List<double>(beats.Length * 2);
        for (int i = 0; i < beats.Length; i++)
        {
            result.Add(beats[i]);
            double next = i + 1 < beats.Length ? beats[i + 1]
                : beats[i] + (beats.Length > 1 ? beats[i] - beats[i - 1] : period);
            result.Add(Math.Round((beats[i] + next) / 2.0, 4));
        }
        result.Sort();
        return result.Distinct().ToArray();
    }

    /// <summary>÷2: every other beat, keeping the beat that sits on Bar 1 (<paramref name="downbeat"/>).</summary>
    internal static double[] KeepEveryOtherBeat(double[] beats, double downbeat)
    {
        int idx = NearestBeatIndex(beats, downbeat);
        if (idx < 0) return beats;

        var result = new List<double>();
        for (int i = idx; i < beats.Length; i += 2) result.Add(beats[i]);
        for (int i = idx - 2; i >= 0; i -= 2) result.Add(beats[i]);
        result.Sort();
        return result.ToArray();
    }

    internal static double MedianInterval(double[] beats)
    {
        if (beats.Length < 2) return 0;
        var intervals = new double[beats.Length - 1];
        for (int i = 1; i < beats.Length; i++) intervals[i - 1] = beats[i] - beats[i - 1];
        Array.Sort(intervals);
        return intervals[intervals.Length / 2];
    }

    private static double MedianIntervalBpm(double[] beats) =>
        beats.Length >= 2 && MedianInterval(beats) > 0 ? 60.0 / MedianInterval(beats) : 0;

    private static int NearestBeatIndex(double[] beats, double downbeat)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < beats.Length; i++)
        {
            double d = Math.Abs(beats[i] - downbeat);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    private static double SnapToBeat(double[] beats, double seconds)
    {
        int idx = NearestBeatIndex(beats, seconds);
        return idx < 0 ? Math.Round(seconds, 4) : beats[idx];
    }
}
