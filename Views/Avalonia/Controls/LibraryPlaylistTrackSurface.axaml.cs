using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Views.Avalonia;

using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class LibraryPlaylistTrackSurface : UserControl
{
    private int _selectionAnchorIndex = -1;
    private int _focusedIndex = -1;
    private InsertBetweenRowControl? _activeMagneticGap;
    private TrackListViewModel? _boundVm;
    private INotifyCollectionChanged? _boundFilteredCollection;

    public LibraryPlaylistTrackSurface()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();

        if (DataContext is not TrackListViewModel vm)
            return;

        if (!TryResolveGapContextRequest(e.Source, out var request, out var gapControl))
            return;

        var focusIndex = vm.FilteredTracks.IndexOf(request.FromTrack);
        if (focusIndex < 0)
            return;

        _selectionAnchorIndex = focusIndex;
        _focusedIndex = focusIndex;
        ApplySelectionSet(vm, new[] { request.FromTrack });
        SetMagneticGap(gapControl);
    }

    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {
        SetMagneticGap(null);
    }

    private void OnTrackRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Control source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
            return;

        if (DataContext is not TrackListViewModel vm || sender is not Border row || row.DataContext is not PlaylistTrackViewModel track)
            return;

        var index = vm.FilteredTracks.IndexOf(track);
        if (index < 0)
            return;

        ApplyPointerSelection(vm, index, e.KeyModifiers);
        _focusedIndex = index;
        UpdateFocusedRowVisual(vm);
        RefreshInsertGapStates(vm);
        Focus();
        e.Handled = true;
    }

    private void OnTrackRowPointerEntered(object? sender, PointerEventArgs e)
    {
        OnTrackRowPointerMoved(sender, e);
    }

    private void OnTrackRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not TrackListViewModel vm || sender is not Border row || row.DataContext is not PlaylistTrackViewModel track)
            return;

        RefreshInsertGapStates(vm);

        var index = vm.FilteredTracks.IndexOf(track);
        if (index < 0)
            return;

        var y = e.GetPosition(row).Y;
        var targetAnchorIndex = y < row.Bounds.Height * 0.5 ? index - 1 : index;
        var targetGap = ResolveGapByAnchorIndex(vm, targetAnchorIndex);
        SetMagneticGap(targetGap);
    }

    private void OnTrackRowPointerExited(object? sender, PointerEventArgs e)
    {
        // Keep current magnetic state until another row/gap claims hover.
    }

    private void OnInsertGapPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is InsertBetweenRowControl gap)
            SetMagneticGap(gap);
    }

    private void OnInsertGapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is InsertBetweenRowControl gap)
            SetMagneticGap(gap);
    }

    private void OnInsertGapPointerExited(object? sender, PointerEventArgs e)
    {
        // Keep current magnetic state while moving between row and gap; root exit clears stale state.
    }

    private void OnTrackScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is TrackListViewModel vm)
            RefreshInsertGapStates(vm);
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TrackListViewModel vm || vm.FilteredTracks.Count == 0)
            return;

        var currentIndex = _focusedIndex >= 0 ? _focusedIndex : ResolveLeadSelectionIndex(vm);
        if (currentIndex < 0)
            currentIndex = 0;

        var targetIndex = currentIndex;
        switch (e.Key)
        {
            case Key.Up:
                targetIndex = currentIndex - 1;
                break;
            case Key.Down:
                targetIndex = currentIndex + 1;
                break;
            case Key.PageUp:
                targetIndex = currentIndex - 10;
                break;
            case Key.PageDown:
                targetIndex = currentIndex + 10;
                break;
            case Key.Home:
                targetIndex = 0;
                break;
            case Key.End:
                targetIndex = vm.FilteredTracks.Count - 1;
                break;
            case Key.Enter:
                ExecuteLeadTrackPrimaryAction(vm);
                e.Handled = true;
                return;
            case Key.Space:
                ExecuteFocusedSpaceAction(vm, e.KeyModifiers);
                e.Handled = true;
                return;
            default:
                return;
        }

        targetIndex = Clamp(targetIndex, 0, vm.FilteredTracks.Count - 1);
        ApplyKeyboardSelection(vm, targetIndex, e.KeyModifiers);
        _focusedIndex = targetIndex;
        UpdateFocusedRowVisual(vm);
        RefreshInsertGapStates(vm);
        e.Handled = true;
    }

    private void ApplyPointerSelection(TrackListViewModel vm, int index, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
        {
            SetRangeSelection(vm, _selectionAnchorIndex, index);
            return;
        }

        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            var candidate = vm.FilteredTracks[index];
            var next = vm.SelectedTracks.ToList();
            if (next.Contains(candidate))
                next.Remove(candidate);
            else
                next.Add(candidate);

            _selectionAnchorIndex = index;
            ApplySelectionSet(vm, next);
            return;
        }

        _selectionAnchorIndex = index;
        ApplySelectionSet(vm, new[] { vm.FilteredTracks[index] });
    }

    private void ApplyKeyboardSelection(TrackListViewModel vm, int index, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
            return;

        if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
        {
            SetRangeSelection(vm, _selectionAnchorIndex, index);
            return;
        }

        _selectionAnchorIndex = index;
        ApplySelectionSet(vm, new[] { vm.FilteredTracks[index] });
    }

    private void SetRangeSelection(TrackListViewModel vm, int startIndex, int endIndex)
    {
        var from = startIndex <= endIndex ? startIndex : endIndex;
        var to = startIndex <= endIndex ? endIndex : startIndex;
        var range = new List<PlaylistTrackViewModel>();
        for (var idx = from; idx <= to; idx++)
            range.Add(vm.FilteredTracks[idx]);

        ApplySelectionSet(vm, range);
    }

    private void ApplySelectionSet(TrackListViewModel vm, IEnumerable<PlaylistTrackViewModel> nextSelection)
    {
        var selected = nextSelection.Distinct().ToList();

        foreach (var track in vm.SelectedTracks.ToList())
            track.IsSelected = false;

        foreach (var track in selected)
            track.IsSelected = true;

        vm.UpdateSelection(selected);
        UpdateFocusedRowVisual(vm);
        RefreshInsertGapStates(vm);
    }

    private void ExecuteFocusedSpaceAction(TrackListViewModel vm, KeyModifiers modifiers)
    {
        var index = _focusedIndex >= 0 ? _focusedIndex : ResolveLeadSelectionIndex(vm);
        if (index < 0 || index >= vm.FilteredTracks.Count)
            return;

        var track = vm.FilteredTracks[index];
        if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            ApplyPointerSelection(vm, index, KeyModifiers.Control);
            return;
        }

        ApplySelectionSet(vm, new[] { track });
    }

    private void UpdateFocusedRowVisual(TrackListViewModel vm)
    {
        var focusedTrack = (_focusedIndex >= 0 && _focusedIndex < vm.FilteredTracks.Count)
            ? vm.FilteredTracks[_focusedIndex]
            : vm.LeadSelectedTrack;

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (!border.Classes.Contains("track-row"))
                continue;

            if (focusedTrack is not null && ReferenceEquals(border.DataContext, focusedTrack))
                border.Classes.Add("focused");
            else
                border.Classes.Remove("focused");
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachToVm(DataContext as TrackListViewModel);
        if (DataContext is TrackListViewModel vm)
            RefreshInsertGapStates(vm);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        PublishSmartInsertPreviewHint(null);
        AttachToVm(null);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        AttachToVm(DataContext as TrackListViewModel);
        if (DataContext is TrackListViewModel vm)
            RefreshInsertGapStates(vm);
    }

    private void AttachToVm(TrackListViewModel? nextVm)
    {
        if (ReferenceEquals(_boundVm, nextVm))
            return;

        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            if (_boundFilteredCollection is not null)
                _boundFilteredCollection.CollectionChanged -= OnFilteredTracksCollectionChanged;
            _boundFilteredCollection = null;
        }

        _boundVm = nextVm;

        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged += OnVmPropertyChanged;
            _boundFilteredCollection = _boundVm.FilteredTracks as INotifyCollectionChanged;
            if (_boundFilteredCollection is not null)
                _boundFilteredCollection.CollectionChanged += OnFilteredTracksCollectionChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(TrackListViewModel.FilteredTracks), System.StringComparison.Ordinal))
            return;

        if (_boundVm is null)
            return;

        if (_boundFilteredCollection is not null)
            _boundFilteredCollection.CollectionChanged -= OnFilteredTracksCollectionChanged;

        _boundFilteredCollection = _boundVm.FilteredTracks as INotifyCollectionChanged;
        if (_boundFilteredCollection is not null)
            _boundFilteredCollection.CollectionChanged += OnFilteredTracksCollectionChanged;

        RefreshInsertGapStates(_boundVm);
    }

    private void OnFilteredTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_boundVm is not null)
            RefreshInsertGapStates(_boundVm);
    }

    private void RefreshInsertGapStates(TrackListViewModel vm)
    {
        foreach (var item in EnumerateRealizedRowsAndGaps(vm))
        {
            var hasNext = item.Index + 1 < vm.FilteredTracks.Count;
            item.Gap.IsVisible = hasNext;

            if (!hasNext)
            {
                item.Gap.IsMagneticHover = false;
                item.Gap.InsertBetweenCommandParameter = null;
                continue;
            }

            var nextTrack = vm.FilteredTracks[item.Index + 1];
            item.Gap.InsertBetweenCommandParameter = new SmartInsertContextRequest(item.Track, nextTrack);
        }

        if (_activeMagneticGap is { IsVisible: false })
            SetMagneticGap(null);
    }

    private InsertBetweenRowControl? ResolveGapByAnchorIndex(TrackListViewModel vm, int anchorIndex)
    {
        if (anchorIndex < 0 || anchorIndex >= vm.FilteredTracks.Count - 1)
            return null;

        return EnumerateRealizedRowsAndGaps(vm)
            .Where(item => item.Index == anchorIndex)
            .Select(item => item.Gap)
            .FirstOrDefault();
    }

    private IEnumerable<(PlaylistTrackViewModel Track, Border Row, InsertBetweenRowControl Gap, int Index)> EnumerateRealizedRowsAndGaps(TrackListViewModel vm)
    {
        foreach (var panel in this.GetVisualDescendants().OfType<StackPanel>())
        {
            if (panel.DataContext is not PlaylistTrackViewModel track)
                continue;

            var row = panel.Children.OfType<Border>().FirstOrDefault(border => border.Classes.Contains("track-row"));
            var gap = panel.Children.OfType<InsertBetweenRowControl>().FirstOrDefault();
            if (row is null || gap is null)
                continue;

            var index = vm.FilteredTracks.IndexOf(track);
            if (index < 0)
                continue;

            yield return (track, row, gap, index);
        }
    }

    private void SetMagneticGap(InsertBetweenRowControl? gap)
    {
        if (ReferenceEquals(_activeMagneticGap, gap))
            return;

        if (_activeMagneticGap is not null)
            _activeMagneticGap.IsMagneticHover = false;

        _activeMagneticGap = gap;

        if (_activeMagneticGap is { IsVisible: true })
            _activeMagneticGap.IsMagneticHover = true;

        var previewRequest = _activeMagneticGap?.InsertBetweenCommandParameter as SmartInsertContextRequest;
        PublishSmartInsertPreviewHint(previewRequest);
    }

    private void PublishSmartInsertPreviewHint(SmartInsertContextRequest? request)
    {
        var libraryVm = ResolveLibraryViewModel();
        if (libraryVm?.PreviewSmartInsertContextCommand is null)
            return;

        if (libraryVm.PreviewSmartInsertContextCommand.CanExecute(request))
            libraryVm.PreviewSmartInsertContextCommand.Execute(request);
    }

    private LibraryViewModel? ResolveLibraryViewModel()
    {
        return this.GetSelfAndVisualAncestors()
            .OfType<LibraryPage>()
            .Select(page => page.DataContext)
            .OfType<LibraryViewModel>()
            .FirstOrDefault();
    }

    private bool TryResolveGapContextRequest(object? source, out SmartInsertContextRequest request, out InsertBetweenRowControl? gapControl)
    {
        request = default!;
        gapControl = null;

        if (source is not Control control)
            return false;

        gapControl = control
            .GetSelfAndVisualAncestors()
            .OfType<InsertBetweenRowControl>()
            .FirstOrDefault();

        if (gapControl?.InsertBetweenCommandParameter is not SmartInsertContextRequest resolved)
            return false;

        request = resolved;
        return true;
    }

    private static int ResolveLeadSelectionIndex(TrackListViewModel vm)
    {
        var lead = vm.LeadSelectedTrack;
        if (lead is null)
            return -1;

        return vm.FilteredTracks.IndexOf(lead);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static void ExecuteLeadTrackPrimaryAction(TrackListViewModel vm)
    {
        var lead = vm.LeadSelectedTrack;
        if (lead is null || vm.Operations?.PlayTrackCommand is null)
            return;

        if (vm.Operations.PlayTrackCommand.CanExecute(lead))
            vm.Operations.PlayTrackCommand.Execute(lead);
    }
}
