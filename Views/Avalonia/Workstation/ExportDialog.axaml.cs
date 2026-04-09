using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ReactiveUI;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Views.Avalonia.Workstation;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Opens this dialog modally and populates decks from the caller.</summary>
    public static async Task ShowForWorkstationAsync(
        Window owner,
        ExportDialogViewModel vm)
    {
        var dlg = new ExportDialog { DataContext = vm };
        await dlg.ShowDialog(owner);
    }
}
