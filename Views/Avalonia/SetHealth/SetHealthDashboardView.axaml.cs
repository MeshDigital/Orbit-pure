using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia.SetHealth;

/// <summary>
/// Slice K4: the Set Health dashboard — docked alongside the Story Graph canvas. Renders the
/// overall/narrative/technical/risk scores and the narrative breakdown. DataContext is a
/// <see cref="SLSKDONET.ViewModels.SetHealth.SetHealthDashboardViewModel"/>.
/// </summary>
public partial class SetHealthDashboardView : UserControl
{
    public SetHealthDashboardView()
    {
        InitializeComponent();
    }
}
