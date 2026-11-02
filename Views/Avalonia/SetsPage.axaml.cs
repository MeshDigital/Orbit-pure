using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Sets;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// The Sets page (rebuild plan Phase 2): set planning as its own page instead of a Workstation
/// overlay. Reloads the rail every time it is shown, so sets created from the Library's
/// "Add to Set" appear without a manual refresh. Code-behind only maps pointer gestures in the
/// lane (select, drag to reorder, drop library tracks) onto view-model calls.
/// </summary>
public partial class SetsPage : UserControl
{
    private const double DragThreshold = 6;
    private Point? _dragStart;
    private SlotCardViewModel? _dragCard;

    public SetsPage()
    {
        InitializeComponent();
        Lane.AddHandler(DragDrop.DragOverEvent, OnLaneDragOver);
        Lane.AddHandler(DragDrop.DropEvent, OnLaneDrop);
    }

    public SetsPage(SetsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private SetsViewModel? ViewModel => DataContext as SetsViewModel;

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ViewModel is { } vm)
            await vm.LoadAsync(); // reports its own failures (log + toast + inline error)
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SlotCardViewModel card || ViewModel is not { } vm)
            return;

        // Left and right click both select, so the lane's context menu acts on the clicked card.
        vm.SelectSlot(card);
        Lane.Focus(); // lane key bindings (arrows, Delete, Ctrl+Z) need focus

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(this);
            _dragCard = card;
        }
    }

    private async void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragCard is not { } card
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragStart = null;
        _dragCard = null;
        var data = new DataObject();
        data.Set(DragContext.SetSlotFormat, card.SlotId.ToString());
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnLaneDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DragContext.SetSlotFormat) ? DragDropEffects.Move
            : e.Data.Contains(DragContext.LibraryTrackFormat) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLaneDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        e.Handled = true;

        // Drop target: the card under the pointer (or the chip's incoming card); empty lane = end.
        // DataContext is inherited, so any element inside a card or chip resolves to its item.
        var target = (e.Source as StyledElement)?.DataContext switch
        {
            SlotCardViewModel c => c.Index,
            TransitionChipViewModel chip => chip.To.Index,
            _ => vm.Slots.Count,
        };

        if (e.Data.Get(DragContext.SetSlotFormat) is string slotText && Guid.TryParse(slotText, out var slotId))
            await vm.MoveSlotAsync(slotId, target); // reports its own failures
        else if (e.Data.Get(DragContext.LibraryTrackFormat) is string trackHash && !string.IsNullOrEmpty(trackHash))
            await vm.InsertTrackAsync(trackHash, target);
    }
}
