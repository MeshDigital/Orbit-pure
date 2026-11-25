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
/// Library integrity scan, corruption remediation, playlist analysis coverage, and automix staging (#307).
/// </summary>
public partial class AnalysisPageViewModel
{
    private async Task ScanLibraryIntegrityAsync()
    {
        if (_corruptionScanService is null || _isIntegrityScanRunning)
            return;

        IsIntegrityScanRunning = true;
        IntegrityScanProgress  = 0;
        IntegrityScanStatusText = "Starting integrity scan…";
        ScanIssues = Array.Empty<CorruptFileScanEntry>();
        RemediationStatusText = string.Empty;

        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                IntegrityScanProgress   = p.Total > 0 ? (int)((double)p.Done / p.Total * 100) : 0;
                IntegrityScanStatusText = $"Scanning {p.Done}/{p.Total}: {p.File}";
            });

            var result = await _corruptionScanService
                .ScanLibraryAsync(progress, _cts.Token)
                .ConfigureAwait(true);

            ScanIssues = result.Issues;
            IntegrityScanProgress   = 100;
            IntegrityScanStatusText =
                $"Scan complete — {result.Clean} clean, {result.Warned} warnings, {result.Fatal - result.Missing} corrupt, {result.Missing} missing";
            AutomixStatusMessage = IntegrityScanStatusText;

            // Fatal issues are never remediated automatically: deletion is irreversible and the
            // probe has produced false positives before, so the full list is surfaced for an
            // explicit confirmation first. Warnings stay untouched (isolated, recoverable decode
            // glitches — see AudioCorruptionScannerService).
            var fatalIssues = result.Issues.Where(i => i.Status == Data.Entities.CorruptionStatus.Fatal).ToList();
            if (fatalIssues.Count > 0)
            {
                await ConfirmAndRemediateAsync(fatalIssues).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            IntegrityScanStatusText = "Integrity scan cancelled.";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "AnalysisPage: library integrity scan failed");
            IntegrityScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsIntegrityScanRunning = false;
        }
    }

    /// <summary>
    /// Builds a remediation entry for a track this session's own analysis pass already flagged
    /// corrupt, reusing the same <see cref="CorruptFileRemediationService"/> path the "Scan
    /// Library Integrity" flow uses.
    /// </summary>
    private static CorruptFileScanEntry BuildScanEntry(AnalysisTrackItem track) => new(
        track.TrackId,
        track.FilePath ?? string.Empty,
        track.Artist,
        track.Title,
        CorruptionStatus.Fatal,
        track.AnalysisError ?? "Corrupt file detected during analysis");

    private async Task ConfirmAndRemediateAsync(IReadOnlyList<CorruptFileScanEntry> fatalIssues)
    {
        var corruptCount = fatalIssues.Count(e => !e.IsMissing);
        var missingCount = fatalIssues.Count(e => e.IsMissing);
        var items = fatalIssues.Select(e => $"{e.StatusBadge} — {e.FilePath}").ToList();
        var message =
            $"{corruptCount} corrupt and {missingCount} missing file(s) will be deleted from disk " +
            "and re-queued for download. The full list stays available in the File Integrity panel.";

        // Fail safe: without a dialog service (tests / headless) we can't get an explicit
        // confirmation, so we must not delete anything.
        var confirmed = _dialogService is not null
            && await _dialogService.ConfirmListAsync(
                "Scan library integrity",
                message,
                items,
                confirmLabel: "Delete & re-download",
                cancelLabel: "Keep files").ConfigureAwait(true);

        if (!confirmed)
        {
            IntegrityScanStatusText = $"{IntegrityScanStatusText} — {fatalIssues.Count} file(s) kept";
            AutomixStatusMessage = IntegrityScanStatusText;
            _eventBus.Publish(new ToastRequestedEvent(
                "Files kept",
                $"{fatalIssues.Count} file(s) left unchanged.",
                NotificationType.Information,
                TimeSpan.FromSeconds(6)));
            return;
        }

        var result = await RemediateCoreAsync(fatalIssues).ConfigureAwait(true);
        IntegrityScanStatusText = $"{IntegrityScanStatusText} — {RemediationStatusText}";
        AutomixStatusMessage = IntegrityScanStatusText;

        if (result is null)
        {
            // RemediationStatusText already reports the cancel/failure; don't emit a success toast.
            return;
        }

        if (result.HasErrors)
        {
            _eventBus.Publish(new ToastRequestedEvent(
                "Integrity remediation",
                $"{result.Processed - result.Errors.Count} of {result.Processed} fixed — {result.Errors.Count} failed.",
                NotificationType.Error,
                TimeSpan.FromSeconds(8)));
        }
        else
        {
            _eventBus.Publish(new ToastRequestedEvent(
                "Integrity remediation",
                $"{result.FilesDeleted} file(s) deleted and re-queued for download.",
                NotificationType.Success,
                TimeSpan.FromSeconds(6)));
        }
    }

    private async Task RemediateAsync(IReadOnlyList<CorruptFileScanEntry> entries)
    {
        await RemediateCoreAsync(entries).ConfigureAwait(true);
    }

    private async Task<RemediationResult?> RemediateCoreAsync(IReadOnlyList<CorruptFileScanEntry> entries)
    {
        if (_remediationService is null || entries.Count == 0 || _isRemediating)
            return null;

        IsRemediating = true;
        RemediationStatusText = $"Fixing {entries.Count} files…";

        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                RemediationStatusText = $"Fixing {p.Done}/{p.Total}: {p.File}";
            });

            var result = await _remediationService
                .RemediateAsync(entries, progress, _cts.Token)
                .ConfigureAwait(true);

            // Remove fixed entries from the issues list
            var fixedHashes = new HashSet<string>(entries.Select(e => e.Hash), StringComparer.Ordinal);
            ScanIssues = _scanIssues.Where(e => !fixedHashes.Contains(e.Hash)).ToList();

            RemediationStatusText = result.Summary;
            AutomixStatusMessage  = result.Summary;
            return result;
        }
        catch (OperationCanceledException)
        {
            RemediationStatusText = "Remediation cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "AnalysisPage: corrupt-file remediation failed");
            RemediationStatusText = $"Remediation failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsRemediating = false;
        }
    }


    // ── Playlist analysis coverage ────────────────────────────────────────
    // "How much of this set is actually analysed, and what exactly is missing?" — the question
    // the old page could not answer because every library row was mapped with a fabricated
    // AnalysisData and therefore always looked complete.

    private readonly AnalysisCoverageService? _coverageService;
    private readonly System.Reactive.Subjects.Subject<Unit> _coverageRefreshSubject = new();
    private PlaylistAnalysisCoverage? _coverage;
    private bool _isCoverageLoading;
    private string? _coverageError;
    private int _coverageRefreshVersion;

    /// <summary>Playlists offered in the picker, plus a "whole library" entry at the top.</summary>
    public ObservableCollection<PlaylistChoice> CoveragePlaylists { get; } = new();

    private PlaylistChoice? _selectedCoveragePlaylist;

    /// <summary>Which playlist the coverage card describes. Changing it reloads the card.</summary>
    public PlaylistChoice? SelectedCoveragePlaylist
    {
        get => _selectedCoveragePlaylist;
        set
        {
            if (ReferenceEquals(_selectedCoveragePlaylist, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedCoveragePlaylist, value);
            _ = RefreshCoverageAsync();
        }
    }

    /// <summary>One row per engine: how many tracks have it, how many do not, and a run action.</summary>
    public ObservableCollection<EngineCoverageRow> CoverageRows { get; } = new();

    public PlaylistAnalysisCoverage? Coverage
    {
        get => _coverage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _coverage, value);
            this.RaisePropertyChanged(nameof(HasCoverageData));
            this.RaisePropertyChanged(nameof(CoverageHeadline));
            this.RaisePropertyChanged(nameof(CoverageSummary));
            this.RaisePropertyChanged(nameof(CoverageOnDiskCount));
            this.RaisePropertyChanged(nameof(PlaylistMissingEngines));
            this.RaisePropertyChanged(nameof(CanRunMissingForPlaylist));
        }
    }

    public bool IsCoverageLoading
    {
        get => _isCoverageLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCoverageLoading, value);
            this.RaisePropertyChanged(nameof(CanRunMissingForPlaylist));
        }
    }

    public string? CoverageError
    {
        get => _coverageError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _coverageError, value);
            this.RaisePropertyChanged(nameof(HasCoverageError));
        }
    }

    public bool HasCoverageError => !string.IsNullOrWhiteSpace(CoverageError);

    public bool HasCoverageData => Coverage is not null;

    /// <summary>"92 of 140 tracks fully analysed" — the one number this card exists to show.</summary>
    public string CoverageHeadline => Coverage is null
        ? "No playlist selected"
        : $"{Coverage.FullyAnalyzedTracks} of {Coverage.TotalTracks} tracks fully analysed";

    /// <summary>The breakdown line under the headline.</summary>
    public string CoverageSummary => Coverage is null
        ? string.Empty
        : Coverage switch
        {
            { TotalTracks: 0 } => "No tracks with audio on disk in this selection yet.",
            { IsComplete: true } => "Nothing missing — every engine has current output for every track.",
            { OutdatedWaveformTracks: > 0 } =>
                $"{Coverage.PartiallyAnalyzedTracks} partially analysed · {Coverage.UnanalysedTracks} not analysed at all · " +
                $"{Coverage.OutdatedWaveformTracks} with an outdated waveform",
            _ => $"{Coverage.PartiallyAnalyzedTracks} partially analysed · {Coverage.UnanalysedTracks} not analysed at all",
        };

    /// <summary>How many tracks in the selection are downloaded and on disk (the measurable set).</summary>
    public int CoverageOnDiskCount => Coverage?.TotalTracks ?? 0;

    /// <summary>Every engine missing somewhere in this playlist.</summary>
    public AnalysisEngine PlaylistMissingEngines => Coverage?.MissingAnywhere ?? AnalysisEngine.None;

    public bool CanRunMissingForPlaylist =>
        _coverageService is not null && PlaylistMissingEngines != AnalysisEngine.None && !IsCoverageLoading;

    public ReactiveCommand<Unit, Unit> RefreshCoverageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RunMissingForPlaylistCommand { get; private set; } = null!;
    public ReactiveCommand<AnalysisTrackItem, Unit> RunMissingForTrackCommand { get; private set; } = null!;
    public ReactiveCommand<EngineCoverageRow, Unit> RunEngineForPlaylistCommand { get; private set; } = null!;

    private async Task RefreshCoverageAsync()
    {
        if (_coverageService is null) return;

        var version = ++_coverageRefreshVersion;
        var selection = SelectedCoveragePlaylist;
        IsCoverageLoading = true;
        CoverageError = null;
        try
        {
            var coverage = selection?.PlaylistId is { } playlistId
                ? await _coverageService.GetPlaylistCoverageAsync(playlistId, selection.Name).ConfigureAwait(true)
                : await _coverageService.GetLibraryCoverageAsync().ConfigureAwait(true);

            // A slower response for an old playlist must not overwrite the newer selection.
            if (version != _coverageRefreshVersion || !ReferenceEquals(selection, SelectedCoveragePlaylist))
                return;

            Coverage = coverage;
            RebuildCoverageRows();
            ApplyCoverageToTracks();
        }
        catch (Exception ex)
        {
            if (version == _coverageRefreshVersion)
                CoverageError = ex.Message;
        }
        finally
        {
            if (version == _coverageRefreshVersion)
            {
                IsCoverageLoading = false;
                this.RaisePropertyChanged(nameof(CanRunMissingForPlaylist));
            }
        }
    }

    private void RebuildCoverageRows()
    {
        CoverageRows.Clear();
        if (Coverage is null) return;

        foreach (var summary in Coverage.EngineRows)
            CoverageRows.Add(new EngineCoverageRow(summary));
    }

    /// <summary>
    /// Pushes measured coverage onto the library rows, matched by track hash. The library list and
    /// a playlist are different populations in different orders, so positional matching would
    /// attribute one track's gaps to another.
    /// </summary>
    private void ApplyCoverageToTracks()
    {
        if (Coverage is null) return;

        foreach (var item in LibraryTracks)
            item.Coverage = Coverage.ForTrack(item.TrackId);
    }

    /// <summary>Queues only the selected coverage population and each track's own missing engines.</summary>
    private void QueueCoverageRequests(IEnumerable<TrackCoverageRequest> requests)
    {
        foreach (var request in requests)
        {
            var item = LibraryTracks.FirstOrDefault(t =>
                string.Equals(t.TrackId, request.TrackHash, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                // The selected playlist can contain tracks outside the currently loaded page.
                // The normal request handler resolves the real library entry instead of inventing one.
                _eventBus.Publish(new TrackAnalysisRequestedEvent(
                    request.TrackHash, AnalysisTier.Tier1, false, request.Engines));
                continue;
            }

            QueueEngineRequest(item, request.Engines);
        }

        NotifyAnalysisQueueChanged();
    }

    /// <summary>Queues the given engines for a set of tracks, reusing the normal analysis queue.</summary>
    private void QueueEngines(IEnumerable<AnalysisTrackItem> tracks, AnalysisEngine engines)
    {
        if (engines == AnalysisEngine.None) return;

        foreach (var item in tracks)
            QueueEngineRequest(item, engines);

        NotifyAnalysisQueueChanged();
    }

    private void QueueEngineRequest(AnalysisTrackItem item, AnalysisEngine engines)
    {
        var requested = item.HasCoverage ? item.RunnableMissingEngines & engines : engines;
        if (requested == AnalysisEngine.None) return;

        if (!AnalysisQueue.Contains(item))
        {
            item.AnalysisStatus = AnalysisRunStatus.Queued;
            AnalysisQueue.Add(item);
        }

        item.IsInQueue = true;
        _eventBus.Publish(new TrackAnalysisRequestedEvent(item.TrackId, AnalysisTier.Tier1, false, requested));
    }

    private void NotifyAnalysisQueueChanged()
    {
        this.RaisePropertyChanged(nameof(QueueTrackCount));
        this.RaisePropertyChanged(nameof(IsQueueEmpty));
        RefreshComputedStateInternal();
    }

    /// <summary>Loads the playlist picker, then the coverage for the current selection.</summary>
    private async Task LoadCoveragePlaylistsAsync()
    {
        try
        {
            CoveragePlaylists.Clear();
            CoveragePlaylists.Add(PlaylistChoice.Library);

            if (_libraryService is not null)
            {
                var jobs = await _libraryService.LoadAllPlaylistJobsAsync().ConfigureAwait(true);
                foreach (var job in jobs.Where(j => !j.IsDeleted).OrderByDescending(j => j.DateUpdated))
                {
                    CoveragePlaylists.Add(new PlaylistChoice(job.Id, job.SourceTitle, job.PlaylistTracks?.Count ?? 0));
                }
            }

            _selectedCoveragePlaylist = CoveragePlaylists.Count > 0 ? CoveragePlaylists[0] : null;
            this.RaisePropertyChanged(nameof(SelectedCoveragePlaylist));
            await RefreshCoverageAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CoverageError = ex.Message;
        }
    }

    /// <summary>Queues every engine that is missing anywhere in the selected playlist.</summary>
    private Task RunMissingForPlaylistAsync()
    {
        QueueCoverageRequests(Coverage?.GetRequests(PlaylistMissingEngines) ?? Array.Empty<TrackCoverageRequest>());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Force-queues every track in the selected playlist for a full re-analysis, bypassing the
    /// "already analysed" completeness check — the playlist-scoped counterpart to
    /// <see cref="ReanalyzeAllTracksAsync"/>. Use it after a detection-pipeline change, or
    /// whenever one set's stored analysis looks stale and you want it redone from scratch.
    /// </summary>
    public async Task ReanalyzePlaylistAsync()
    {
        var coverage = Coverage;
        if (coverage is null || coverage.TotalTracks == 0)
        {
            AutomixStatusMessage = "No playlist selected, or no tracks with audio on disk to re-analyse.";
            return;
        }

        var hashes = coverage.Tracks
            .Select(t => t.TrackHash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int batchCount = 0;
        foreach (var hash in hashes)
        {
            var item = LibraryTracks.FirstOrDefault(t =>
                string.Equals(t.TrackId, hash, StringComparison.OrdinalIgnoreCase));

            if (item is not null)
            {
                ResetTrackAnalysisState(item);
                AddToQueueCore(item, refreshState: false);
            }

            _eventBus.Publish(new TrackAnalysisRequestedEvent(hash, AnalysisTier.Tier1));

            if (++batchCount % 10 == 0)
                await Task.Yield();
        }

        AutomixStatusMessage = $"Queued {hashes.Count} track(s) in '{coverage.PlaylistName}' for full re-analysis.";
        RefreshComputedState();
    }

    // ── Automix flow (#43) ────────────────────────────────────────────────────

    /// <summary>
    /// Toggles membership of <paramref name="track"/> in the automix playlist.
    /// The track must have completed analysis to be eligible.
    /// </summary>
    public void TogglePlaylist(AnalysisTrackItem track)
    {
        if (track.IsInPlaylist)
        {
            PlaylistTracks.Remove(track);
            track.IsInPlaylist = false;
        }
        else if (track.HasAnalysis)
        {
            PlaylistTracks.Add(track);
            track.IsInPlaylist = true;
        }
    }

    public void StageAllAnalyzedForAutomix()
    {
        PlaylistTracks.Clear();
        foreach (var track in LibraryTracks.Where(t => t.HasAnalysis).OrderBy(t => t.Artist).ThenBy(t => t.Title))
        {
            if (!PlaylistTracks.Contains(track))
            {
                PlaylistTracks.Add(track);
            }

            track.IsInPlaylist = true;
        }

        AutomixStatusMessage = PlaylistTracks.Count == 0
            ? "No analyzed tracks available for staging yet."
            : $"Staged {PlaylistTracks.Count} analyzed track(s) for automix.";
        RefreshComputedState();
    }

    public void ClearAutomixStaging()
    {
        foreach (var track in PlaylistTracks)
        {
            track.IsInPlaylist = false;
        }

        PlaylistTracks.Clear();
        AutomixStatusMessage = "Automix staging cleared.";
        RefreshComputedState();
    }

}
