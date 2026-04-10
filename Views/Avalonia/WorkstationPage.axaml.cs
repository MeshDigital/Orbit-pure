using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Workstation;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

namespace SLSKDONET.Views.Avalonia;

public partial class WorkstationPage : UserControl
{
    internal const string WorkstationPlaylistTrackFormat = "ORBIT_WorkstationPlaylistTrack";

    private GridLength _lastExpandedDrawerHeight = new(300, GridUnitType.Pixel);
    private Point? _flowGridDragStart;

    public WorkstationPage()
    {
        InitializeComponent();
        if (!Design.IsDesignMode &&
            Application.Current is App app && app.Services != null)
        {
            DataContext = app.Services.GetService(typeof(WorkstationViewModel))
                          as WorkstationViewModel;
        }
    }

    private void OnDrawerSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.FindControl<Grid>("CockpitGrid") is not { } grid || grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var drawerRow = grid.RowDefinitions[3];
        if (drawerRow.Height.Value > 1)
        {
            _lastExpandedDrawerHeight = drawerRow.Height;
            drawerRow.Height = new GridLength(0, GridUnitType.Pixel);
            return;
        }

        drawerRow.Height = _lastExpandedDrawerHeight.Value > 1
            ? _lastExpandedDrawerHeight
            : new GridLength(300, GridUnitType.Pixel);
    }

    private void OnFlowTrackGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _flowGridDragStart = e.GetPosition(this);
    }

    private void OnAnalyzeSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkstationViewModel vm) return;

        var grid = this.FindControl<DataGrid>("FlowTrackGrid");
        if (grid == null) return;

        var selected = grid.SelectedItems
            .OfType<PlaylistTrack>()
            .ToList();

        if (selected.Count == 0) return;

        vm.AnalyzeSelectedCuesCommand.Execute(selected).Subscribe();
    }

    private async void OnFlowTrackGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_flowGridDragStart == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _flowGridDragStart.Value.X) < 6 &&
            Math.Abs(current.Y - _flowGridDragStart.Value.Y) < 6)
        {
            return;
        }

        if (e.Source is not Visual sourceVisual)
        {
            return;
        }

        var row = sourceVisual.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not PlaylistTrack track)
        {
            return;
        }

        var data = new DataObject();
        data.Set(WorkstationPlaylistTrackFormat, track);
        data.Set(DataFormats.Text, $"{track.Artist} - {track.Title}");

        _flowGridDragStart = null;
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private async void OnCockpitKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z)
        {
            vm.UndoCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y)
        {
            vm.RedoCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            vm.PlayPauseAllCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Add || e.Key == Key.OemPlus)
        {
            vm.ZoomInCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
        {
            vm.ZoomOutCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            vm.PanLeftCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            vm.PanRightCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && vm.FocusedDeck != null)
        {
            if (e.Key == Key.V)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleVocalsCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.D)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleDrumsCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.B)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleBassCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.O)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleOtherCommand);
                e.Handled = true;
            }
        }
    }

    private static async System.Threading.Tasks.Task ExecuteStemToggle(ReactiveCommand<Unit, Unit> command)
    {
        await command.Execute().FirstAsync();
    }
}
