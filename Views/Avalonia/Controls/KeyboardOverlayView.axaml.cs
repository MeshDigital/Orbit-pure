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
        // Close when clicking the scrim (outside the card)
        if (DataContext is KeyboardOverlayViewModel vm)
            vm.Hide();
    }

    private void OnClosePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is KeyboardOverlayViewModel vm)
            vm.Hide();
        e.Handled = true;
    }
}
