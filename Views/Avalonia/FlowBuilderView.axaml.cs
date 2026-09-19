using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class FlowBuilderView : UserControl
{
    public FlowBuilderView()
    {
        InitializeComponent();
        if (!Design.IsDesignMode &&
            Application.Current is App app && app.Services != null)
        {
            DataContext = app.Services.GetService(typeof(FlowBuilderViewModel))
                          as FlowBuilderViewModel;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCardArtworkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual visual) return;
        // Walk up to the card DataContext (the artwork Border's DC inherits from the DataTemplate)
        var card = visual.DataContext as FlowTrackCardViewModel;
        if (card == null) return;

        var data = new DataObject();
        data.Set("FlowCard", card);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        bool canDrop = e.Data.Contains("FlowCard");
        e.DragEffects = canDrop ? DragDropEffects.Move : DragDropEffects.None;

        // MoveCardToIndex already worked when a drop landed, but nothing showed WHERE it would
        // land while dragging — toggle the same "drag-over" highlight WorkstationDeckRow's drop
        // zone already uses.
        if (sender is Border border)
        {
            border.Classes.Set("drag-over", canDrop);
        }

        e.Handled = true;
    }

    private void OnCardDragLeave(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }
    }

    private void OnCardDrop(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }

        if (!e.Data.Contains("FlowCard")) return;
        if (sender is not Visual visual) return;

        var source = e.Data.Get("FlowCard") as FlowTrackCardViewModel;
        var target = visual.DataContext as FlowTrackCardViewModel;
        if (source == null || target == null || source == target) return;

        var vm = DataContext as FlowBuilderViewModel;
        if (vm == null) return;

        int targetIdx = vm.Tracks.IndexOf(target);
        vm.MoveCardToIndex(source, targetIdx);
        e.Handled = true;
    }
}
