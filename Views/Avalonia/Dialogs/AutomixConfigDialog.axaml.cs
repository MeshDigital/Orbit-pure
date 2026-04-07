using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Dialogs;

/// <summary>
/// Code-behind for the Automix Configuration dialog.
///
/// Typical usage from the AnalysisPageViewModel or PlaylistViewModel:
/// <code>
///   var vm = new AutomixConfigViewModel();
///   vm.LoadFrom(appConfig);
///   var dlg = new AutomixConfigDialog { DataContext = vm };
///
///   // Subscribe to Save and close the window
///   vm.SaveCommand.Subscribe(_ => { vm.SaveTo(appConfig); dlg.Close(); });
///   vm.CancelCommand.Subscribe(_ => dlg.Close());
///
///   await dlg.ShowDialog(parentWindow);
/// </code>
/// </summary>
public partial class AutomixConfigDialog : Window
{
    public AutomixConfigDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Typed access to the bound ViewModel.</summary>
    public AutomixConfigViewModel? ViewModel => DataContext as AutomixConfigViewModel;
}
