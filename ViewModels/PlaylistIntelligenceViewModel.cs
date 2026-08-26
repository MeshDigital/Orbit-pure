using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Views;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Playlist;

namespace SLSKDONET.ViewModels;

public sealed class PlaylistIntelligenceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryViewModel _library;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private string _selectedLibraryIntelligenceTab = IntelligenceTabOverview;
    private double _librarySmartInsertMinConfidence;
    private int _librarySmartInsertStructureSensitivity;
    private bool _isSuggestNextLoading;
    private string _suggestNextInfoText = "Showing placeholder candidates. Slice 10 Commit 2 will wire real-time ranking and priors.";
    private bool _isPlaylistUpgradeLoading;
    private string _playlistUpgradeInfoText = "Upgrade candidates are ranked by transition score, harmonic fit, and saved-double priors.";
    private string _smartInsertFromLabel = "Select a source track";
    private string _smartInsertToLabel = "Select a target track";
    private string _smartInsertPreparationHint = string.Empty;
    private PlaylistTrack? _smartInsertFromTrack;
    private PlaylistTrack? _smartInsertToTrack;
    private int _suggestNextRefreshVersion;
    private int _playlistUpgradeRefreshVersion;
    private const double SavedDoublePriorBonus = 0.03;
    private readonly ICommand _setLibraryIntelligenceTabCommand;
    private readonly ICommand _setSmartInsertStrictPresetCommand;
    private readonly ICommand _setSmartInsertNormalPresetCommand;
    private readonly ICommand _setSmartInsertLoosePresetCommand;
    private readonly ICommand _applyPreparedSmartInsertCommand;

    private readonly PlaylistOptimizer? _playlistOptimizer;
    private AutomixConstraints _automixConstraints = new();
    private string? _automixStatusMessage;
    private readonly ICommand _stageAllAnalyzedForAutomixCommand;
    private readonly ICommand _clearAutomixStagingCommand;
    private readonly ICommand _createAutomixCommand;
    private readonly ICommand _applyAutomixCommand;

    public ObservableCollection<PlaylistTrackViewModel> StagedAutomixTracks { get; } = new();

    public const string IntelligenceTabOverview = "Overview";
    private const string IntelligenceTabSmartInsert = "SmartInsert";
    private const string IntelligenceTabSuggestNext = "SuggestNext";
    private const string IntelligenceTabUpgrade = "Upgrade";
    private const string IntelligenceTabAutomix = "Automix";

    private int _overviewRefreshVersion;
    private int _overviewTrackCount;
    private string _overviewDurationDisplay = "—";
    private string _overviewBpmRangeDisplay = "—";
    private string _overviewAvgEnergyDisplay = "—";
    private double _overviewAvgEnergyPercent;
    private int _overviewAnalyzedCount;
    private double _overviewAnalysisCoveragePercent;

    public ObservableCollection<PlaylistStatBar> OverviewTopArtists { get; } = new();
    public ObservableCollection<PlaylistStatBar> OverviewTopGenres { get; } = new();
    public ObservableCollection<PlaylistStatBar> OverviewKeyDistribution { get; } = new();
    public ObservableCollection<PlaylistStatBar> OverviewBpmBrackets { get; } = new();

    public PlaylistIntelligenceViewModel(
        LibraryViewModel library,
        TrackSimilarityService? trackSimilarityService = null,
        PlaylistOptimizer? playlistOptimizer = null)
    {
        _library = library;
        _trackSimilarityService = trackSimilarityService;
        _playlistOptimizer = playlistOptimizer;
        var settings = _library.GetSmartInsertSettingsSnapshot();
        _librarySmartInsertMinConfidence = settings.MinConfidence;
        _librarySmartInsertStructureSensitivity = settings.StructureSensitivity;
        _setLibraryIntelligenceTabCommand = new RelayCommand<object>(ExecuteSetLibraryIntelligenceTab);
        _setSmartInsertStrictPresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.80, 85));
        _setSmartInsertNormalPresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.72, 55));
        _setSmartInsertLoosePresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.65, 30));
        _applyPreparedSmartInsertCommand = new AsyncRelayCommand(_library.ApplyPreparedSmartInsertFromIntelligenceAsync);

        _stageAllAnalyzedForAutomixCommand = new AsyncRelayCommand(StageAllAnalyzedForAutomixAsync);
        _clearAutomixStagingCommand = new RelayCommand(ClearAutomixStaging);
        _createAutomixCommand = new AsyncRelayCommand(CreateAutomixPlaylistAsync);
        _applyAutomixCommand = new AsyncRelayCommand(ApplyAutomixAsync);

        StagedAutomixTracks.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCreateAutomix)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomixSelectionSummary)));
        };

        _library.PropertyChanged += OnLibraryPropertyChanged;
    }

    internal LibraryViewModel Library => _library;

    public string LibraryIntelligencePlaylistTitle => _library.LibraryIntelligencePlaylistTitle;
    public string SmartInsertContextSummary => $"{SmartInsertFromLabel} -> {SmartInsertToLabel}";

    public string SelectedLibraryIntelligenceTab => _selectedLibraryIntelligenceTab;

    public bool IsLibraryIntelligenceOverviewActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabOverview, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceSmartInsertActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabSmartInsert, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceSuggestNextActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabSuggestNext, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceUpgradeActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabUpgrade, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceAutomixActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabAutomix, StringComparison.Ordinal);

    // ── Playlist Overview — general "what's in this list" stats, recomputed live as tracks are
    // added/removed/analyzed. This is what the sidepanel now defaults to on open, instead of the
    // Smart Insert tool tab, which requires an explicit source/target track pick to be useful.
    public bool HasOverviewData => _overviewTrackCount > 0;
    public int OverviewTrackCount => _overviewTrackCount;
    public string OverviewDurationDisplay => _overviewDurationDisplay;
    public string OverviewBpmRangeDisplay => _overviewBpmRangeDisplay;
    public string OverviewAvgEnergyDisplay => _overviewAvgEnergyDisplay;
    public double OverviewAvgEnergyPercent => _overviewAvgEnergyPercent;
    public string OverviewAnalysisCoverageDisplay => $"{_overviewAnalyzedCount}/{_overviewTrackCount} analyzed";
    public double OverviewAnalysisCoveragePercent => _overviewAnalysisCoveragePercent;
    public bool HasOverviewTopArtists => OverviewTopArtists.Count > 0;
    public bool HasOverviewTopGenres => OverviewTopGenres.Count > 0;
    public bool HasOverviewKeyDistribution => OverviewKeyDistribution.Count > 0;
    public bool HasOverviewBpmBrackets => OverviewBpmBrackets.Count > 0;

    public ICommand SetLibraryIntelligenceTabCommand => _setLibraryIntelligenceTabCommand;
    public ICommand SetSmartInsertStrictPresetCommand => _setSmartInsertStrictPresetCommand;
    public ICommand SetSmartInsertNormalPresetCommand => _setSmartInsertNormalPresetCommand;
    public ICommand SetSmartInsertLoosePresetCommand => _setSmartInsertLoosePresetCommand;

    public string LibrarySmartInsertThresholdPreset
    {
        get
        {
            var threshold = LibrarySmartInsertMinConfidence;
            if (threshold >= 0.79) return "Strict";
            if (threshold >= 0.71) return "Normal";
            return "Loose";
        }
    }

    public bool IsSmartInsertStrictPresetActive => string.Equals(LibrarySmartInsertThresholdPreset, "Strict", StringComparison.Ordinal);
    public bool IsSmartInsertNormalPresetActive => string.Equals(LibrarySmartInsertThresholdPreset, "Normal", StringComparison.Ordinal);
    public bool IsSmartInsertLoosePresetActive => string.Equals(LibrarySmartInsertThresholdPreset, "Loose", StringComparison.Ordinal);

    public double LibrarySmartInsertMinConfidence
    {
        get => _librarySmartInsertMinConfidence;
        set
        {
            var normalized = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_librarySmartInsertMinConfidence - normalized) < 0.0001)
                return;

            _librarySmartInsertMinConfidence = normalized;
            RaiseSmartInsertPresetStateChanged();

            if (_library.UpdateSmartInsertSettingsFromIntelligence(
                    _librarySmartInsertMinConfidence,
                    _librarySmartInsertStructureSensitivity))
            {
                _ = _library.PersistLibrarySmartInsertConfigAsync();
            }
        }
    }

    public int LibrarySmartInsertStructureSensitivity
    {
        get => _librarySmartInsertStructureSensitivity;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_librarySmartInsertStructureSensitivity == normalized)
                return;

            _librarySmartInsertStructureSensitivity = normalized;
            RaiseSmartInsertPresetStateChanged();

            if (_library.UpdateSmartInsertSettingsFromIntelligence(
                    _librarySmartInsertMinConfidence,
                    _librarySmartInsertStructureSensitivity))
            {
                _ = _library.PersistLibrarySmartInsertConfigAsync();
            }
        }
    }

    public string SmartInsertFromLabel => _smartInsertFromLabel;
    public string SmartInsertToLabel => _smartInsertToLabel;
    public bool IsSmartInsertPreparationHintVisible => !string.IsNullOrWhiteSpace(_smartInsertPreparationHint);
    public string SmartInsertPreparationHint => _smartInsertPreparationHint;
    public ICommand ApplyPreparedSmartInsertCommand => _applyPreparedSmartInsertCommand;
    public bool HasPendingSmartInsertContext =>
        _smartInsertFromTrack is not null &&
        _smartInsertToTrack is not null &&
        !string.IsNullOrWhiteSpace(_smartInsertFromTrack.TrackUniqueHash) &&
        !string.IsNullOrWhiteSpace(_smartInsertToTrack.TrackUniqueHash);

    public string SuggestNextInfoText => _suggestNextInfoText;
    public bool IsSuggestNextLoading => _isSuggestNextLoading;
    public bool HasSuggestNextCandidates => SuggestNextCandidates.Count > 0;
    public ObservableCollection<SuggestNextCandidateViewModel> SuggestNextCandidates { get; } = new();
    public ICommand SuggestNextCandidateCommand => _library.SuggestNextCandidateCommand;

    public string PlaylistUpgradeInfoText => _playlistUpgradeInfoText;
    public bool IsPlaylistUpgradeLoading => _isPlaylistUpgradeLoading;
    public bool HasPlaylistUpgradeCandidates => PlaylistUpgradeCandidates.Count > 0;
    public ObservableCollection<PlaylistUpgradeCandidateViewModel> PlaylistUpgradeCandidates { get; } = new();
    public ICommand PlaylistUpgradeCandidateCommand => _library.PlaylistUpgradeCandidateCommand;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplySmartInsertPreset(double minConfidence, int structureSensitivity)
    {
        _librarySmartInsertMinConfidence = Math.Clamp(minConfidence, 0.0, 1.0);
        _librarySmartInsertStructureSensitivity = Math.Clamp(structureSensitivity, 0, 100);
        RaiseSmartInsertPresetStateChanged();

        if (_library.UpdateSmartInsertSettingsFromIntelligence(
                _librarySmartInsertMinConfidence,
                _librarySmartInsertStructureSensitivity))
        {
            _ = _library.PersistLibrarySmartInsertConfigAsync();
        }
    }

    public bool FocusLibraryIntelligenceTab(string? tab)
    {
        var normalized = NormalizeIntelligenceTab(tab);
        if (string.Equals(_selectedLibraryIntelligenceTab, normalized, StringComparison.Ordinal))
            return false;

        _selectedLibraryIntelligenceTab = normalized;
        RaiseIntelligenceTabStateChanged();
        return true;
    }

    public void SetSmartInsertPairContext(PlaylistTrack from, PlaylistTrack to)
    {
        _smartInsertFromTrack = from;
        _smartInsertToTrack = to;
        _smartInsertFromLabel = FormatSmartInsertTrackLabel(from);
        _smartInsertToLabel = FormatSmartInsertTrackLabel(to);
        ClearSmartInsertPreparationHint();
        RaiseSmartInsertContextStateChanged();
    }

    public void ResetSmartInsertPairContext()
    {
        _smartInsertFromTrack = null;
        _smartInsertToTrack = null;
        _smartInsertFromLabel = "Select a source track";
        _smartInsertToLabel = "Select a target track";
        ClearSmartInsertPreparationHint();
        RaiseSmartInsertContextStateChanged();
    }

    public void SetSmartInsertPreparationHint(PlaylistTrack from, PlaylistTrack to)
    {
        _smartInsertPreparationHint = $"Preparing suggestions for {FormatSmartInsertTrackLabel(from)} -> {FormatSmartInsertTrackLabel(to)}";
        RaiseSmartInsertContextStateChanged();
    }

    public void ClearSmartInsertPreparationHint()
    {
        _smartInsertPreparationHint = string.Empty;
        RaiseSmartInsertContextStateChanged();
    }

    public bool TryGetPendingSmartInsertContext(out PlaylistTrack? from, out PlaylistTrack? to)
    {
        from = _smartInsertFromTrack;
        to = _smartInsertToTrack;
        return HasPendingSmartInsertContext;
    }

    public void SeedSuggestNextScaffoldCandidates()
    {
        if (SuggestNextCandidates.Count > 0)
            return;

        var seedPool = _library.Tracks.FilteredTracks.Any()
            ? _library.Tracks.FilteredTracks.Take(3)
            : _library.Tracks.CurrentProjectTracks.Take(3);

        foreach (var track in seedPool)
            SuggestNextCandidates.Add(new SuggestNextCandidateViewModel(track));

        if (SuggestNextCandidates.Count == 0)
        {
            SetSuggestNextState(false, "Select or play tracks to preview Suggest Next candidates.");
        }
    }

    public void SeedPlaylistUpgradeScaffoldCandidates()
    {
        if (PlaylistUpgradeCandidates.Count > 0)
            return;

        var seedPool = _library.Tracks.CurrentProjectTracks.Any()
            ? _library.Tracks.CurrentProjectTracks.Take(3)
            : _library.Tracks.FilteredTracks.Take(3);

        foreach (var track in seedPool)
        {
            PlaylistUpgradeCandidates.Add(new PlaylistUpgradeCandidateViewModel(
                track,
                isSavedDoubleAligned: false,
                isBridgeCandidate: false,
                isReplacementCandidate: false,
                upgradeReason: "Scaffold candidate"));
        }

        if (PlaylistUpgradeCandidates.Count == 0)
        {
            SetPlaylistUpgradeState(false, "Select a playlist to preview upgrade candidates.");
            return;
        }

        SetPlaylistUpgradeState(false, "Upgrade candidates are shown once live ranking resolves.");
    }

    public async Task RefreshSuggestNextCandidatesAsync()
    {
        var refreshVersion = System.Threading.Interlocked.Increment(ref _suggestNextRefreshVersion);
        await Dispatcher.UIThread.InvokeAsync(() => SetSuggestNextState(true, "Scanning transition candidates..."));

        try
        {
            var contextTrack = ResolveSuggestNextContextTrack();
            var contextTrackId = contextTrack?.GlobalId;
            if (string.IsNullOrWhiteSpace(contextTrackId))
            {
                if (refreshVersion != _suggestNextRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SuggestNextCandidates.Clear();
                    SetSuggestNextState(false, "Select or play tracks to preview Suggest Next candidates.");
                });
                return;
            }

            var contextTrackTitle = string.IsNullOrWhiteSpace(contextTrack?.TrackTitle)
                ? "current context track"
                : contextTrack.TrackTitle;

            var similarity = _trackSimilarityService;
            if (similarity is null)
            {
                if (refreshVersion != _suggestNextRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SuggestNextCandidates.Clear();
                    SetSuggestNextState(false, "Suggest Next is unavailable: similarity service is missing.");
                });
                return;
            }

            var trackPool = _library.Tracks.FilteredTracks
                .Concat(_library.Tracks.CurrentProjectTracks)
                .Where(track => !string.IsNullOrWhiteSpace(track.GlobalId))
                .GroupBy(track => track.GlobalId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            trackPool.Remove(contextTrackId);

            if (trackPool.Count == 0)
            {
                if (refreshVersion != _suggestNextRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SuggestNextCandidates.Clear();
                    SetSuggestNextState(false, "No local candidate pool is available for Suggest Next yet.");
                });
                return;
            }

            var minScore = Math.Clamp(_library.LibrarySmartInsertMinConfidence, 0.0, 1.0);
            var ranked = new List<(PlaylistTrackViewModel Track, double BaseScore, double Bonus, double AdjustedScore)>();

            foreach (var candidate in trackPool.Values.Take(120))
            {
                if (refreshVersion != _suggestNextRefreshVersion)
                    return;

                var candidateId = candidate.GlobalId;
                if (string.IsNullOrWhiteSpace(candidateId))
                    continue;

                var score = await similarity.ScoreAsync(
                    contextTrackId,
                    candidateId,
                    TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

                if (score is null)
                    continue;

                var baseScore = score.FinalSimilarity;
                if (baseScore < minScore)
                    continue;

                var isSavedDoubleSuggested = IsSavedDoublePair(contextTrackId, candidateId);
                var bonus = isSavedDoubleSuggested ? SavedDoublePriorBonus : 0.0;
                ranked.Add((candidate, baseScore, bonus, baseScore + bonus));
            }

            var topCandidates = ranked
                .OrderByDescending(item => item.AdjustedScore)
                .ThenByDescending(item => item.BaseScore)
                .Take(5)
                .ToList();

            if (refreshVersion != _suggestNextRefreshVersion)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SuggestNextCandidates.Clear();
                foreach (var item in topCandidates)
                {
                    SuggestNextCandidates.Add(new SuggestNextCandidateViewModel(
                        item.Track,
                        item.BaseScore,
                        item.Bonus,
                        item.Bonus > 0.0));
                }

                SetSuggestNextState(
                    false,
                    topCandidates.Count == 0
                        ? $"No qualifying candidates found for {contextTrackTitle}."
                        : $"Top suggestions after {contextTrackTitle} (base score shown)."
                );
            });
        }
        catch (Exception ex)
        {
            _library.Logger.LogWarning(ex, "Failed to refresh Suggest Next candidates");
            if (refreshVersion != _suggestNextRefreshVersion)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SuggestNextCandidates.Clear();
                SetSuggestNextState(false, "Suggest Next refresh failed. Try selecting a track again.");
            });
        }
    }

    public async Task RefreshPlaylistUpgradeCandidatesAsync()
    {
        var refreshVersion = System.Threading.Interlocked.Increment(ref _playlistUpgradeRefreshVersion);
        await Dispatcher.UIThread.InvokeAsync(() => SetPlaylistUpgradeState(true, "Scanning upgrade opportunities..."));

        try
        {
            var contextTrack = ResolvePlaylistUpgradeContextTrack();
            var contextTrackId = contextTrack?.GlobalId;
            if (string.IsNullOrWhiteSpace(contextTrackId))
            {
                if (refreshVersion != _playlistUpgradeRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PlaylistUpgradeCandidates.Clear();
                    SetPlaylistUpgradeState(false, "Select a track to evaluate upgrade opportunities.");
                });
                return;
            }

            var similarity = _trackSimilarityService;
            if (similarity is null)
            {
                if (refreshVersion != _playlistUpgradeRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PlaylistUpgradeCandidates.Clear();
                    SetPlaylistUpgradeState(false, "Upgrade scoring is unavailable: similarity service missing.");
                });
                return;
            }

            var sourcePool = _library.Tracks.CurrentProjectTracks.Any()
                ? _library.Tracks.CurrentProjectTracks
                : _library.Tracks.FilteredTracks;

            var candidatePool = sourcePool
                .Where(track => !string.IsNullOrWhiteSpace(track.GlobalId))
                .Where(track => !string.Equals(track.GlobalId, contextTrackId, StringComparison.Ordinal))
                .GroupBy(track => track.GlobalId, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(140)
                .ToList();

            if (candidatePool.Count == 0)
            {
                if (refreshVersion != _playlistUpgradeRefreshVersion)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PlaylistUpgradeCandidates.Clear();
                    SetPlaylistUpgradeState(false, "No candidates available in the current track pool.");
                });
                return;
            }

            var ranked = new List<PlaylistUpgradeCandidateViewModel>();
            var minThreshold = Math.Clamp(_library.LibrarySmartInsertMinConfidence, 0.0, 1.0);

            foreach (var candidate in candidatePool)
            {
                if (refreshVersion != _playlistUpgradeRefreshVersion)
                    return;

                var candidateId = candidate.GlobalId;
                if (string.IsNullOrWhiteSpace(candidateId))
                    continue;

                var similarityScore = await similarity.ScoreAsync(
                    contextTrackId,
                    candidateId,
                    TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

                if (similarityScore is null)
                    continue;

                var baseScore = similarityScore.FinalSimilarity;
                if (baseScore < minThreshold)
                    continue;

                var isSavedDoubleAligned = IsSavedDoublePair(contextTrackId, candidateId);
                var bonus = isSavedDoubleAligned ? SavedDoublePriorBonus : 0.0;
                var adjusted = baseScore + bonus;

                var bpmDelta = contextTrack?.HasBpm == true && candidate.HasBpm
                    ? Math.Abs(contextTrack.BPM - candidate.BPM)
                    : double.NaN;

                var isBridgeCandidate = !double.IsNaN(bpmDelta) && bpmDelta <= 6.0 && similarityScore.SegmentScores.Drop >= 0.55;
                var isReplacementCandidate = adjusted >= Math.Max(minThreshold + 0.08, 0.78);

                var reason = isSavedDoubleAligned
                    ? "Boosted by saved-double history and transition fit."
                    : isBridgeCandidate
                        ? "Strong bridge fit between tempo/key context."
                        : "High transition compatibility for upgrade lane.";

                ranked.Add(new PlaylistUpgradeCandidateViewModel(
                    candidate,
                    isSavedDoubleAligned,
                    isBridgeCandidate,
                    isReplacementCandidate,
                    reason,
                    baseScore,
                    bonus));
            }

            var topCandidates = ranked
                .OrderByDescending(item => item.AdjustedScore)
                .ThenByDescending(item => item.BaseScore)
                .Take(6)
                .ToList();

            if (refreshVersion != _playlistUpgradeRefreshVersion)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PlaylistUpgradeCandidates.Clear();
                foreach (var candidate in topCandidates)
                    PlaylistUpgradeCandidates.Add(candidate);

                SetPlaylistUpgradeState(
                    false,
                    topCandidates.Count == 0
                        ? "No qualifying upgrades found for the current context."
                        : "Upgrade candidates ranked by transition score (with priors)."
                );
            });
        }
        catch (Exception ex)
        {
            _library.Logger.LogWarning(ex, "Failed to refresh Playlist Upgrade candidates");
            if (refreshVersion != _playlistUpgradeRefreshVersion)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PlaylistUpgradeCandidates.Clear();
                SetPlaylistUpgradeState(false, "Upgrade candidate refresh failed. Try selecting a track again.");
            });
        }
    }

    public void Dispose()
    {
        _library.PropertyChanged -= OnLibraryPropertyChanged;
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        }
    }

    /// <summary>
    /// Refreshes the Playlist Overview stats for whatever's currently selected. Deliberately
    /// re-reads from source rather than trusting whatever's materialized in the UI-bound track
    /// collections: for a regular (DB-virtualized) project, Tracks.CurrentProjectTracks is left
    /// empty by design (Tracks.FilteredTracks holds a VirtualizedTrackCollection instead, which
    /// only has the currently-scrolled-into-view page loaded in memory — enumerating "all" of it
    /// silently returns an incomplete/placeholder-heavy set). Smart Crates/Smart Playlists DO
    /// populate CurrentProjectTracks fully in-memory, so that path is used when non-empty; the
    /// DB query below covers everything else.
    /// </summary>
    public async Task RefreshOverviewStatsAsync()
    {
        var refreshVersion = System.Threading.Interlocked.Increment(ref _overviewRefreshVersion);

        var inMemory = _library.Tracks.CurrentProjectTracks;
        if (inMemory.Count > 0)
        {
            var models = inMemory.Select(t => t.Model).ToList();
            if (refreshVersion != _overviewRefreshVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() => ApplyOverviewStats(models));
            return;
        }

        var projectId = _library.SelectedProject?.Id;
        if (projectId is null || projectId == Guid.Empty)
        {
            if (refreshVersion != _overviewRefreshVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() => ApplyOverviewStats(new List<PlaylistTrack>()));
            return;
        }

        List<PlaylistTrack> tracks;
        try
        {
            tracks = await _library.LibraryService.LoadPlaylistTracksAsync(projectId.Value);
        }
        catch (Exception ex)
        {
            _library.Logger.LogWarning(ex, "Failed to load tracks for Playlist Overview (playlist {PlaylistId})", projectId.Value);
            return;
        }

        if (refreshVersion != _overviewRefreshVersion) return;
        await Dispatcher.UIThread.InvokeAsync(() => ApplyOverviewStats(tracks));
    }

    private void ApplyOverviewStats(List<PlaylistTrack> tracks)
    {
        var count = tracks.Count;
        _overviewTrackCount = count;

        if (count == 0)
        {
            _overviewDurationDisplay = "—";
            _overviewBpmRangeDisplay = "—";
            _overviewAvgEnergyDisplay = "—";
            _overviewAvgEnergyPercent = 0;
            _overviewAnalyzedCount = 0;
            _overviewAnalysisCoveragePercent = 0;
            OverviewTopArtists.Clear();
            OverviewTopGenres.Clear();
            OverviewKeyDistribution.Clear();
            OverviewBpmBrackets.Clear();
            RaiseOverviewStateChanged();
            return;
        }

        var totalSeconds = tracks.Sum(t => t.Duration);
        _overviewDurationDisplay = FormatOverviewDuration(totalSeconds);

        var bpms = tracks.Where(t => (t.BPM ?? 0) > 0).Select(t => t.BPM!.Value).ToList();
        _overviewBpmRangeDisplay = bpms.Count > 0
            ? $"{bpms.Min():0}–{bpms.Max():0} BPM · avg {bpms.Average():0}"
            : "No BPM data yet";

        var energies = tracks.Where(t => (t.Energy ?? 0) > 0).Select(t => t.Energy!.Value).ToList();
        if (energies.Count > 0)
        {
            var avgEnergy = energies.Average();
            _overviewAvgEnergyDisplay = $"{avgEnergy * 10:0.0} / 10";
            _overviewAvgEnergyPercent = Math.Clamp(avgEnergy * 100, 0, 100);
        }
        else
        {
            _overviewAvgEnergyDisplay = "No energy data yet";
            _overviewAvgEnergyPercent = 0;
        }

        _overviewAnalyzedCount = tracks.Count(t => (t.BPM ?? 0) > 0 || !string.IsNullOrEmpty(t.MusicalKey));
        _overviewAnalysisCoveragePercent = _overviewAnalyzedCount * 100.0 / count;

        RebuildOverviewStatBars(OverviewTopArtists, tracks
            .Select(t => t.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, "Unknown Artist", StringComparison.Ordinal)),
            top: 5);

        RebuildOverviewStatBars(OverviewTopGenres, tracks
            .Select(t => !string.IsNullOrEmpty(t.DetectedSubGenre) ? t.DetectedSubGenre : t.PrimaryGenre),
            top: 5);

        RebuildOverviewStatBars(OverviewKeyDistribution, tracks
            .Where(t => !string.IsNullOrEmpty(t.MusicalKey))
            .Select(t => SLSKDONET.Utils.KeyConverter.ToCamelot(t.MusicalKey)),
            top: 8);

        RebuildOverviewBpmBrackets(bpms);

        RaiseOverviewStateChanged();
    }

    private static void RebuildOverviewStatBars(ObservableCollection<PlaylistStatBar> target, IEnumerable<string?> values, int top)
    {
        var grouped = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        target.Clear();
        var max = grouped.Count > 0 ? grouped[0].Count : 1;
        foreach (var g in grouped)
        {
            target.Add(new PlaylistStatBar(g.Label, g.Count.ToString(), Math.Clamp(g.Count * 100.0 / max, 4, 100)));
        }
    }

    private void RebuildOverviewBpmBrackets(List<double> bpms)
    {
        OverviewBpmBrackets.Clear();
        if (bpms.Count == 0) return;

        (string Label, Func<double, bool> Match)[] brackets =
        {
            ("<100", b => b < 100),
            ("100-120", b => b >= 100 && b < 120),
            ("120-140", b => b >= 120 && b < 140),
            ("140-160", b => b >= 140 && b < 160),
            ("160-180", b => b >= 160 && b < 180),
            ("180+", b => b >= 180),
        };

        var counts = brackets
            .Select(b => (b.Label, Count: bpms.Count(b.Match)))
            .Where(x => x.Count > 0)
            .ToList();
        var max = counts.Count > 0 ? counts.Max(c => c.Count) : 1;
        foreach (var (label, cnt) in counts)
        {
            OverviewBpmBrackets.Add(new PlaylistStatBar(label, cnt.ToString(), Math.Clamp(cnt * 100.0 / max, 4, 100)));
        }
    }

    private static string FormatOverviewDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }

    private void RaiseOverviewStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOverviewData)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewTrackCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewDurationDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewBpmRangeDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewAvgEnergyDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewAvgEnergyPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewAnalysisCoverageDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverviewAnalysisCoveragePercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOverviewTopArtists)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOverviewTopGenres)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOverviewKeyDistribution)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOverviewBpmBrackets)));
    }

    private void ExecuteSetLibraryIntelligenceTab(object? parameter)
    {
        _library.FocusLibraryIntelligenceTab(parameter?.ToString() ?? IntelligenceTabSmartInsert);
    }

    private void RaiseSmartInsertContextStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartInsertFromLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartInsertToLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartInsertContextSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartInsertPreparationHint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmartInsertPreparationHintVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingSmartInsertContext)));
    }

    private static string FormatSmartInsertTrackLabel(PlaylistTrack track)
    {
        return string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";
    }

    private void SetSuggestNextState(bool loading, string infoText)
    {
        _isSuggestNextLoading = loading;
        _suggestNextInfoText = infoText;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSuggestNextLoading)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SuggestNextInfoText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSuggestNextCandidates)));
    }

    private void SetPlaylistUpgradeState(bool loading, string infoText)
    {
        _isPlaylistUpgradeLoading = loading;
        _playlistUpgradeInfoText = infoText;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaylistUpgradeLoading)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaylistUpgradeInfoText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlaylistUpgradeCandidates)));
    }

    private PlaylistTrackViewModel? ResolveSuggestNextContextTrack()
    {
        var currentTrack = _library.Player.CurrentTrack;
        if (currentTrack is not null && !string.IsNullOrWhiteSpace(currentTrack.GlobalId))
            return currentTrack;

        var lead = _library.Tracks.LeadSelectedTrack;
        if (lead is not null && !string.IsNullOrWhiteSpace(lead.GlobalId))
            return lead;

        return null;
    }

    private PlaylistTrackViewModel? ResolvePlaylistUpgradeContextTrack()
    {
        var lead = _library.Tracks.LeadSelectedTrack;
        if (lead is not null && !string.IsNullOrWhiteSpace(lead.GlobalId))
            return lead;

        var currentTrack = _library.Player.CurrentTrack;
        if (currentTrack is not null && !string.IsNullOrWhiteSpace(currentTrack.GlobalId))
            return currentTrack;

        return null;
    }

    private bool IsSavedDoublePair(string leftTrackId, string rightTrackId)
    {
        if (string.IsNullOrWhiteSpace(leftTrackId) || string.IsNullOrWhiteSpace(rightTrackId))
            return false;

        var (normalizedA, normalizedB) = SavedDoublesService.Normalize(leftTrackId, rightTrackId);

        return _library.SavedDoubles.Any(saved =>
            string.Equals(saved.Model.TrackAId, normalizedA, StringComparison.Ordinal) &&
            string.Equals(saved.Model.TrackBId, normalizedB, StringComparison.Ordinal));
    }

    private void RaiseSmartInsertPresetStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySmartInsertMinConfidence)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySmartInsertStructureSensitivity)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySmartInsertThresholdPreset)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmartInsertStrictPresetActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmartInsertNormalPresetActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmartInsertLoosePresetActive)));
    }

    private static string NormalizeIntelligenceTab(string? tab)
    {
        if (string.Equals(tab, IntelligenceTabOverview, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabOverview;
        if (string.Equals(tab, IntelligenceTabSmartInsert, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabSmartInsert;
        if (string.Equals(tab, IntelligenceTabSuggestNext, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabSuggestNext;
        if (string.Equals(tab, IntelligenceTabUpgrade, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabUpgrade;
        if (string.Equals(tab, IntelligenceTabAutomix, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabAutomix;
        return IntelligenceTabOverview;
    }

    private void RaiseIntelligenceTabStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLibraryIntelligenceTab)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceOverviewActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceSmartInsertActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceSuggestNextActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceUpgradeActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceAutomixActive)));
    }

    public AutomixConstraints AutomixConstraints
    {
        get => _automixConstraints;
        set
        {
            if (_automixConstraints != value)
            {
                _automixConstraints = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomixConstraints)));
            }
        }
    }

    public string? AutomixStatusMessage
    {
        get => _automixStatusMessage;
        private set
        {
            if (_automixStatusMessage != value)
            {
                _automixStatusMessage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomixStatusMessage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomixStatusMessageVisible)));
            }
        }
    }

    public bool AutomixStatusMessageVisible => !string.IsNullOrEmpty(AutomixStatusMessage);

    public string AutomixSelectionSummary => StagedAutomixTracks.Count == 0
        ? "No tracks staged for automix yet."
        : $"{StagedAutomixTracks.Count} track(s) staged for mix-building.";

    public bool CanCreateAutomix => StagedAutomixTracks.Count >= 2;

    public ICommand StageAllAnalyzedForAutomixCommand => _stageAllAnalyzedForAutomixCommand;
    public ICommand ClearAutomixStagingCommand => _clearAutomixStagingCommand;
    public ICommand CreateAutomixCommand => _createAutomixCommand;
    public ICommand ApplyAutomixCommand => _applyAutomixCommand;

    public async Task StageAllAnalyzedForAutomixAsync()
    {
        StagedAutomixTracks.Clear();
        var sourceTracks = _library.Tracks.CurrentProjectTracks.Any()
            ? _library.Tracks.CurrentProjectTracks.ToList()
            : _library.Tracks.FilteredTracks.ToList().Where(t => !t.IsPlaceholder).ToList();

        var analyzed = sourceTracks.Where(t => t.HasBpm || t.HasAnalysisData).ToList();

        int batchCount = 0;
        foreach (var track in analyzed)
        {
            StagedAutomixTracks.Add(track);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        AutomixStatusMessage = StagedAutomixTracks.Count == 0
            ? "No analyzed tracks available for staging yet."
            : $"Staged {StagedAutomixTracks.Count} analyzed track(s) for automix.";
    }

    public void ClearAutomixStaging()
    {
        StagedAutomixTracks.Clear();
        AutomixStatusMessage = "Automix staging cleared.";
    }

    public async Task CreateAutomixPlaylistAsync()
    {
        if (StagedAutomixTracks.Count < 2)
        {
            AutomixStatusMessage = "Add at least 2 analyzed tracks to the staging first.";
            return;
        }

        var c = AutomixConstraints;
        var eligible = StagedAutomixTracks
            .Where(t => t.BPM >= c.MinBpm && t.BPM <= c.MaxBpm)
            .ToList();

        if (eligible.Count < 2)
        {
            AutomixStatusMessage = $"Not enough tracks in the BPM range {c.MinBpm:F0}–{c.MaxBpm:F0}.";
            return;
        }

        if (_playlistOptimizer is null)
        {
            AutomixStatusMessage = "Playlist optimizer service is unavailable.";
            return;
        }

        var hashes = eligible.Select(t => t.GlobalId).Where(h => !string.IsNullOrEmpty(h)).ToList();
        var opts = new PlaylistOptimizerOptions
        {
            HarmonicWeight = c.HarmonicWeight,
            TempoWeight = c.TempoWeight,
            EnergyWeight = c.EnergyWeight,
            MaxBpmJump = Math.Max(1, (int)(c.MaxBpm - c.MinBpm)),
            EnergyCurve = c.EnergyCurve switch
            {
                "Rising" => EnergyCurvePattern.Rising,
                "Wave" => EnergyCurvePattern.Wave,
                "Peak" => EnergyCurvePattern.Peak,
                _ => EnergyCurvePattern.None,
            }
        };

        try
        {
            var result = await _playlistOptimizer.OptimizeAsync(hashes, opts);
            if (result.OrderedHashes.Count < 2)
            {
                AutomixStatusMessage = "Optimization failed to produce an ordering.";
                return;
            }

            var lookup = eligible.ToDictionary(t => t.GlobalId);
            var ordered = new List<PlaylistTrackViewModel>();
            foreach (var hash in result.OrderedHashes)
            {
                if (lookup.TryGetValue(hash, out var track))
                {
                    ordered.Add(track);
                }
            }

            StagedAutomixTracks.Clear();
            foreach (var t in ordered)
            {
                StagedAutomixTracks.Add(t);
            }

            // Update SortOrder property in the preview views
            for (int i = 0; i < StagedAutomixTracks.Count; i++)
            {
                StagedAutomixTracks[i].SortOrder = i + 1;
            }

            var minBpm = ordered.First().BPM;
            var maxBpm = ordered.Last().BPM;
            AutomixStatusMessage = $"Automix ready: {ordered.Count} tracks, {minBpm:F0}–{maxBpm:F0} BPM. Click 'Apply' to save order.";
        }
        catch (Exception ex)
        {
            _library.Logger.LogWarning(ex, "Automix build failed");
            AutomixStatusMessage = $"Optimization failed: {ex.Message}";
        }
    }

    public async Task ApplyAutomixAsync()
    {
        var project = _library.SelectedProject;
        if (project is null || project.Id == Guid.Empty)
        {
            AutomixStatusMessage = "Please select a specific playlist first to apply.";
            return;
        }

        if (StagedAutomixTracks.Count < 2)
        {
            AutomixStatusMessage = "Build an automix ordering first.";
            return;
        }

        try
        {
            var orderedTracks = StagedAutomixTracks.Select((t, index) =>
            {
                var trackModel = t.Model;
                trackModel.SortOrder = index + 1;
                trackModel.TrackNumber = index + 1;
                return trackModel;
            }).ToList();

            await _library.LibraryService.SaveTrackOrderAsync(project.Id, orderedTracks);

            // Refresh UI in Library track list
            await _library.Tracks.LoadProjectTracksAsync(project);
            _library.Tracks.RefreshFilteredTracks();

            AutomixStatusMessage = $"Successfully applied automix order to playlist '{project.SourceTitle}'!";
        }
        catch (Exception ex)
        {
            _library.Logger.LogError(ex, "Automix apply (SaveTrackOrderAsync) failed for playlist {ProjectId}", project.Id);
            AutomixStatusMessage = $"Failed to save track order: {ex.Message}";
        }
    }
}

/// <summary>One row in a Playlist Overview stat bar list (top artists/genres/keys/BPM brackets).</summary>
public sealed record PlaylistStatBar(string Label, string CountDisplay, double Percent);
