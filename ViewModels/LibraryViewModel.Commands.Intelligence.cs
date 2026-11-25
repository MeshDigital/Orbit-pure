using System;
using System.IO;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Services.Models;
using SLSKDONET.Services;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;
using SLSKDONET.Events;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels;


/// <summary>
/// Flow builder, playlist combination, bridge finder, Smart Insert, and Saved Doubles commands (#307).
/// </summary>
public partial class LibraryViewModel
{
    private void ExecuteOpenFlowBuilder()
    {
        try
        {
            // Flow Builder edits the selected playlist's in-app MIX transitions. It is not a
            // Workstation timeline handoff and a temporary track selection must not change that.
            var playlist = SelectedProject;
            var playlistId = playlist?.Id ?? Guid.Empty;
            // The actual Flow Builder handler reports the outcome after the playlist has loaded.
            // Publishing the request is not itself proof that the handoff succeeded.
            _eventBus.Publish(new OpenFlowBuilderForPlaylistEvent(playlistId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open flow builder");
            _notificationService.Show("Flow Builder Failed", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>
    /// Combines the selected library playlists directly, without navigating away from the Library.
    /// </summary>
    private async Task ExecuteCombineSelectedPlaylistsAsync()
    {
        var selectedPlaylists = Projects.SelectedTreeNodes
            .OfType<PlaylistTreeCardNodeViewModel>()
            .Select(node => node.Card.Model)
            .ToList();

        if (selectedPlaylists.Count < 2)
        {
            _notificationService.Show(
                "Combine Playlists",
                "Select 2 or more playlists first (Ctrl/Shift-click in the sidebar).",
                NotificationType.Warning);
            return;
        }

        try
        {
            var availablePlaylists = await _libraryService.LoadAllPlaylistJobsAsync();
            var availableIds = availablePlaylists.Select(playlist => playlist.Id).ToHashSet();
            availablePlaylists.AddRange(
                selectedPlaylists.Where(playlist => availableIds.Add(playlist.Id)));
            var dialogResult = await _dialogService.ShowCombinePlaylistsDialogAsync(
                availablePlaylists,
                selectedPlaylists);
            if (dialogResult == null || !dialogResult.IsConfirmed)
                return;

            if (dialogResult.SelectedPlaylists.Count < 2)
            {
                _notificationService.Show(
                    "Combine Playlists",
                    "Select at least two playlists to combine.",
                    NotificationType.Warning);
                return;
            }

            var sourcePlaylistTracks = new List<List<PlaylistTrack>>();
            foreach (var playlist in dialogResult.SelectedPlaylists)
            {
                // Combining playlists must preserve their tracks even if they are not yet
                // prepared for workstation playback.
                sourcePlaylistTracks.Add(await _libraryService.LoadPlaylistTracksAsync(playlist.Id));
            }
            var combinedTracks = PlaylistCombinePersistence.CollectTracks(
                sourcePlaylistTracks,
                dialogResult.SkipDuplicateTracks);

            if (combinedTracks.Count == 0)
            {
                _notificationService.Show(
                    "Combine Playlists",
                    "The selected playlists do not contain any tracks to combine.",
                    NotificationType.Warning);
                return;
            }

            var orderedTracks = combinedTracks;
            string? optimizationWarning = null;
            var hashes = combinedTracks
                .Select(track => track.TrackUniqueHash ?? string.Empty)
                .Where(hash => hash.Length > 0)
                .ToList();
            var optimizer = _serviceProvider?.GetService(typeof(Services.Playlist.PlaylistOptimizer))
                as Services.Playlist.PlaylistOptimizer;
            if (optimizer != null && hashes.Count > 0)
            {
                try
                {
                    var result = await optimizer.OptimizeAsync(hashes);
                    orderedTracks = FlowBuilderViewModel.ExpandOrderedTracksWithDuplicates(
                        result.OrderedHashes,
                        combinedTracks);
                    if (result.OrderedHashes.Count == 0)
                        optimizationWarning = "No transition order was available; source order was preserved.";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not optimize combined playlist; preserving source order");
                    optimizationWarning = "Transition optimization was unavailable; source order was preserved.";
                }
            }
            else
            {
                optimizationWarning = "Transition optimization was unavailable; source order was preserved.";
            }

            var newPlaylist = await PlaylistCombinePersistence.SaveAsync(
                _libraryService,
                dialogResult.NewPlaylistName,
                orderedTracks);

            await Projects.LoadProjectsAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Projects.SelectedProject = Projects.AllProjects.FirstOrDefault(
                    playlist => playlist.Id == newPlaylist.Id);
            });

            var message = $"Created \"{newPlaylist.SourceTitle}\" with {orderedTracks.Count} tracks.";
            if (!string.IsNullOrEmpty(optimizationWarning))
                message += $" {optimizationWarning}";
            _notificationService.Show(
                "Playlists Combined",
                message,
                string.IsNullOrEmpty(optimizationWarning)
                    ? NotificationType.Success
                    : NotificationType.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to combine selected playlists");
            _notificationService.Show("Combine Playlists Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteExportMonthlyDropAsync()
    {
        try
        {
            if (Tracks.SelectedTracks.Count == 0)
            {
                _notificationService.Show(
                    "Monthly Drop Export",
                    "Select tracks first, then export your monthly drop.",
                    NotificationType.Information);
                return;
            }

            await ExecuteBatchExportRekordboxAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monthly drop export failed");
            _notificationService.Show("Monthly Drop Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private void ExecuteFindBridgeBetweenSelected()
    {
        try
        {
            var selected = Tracks.SelectedTracks
                .Where(t => t.Model != null && !string.IsNullOrWhiteSpace(t.Model.TrackUniqueHash))
                .Take(2)
                .ToList();

            if (selected.Count < 2)
            {
                _notificationService.Show(
                    "Bridge Finder",
                    "Select exactly two tracks in Library first.",
                    NotificationType.Information);
                return;
            }

            var from = selected[0];
            var to = selected[1];

            ReactiveUI.MessageBus.Current.SendMessage(
                new FindBridgeBetweenTracksEvent(
                    from.Model.TrackUniqueHash,
                    to.Model.TrackUniqueHash,
                    $"{from.ArtistName} - {from.TrackTitle}",
                    $"{to.ArtistName} - {to.TrackTitle}"));

            _notificationService.Show(
                "Bridge Finder",
                $"Searching bridge tracks between \"{from.TrackTitle}\" and \"{to.TrackTitle}\" in Similar Tracks panel.",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger bridge finder from selection");
            _notificationService.Show("Bridge Finder Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteSmartInsertBetweenSelectedAsync()
    {
        try
        {
            if (_playlistIntelligenceService is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Playlist intelligence service is unavailable.",
                    NotificationType.Error);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var selected = Tracks.SelectedTracks
                .Where(t => t.Model != null && !string.IsNullOrWhiteSpace(t.Model.TrackUniqueHash))
                .OrderBy(t => t.SortOrder)
                .Take(2)
                .ToList();

            if (selected.Count < 2)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select two playlist tracks to insert a best-fit bridge between them.",
                    NotificationType.Information);
                return;
            }

            var from = selected[0].Model;
            var to = selected[1].Model;
            if (from == null || to == null)
            {
                return;
            }

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            if (string.Equals(from.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase))
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select two different tracks.",
                    NotificationType.Information);
                return;
            }

            IsLoading = true;

            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, from, to);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                insertResult.Message,
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart insert between selected tracks failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteSmartInsertAfterTrackAsync(object? parameter)
    {
        try
        {
            if (parameter is not PlaylistTrackViewModel anchorVm || anchorVm.Model is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Choose a track row first.",
                    NotificationType.Information);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            var fromIndex = orderedProjectTracks.FindIndex(t => t.Id == anchorVm.Model.Id);
            if (fromIndex < 0)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "That track is not in the active playlist ordering.",
                    NotificationType.Warning);
                return;
            }

            if (fromIndex >= orderedProjectTracks.Count - 1)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "No next track exists after this row.",
                    NotificationType.Information);
                return;
            }

            var from = orderedProjectTracks[fromIndex];
            var to = orderedProjectTracks[fromIndex + 1];

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            IsLoading = true;

            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, from, to);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                $"{insertResult.Message} (after \"{from.Title}\").",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart insert after track failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteFavoriteSelectedPairAsDoubleAsync()
    {
        var first = DoubleInspector.TrackA;
        var second = DoubleInspector.TrackB;
        if (first is null || second is null)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        first.IsLiked = true;
        second.IsLiked = true;

        if (_savedDoublesService is null)
        {
            _notificationService.Show(
                "Double Inspector",
                "Saved doubles persistence is unavailable in this session.",
                NotificationType.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(first.GlobalId) || string.IsNullOrWhiteSpace(second.GlobalId))
        {
            _notificationService.Show(
                "Double Inspector",
                "Selected tracks need stable IDs before saving.",
                NotificationType.Information);
            return;
        }

        var (trackA, trackB) = SavedDoublesService.Normalize(first.GlobalId, second.GlobalId);
        double? score = DoubleInspector.HasPairContext ? DoubleInspector.TransitionScore : null;
        var saved = new SavedDouble(trackA, trackB, DateTime.UtcNow, score, null);
        await _savedDoublesService.AddOrUpdateAsync(saved);
        await RefreshSavedDoublesAsync();

        _notificationService.Show(
            "Double Inspector",
            "Saved as favorite pair in Library.",
            NotificationType.Success);
    }

    private void ExecuteActivateSavedDouble(object? parameter)
    {
        if (parameter is not Library.SavedDoubleViewModel saved)
            return;

        SelectTrackPair(saved.TrackA, saved.TrackB);
    }

    private async Task ExecuteRemoveSavedDoubleAsync(object? parameter)
    {
        if (_savedDoublesService is null || parameter is not Library.SavedDoubleViewModel saved)
            return;

        await _savedDoublesService.RemoveAsync(saved.Model);
        await RefreshSavedDoublesAsync();
    }

    private void SelectTrackPair(PlaylistTrackViewModel first, PlaylistTrackViewModel second)
    {
        foreach (var selected in Tracks.SelectedTracks.ToList())
            selected.IsSelected = false;

        first.IsSelected = true;
        second.IsSelected = true;
        Tracks.UpdateSelection(new[] { first, second });
    }

    private void ExecuteOpenSelectedPairInWorkstation()
    {
        var selectedModels = Tracks.SelectedTracks
            .Take(2)
            .Where(track => track.Model != null)
            .Select(track => track.Model)
            .ToList();

        if (selectedModels.Count < 2)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        _eventBus.Publish(new AddToTimelineRequestEvent(selectedModels));
        _notificationService.Show(
            "Double Inspector",
            "Sent selected pair to Workstation timeline.",
            NotificationType.Success);
    }

    private void ExecuteAnalyzeSelectedPair()
    {
        var selected = Tracks.SelectedTracks.Take(2).ToList();
        if (selected.Count < 2)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        var queued = 0;
        foreach (var track in selected)
        {
            if (track.AnalyzeTrackCommand.CanExecute(null))
            {
                track.AnalyzeTrackCommand.Execute(null);
                queued++;
            }
        }

        if (queued == 0)
        {
            _notificationService.Show(
                "Double Inspector",
                "Both tracks need to be downloaded before analysis can start.",
                NotificationType.Information);
            return;
        }

        _notificationService.Show(
            "Double Inspector",
            queued == 1 ? "Queued analysis for one track." : "Queued analysis for both tracks.",
            NotificationType.Success);
    }

    private async Task ExecutePrepareSmartInsertAfterTrackAsync(object? parameter)
    {
        try
        {
            if (parameter is SmartInsertContextRequest contextRequest
                && contextRequest.FromTrack.Model is PlaylistTrack fromModel
                && contextRequest.ToTrack.Model is PlaylistTrack toModel)
            {
                FocusLibraryIntelligenceTab("SmartInsert");
                Intelligence.SetSmartInsertPairContext(fromModel, toModel);

                _notificationService.Show(
                    "Smart Insert",
                    $"Context ready between \"{fromModel.Title}\" and \"{toModel.Title}\".",
                    NotificationType.Information);
                return;
            }

            if (parameter is not PlaylistTrackViewModel anchorVm || anchorVm.Model is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Choose a track row first.",
                    NotificationType.Information);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            var fromIndex = orderedProjectTracks.FindIndex(t => t.Id == anchorVm.Model.Id);
            if (fromIndex < 0)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "That track is not in the active playlist ordering.",
                    NotificationType.Warning);
                return;
            }

            if (fromIndex >= orderedProjectTracks.Count - 1)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "No next track exists after this row.",
                    NotificationType.Information);
                return;
            }

            var from = orderedProjectTracks[fromIndex];
            var to = orderedProjectTracks[fromIndex + 1];

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            _notificationService.Show(
                "Smart Insert",
                $"Context ready between \"{from.Title}\" and \"{to.Title}\".",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing smart insert context from row failed");
            _notificationService.Show("Smart Insert", "Failed to prepare insert context.", NotificationType.Error);
        }
    }

    private async Task ExecuteApplyPreparedSmartInsertAsync()
    {
        try
        {
            if (_playlistIntelligenceService is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Playlist intelligence service is unavailable.",
                    NotificationType.Error);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            if (!Intelligence.TryGetPendingSmartInsertContext(out var pendingFromTrack, out var pendingToTrack)
                || pendingFromTrack is null
                || pendingToTrack is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Prepare a Smart Insert context first.",
                    NotificationType.Information);
                return;
            }

            IsLoading = true;
            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, pendingFromTrack, pendingToTrack);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                $"{insertResult.Message} (explicit apply).",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explicit Smart Insert apply failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal Task ApplyPreparedSmartInsertFromIntelligenceAsync()
        => ExecuteApplyPreparedSmartInsertAsync();

    private async Task<SmartInsertResult> ExecuteSmartInsertBetweenCoreAsync(PlaylistJob project, PlaylistTrack from, PlaylistTrack to)
    {
        var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.AddedAt)
            .ToList();

        var fromIndex = orderedProjectTracks.FindIndex(t => string.Equals(t.TrackUniqueHash, from.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
        var toIndex = orderedProjectTracks.FindIndex(t => string.Equals(t.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
        if (fromIndex < 0 || toIndex < 0)
        {
            return new SmartInsertResult(false, "Selected tracks were not found in current playlist order.", NotificationType.Warning);
        }

        var existingHashes = new HashSet<string>(
            orderedProjectTracks.Select(t => t.TrackUniqueHash),
            StringComparer.OrdinalIgnoreCase);

        var candidateHashes = (await _libraryService.LoadDownloadedTracksAsync())
            .Select(e => e.UniqueHash)
            .Where(h => !string.IsNullOrWhiteSpace(h) && !existingHashes.Contains(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1200)
            .ToList();

        if (candidateHashes.Count == 0)
        {
            return new SmartInsertResult(false, "No external candidate tracks are available to insert.", NotificationType.Information);
        }

        // Smart Insert scores candidates against the two anchor tracks' audio fingerprints —
        // if either hasn't been analyzed yet, InsertBetweenAsync returns an empty list with no
        // way to tell "not analyzed" apart from "no good match", so check that distinctly here.
        var fromHasFingerprint = await _playlistIntelligenceService!.HasFingerprintAsync(from.TrackUniqueHash);
        var toHasFingerprint = await _playlistIntelligenceService!.HasFingerprintAsync(to.TrackUniqueHash);
        if (!fromHasFingerprint || !toHasFingerprint)
        {
            var whichTrack = !fromHasFingerprint && !toHasFingerprint
                ? $"\"{from.Title}\" and \"{to.Title}\" haven't"
                : !fromHasFingerprint
                    ? $"\"{from.Title}\" hasn't"
                    : $"\"{to.Title}\" hasn't";
            return new SmartInsertResult(
                false,
                $"{whichTrack} been analyzed yet. Analyze it first, then try Smart Insert again.",
                NotificationType.Information);
        }

        var recommendations = await _playlistIntelligenceService!.InsertBetweenAsync(
            from.TrackUniqueHash,
            to.TrackUniqueHash,
            candidateHashes,
            TrackSimilarityProfile.BlendSafe,
            topK: 3,
            ct: default,
            minConfidenceThreshold: Math.Clamp(_appConfig.LibrarySmartInsertMinConfidence, 0.0, 1.0),
            structureSensitivity: Math.Clamp(_appConfig.LibrarySmartInsertStructureSensitivity / 100.0, 0.0, 1.0));

        var rankedRecommendations = recommendations
            .Select(recommendation => new
            {
                Recommendation = recommendation,
                BaseScore = recommendation.Score,
                Bonus = GetSavedDoubleBonus(from.TrackUniqueHash, to.TrackUniqueHash, recommendation.TrackHash)
            })
            .OrderByDescending(item => item.BaseScore + item.Bonus)
            .ToList();

        var bestRanked = rankedRecommendations.FirstOrDefault();
        var best = bestRanked?.Recommendation;
        if (best is null || string.IsNullOrWhiteSpace(best.TrackHash))
        {
            var fingerprintedCandidates = await _playlistIntelligenceService!.CountFingerprintedAsync(candidateHashes);
            var message = fingerprintedCandidates == 0
                ? "None of your other library tracks have been analyzed yet, so there's nothing to compare against. Analyze some tracks first."
                : "No suitable bridge track found for the selected pair.";
            return new SmartInsertResult(false, message, NotificationType.Information);
        }

        var entry = await _libraryService.FindLibraryEntryAsync(best.TrackHash);
        if (entry is null)
        {
            return new SmartInsertResult(false, "A recommendation was found, but metadata is unavailable.", NotificationType.Warning);
        }

        var bridgeTrack = new PlaylistTrack
        {
            PlaylistId = project.Id,
            TrackUniqueHash = entry.UniqueHash,
            Artist = entry.Artist,
            Title = entry.Title,
            Album = entry.Album,
            ResolvedFilePath = entry.FilePath,
            Format = entry.Format,
            Status = TrackStatus.Downloaded,
            BPM = entry.BPM,
            MusicalKey = entry.MusicalKey,
            AddedAt = DateTime.UtcNow,
            SortOrder = to.SortOrder,
            TrackNumber = to.TrackNumber
        };

        await _libraryService.AddTracksToProjectAsync(new[] { bridgeTrack }, project.Id);

        var refreshed = await _libraryService.LoadPlaylistTracksAsync(project.Id);
        var insertedTrack = refreshed
            .Where(t => string.Equals(t.TrackUniqueHash, bridgeTrack.TrackUniqueHash, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.AddedAt)
            .FirstOrDefault();

        if (insertedTrack is not null)
        {
            var ordered = refreshed
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            ordered.RemoveAll(t => t.Id == insertedTrack.Id);

            var idxA = ordered.FindIndex(t => string.Equals(t.TrackUniqueHash, from.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
            var idxB = ordered.FindIndex(t => string.Equals(t.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
            var upper = Math.Max(idxA, idxB);
            var insertAt = Math.Clamp(upper >= 0 ? upper : ordered.Count, 0, ordered.Count);

            ordered.Insert(insertAt, insertedTrack);

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].SortOrder = i + 1;
                ordered[i].TrackNumber = i + 1;
            }

            await _libraryService.SaveTrackOrderAsync(project.Id, ordered);
        }

        await Tracks.LoadProjectTracksAsync(project);
        Tracks.RefreshFilteredTracks();

        var savedDoublePriorSuffix = bestRanked is { Bonus: > 0.0 }
            ? " This choice aligns with one of your saved doubles."
            : string.Empty;

        return new SmartInsertResult(
            true,
            NumFormat.Ui($"Inserted {entry.Artist} - {entry.Title} between tracks (fit {(best.Score * 100):F0}%, threshold {_appConfig.LibrarySmartInsertMinConfidence:F2}, structure {_appConfig.LibrarySmartInsertStructureSensitivity}%).{savedDoublePriorSuffix}"),
            NotificationType.Success);
    }

    private double GetSavedDoubleBonus(string fromTrackId, string toTrackId, string? candidateTrackId)
    {
        if (string.IsNullOrWhiteSpace(fromTrackId) ||
            string.IsNullOrWhiteSpace(toTrackId) ||
            string.IsNullOrWhiteSpace(candidateTrackId))
        {
            return 0.0;
        }

        var bonus = 0.0;

        if (IsSavedDoublePair(fromTrackId, candidateTrackId))
            bonus += SavedDoublePriorBonus;

        if (IsSavedDoublePair(candidateTrackId, toTrackId))
            bonus += SavedDoublePriorBonus;

        return bonus;
    }

    private bool IsSavedDoublePair(string leftTrackId, string rightTrackId)
    {
        if (string.IsNullOrWhiteSpace(leftTrackId) || string.IsNullOrWhiteSpace(rightTrackId))
            return false;

        var (normalizedA, normalizedB) = SavedDoublesService.Normalize(leftTrackId, rightTrackId);

        return SavedDoubles.Any(saved =>
            string.Equals(saved.Model.TrackAId, normalizedA, StringComparison.Ordinal) &&
            string.Equals(saved.Model.TrackBId, normalizedB, StringComparison.Ordinal));
    }

    private sealed record SmartInsertResult(bool Success, string Message, NotificationType Type);

}
