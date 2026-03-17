using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Controls.Primitives;

namespace SLSKDONET.Views.Avalonia;

public partial class PlaylistGridView : UserControl
{
    public PlaylistGridView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHealthRingPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            var flyout = FlyoutBase.GetAttachedFlyout(control);
            flyout?.ShowAt(control);
        }
    }
}