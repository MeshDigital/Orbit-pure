using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class SmartCrateEditorDialog : Window
{
    public SmartCrateEditorDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    // Save is driven from Click only (the button used to carry Command="{Binding SaveCommand}"
    // as well, so one click could run SaveAsync twice). The dialog closes only on a real
    // result; on failure the VM sets ErrorMessage and the editor stays open.
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SmartCrateEditorViewModel vm) return;

        try
        {
            var result = await vm.SaveCommand.Execute();
            if (result != null)
                Close(result);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Smart crate editor: save command threw");
            vm.ErrorMessage = $"Could not save the crate: {ex.Message}";
        }
    }
}
