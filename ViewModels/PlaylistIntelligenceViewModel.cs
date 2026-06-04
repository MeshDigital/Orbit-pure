using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
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

namespace SLSKDONET.ViewModels;

public sealed class PlaylistIntelligenceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryViewModel _library;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private string _selectedLibraryIntelligenceTab = IntelligenceTabSmartInsert;
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

    private const string IntelligenceTabSmartInsert = "SmartInsert";
    private const string IntelligenceTabSuggestNext = "SuggestNext";
    private const string IntelligenceTabUpgrade = "Upgrade";
    private const string IntelligenceTabAutomix = "Automix";

    public PlaylistIntelligenceViewModel(LibraryViewModel library, TrackSimilarityService? trackSimilarityService = null)
    {
        _library = library;
        _trackSimilarityService = trackSimilarityService;
        var settings = _library.GetSmartInsertSettingsSnapshot();
        _librarySmartInsertMinConfidence = settings.MinConfidence;
        _librarySmartInsertStructureSensitivity = settings.StructureSensitivity;
        _setLibraryIntelligenceTabCommand = new RelayCommand<object>(ExecuteSetLibraryIntelligenceTab);
        _setSmartInsertStrictPresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.80, 85));
        _setSmartInsertNormalPresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.72, 55));
        _setSmartInsertLoosePresetCommand = new RelayCommand(() => ApplySmartInsertPreset(0.65, 30));
        _applyPreparedSmartInsertCommand = new AsyncRelayCommand(_library.ApplyPreparedSmartInsertFromIntelligenceAsync);
        _library.PropertyChanged += OnLibraryPropertyChanged;
    }

    internal LibraryViewModel Library => _library;

    public string LibraryIntelligencePlaylistTitle => _library.LibraryIntelligencePlaylistTitle;
    public string SmartInsertContextSummary => $"{SmartInsertFromLabel} -> {SmartInsertToLabel}";

    public string SelectedLibraryIntelligenceTab => _selectedLibraryIntelligenceTab;

    public bool IsLibraryIntelligenceSmartInsertActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabSmartInsert, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceSuggestNextActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabSuggestNext, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceUpgradeActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabUpgrade, StringComparison.Ordinal);
    public bool IsLibraryIntelligenceAutomixActive => string.Equals(SelectedLibraryIntelligenceTab, IntelligenceTabAutomix, StringComparison.Ordinal);

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
            _library.Logger.LogDebug(ex, "Failed to refresh Suggest Next candidates");
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
            _library.Logger.LogDebug(ex, "Failed to refresh Playlist Upgrade candidates");
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
        if (string.Equals(tab, IntelligenceTabSuggestNext, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabSuggestNext;
        if (string.Equals(tab, IntelligenceTabUpgrade, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabUpgrade;
        if (string.Equals(tab, IntelligenceTabAutomix, StringComparison.OrdinalIgnoreCase)) return IntelligenceTabAutomix;
        return IntelligenceTabSmartInsert;
    }

    private void RaiseIntelligenceTabStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLibraryIntelligenceTab)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceSmartInsertActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceSuggestNextActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceUpgradeActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibraryIntelligenceAutomixActive)));
    }
}
