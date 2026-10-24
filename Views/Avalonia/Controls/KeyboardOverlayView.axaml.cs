using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class KeyboardOverlayView : UserControl
{
    public KeyboardOverlayView()
    {
        InitializeComponent();
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Close only when the scrim itself (outside the card) was clicked. Presses inside the
        // card bubble up to here too and must not dismiss the overlay.
        if (!ReferenceEquals(e.Source, sender))
            return;

        if (DataContext is KeyboardOverlayViewModel vm)
            vm.Hide();
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KeyboardOverlayViewModel vm)
            vm.Hide();
        e.Handled = true;
    }
}
