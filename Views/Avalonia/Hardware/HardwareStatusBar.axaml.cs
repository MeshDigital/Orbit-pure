using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia.Hardware;

/// <summary>
/// Phase 3.2: persistent Pro DJ Link hardware indicator for the cockpit status bar. DataContext is a
/// <see cref="SLSKDONET.ViewModels.Hardware.HardwareStatusViewModel"/>.
/// </summary>
public partial class HardwareStatusBar : UserControl
{
    public HardwareStatusBar()
    {
        InitializeComponent();
    }
}
