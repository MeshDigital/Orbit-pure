using Avalonia;
using Avalonia.Controls;
using SLSKDONET.ViewModels.Sets;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// The Sets page (rebuild plan P2.2): set planning as its own page instead of a Workstation
/// overlay. Reloads the rail every time it is shown, so sets created from the Library's
/// "Add to Set" appear without a manual refresh.
/// </summary>
public partial class SetsPage : UserControl
{
    public SetsPage()
    {
        InitializeComponent();
    }

    public SetsPage(SetsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SetsViewModel vm)
            await vm.LoadAsync(); // reports its own failures (log + toast + inline error)
    }
}
