using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Views;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Workstation;


/// <summary>
/// Session persistence, playlist loading, repair/relink, reanalysis, Rekordbox/ORB export, and disposal (#307).
/// </summary>
public sealed partial class WorkstationViewModel
{
    // ── Session persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Captures current deck state and writes it atomically to disk.
    /// Safe to fire-and-forget — errors are silently swallowed.
    /// </summary>
    public async Task SaveSessionAsync()
    {
        try
        {
            var session = new WorkstationSession
            {
                ActiveModeIndex     = (int)ActiveMode,
                ActivePlaylistId    = ActivePlaylist?.Id,
                TimelineOffsetSeconds = TimelineOffsetSeconds,
                TimelineWindowSeconds = TimelineWindowSeconds,
            };

            foreach (var deck in Decks)
            {
                session.Decks.Add(new WorkstationDeckState
                {
                    DeckLabel       = deck.DeckLabel,
                    FilePath        = deck.Deck.LoadedFilePath,
                    TrackUniqueHash = deck.TrackHash,
                    TrackTitle      = deck.TrackTitle,
                    TrackArtist     = deck.TrackArtist,
                    Bpm             = deck.DisplayBpm,
                    Key             = deck.TrackKey,
                    PositionSeconds = deck.Deck.PositionSeconds,
                });
            }

            await _sessionService.SaveAsync(session);
        }
        catch (Exception ex)
        {
            // Never crash the UI thread over a save failure, but a silent failure here means the
            // user's deck/timeline session is lost on next launch with zero diagnostic trail.
            _logger?.LogWarning(ex, "Failed to save workstation session");
        }
    }

    /// <summary>
    /// Called once on startup. Restores the last session if one exists.
    /// Tracks are reloaded by file path; cue points are re-fetched from the DB
    /// by <see cref="WorkstationDeckViewModel.LoadPlaylistTrackCommand"/>.
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        var session = await _sessionService.LoadAsync();
        if (session == null) return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                // Legacy sessions may point at Flow or other retired modes. Workstation always
                // restores professional DJ prep; Flow Builder keeps its own independent state.
                ActiveMode = WorkstationMode.Waveform;
                _pendingActivePlaylistId = session.ActivePlaylistId;
                TimelineOffsetSeconds = session.TimelineOffsetSeconds;
                TimelineWindowSeconds = session.TimelineWindowSeconds;

