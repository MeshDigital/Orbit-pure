using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Workstation;
using System.Reactive.Linq;

namespace SLSKDONET.Views.Avalonia.Workstation;

public partial class WorkstationDeckRow : UserControl
{
    private Border? _dropZone;

    public WorkstationDeckRow()
    {
        InitializeComponent();
        _dropZone = this.FindControl<Border>("DeckDropZone");
        if (_dropZone != null)
        {
            DragDrop.SetAllowDrop(_dropZone, true);
            _dropZone.AddHandler(DragDrop.DragOverEvent, OnDeckDragOver);
            _dropZone.AddHandler(DragDrop.DragLeaveEvent, OnDeckDragLeave);
            _dropZone.AddHandler(DragDrop.DropEvent, OnDeckDrop);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDeckDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(WorkstationPage.WorkstationPlaylistTrackFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            _dropZone?.Classes.Add("drag-over");
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            _dropZone?.Classes.Remove("drag-over");
        }

        e.Handled = true;
    }

    private void OnDeckDragLeave(object? sender, RoutedEventArgs e)
    {
        _dropZone?.Classes.Remove("drag-over");
    }

    private void OnDeckPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not WorkstationDeckViewModel deckVm)
        {
            return;
        }

        if (this.FindAncestorOfType<WorkstationPage>()?.DataContext is WorkstationViewModel workstationVm)
        {
            workstationVm.FocusedDeck = deckVm;
        }
    }

    private async void OnDeckDrop(object? sender, DragEventArgs e)
    {
        _dropZone?.Classes.Remove("drag-over");

        if (!e.Data.Contains(WorkstationPage.WorkstationPlaylistTrackFormat))
        {
            return;
        }

        if (e.Data.Get(WorkstationPage.WorkstationPlaylistTrackFormat) is not PlaylistTrack track)
        {
            return;
        }

        if (DataContext is not WorkstationDeckViewModel deckVm)
        {
            return;
        }

        if (deckVm.IsLocked)
        {
            e.Handled = true;
            return;
        }

        if (this.FindAncestorOfType<WorkstationPage>()?.DataContext is WorkstationViewModel workstationVm)
        {
            workstationVm.FocusedDeck = deckVm;
        }

        await deckVm.LoadPlaylistTrackCommand.Execute(track).FirstAsync();

        if (this.FindAncestorOfType<WorkstationPage>()?.DataContext is WorkstationViewModel vmAfterLoad)
        {
            vmAfterLoad.ApplySmartSnapForDeckDrop(deckVm);
        }

        e.Handled = true;
    }
}
