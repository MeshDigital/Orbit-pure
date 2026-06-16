using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        e.DragEffects = e.Data.Contains("FlowCard") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCardDrop(object? sender, DragEventArgs e)
    {
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
