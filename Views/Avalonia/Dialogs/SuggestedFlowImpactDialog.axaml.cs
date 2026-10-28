using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia.Dialogs;

/// <summary>
/// Read-only summary of how a suggested flow changes transition styles. Layout lives in
/// XAML (compiled bindings to <see cref="ViewModels.SuggestedFlowImpactViewModel"/>).
/// </summary>
public partial class SuggestedFlowImpactDialog : Window
{
    public SuggestedFlowImpactDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
