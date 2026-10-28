using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;

namespace SLSKDONET.ViewModels.Library;

public partial class TrackListViewModel
{
    private readonly ILibraryTrackListViewStateStore? _viewStateStore;
    private readonly Dictionary<string, LibraryTrackListViewState> _sessionViewStates = new(StringComparer.Ordinal);
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _viewStateSaveRequests = new();
    private string? _activeViewStateKey;
    private bool _isApplyingViewState;
    private DateTime _lastViewStateRestoreAt = DateTime.MinValue;
    private double _listScrollOffset;
    private HashSet<string> _selectedStyleNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ViewStatePropertyNames = new(StringComparer.Ordinal)
    {
        nameof(SearchText), nameof(FilterArtist), nameof(FilterTitle),
        nameof(IsFilterAll), nameof(IsFilterDownloaded), nameof(IsFilterPending),
        nameof(IsFilterNeedsReview), nameof(IsFilterLiked), nameof(CamelotKeyFilter),
        nameof(QualityTierFilter), nameof(IsBouncerActive), nameof(VibeFilter),
        nameof(IsFilterFlac), nameof(IsFilterMp3), nameof(IsFilterWav), nameof(IsFilterLossless),
        nameof(IsFilterQualityGold), nameof(IsFilterQualityVerified), nameof(IsFilterQualityReview),
        nameof(DuplicateHashesFilter), nameof(SortColumn), nameof(SortDescending),
        nameof(IsColumnFilterStripVisible), nameof(IsFormatColumnVisible),
        nameof(IsForensicsColumnVisible), nameof(IsDurationColumnVisible),
        nameof(ArtistColumnWidth), nameof(EnergyColumnWidth), nameof(FormatColumnWidth),
        nameof(ForensicsColumnWidth), nameof(DurationColumnWidth), nameof(RatingColumnWidth),
        nameof(PreparationColumnWidth),
    };

    /// <summary>The active view asks the grid to restore only after its new ItemsSource is assigned.</summary>
    public event EventHandler<double>? ScrollRestoreRequested;

    public double ListScrollOffset => _listScrollOffset;