                foreach (var deckState in session.Decks)
                {
                    if (string.IsNullOrEmpty(deckState.FilePath)) continue;
                    if (!System.IO.File.Exists(deckState.FilePath)) continue;

                    var deck = Decks.FirstOrDefault(d => d.DeckLabel == deckState.DeckLabel)
                               ?? Decks.FirstOrDefault();
                    if (deck == null) continue;

                    // Use raw path load; cue points load via hash when available.
                    // AvailabilityState/Status must be set explicitly — the default (Ghost/Missing)
                    // is wrong here since File.Exists(deckState.FilePath) was just confirmed above.
                    // Leaving them at their zero-value defaults made LoadPlaylistTrackCommand treat
                    // a genuinely-local file as a "cloud/ghost" track on every single app restart,
                    // showing a bogus "queuing for download" toast and firing a real (if harmless)
                    // QueueTracks call for it each time.
                    var track = new Models.PlaylistTrack
                    {
                        Title            = deckState.TrackTitle ?? string.Empty,
                        Artist           = deckState.TrackArtist ?? string.Empty,
                        ResolvedFilePath = deckState.FilePath,
                        TrackUniqueHash  = deckState.TrackUniqueHash ?? string.Empty,
                        BPM              = deckState.Bpm > 0 ? deckState.Bpm : null,
                        MusicalKey       = deckState.Key,
                        Status           = Models.TrackStatus.Downloaded,
                        AvailabilityState = Models.TrackAvailabilityState.LocalUnanalyzed,
                    };
                    await deck.LoadPlaylistTrackCommand.Execute(track).FirstAsync();
                }
            }
            catch (Exception ex)
            {
                // Corrupt session — proceed with a blank workstation, but leave a trace.
                _logger?.LogWarning(ex, "Failed to restore workstation session; starting blank");
            }
        });
    }

    private async Task LoadPlaylistTracksSafelyAsync(PlaylistJob playlist)
    {
        try
        {
            await LoadPlaylistTracksAsync(playlist);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load Workstation playlist {PlaylistId}", playlist.Id);
            AnalysisStatusText = $"Could not load playlist tracks: {ex.Message}";
        }
    }

    private async Task LoadPlaylistsAsync()
    {
        var jobs = await _library.LoadAllPlaylistJobsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Playlists.Clear();
            foreach (var j in jobs) Playlists.Add(j);

            if (Playlists.Count == 0)
            {
                ActivePlaylist = null;
                return;
            }

            Guid? desiredId = _pendingActivePlaylistId;
            if (!desiredId.HasValue && Guid.TryParse(_appConfig.WorkstationActivePlaylistId, out var configuredId))
            {
                desiredId = configuredId;
            }

            if (desiredId.HasValue)
            {
                var matched = Playlists.FirstOrDefault(p => p.Id == desiredId.Value);
                if (matched != null)
                {
                    ActivePlaylist = matched;
                    _pendingActivePlaylistId = null;
                    return;
                }
            }

            if (ActivePlaylist == null)
            {
                ActivePlaylist = Playlists[0];
            }
        });
    }

    private async Task LoadPlaylistTracksAsync(PlaylistJob job, string? completionStatus = null)
    {
        var tracks = await _library.GetPagedPlaylistTracksAsync(
            job.Id, skip: 0, take: 1000);

        var readyTracks = tracks
            .Where(WorkstationDeckViewModel.IsTrackReadyForWorkstation)
            .ToList();
        var attentionTracks = tracks
            .Where(track => !WorkstationDeckViewModel.IsTrackReadyForWorkstation(track))
            .ToList();
        var attentionBreakdown = BuildHiddenEligibilityBreakdown(attentionTracks);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ActivePlaylist?.Id != job.Id) return;

            _lastHiddenTracks = attentionTracks;
            PlaylistTracks.Clear();
            foreach (var track in tracks) PlaylistTracks.Add(track);
            this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
            HiddenEligibilityBreakdown = attentionBreakdown;
            this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));

            AnalysisStatusText = completionStatus ?? (tracks.Count == 0
                ? "This playlist is empty. Import or acquire tracks to begin."
                : readyTracks.Count == 0
                    ? $"{tracks.Count} tracks need attention. Use each row's repair action; nothing has been hidden."
                    : attentionTracks.Count > 0
                        ? $"{readyTracks.Count} of {tracks.Count} tracks ready • {attentionTracks.Count} need attention below."
                        : $"{readyTracks.Count} tracks ready for Workstation.");
        });
    }

    private async Task RepairTrackAsync(PlaylistTrack track)
    {
        var issue = WorkstationDeckViewModel.GetTrackEligibilityIssue(track);
        switch (issue)
        {
            case WorkstationTrackEligibilityIssue.MissingFile:
                await RelinkMissingTrackAsync(track);
                break;
            case WorkstationTrackEligibilityIssue.MissingWaveform:
            case WorkstationTrackEligibilityIssue.MissingCues:
            case WorkstationTrackEligibilityIssue.MissingAnalysis:
                await RunCueAnalysisAsync(new List<PlaylistTrack> { track });
                break;
            case WorkstationTrackEligibilityIssue.NotDownloaded:
                _eventBus.Publish(new NavigateToPageEvent("Projects"));
                break;
            case WorkstationTrackEligibilityIssue.NoTrack:
                _eventBus.Publish(new NavigateToPageEvent("Library"));
                break;
            default:
                _eventBus.Publish(new NavigateToPageEvent("Import"));
                break;
        }
    }

    private async Task RelinkMissingTrackAsync(PlaylistTrack track)
    {
        if (_fileInteractionService == null)
        {
            AnalysisStatusText = "Locate is unavailable in this session. The track has not been changed.";
            return;
        }

        try
        {
            var result = await WorkstationTrackRelink.RelinkAsync(track, _library, _fileInteractionService);
            switch (result.Outcome)
            {
                case TrackRelinkOutcome.Cancelled:
                    AnalysisStatusText = $"Locate cancelled for {track.Artist} — {track.Title}; no changes were made.";
                    return;
                case TrackRelinkOutcome.InvalidFile:
                    AnalysisStatusText = "The selected file no longer exists. No library records were changed.";
                    if (_dialogService != null)
                        await _dialogService.ShowAlertAsync("Invalid file", "The selected file does not exist. Choose the moved audio file and try again.");
                    return;
                case TrackRelinkOutcome.MissingIdentity:
                    AnalysisStatusText = "This row needs its library identity rebuilt before it can be relinked safely.";
                    if (_dialogService != null)
                        await _dialogService.ShowAlertAsync("Relink unavailable", "This track has no stable library hash. Rebuild its library metadata before locating the file; no records were changed.");
                    return;
            }

            var successStatus = $"Relinked {track.Artist} — {track.Title}. Existing cues, grids, and analysis were preserved.";
            if (ActivePlaylist is { } playlist)
            {
                await LoadPlaylistTracksAsync(playlist, successStatus);
            }
            else
            {
                track.ResolvedFilePath = result.FilePath!;
                track.Status = TrackStatus.Downloaded;
                if (track.AvailabilityState is TrackAvailabilityState.Ghost
                    or TrackAvailabilityState.QueuedForDownload
                    or TrackAvailabilityState.Downloading)
                {
                    track.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
                }
                RaiseLaneActionProperties();
                AnalysisStatusText = successStatus;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to relink Workstation track {TrackId}", track.Id);
            AnalysisStatusText = $"Could not relink {track.Artist} — {track.Title}: {ex.Message}";
            if (_dialogService != null)
                await _dialogService.ShowAlertAsync("Relink failed", $"The library could not be updated: {ex.Message}");
        }
    }

    private async Task LoadTrackIntoDeckAsync(WorkstationDeckViewModel? deck, PlaylistTrack track)
    {
        if (deck == null)
        {
            return;
        }

        var readinessMessage = WorkstationDeckViewModel.GetTrackLoadReadinessMessage(track);
        if (!string.IsNullOrWhiteSpace(readinessMessage))
        {
            deck.TrackLoadError = readinessMessage;
            AnalysisStatusText = readinessMessage;
            return;
        }

        await deck.LoadPlaylistTrackCommand.Execute(track).FirstAsync();

        if (string.IsNullOrWhiteSpace(deck.TrackLoadError))
        {
            ApplySmartSnapForDeckDrop(deck);
            AnalysisStatusText = $"Loaded {track.Artist} — {track.Title} into Deck {deck.DeckLabel}.";
        }
    }

    private static string BuildHiddenEligibilityBreakdown(IReadOnlyCollection<PlaylistTrack> hiddenTracks)
    {
        if (hiddenTracks.Count == 0)
        {
            return string.Empty;
        }

        var missingDownload = 0;
        var missingFile = 0;
        var missingHash = 0;
        var missingWaveform = 0;
        var missingCues = 0;
        var other = 0;

        foreach (var track in hiddenTracks)
        {
            switch (WorkstationDeckViewModel.GetTrackEligibilityIssue(track))
            {
                case WorkstationTrackEligibilityIssue.NotDownloaded:
                    missingDownload++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingFile:
                    missingFile++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingHash:
                    missingHash++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingWaveform:
                    missingWaveform++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingCues:
                    missingCues++;
                    break;
                case WorkstationTrackEligibilityIssue.MissingAnalysis:
                case WorkstationTrackEligibilityIssue.NoTrack:
                    other++;
                    break;
            }
        }

        var segments = new List<string>();
        if (missingDownload > 0) segments.Add($"{missingDownload} not downloaded");
        if (missingFile > 0) segments.Add($"{missingFile} missing file");
        if (missingHash > 0) segments.Add($"{missingHash} missing hash");
        if (missingWaveform > 0) segments.Add($"{missingWaveform} missing waveform");
        if (missingCues > 0) segments.Add($"{missingCues} missing cues");
        if (other > 0) segments.Add($"{other} other prep issue");

        return segments.Count == 0
            ? string.Empty
            : $"Needs attention: {string.Join(" • ", segments)}.";
    }

    private static bool IsReanalysisCandidate(PlaylistTrack track)
    {
        return WorkstationDeckViewModel.GetTrackEligibilityIssue(track) switch
        {
            WorkstationTrackEligibilityIssue.MissingWaveform => true,
            WorkstationTrackEligibilityIssue.MissingCues => true,
            WorkstationTrackEligibilityIssue.MissingAnalysis => true,
            _ => false,
        };
    }

    private async Task QueueIncompleteTracksForReanalysisAsync()
    {
        if (ActivePlaylist == null)
        {
            AnalysisStatusText = "Select a playlist before queuing incomplete tracks for reanalysis.";
            return;
        }

        var sourceHiddenTracks = _lastHiddenTracks;
        if (sourceHiddenTracks.Count == 0)
        {
            var allTracks = await _library.GetPagedPlaylistTracksAsync(ActivePlaylist.Id, skip: 0, take: 1000);
            sourceHiddenTracks = allTracks
                .Where(track => !WorkstationDeckViewModel.IsTrackReadyForWorkstation(track))
                .ToList();
            _lastHiddenTracks = sourceHiddenTracks;
            this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }

        var candidates = sourceHiddenTracks
            .Where(IsReanalysisCandidate)
            .Where(track => !string.IsNullOrWhiteSpace(track.TrackUniqueHash))
            .ToList();

        foreach (var track in candidates)
        {
            _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackUniqueHash));
        }

        AnalysisStatusText = candidates.Count == 0
            ? "No incomplete tracks are currently eligible for reanalysis queueing."
            : $"Queued {candidates.Count} incomplete track(s) for full reanalysis.";
    }

    private async Task HandleFlowLaunchRequestAsync(AddToTimelineRequestEvent request)
    {
        var track = request.Tracks?.FirstOrDefault(item => item != null);
        if (track == null)
        {
            return;
        }

        // A deck-preparation request loads the first selected track for inspection. It must not
        // open or modify Flow Builder, whose transitions belong to in-app playlist playback.
        ActiveMode = WorkstationMode.Waveform;
        var targetDeck = FocusedDeck
            ?? Decks.FirstOrDefault(deck => deck.DeckLabel == "A")
            ?? Decks.FirstOrDefault();
        if (targetDeck == null)
        {
            return;
        }

        FocusedDeck = targetDeck;
        await LoadTrackIntoDeckAsync(targetDeck, track);
    }

    private async Task HandleWorkspaceOpenRequestAsync(OpenStemWorkspaceRequestEvent request)
    {
        if (request.Track == null)
        {
            return;
        }

        // The stem rack lives inside the Prep workspace now, so both paths land on Prep.
        ActiveMode = WorkstationMode.Waveform;

        // Keep the Library's playlist context with the track. This makes the prep list, readiness
        // counts, relink refresh, and Rekordbox preflight refer to the playlist the user came from.
        var sourcePlaylist = Playlists.FirstOrDefault(playlist => playlist.Id == request.Track.PlaylistId);
        if (sourcePlaylist != null && ActivePlaylist?.Id != sourcePlaylist.Id)
        {
            ActivePlaylist = sourcePlaylist;
        }

        var targetDeck = !string.IsNullOrWhiteSpace(request.PreferredDeck)
            ? Decks.FirstOrDefault(deck => string.Equals(deck.DeckLabel, request.PreferredDeck, StringComparison.OrdinalIgnoreCase))
            : FocusedDeck ?? Decks.FirstOrDefault(deck => deck.DeckLabel == "A") ?? Decks.FirstOrDefault();

        if (targetDeck == null)
        {
            return;
        }

        FocusedDeck = targetDeck;
        await LoadTrackIntoDeckAsync(targetDeck, request.Track);

        if (request.OpenStemRack && string.IsNullOrWhiteSpace(targetDeck.TrackLoadError))
        {
            AnalysisStatusText = $"Opening stems for {request.Track.Artist} — {request.Track.Title} on Deck {targetDeck.DeckLabel}.";
            await targetDeck.SeparateStemsCommand.Execute().FirstAsync();
        }
    }

    private async Task ExportRekordboxAsync()
    {
        var playlist = ActivePlaylist;
        if (playlist == null) return;

        if (_playlistExporter == null)
        {
            AnalysisStatusText = "Rekordbox export unavailable (export service not initialized).";
            return;
        }

        try
        {
            AnalysisStatusText = $"Checking \"{playlist.SourceTitle}\" for Rekordbox export…";

            // Export the playlist's saved order, not just the workstation-ready subset —
            // the handoff to DJ software should carry the whole crate.
            var tracks = await _library.LoadPlaylistTracksAsync(playlist.Id);
            var ordered = tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.TrackNumber).ToList();
            if (ordered.Count == 0)
            {
                AnalysisStatusText = "Rekordbox export blocked: this playlist is empty.";
                return;
            }

            var audioPreflight = await SLSKDONET.Services.Export.TrackExportPreflight.ScanAsync(ordered);
            if (audioPreflight.Problems.Count > 0)
            {
                var details = string.Join(Environment.NewLine, audioPreflight.Problems.Take(6).Select(problem => $"• {problem}"));
                var remainder = audioPreflight.Problems.Count > 6
                    ? $"{Environment.NewLine}• …and {audioPreflight.Problems.Count - 6} more."
                    : string.Empty;
                var message = $"{ordered.Count} tracks checked; {audioPreflight.ValidTracks.Count} readable; {audioPreflight.Problems.Count} need attention.{Environment.NewLine}{Environment.NewLine}{details}{remainder}{Environment.NewLine}{Environment.NewLine}Nothing was exported. Repair the listed files and try again.";
                AnalysisStatusText = $"Rekordbox export blocked: {audioPreflight.Problems.Count} of {ordered.Count} tracks need attention.";
                if (_dialogService != null)
                    await _dialogService.ShowAlertAsync("Rekordbox export preflight", message);
                return;
            }

            var integrity = await _playlistExporter.PreflightRekordboxExportAsync(ordered);
            if (!integrity.CanExport)
            {
                var details = string.Join(Environment.NewLine, integrity.Blockers.Take(8).Select(problem => $"• {problem}"));
                var remainder = integrity.Blockers.Count > 8
                    ? $"{Environment.NewLine}• …and {integrity.Blockers.Count - 8} more."
                    : string.Empty;
                var message = $"{integrity.Blockers.Count} integrity issue(s) would lose or corrupt saved preparation data.{Environment.NewLine}{Environment.NewLine}{details}{remainder}{Environment.NewLine}{Environment.NewLine}Nothing was exported. Repair the listed tracks in the Prep Playlist and try again.";
                AnalysisStatusText = $"Rekordbox export blocked: {integrity.Blockers.Count} grid, cue, or file integrity issue(s).";
                if (_dialogService != null)
                    await _dialogService.ShowAlertAsync("Rekordbox export blocked", message);
                return;
            }

            var safeName = string.Join("_", playlist.SourceTitle.Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (safeName.Length == 0) safeName = "orbit-playlist";
            var outputPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"{safeName}-rekordbox-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
            var needsCuePrep = ordered.Count(track => WorkstationDeckViewModel.GetTrackEligibilityIssue(track) is
                WorkstationTrackEligibilityIssue.MissingWaveform or
                WorkstationTrackEligibilityIssue.MissingCues or
                WorkstationTrackEligibilityIssue.MissingAnalysis);
            var warningDetails = integrity.Warnings.Count == 0
                ? "No export warnings."
                : $"{integrity.Warnings.Count} explicit export warning(s):{Environment.NewLine}" +
                  string.Join(Environment.NewLine, integrity.Warnings.Take(4).Select(warning => $"• {warning}")) +
                  (integrity.Warnings.Count > 4 ? $"{Environment.NewLine}• …and {integrity.Warnings.Count - 4} more." : string.Empty);
            var confirmation = $"Playlist: {playlist.SourceTitle}{Environment.NewLine}" +
                $"Included: {ordered.Count} playlist entries / {integrity.UniqueTrackCount} unique readable files, in saved order.{Environment.NewLine}" +
                $"Needs cue preparation: {needsCuePrep} tracks.{Environment.NewLine}" +
                $"{warningDetails}{Environment.NewLine}{Environment.NewLine}" +
                $"Destination: {outputPath}{Environment.NewLine}{Environment.NewLine}" +
                "Not included: Flow Builder transition designs. Those are used only by ORBIT playlist playback with MIX enabled.";
            if (_dialogService != null && !await _dialogService.ConfirmAsync(
                    "Export Rekordbox XML", confirmation, "Export XML", "Cancel"))
            {
                AnalysisStatusText = "Rekordbox export cancelled.";
                return;
            }

            AnalysisStatusText = $"Exporting {ordered.Count} playlist entries to Rekordbox XML…";
            await _playlistExporter.ExportToRekordboxXmlAsync(playlist.SourceTitle, ordered, outputPath, folderId: playlist.FolderId);

            AnalysisStatusText = integrity.Warnings.Count == 0
                ? $"Rekordbox XML exported and verified → {outputPath}"
                : $"Rekordbox XML exported with {integrity.Warnings.Count} acknowledged warning(s) → {outputPath}";
        }
        catch (SLSKDONET.Services.Export.RekordboxExportIntegrityException ex)
        {
            _logger?.LogWarning(ex, "Rekordbox export integrity guard stopped playlist {Playlist}", playlist.SourceTitle);
            AnalysisStatusText = "Rekordbox export blocked by the final integrity guard; nothing was overwritten.";
            if (_dialogService != null)
                await _dialogService.ShowAlertAsync("Rekordbox export blocked", ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rekordbox export failed for playlist {Playlist}", playlist.SourceTitle);
            AnalysisStatusText = $"Rekordbox export failed: {ex.Message}";
        }
    }

    private async Task OpenExportDialogAsync()
    {
        var loadedDecks = Decks.Where(deck => deck.IsLoaded).ToList();
        if (loadedDecks.Count == 0)
        {
            const string message = "Load at least one track onto a Prep Tracks deck before rendering. Render Mix does not render a Flow Builder playlist or its in-app MIX transitions.";
            AnalysisStatusText = message;
            if (_dialogService != null)
                await _dialogService.ShowAlertAsync("Render Mix", message);
            return;
        }

        var deckSummary = string.Join(", ", loadedDecks.Select(deck => $"Deck {deck.DeckLabel}"));
        if (_dialogService != null && !await _dialogService.ConfirmAsync(
                "Render loaded deck sources",
                $"Render the current audio and deck settings from: {deckSummary}.{Environment.NewLine}{Environment.NewLine}" +
                "This is not a Flow Builder render: playlist order and in-app MIX transition designs are not included.",
                "Continue", "Cancel"))
        {
            AnalysisStatusText = "Mix rendering cancelled.";
            return;
        }

        var vm = new ExportDialogViewModel(_mixdown);
        try
        {
            vm.SetDecks(Decks);
            // Show from UI thread — caller must be on UI thread (ReactiveCommand is)
            var owner = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
                ? lt.MainWindow : null;
            if (owner != null)
                await Views.Avalonia.Workstation.ExportDialog.ShowForWorkstationAsync(owner, vm);
        }
        finally
        {
            vm.Dispose();
        }
    }

    private async Task ExportOrbSessionAsync()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
            ? lt.MainWindow : null;
        if (owner == null) return;

        var dialog = new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save Orbit Session Bundle",
            SuggestedFileName = $"orbit-session-{DateTime.Now:yyyyMMdd-HHmm}.orbsession",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Orbit Session Bundle")
                {
                    Patterns = ["*.orbsession"],
                },
            ],
        };

        var file = await owner.StorageProvider.SaveFilePickerAsync(dialog);
        if (file == null) return;

        var session = new Models.Stem.WorkstationSession
        {
            ActiveModeIndex       = (int)ActiveMode,
            ActivePlaylistId      = ActivePlaylist?.Id,
            TimelineOffsetSeconds = TimelineOffsetSeconds,
            TimelineWindowSeconds = TimelineWindowSeconds,
        };
        foreach (var deck in Decks)
        {
            session.Decks.Add(new Models.Stem.WorkstationDeckState
            {
                DeckLabel        = deck.DeckLabel,
                FilePath         = deck.Deck.LoadedFilePath,
                TrackUniqueHash  = deck.TrackHash,
                TrackTitle       = deck.TrackTitle,
                TrackArtist      = deck.TrackArtist,
                Bpm              = deck.DisplayBpm,
                Key              = deck.TrackKey,
                PositionSeconds  = deck.Deck.Engine.PositionSeconds,
            });
        }

        await _orbBundleService.ExportAsync(session, PlaylistTracks.ToList(), file.Path.LocalPath);
    }

    // ── Cue auto-analysis ─────────────────────────────────────────────────────

    /// <summary>
    /// For each track in <paramref name="tracks"/>: skip those that already have
    /// cue points, then run <see cref="AnalyzeTrackStructureJob"/> for the rest.
    /// Progress and busy-state are reported on the UI thread so the button can
    /// show a spinner / progress text while running.
    /// </summary>
    private async Task RunCueAnalysisAsync(List<PlaylistTrack> tracks)
    {
        tracks = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.TrackUniqueHash))
            .Where(track => WorkstationDeckViewModel.GetTrackEligibilityIssue(track) is not
                (WorkstationTrackEligibilityIssue.NoTrack
                 or WorkstationTrackEligibilityIssue.NotDownloaded
                 or WorkstationTrackEligibilityIssue.MissingFile
                 or WorkstationTrackEligibilityIssue.MissingHash))
            .ToList();
        if (tracks.Count == 0)
        {
            AnalysisStatusText = "No local, hash-valid tracks in this selection can be analyzed. Acquire or import them first.";
            return;
        }

        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;

        IsAnalyzing = true;
        AnalysisProgress = 0;

        int done = 0;
        int skipped = 0;
        int total = tracks.Count;

        try
        {
            foreach (var track in tracks)
            {
                if (ct.IsCancellationRequested) break;

                string? hash = track.TrackUniqueHash;
                if (string.IsNullOrWhiteSpace(hash))
                {
                    done++;
                    continue;
                }

                // Skip tracks that already have cue data
                var existing = await _cueService.GetByTrackIdAsync(hash, ct);
                if (existing.Count > 0)
                {
                    skipped++;
                    done++;
                    AnalysisProgress = (int)(done * 100.0 / total);
                    AnalysisStatusText = $"Skipped {skipped} · {done}/{total}";
                    continue;
                }

                var shortHash = hash.Length > 8 ? hash[..8] : hash;
                AnalysisStatusText = $"Analyzing {track.Title ?? shortHash}… ({done + 1}/{total})";

                await _analyzeJob.ExecuteAsync(hash, ct);

                done++;
                AnalysisProgress = (int)(done * 100.0 / total);
            }

            AnalysisStatusText = ct.IsCancellationRequested
                ? $"Cancelled — {done} processed"
                : $"Done — {done - skipped} analyzed · {skipped} already had cues";

            if (ActivePlaylist is { } playlist)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => LoadPlaylistTracksSafelyAsync(playlist));
            }
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
