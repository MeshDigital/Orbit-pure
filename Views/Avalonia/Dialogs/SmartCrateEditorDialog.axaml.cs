using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Data.Entities;
using System.Reactive.Linq;

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
    
    // Wire up specific interaction if needed, or rely on Command execution in VM
    // Since VM SaveCommand returns the Entity, we can capture it here?
    // But Button Command executes independently.
    // Better pattern: Button Click calls this method. This method invokes Command explicitly OR observes it.
    
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SmartCrateEditorViewModel vm)
        {
            try
            {
                // Execute and await result
                var result = await vm.SaveCommand.Execute();
                Close(result);
            }
            catch (Exception)
            {
                // Error handled in VM
            }
        }
    }
}