    private void InitializeViewStatePersistence()
    {
        this.Changed
            .Where(e => e.PropertyName != null && ViewStatePropertyNames.Contains(e.PropertyName))
            .Subscribe(_ => ScheduleViewStateSave())
            .DisposeWith(_disposables);

        _viewStateSaveRequests
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SaveViewStateNow())
            .DisposeWith(_disposables);
    }

    public void SwitchToProjectViewState(Guid projectId) =>
        SwitchViewState(projectId == Guid.Empty ? "all-tracks" : $"playlist-{projectId:N}");

    public void SwitchToSmartPlaylistViewState(Guid playlistId) =>
        SwitchViewState($"smart-{playlistId:N}");

    public void SuspendViewState()
    {
        SaveViewStateNow();
        _activeViewStateKey = null;
    }

    private void SwitchViewState(string key)
    {
        SaveViewStateNow();
        _activeViewStateKey = key;

        if (!_sessionViewStates.TryGetValue(key, out var state) && _viewStateStore is not null)
        {
            try
            {
                state = _viewStateStore.Load(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not restore Library view state {Key}; using defaults", key);
            }
        }

        ApplyViewState(state ?? new LibraryTrackListViewState());
    }

    private void ScheduleViewStateSave()
    {
        if (_isApplyingViewState || _activeViewStateKey is null || _isDisposed) return;
        _viewStateSaveRequests.OnNext(System.Reactive.Unit.Default);
    }

    public void SaveViewStateNow()
    {
        if (_activeViewStateKey is not { } key || _isApplyingViewState) return;
        var state = CaptureViewState();
        _sessionViewStates[key] = state;
        if (_viewStateStore is null) return;
        _ = PersistViewStateAsync(key, state);
    }

    private async System.Threading.Tasks.Task PersistViewStateAsync(string key, LibraryTrackListViewState state)
    {
        try
        {
            await _viewStateStore!.SaveAsync(key, state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save Library view state {Key}", key);
        }
    }

    internal LibraryTrackListViewState CaptureViewState() => new()
    {
        SearchText = _searchText,
        FilterArtist = _filterArtist,
        FilterTitle = _filterTitle,
        IsFilterAll = _isFilterAll,
        IsFilterDownloaded = _isFilterDownloaded,
        IsFilterPending = _isFilterPending,
        IsFilterNeedsReview = _isFilterNeedsReview,
        IsFilterLiked = _isFilterLiked,
        CamelotKeyFilter = _camelotKeyFilter,
        QualityTierFilter = _qualityTierFilter,
        IsBouncerActive = _isBouncerActive,
        VibeFilter = _vibeFilter,
        IsFilterFlac = _isFilterFlac,
        IsFilterMp3 = _isFilterMp3,
        IsFilterWav = _isFilterWav,
        IsFilterLossless = _isFilterLossless,
        IsFilterQualityGold = _isFilterQualityGold,
        IsFilterQualityVerified = _isFilterQualityVerified,
        IsFilterQualityReview = _isFilterQualityReview,
        SelectedStyles = _selectedStyleNames.ToList(),
        DuplicateHashes = _duplicateHashesFilter?.ToList() ?? new List<string>(),
        SortColumn = _sortColumn,
        SortDescending = _sortDescending,
        IsColumnFilterStripVisible = _isColumnFilterStripVisible,
        IsFormatColumnVisible = _isFormatColumnVisible,
        IsForensicsColumnVisible = _isForensicsColumnVisible,
        IsDurationColumnVisible = _isDurationColumnVisible,
        ArtistColumnWidth = _artistColumnWidth,
        EnergyColumnWidth = _energyColumnWidth,
        FormatColumnWidth = _formatColumnWidth,
        ForensicsColumnWidth = _forensicsColumnWidth,
        DurationColumnWidth = _durationColumnWidth,
        RatingColumnWidth = _ratingColumnWidth,
        PreparationColumnWidth = _preparationColumnWidth,
        ScrollOffset = _listScrollOffset,
    };

    internal void ApplyViewState(LibraryTrackListViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Normalize();
        _isApplyingViewState = true;
        try
        {
            _searchText = state.SearchText;
            _filterArtist = state.FilterArtist;
            _filterTitle = state.FilterTitle;
            _isFilterAll = state.IsFilterAll;
            _isFilterDownloaded = state.IsFilterDownloaded;
            _isFilterPending = state.IsFilterPending;
            _isFilterNeedsReview = state.IsFilterNeedsReview;
            _isFilterLiked = state.IsFilterLiked;
            _camelotKeyFilter = state.CamelotKeyFilter;
            _qualityTierFilter = state.QualityTierFilter;
            _isBouncerActive = state.IsBouncerActive;
            _vibeFilter = state.VibeFilter;
            _isFilterFlac = state.IsFilterFlac;
            _isFilterMp3 = state.IsFilterMp3;
            _isFilterWav = state.IsFilterWav;
            _isFilterLossless = state.IsFilterLossless;
            _isFilterQualityGold = state.IsFilterQualityGold;
            _isFilterQualityVerified = state.IsFilterQualityVerified;
            _isFilterQualityReview = state.IsFilterQualityReview;
            _selectedStyleNames = new HashSet<string>(state.SelectedStyles, StringComparer.OrdinalIgnoreCase);
            _duplicateHashesFilter = state.DuplicateHashes.Count > 0
                ? new HashSet<string>(state.DuplicateHashes, StringComparer.Ordinal)
                : null;
            _sortColumn = state.SortColumn;
            _sortDescending = state.SortDescending;
            _isColumnFilterStripVisible = state.IsColumnFilterStripVisible;
            _isFormatColumnVisible = state.IsFormatColumnVisible;
            _isForensicsColumnVisible = state.IsForensicsColumnVisible;
            _isDurationColumnVisible = state.IsDurationColumnVisible;
            _artistColumnWidth = state.ArtistColumnWidth;
            _energyColumnWidth = state.EnergyColumnWidth;
            _formatColumnWidth = state.FormatColumnWidth;
            _forensicsColumnWidth = state.ForensicsColumnWidth;
            _durationColumnWidth = state.DurationColumnWidth;
            _ratingColumnWidth = state.RatingColumnWidth;
            _preparationColumnWidth = state.PreparationColumnWidth;
            _listScrollOffset = state.ScrollOffset;

            foreach (var name in ViewStatePropertyNames) this.RaisePropertyChanged(name);
            this.RaisePropertyChanged(nameof(HasActiveFilters));
            this.RaisePropertyChanged(nameof(HasCamelotKeyFilter));
            this.RaisePropertyChanged(nameof(HasDuplicateFilter));
            this.RaisePropertyChanged(nameof(IsTierLosslessFilter));
            this.RaisePropertyChanged(nameof(IsTierHighFilter));
            this.RaisePropertyChanged(nameof(IsTierLowFilter));
            RaiseSortIndicatorProperties();
            ApplyStyleSelectionToLoadedFilters();
            RefitColumns();
            RefitToolbar();
            _lastViewStateRestoreAt = DateTime.UtcNow;
        }
        finally
        {
            _isApplyingViewState = false;
        }
    }

    private void ApplyStyleSelectionToLoadedFilters()
    {
        foreach (var style in StyleFilters)
        {
            style.PropertyChanged -= OnStyleFilterChanged;
            style.IsSelected = _selectedStyleNames.Contains(style.Style.Name);
            style.PropertyChanged += OnStyleFilterChanged;
        }
    }

    private void UpdateListScrollOffset(double offsetY)
    {
        if (double.IsFinite(offsetY)) _listScrollOffset = Math.Max(0, offsetY);
        ScheduleViewStateSave();
    }

    public void RequestScrollRestore() => ScrollRestoreRequested?.Invoke(this, _listScrollOffset);

    private double _artistColumnWidth = TrackListColumnLayout.ArtistWidth;
    public double ArtistColumnWidth
    {
        get => _artistColumnWidth;
        set => SetColumnWidth(ref _artistColumnWidth, value, 90, 320);
    }

    private double _energyColumnWidth = TrackListColumnLayout.EnergyWidth;
    public double EnergyColumnWidth
    {
        get => _energyColumnWidth;
        set => SetColumnWidth(ref _energyColumnWidth, value, 60, 160);
    }

    private double _formatColumnWidth = TrackListColumnLayout.FormatWidth;
    public double FormatColumnWidth
    {
        get => _formatColumnWidth;
        set => SetColumnWidth(ref _formatColumnWidth, value, 80, 180);
    }

    private double _forensicsColumnWidth = TrackListColumnLayout.ForensicsWidth;
    public double ForensicsColumnWidth
    {
        get => _forensicsColumnWidth;
        set => SetColumnWidth(ref _forensicsColumnWidth, value, 90, 220);
    }

    private double _durationColumnWidth = TrackListColumnLayout.DurationWidth;
    public double DurationColumnWidth
    {
        get => _durationColumnWidth;
        set => SetColumnWidth(ref _durationColumnWidth, value, 64, 140);
    }

    private double _ratingColumnWidth = TrackListColumnLayout.RatingWidth;
    public double RatingColumnWidth
    {
        get => _ratingColumnWidth;
        set => SetColumnWidth(ref _ratingColumnWidth, value, 64, 140);
    }

    private double _preparationColumnWidth = TrackListColumnLayout.PreparationWidth;
    public double PreparationColumnWidth
    {
        get => _preparationColumnWidth;
        set => SetColumnWidth(ref _preparationColumnWidth, value, 96, 180);
    }

    private void SetColumnWidth(
        ref double field,
        double value,
        double min,
        double max,
        [CallerMemberName] string? propertyName = null)
    {
        if (!double.IsFinite(value)) return;
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(field - clamped) < 0.5) return;
        this.RaiseAndSetIfChanged(ref field, clamped, propertyName);
        RefitColumns();
    }

    public void ResizeColumn(string? column, double deltaX)
    {
        if (!double.IsFinite(deltaX)) return;
        switch (column)
        {
            case "Artist": ArtistColumnWidth += deltaX; break;
            case "Energy": EnergyColumnWidth += deltaX; break;
            case "Format": FormatColumnWidth += deltaX; break;
            case "Forensics": ForensicsColumnWidth += deltaX; break;
            case "Duration": DurationColumnWidth += deltaX; break;
            case "Rating": RatingColumnWidth += deltaX; break;
            case "Preparation": PreparationColumnWidth += deltaX; break;
        }
    }
}
