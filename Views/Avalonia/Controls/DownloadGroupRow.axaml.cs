using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class DownloadGroupRow : UserControl
{
    public DownloadGroupRow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Called by the drag handle's EventTriggerBehavior to absorb the PointerPressed event
    // so it doesn't bubble up to the HeaderBorder's expand-toggle behavior.
    public void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        // Future: initiate DragDrop.DoDragDropAsync here for manual playlist reordering
    }
}
