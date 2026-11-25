using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Library;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;


/// <summary>
/// Queue management, reanalysis commands, event bus handlers, filtering, performance probe, and library loading (#307).
/// </summary>
public partial class AnalysisPageViewModel
{
    // ── Commands / Actions ───────────────────────────────────────────────────────

    /// <summary>Adds a library track to the analysis queue (if not already present).</summary>
    public void AddToQueue(AnalysisTrackItem track)
    {
        var timer = Stopwatch.StartNew();
        AddToQueueCore(track, refreshState: true);
        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    /// <summary>Removes a track from the analysis queue.</summary>
    public void RemoveFromQueue(AnalysisTrackItem track)
    {
        var timer = Stopwatch.StartNew();
        AnalysisQueue.Remove(track);
        track.IsInQueue = false;
        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
        RefreshComputedState();
    }

    /// <summary>Clears existing output and stages a track for a fresh analysis pass.</summary>
    public void Reanalyze(AnalysisTrackItem track)
    {
        ResetTrackAnalysisState(track);
        AddToQueueCore(track, refreshState: true, prioritize: true);
        _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackId, AnalysisTier.Tier1, IsHighPriority: true));
        AutomixStatusMessage = $"Priority reanalyze queued: {track.Artist} — {track.Title}.";
    }

    public async Task ReanalyzeAllIncompleteAsync()
    {
        var timer = Stopwatch.StartNew();

        var incompleteTracks = LibraryTracks
            .Where(t => t.HasIncompleteAnalysis)
            .ToList();

        int batchCount = 0;
        foreach (var track in incompleteTracks)
        {
            ResetTrackAnalysisState(track);
            AddToQueueCore(track, refreshState: false);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));

        AutomixStatusMessage = incompleteTracks.Count == 0
            ? "No incomplete tracks found for reanalysis."
            : $"Queued {incompleteTracks.Count} incomplete track(s) for reanalysis.";

        RefreshComputedState();
    }

    /// <summary>
    /// Force-queues every track for a fresh analysis pass, bypassing the "incomplete" filter.
    /// Use this after a detection-pipeline upgrade, when already-analyzed tracks look
    /// "complete" by every existing check but were produced by the old, worse logic.
    /// </summary>
    public async Task ReanalyzeAllTracksAsync()
    {
        var timer = Stopwatch.StartNew();

        var allTracks = LibraryTracks.ToList();

        int batchCount = 0;
        foreach (var track in allTracks)
        {
            ResetTrackAnalysisState(track);
            AddToQueueCore(track, refreshState: false);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));

        AutomixStatusMessage = allTracks.Count == 0
            ? "No tracks in library to reanalyze."
            : $"Queued all {allTracks.Count} track(s) for full reanalysis. Click Start Analysis to run.";

        RefreshComputedState();
    }

    /// <summary>
    /// Re-maps cue points for every already-analysed track in the library — a DB round-trip per
    /// track via AnalyzeTrackStructureJob.RegenerateCuesOnlyAsync, not a queued full re-analysis.
    /// Runs immediately (no "Start Analysis" step needed) since it never touches the audio-decode
    /// concurrency-gated pipeline.
    /// </summary>
    public async Task RegenerateAllCuesAsync()
    {
        if (_cueStructureJob == null)
        {
            AutomixStatusMessage = "Cue regeneration service unavailable.";
            return;
        }

        var timer = Stopwatch.StartNew();
        var allTracks = LibraryTracks.ToList();

        int succeeded = 0, skipped = 0, batchCount = 0;
        foreach (var track in allTracks)
        {
            if (await _cueStructureJob.RegenerateCuesOnlyAsync(track.TrackId))
                succeeded++;
            else
                skipped++;

            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
                AutomixStatusMessage = $"Regenerating cues… {succeeded + skipped}/{allTracks.Count}";
            }
        }

        timer.Stop();
        AutomixStatusMessage = allTracks.Count == 0
            ? "No tracks in library."
            : NumFormat.Ui($"Regenerated cues for {succeeded} track(s){(skipped > 0 ? $" — {skipped} skipped (not analysed yet)" : "")} in {timer.Elapsed.TotalSeconds:0.#}s.");

        RefreshComputedState();
    }

    private async Task CopyAnalysisJsonAsync(AnalysisTrackItem? track)
    {
        if (track?.AnalysisData is null || _clipboardService is null)
            return;

        var json = JsonSerializer.Serialize(track.AnalysisData, new JsonSerializerOptions { WriteIndented = true });
        await _clipboardService.SetTextAsync(json);
        AutomixStatusMessage = $"Copied analysis payload for {track.Artist} — {track.Title}.";
    }

    private async Task CopyPerformanceSnapshotAsync()
    {
        if (_clipboardService is null)
            return;

        var snapshot = string.Join(Environment.NewLine,
            "Analysis Performance Snapshot",
            $"UTC: {DateTime.UtcNow:O}",
            $"Tracks: {TotalTrackCount}",
            $"Visible: {_filteredTrackCount}",
            $"Queue: {QueueTrackCount}",
            $"Analyzed: {AnalyzedTrackCount}",
            $"{FilterPerformanceDisplay}",
            $"{InteractionPerformanceDisplay}",
            PerformanceProbeSummary ?? "Probe not run yet");

        await _clipboardService.SetTextAsync(snapshot);
        AutomixStatusMessage = "Copied performance snapshot to clipboard.";
    }

    private void ClearPerformanceProbeHistory()
    {
        PerformanceProbeRuns.Clear();
        PerformanceProbeSummary = "Probe history cleared.";
    }

    /// <summary>Adds all visible unanalyzed tracks that are not yet in the queue.</summary>
    public async Task QueueAllUnanalyzedAsync()
    {
        var timer = Stopwatch.StartNew();
        var unanalyzedTracks = FilteredLibraryTracks.Where(t => !t.HasAnalysis && !t.IsInQueue).ToList();

        int batchCount = 0;
        foreach (var track in unanalyzedTracks)
        {
            AddToQueueCore(track, refreshState: false);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
        RefreshComputedState();
    }

    /// <summary>
    /// Starts sequential analysis of all queued tracks.
    /// Progress/completion is driven by queue-service events.
    /// </summary>
    public async Task StartAnalysisAsync()
    {
        if (!CanStartAnalysis)
            return;

        ProcessingState = AnalysisProcessingState.Processing;
        if (!_analysisSessionStopwatch.IsRunning || _analysisSessionStopwatch.Elapsed == TimeSpan.Zero)
            _analysisSessionStopwatch.Restart();

        // Exclude Failed tracks — permanently corrupt files would otherwise re-run on every
        // "Analyze All" invocation and spam the logs with the same FFmpeg decode error.
        // Users can still retry individual failed tracks via the manual re-analyse button.
        var queue = AnalysisQueue
            .Where(t => t.AnalysisStatus != AnalysisRunStatus.Completed
                     && t.AnalysisStatus != AnalysisRunStatus.Failed)
            .ToList();

        foreach (var track in queue)
        {
            if (_cts.Token.IsCancellationRequested)
                break;

            track.AnalysisStatus = AnalysisRunStatus.Queued;
            track.ProgressPercent = 0;
            track.CurrentStep = "Queued for analysis";
            track.AnalysisError = null;
            _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackId));
        }

        RefreshComputedState();
    }

    private void OnAnalysisProgress(AnalysisProgressEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null) return;

        CurrentProcessingTrackId = evt.TrackGlobalId;
        if (track.AnalysisStatus != AnalysisRunStatus.Completed)
            track.AnalysisStatus = AnalysisRunStatus.Processing;

        track.ProgressPercent = evt.ProgressPercent;
        track.CurrentStep = evt.StageCount > 0
            ? $"Stage {evt.StageIndex}/{evt.StageCount} · {evt.CurrentStep}"
            : evt.CurrentStep;
        ProcessingState = AnalysisProcessingState.Processing;
        RefreshComputedState();
    }

    private void OnTrackAnalysisStarted(TrackAnalysisStartedEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
            return;

        _trackAnalysisStartedAtUtc[evt.TrackGlobalId] = DateTime.UtcNow;
        CurrentProcessingTrackId = evt.TrackGlobalId;
        ProcessingState = AnalysisProcessingState.Processing;

        track.AnalysisStatus = AnalysisRunStatus.Processing;
        track.ProgressPercent = Math.Max(track.ProgressPercent, 1);
        track.CurrentStep = $"Analyzing {evt.FileName}";
        track.AnalysisError = null;
        RefreshComputedState();
    }

    /// <summary>
    /// Marks a row this page shows as failed. Requests for tracks the page never resolved to a
    /// real library entry have no row (see <see cref="OnTrackAnalysisRequested"/>), so they are
    /// not counted as failed analyses here.
    /// </summary>
    private Task OnTrackAnalysisFailedAsync(TrackAnalysisFailedEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
            return Task.CompletedTask;

        track.AnalysisStatus = AnalysisRunStatus.Failed;
        track.CurrentStep = "Analysis failed";
        track.AnalysisError = evt.Error;
        track.ProgressPercent = 100;
        RefreshComputedState();
        return Task.CompletedTask;
    }

    private async Task OnTrackAnalysisCompletedAsync(TrackAnalysisCompletedEvent evt)
    {
        if (evt.Success)
            _coverageRefreshSubject.OnNext(Unit.Default);

        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
        {
            TryFinalizeProcessingState();
            return;
        }

        if (evt.Success)
        {
            AnalysisTrackItem? refreshedTrack = null;
            if (_libraryService is not null)
            {
                try
                {
                    var entry = await _libraryService.FindLibraryEntryAsync(evt.TrackGlobalId).ConfigureAwait(true);
                    if (entry is not null)
                    {
                        refreshedTrack = MapLibraryEntryToTrack(entry);
                        refreshedTrack.IsInPlaylist = track.IsInPlaylist;
                    }
                }
                catch (Exception ex)
                {
                    // Keep status updates flowing; the row just keeps its previous values.
                    Serilog.Log.Debug(ex, "AnalysisPage: refresh lookup failed for {TrackId}", evt.TrackGlobalId);
                }
            }

            if (refreshedTrack is not null)
            {
                track.UpdateFrom(refreshedTrack);
            }

            track.AnalysisStatus = AnalysisRunStatus.Completed;
            track.ProgressPercent = 100;
            track.CurrentStep = "Completed";
            track.AnalysisError = null;
            track.LastAnalyzedAt = DateTime.UtcNow;
            track.ModelVersion ??= "essentia-live";

            _completedAnalysisRuns++;
            if (_trackAnalysisStartedAtUtc.TryGetValue(evt.TrackGlobalId, out var startedAt))
            {
                _trackAnalysisStartedAtUtc.Remove(evt.TrackGlobalId);
                _totalAnalysisSeconds += Math.Max(0.0, (DateTime.UtcNow - startedAt).TotalSeconds);
            }
        }
        else
        {
            track.AnalysisStatus = AnalysisRunStatus.Failed;
            track.ProgressPercent = 100;
            track.CurrentStep = "Analysis failed";
            track.AnalysisError = string.IsNullOrWhiteSpace(evt.ErrorMessage) ? "Analysis failed." : evt.ErrorMessage;
            _trackAnalysisStartedAtUtc.Remove(evt.TrackGlobalId);
        }

        track.IsInQueue = false;
        AnalysisQueue.Remove(track);

        if (string.Equals(CurrentProcessingTrackId, evt.TrackGlobalId, StringComparison.Ordinal))
        {
            CurrentProcessingTrackId = null;
        }

        TryFinalizeProcessingState();
        RefreshComputedState();
    }

    private void OnAnalysisQueueStatusChanged(AnalysisQueueStatusChangedEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.CurrentTrackHash))
        {
            CurrentProcessingTrackId = evt.CurrentTrackHash;
        }

        if (evt.QueuedCount > 0 || !string.IsNullOrWhiteSpace(evt.CurrentTrackHash))
        {
            ProcessingState = AnalysisProcessingState.Processing;
            return;
        }

        TryFinalizeProcessingState();
    }

    private AnalysisTrackItem? FindTrack(string trackId)
    {
        return AnalysisQueue.FirstOrDefault(t => t.TrackId == trackId)
            ?? LibraryTracks.FirstOrDefault(t => t.TrackId == trackId);
    }

    private void TryFinalizeProcessingState()
    {
        var hasPending = AnalysisQueue.Any(t => t.AnalysisStatus is AnalysisRunStatus.Queued or AnalysisRunStatus.Processing);
        if (hasPending)
        {
            ProcessingState = AnalysisProcessingState.Processing;
            return;
        }

        if (_analysisSessionStopwatch.IsRunning)
        {
            _analysisSessionStopwatch.Stop();
        }

        ProcessingState = _completedAnalysisRuns > 0
            ? AnalysisProcessingState.Completed
            : AnalysisProcessingState.Idle;
    }

    private void OnTrackAnalysisRequested(TrackAnalysisRequestedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.TrackGlobalId))
            return;

        // Exit early if already queued or processing to avoid loopbacks.
        if (AnalysisQueue.Any(t => t.TrackId == evt.TrackGlobalId))
            return;

        var track = LibraryTracks.FirstOrDefault(t => t.TrackId == evt.TrackGlobalId);
        if (track != null)
        {
            AddToQueue(track);
            return;
        }

        // A track this page hasn't loaded (e.g. one a folder import just added): look up the real
        // entry instead of inventing a row. This used to add an "Selected Artist — Selected Track"
        // placeholder — when the library query was failing (2026-10-19, fresh-install schema
        // drift) a 2,650-track folder import filled the page with 2,650 fake rows, every one then
        // marked "Analysis failed", hiding the real "Couldn't load your library" error.
        _ = QueueUnlistedTrackAsync(evt.TrackGlobalId);
    }

    private async Task QueueUnlistedTrackAsync(string trackHash)
    {
        // The library can't be read right now: the error state already says so, and every lookup
        // would fail the same way.
        if (_libraryService is null || HasLibraryLoadError || !_pendingQueueResolutions.Add(trackHash))
            return;

        try
        {
            var entry = await _libraryService.FindLibraryEntryAsync(trackHash).ConfigureAwait(true);
            if (entry is null)
            {
                Serilog.Log.Debug("AnalysisPage: analysis requested for {TrackId}, which is not in the library; not listing it", trackHash);
                return;
            }

            if (AnalysisQueue.Any(t => t.TrackId == trackHash))
                return;

            var track = LibraryTracks.FirstOrDefault(t => t.TrackId == trackHash);
            if (track is null)
            {
                track = MapLibraryEntryToTrack(entry);
                HookTrack(track);
                LibraryTracks.Add(track);
                ApplyFilter();
            }

            AddToQueue(track);
        }
        catch (Exception ex)
        {
            // The analysis itself still runs (AnalysisQueueService has the request); only this
            // page's row for it is missing.
            Serilog.Log.Warning(ex, "AnalysisPage: could not resolve requested track {TrackId}", trackHash);
        }
        finally
        {
            _pendingQueueResolutions.Remove(trackHash);
        }
    }

    private void OnFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileIngestionQueued(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Ingestion pending: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    private void OnFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileIngestionCompleted(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Indexed: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    private void OnFileMissingDetected(FileMissingDetectedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileMissingDetected(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Stale index detected: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    /// <summary>
    /// Fired by CorruptFileRemediationService (and anything else that removes a LibraryEntry)
    /// after the underlying row is gone. Removes the track from this page entirely rather than
    /// leaving its last-loaded waveform/BPM/analysis on screen looking current — a corrupt or
    /// missing track that was just wiped from the DB has nothing left to show here.
    /// </summary>
    private void OnLibraryEntryDeleted(LibraryEntryDeletedEvent evt)
    {
        var track = FindTrack(evt.UniqueHash);
        if (track is null)
            return;

        AnalysisQueue.Remove(track);
        LibraryTracks.Remove(track);
        AutomixStatusMessage = $"Removed {track.Artist} — {track.Title} (corrupt or missing file).";
        RefreshComputedState();
    }

    private void ApplyFilter(string? queryOverride = null)
    {
        _filterRequestSubject.OnNext(queryOverride ?? SearchText);
    }

    private void ApplyFilterDirect(string? queryOverride = null)
    {
        var timer = Stopwatch.StartNew();
        FilteredLibraryTracks.Clear();
        var activeQuery = queryOverride ?? SearchText;
        var query = string.IsNullOrWhiteSpace(activeQuery)
            ? LibraryTracks
            : LibraryTracks.Where(t =>
                t.Title.Contains(activeQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(activeQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var t in query)
            FilteredLibraryTracks.Add(t);

        timer.Stop();
        _lastFilterMilliseconds = timer.Elapsed.TotalMilliseconds;
        _filterSampleCount++;
        _averageFilterMilliseconds += (_lastFilterMilliseconds - _averageFilterMilliseconds) / _filterSampleCount;
        _filteredTrackCount = FilteredLibraryTracks.Count;

        this.RaisePropertyChanged(nameof(IsLibraryEmpty));
        this.RaisePropertyChanged(nameof(ShowNoLibraryTracks));
        this.RaisePropertyChanged(nameof(ShowNoSearchMatches));
        this.RaisePropertyChanged(nameof(FilterPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    private void RunPerformanceProbe()
    {
        var probeQueries = new[]
        {
            string.Empty,
            "a",
            "the",
            "mix",
            "house",
            "zzzz"
        };

        var samples = new List<double>(probeQueries.Length);
        var originalQuery = SearchText;

        foreach (var query in probeQueries)
        {
            var timer = Stopwatch.StartNew();
            ApplyFilter(query);
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }

        // Restore user-visible query results after the probe run.
        ApplyFilter(originalQuery);

        if (samples.Count == 0)
        {
            PerformanceProbeSummary = "Probe did not collect samples.";
            return;
        }

        var average = samples.Average();
        var max = samples.Max();
        var p95Index = Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1);
        var p95 = samples.OrderBy(x => x).ElementAt(p95Index);

        var previousP95 = PerformanceProbeRuns.Count > 0 ? PerformanceProbeRuns[0].P95Ms : (double?)null;
        var p95Delta = previousP95.HasValue ? p95 - previousP95.Value : (double?)null;

        PerformanceProbeSummary = NumFormat.Ui($"Probe {samples.Count} queries • avg {average:F1} ms • p95 {p95:F1} ms • max {max:F1} ms");
        PerformanceProbeRuns.Insert(0, new PerformanceProbeRun(DateTime.UtcNow, samples.Count, average, p95, max, p95Delta));
        while (PerformanceProbeRuns.Count > 10)
        {
            PerformanceProbeRuns.RemoveAt(PerformanceProbeRuns.Count - 1);
        }

        AutomixStatusMessage = "Developer performance probe completed.";
    }

    private void AddToQueueCore(AnalysisTrackItem track, bool refreshState, bool prioritize = false)
    {
        if (!AnalysisQueue.Any(t => t.TrackId == track.TrackId))
        {
            if (prioritize)
            {
                AnalysisQueue.Insert(0, track);
            }
            else
            {
                AnalysisQueue.Add(track);
            }
        }

        track.IsInQueue = true;
        track.AnalysisStatus = AnalysisRunStatus.Queued;
        track.ProgressPercent = 0;
        track.CurrentStep = "Queued for analysis";

        if (refreshState)
        {
            RefreshComputedState();
        }
    }

    private static void ResetTrackAnalysisState(AnalysisTrackItem track)
    {
        track.AnalysisData = null;
        track.AnalysisError = null;
        track.StemError = null;
        track.LastAnalyzedAt = null;
        track.ModelVersion = null;
    }

    private async Task LoadLibraryAsync()
    {
        if (_libraryService is null)
            return;

        IsLibraryLoading = true;
        LibraryLoadErrorMessage = null;
        try
        {

            // One shared full-library scan (entries + File.Exists sweep) instead of loading
            // and re-checking existence twice — ComputeSnapshotAsync does this same work
            // internally to derive the lifecycle metrics, so reuse its result here too.
            // ConfigureAwait(true): the rest fills LibraryTracks, which the page is bound to. With
            // (false) the import-completed reload resumed on a pool thread and filled the bound
            // ObservableCollection from there ("call from invalid thread" → the page showed
            // "Couldn't load your library" right after a folder import).
            var snapshot = await _lifecycleProjectionService.ComputeSnapshotAsync().ConfigureAwait(true);
            var existingEntries = snapshot.ExistingEntries;
            ApplyLifecycleMetrics(snapshot.Metrics);

            LibraryTracks.Clear();

            foreach (var entry in existingEntries
                .OrderByDescending(e => e.AddedAt)
                .Select(MapLibraryEntryToTrack))
            {
                HookTrack(entry);
                LibraryTracks.Add(entry);
            }

            if (snapshot.Metrics.StaleIndexed > 0)
            {
                AutomixStatusMessage = $"Loaded {existingEntries.Count} valid tracks; skipped {snapshot.Metrics.StaleIndexed} stale index entries.";
            }

            LibraryLoadErrorMessage = null;
            ApplyFilter();
            RefreshComputedState();
        }
        catch (Exception ex)
        {
            // Never substitute sample tracks for the user's collection: show the failure and
            // offer a retry instead (the page's error state binds to LibraryLoadErrorMessage).
            Serilog.Log.Warning(ex, "AnalysisPage: failed to load the library");
            LibraryLoadErrorMessage = $"Couldn't load your library: {ex.Message}";
        }
        finally
        {
            IsLibraryLoading = false;
            this.RaisePropertyChanged(nameof(ShowNoLibraryTracks));
            this.RaisePropertyChanged(nameof(ShowNoSearchMatches));
        }
    }

    private LifecycleMetrics GetCurrentLifecycleMetrics()
    {
        return new LifecycleMetrics(
            _onDiskIndexedTrackCount,
            _indexedCatalogCount,
            _staleIndexedCount,
            _ingestionBacklogCount,
            _desiredDownloadCount);
    }

    private void ApplyLifecycleMetrics(LifecycleMetrics metrics)
    {
        _onDiskIndexedTrackCount = Math.Max(0, metrics.PhysicalOnDisk);
        _indexedCatalogCount = Math.Max(0, metrics.IndexedCatalog);
        _staleIndexedCount = Math.Max(0, metrics.StaleIndexed);
        _ingestionBacklogCount = Math.Max(0, metrics.IngestionBacklog);
        _desiredDownloadCount = Math.Max(0, metrics.DesiredDownloads);
    }

    private AnalysisTrackItem MapLibraryEntryToTrack(LibraryEntry entry)
    {
        var hasRealAnalysis = entry.IsEnriched
            || entry.BPM.HasValue
            || !string.IsNullOrWhiteSpace(entry.MusicalKey)
            || !string.IsNullOrWhiteSpace(entry.PrimaryGenre)
            || entry.Energy.HasValue
            || entry.Valence.HasValue;

        var energyCurvePoints = ParseEnergyCurvePoints(entry.EnergyCurveJson, entry.Energy);
        var segmentedEnergyPoints = ParseSegmentedEnergyPoints(entry.SegmentedEnergyJson, energyCurvePoints);
        var genrePredictions = BuildGenrePredictions(entry);

        AnalysisData? analysisData = null;
        if (hasRealAnalysis)
        {
            var energyPercent = Math.Clamp((entry.Energy ?? 5) * 10.0, 0, 100);
            var valencePercent = Math.Clamp(((entry.Valence ?? 0.5) + 1.0) * 50.0, 0, 100);
            analysisData = new AnalysisData
            {
                Mechanics = new MechanicsData
                {
                    Bpm = entry.BPM ?? entry.SpotifyBPM ?? 0,
                    KeyScale = entry.MusicalKey ?? entry.SpotifyKey ?? string.Empty,
                    TonalProbability = Math.Clamp(entry.KeyConfidence ?? entry.QualityConfidence ?? 0.82, 0.1, 1.0)
                },
                Affective = new AffectiveData
                {
                    Arousal = Math.Clamp((energyPercent / 50.0) - 1.0, -1.0, 1.0),
                    Valence = Math.Clamp((valencePercent / 50.0) - 1.0, -1.0, 1.0)
                },
                Moods = new MoodData
                {
                    Happy = valencePercent,
                    Sad = 100 - valencePercent,
                    Aggressive = energyPercent,
                    Relaxed = 100 - energyPercent,
                    Party = Math.Clamp((energyPercent + valencePercent) / 2.0, 0, 100)
                },
                Genres = genrePredictions,
                Stems = new StemData { AreGenerated = false }
            };
        }

        return new AnalysisTrackItem(
            entry.UniqueHash,
            entry.Artist,
            entry.Title,
            album: entry.Album,
            bpm: entry.BPM,
            musicalKey: entry.MusicalKey,
            filePath: entry.FilePath,
            analysisData: analysisData,
            cueCount: ParseCueCount(entry.CuePointsJson),
            lowBand: entry.LowData,
            midBand: entry.MidData,
            highBand: entry.HighData,
            lastAnalyzedAt: entry.IsEnriched ? entry.AddedAt : null,
            modelVersion: entry.IsEnriched ? "library-cache" : null,
            energyCurvePoints: energyCurvePoints,
            segmentedEnergyPoints: segmentedEnergyPoints,
            dropTimeSeconds: entry.DropTimeSeconds,
            dropConfidence: entry.DropConfidence,
            qualityConfidence: entry.QualityConfidence,
            bpmStability: entry.BpmStability,
            vocalDensity: entry.VocalDensity,
            vocalTypeLabel: entry.VocalType.ToDisplayLabel(),
            loudnessLufs: entry.LoudnessLufs,
            camelotKey: entry.CamelotKey,
                chordProgression: entry.ChordProgression,
                durationSeconds: entry.DurationSeconds,
                measuredBpmConfidence: entry.BpmConfidence,
                measuredKeyConfidence: entry.KeyConfidence,
                measuredEnergy: entry.Energy,
                measuredValence: entry.Valence);
    }

    private static List<double> ParseSegmentedEnergyPoints(string? segmentedEnergyJson, IReadOnlyList<double> fallbackCurve)
    {
        if (!string.IsNullOrWhiteSpace(segmentedEnergyJson) && segmentedEnergyJson != "[]")
        {
            try
            {
                using var doc = JsonDocument.Parse(segmentedEnergyJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<double>();
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetDouble(out var value))
                        {
                            // Segmented energy is commonly 1-10; normalize to 0-1 for sparkline consistency.
                            points.Add(value > 1.0 ? Math.Clamp(value / 10.0, 0.0, 1.0) : Math.Clamp(value, 0.0, 1.0));
                        }
                    }

                    if (points.Count >= 2)
                    {
                        return points;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "AnalysisPage: malformed energy curve JSON; using fallback");
            }
        }

        if (fallbackCurve.Count >= 2)
        {
            return fallbackCurve.ToList();
        }

        return new List<double> { 0.3, 0.45, 0.6, 0.75, 0.68, 0.52, 0.40, 0.35 };
    }

    public void UpdateLayoutMode(double width)
    {
        var compact = width < 1180;
        IsCompactLayout = compact;

        if (!compact)
        {
            ActivePaneMode = AnalysisPaneMode.Queue;
        }
    }

    private void SetPaneMode(AnalysisPaneMode mode)
    {
        ActivePaneMode = mode;
    }

    private static List<double> ParseEnergyCurvePoints(string? energyCurveJson, double? fallbackEnergy)
    {
        if (!string.IsNullOrWhiteSpace(energyCurveJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(energyCurveJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<double>();
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetDouble(out var value))
                        {
                            points.Add(value);
                        }
                    }

                    if (points.Count >= 2)
                    {
                        return points;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "AnalysisPage: malformed curve JSON; using synthetic points");
            }
        }

        var baseEnergy = Math.Clamp(fallbackEnergy ?? 0.5, 0.0, 1.0);
        return new List<double>
        {
            Math.Clamp(baseEnergy * 0.65, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.85, 0.0, 1.0),
            Math.Clamp(baseEnergy * 1.05, 0.0, 1.0),
            Math.Clamp(baseEnergy * 1.15, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.95, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.80, 0.0, 1.0),
        };
    }

    private static List<GenrePrediction> BuildGenrePredictions(LibraryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.GenreDistributionJson) && entry.GenreDistributionJson != "{}")
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(entry.GenreDistributionJson);
                if (parsed != null && parsed.Count > 0)
                {
                    return parsed
                        .OrderByDescending(kvp => kvp.Value)
                        .Take(4)
                        .Select(kvp => new GenrePrediction
                        {
                            Label = kvp.Key,
                            Confidence = Math.Clamp(kvp.Value, 0.0, 1.0)
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "AnalysisPage: malformed genre distribution JSON; using fallback list");
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.PrimaryGenre) || !string.IsNullOrWhiteSpace(entry.Genres))
        {
            return (entry.PrimaryGenre ?? entry.Genres ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(3)
                .Select((label, index) => new GenrePrediction
                {
                    Label = label,
                    Confidence = Math.Max(0.35, 0.85 - (index * 0.15))
                })
                .ToList();
        }

        return new List<GenrePrediction>
        {
            new() { Label = "Analyzed", Confidence = 0.75 }
        };
    }

    private static int ParseCueCount(string? cuePointsJson)
    {
        if (string.IsNullOrWhiteSpace(cuePointsJson))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(cuePointsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "AnalysisPage: malformed cue points JSON; counting 0 cues");
            return 0;
        }
    }

    private void HookTrack(AnalysisTrackItem track)
    {
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => track.PropertyChanged += h,
                h => track.PropertyChanged -= h)
            .Subscribe(pattern =>
            {
                var args = pattern.EventArgs;
                if (args.PropertyName is nameof(AnalysisTrackItem.AnalysisData)
                    or nameof(AnalysisTrackItem.AnalysisStatus)
                    or nameof(AnalysisTrackItem.IsInQueue)
                    or nameof(AnalysisTrackItem.IsInPlaylist)
                    or nameof(AnalysisTrackItem.StemsReady)
                    or nameof(AnalysisTrackItem.AnalysisError)
                    or nameof(AnalysisTrackItem.StemError))
                {
                    RefreshComputedState();
                }
            })
            .DisposeWith(_disposables);
    }

    private void RefreshComputedState()
    {
        _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
    }

    private void RefreshComputedStateInternal()
    {
        this.RaisePropertyChanged(nameof(CanStartAnalysis));
        this.RaisePropertyChanged(nameof(IsQueueEmpty));
        this.RaisePropertyChanged(nameof(TotalTrackCount));
            this.RaisePropertyChanged(nameof(OnDiskIndexedTrackCount));
            this.RaisePropertyChanged(nameof(IndexedCatalogCount));
            this.RaisePropertyChanged(nameof(StaleIndexedCount));
            this.RaisePropertyChanged(nameof(DesiredDownloadCount));
            this.RaisePropertyChanged(nameof(IngestionBacklogCount));
        this.RaisePropertyChanged(nameof(AnalyzedTrackCount));
        this.RaisePropertyChanged(nameof(PendingTrackCount));
        this.RaisePropertyChanged(nameof(QueueTrackCount));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
        this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
        this.RaisePropertyChanged(nameof(HasIncompleteCorruptTracks));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisTracks));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        this.RaisePropertyChanged(nameof(StemsReadyCount));
        this.RaisePropertyChanged(nameof(HasQueueMetrics));
        this.RaisePropertyChanged(nameof(AvgAnalysisTimeDisplay));
        this.RaisePropertyChanged(nameof(ThroughputDisplay));
        this.RaisePropertyChanged(nameof(ElapsedTimeDisplay));
        this.RaisePropertyChanged(nameof(QueueMetricsSummary));
        this.RaisePropertyChanged(nameof(LibraryCountDifferentiationSummary));
        this.RaisePropertyChanged(nameof(CompletionRateDisplay));
        this.RaisePropertyChanged(nameof(FilterPerformanceDisplay));
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    /// <summary>
    /// Loads 10 mock tracks (3 with complete AnalysisData, 7 without)
    /// as per the Phase 1 requirement.
    /// </summary>
    private void LoadMockData()
    {
        // 3 tracks with pre-computed analysis data
        var analysed = new[]
        {
            new AnalysisTrackItem(
                "track-001", "Disclosure", "Latch",
                album: "Settle", bpm: 122.0, musicalKey: "4A",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 122.0, KeyScale = "4A", TonalProbability = 0.92 },
                    Affective = new() { Arousal = 0.65, Valence = 0.70 },
                    Moods = new() { Happy = 80, Sad = 10, Aggressive = 15, Relaxed = 30, Party = 75 },
                    Genres = new()
                    {
                        new() { Label = "House",    Confidence = 0.85 },
                        new() { Label = "UK Garage", Confidence = 0.55 },
                        new() { Label = "Electronic", Confidence = 0.40 }
                    },
                    Stems = new() { AreGenerated = false }
                }),

            new AnalysisTrackItem(
                "track-002", "Aphex Twin", "Windowlicker",
                album: "Windowlicker EP", bpm: 138.0, musicalKey: "1A",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 138.0, KeyScale = "1A", TonalProbability = 0.61 },
                    Affective = new() { Arousal = 0.82, Valence = -0.25 },
                    Moods = new() { Happy = 30, Sad = 20, Aggressive = 70, Relaxed = 5, Party = 60 },
                    Genres = new()
                    {
                        new() { Label = "IDM",      Confidence = 0.90 },
                        new() { Label = "Techno",   Confidence = 0.50 },
                        new() { Label = "Ambient",  Confidence = 0.20 }
                    },
                    Stems = new()
                    {
                        AreGenerated = true,
                        VocalsPath  = "/stems/windowlicker_vocals.flac",
                        DrumsPath   = "/stems/windowlicker_drums.flac",
                        BassPath    = "/stems/windowlicker_bass.flac",
                        OtherPath   = "/stems/windowlicker_other.flac"
                    }
                }),

            new AnalysisTrackItem(
                "track-003", "Four Tet", "Baby",
                album: "There Is Love in You", bpm: 106.0, musicalKey: "11B",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 106.0, KeyScale = "11B", TonalProbability = 0.88 },
                    Affective = new() { Arousal = 0.30, Valence = 0.55 },
                    Moods = new() { Happy = 65, Sad = 15, Aggressive = 5, Relaxed = 75, Party = 40 },
                    Genres = new()
                    {
                        new() { Label = "Electronica", Confidence = 0.82 },
                        new() { Label = "House",       Confidence = 0.45 },
                        new() { Label = "Ambient",     Confidence = 0.35 }
                    },
                    Stems = new() { AreGenerated = false }
                })
        };

        // 7 tracks without analysis data yet
        var unanalysed = new[]
        {
            new AnalysisTrackItem("track-004", "Bicep",        "Glue",     album: "Bicep",       bpm: 123.0, musicalKey: "7A"),
            new AnalysisTrackItem("track-005", "Jon Hopkins",  "Emerald",  album: "Immunity",    bpm: 130.0, musicalKey: "9B"),
            new AnalysisTrackItem("track-006", "Bonobo",       "Kiara",    album: "Black Sands", bpm: 118.0, musicalKey: "2A"),
            new AnalysisTrackItem("track-007", "Jamie xx",     "Loud Places", album: "In Colour", bpm: 124.0),
            new AnalysisTrackItem("track-008", "Floating Points", "Silhouettes", album: "Elaenia", bpm: 116.0),
            new AnalysisTrackItem("track-009", "Caribou",      "Can't Do Without You", album: "Our Love", bpm: 128.0, musicalKey: "6B"),
            new AnalysisTrackItem("track-010", "Nicolas Jaar", "Space Is Only Noise", album: "Space Is Only Noise", bpm: 98.0)
        };

        foreach (var t in analysed)
        {
            HookTrack(t);
            LibraryTracks.Add(t);
        }

        foreach (var t in unanalysed)
        {
            HookTrack(t);
            LibraryTracks.Add(t);
        }
    }

}
