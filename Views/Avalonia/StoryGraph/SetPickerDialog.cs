using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>
/// Opens the Set Projects pickers modally over the main window (same owner lookup as
/// <see cref="SLSKDONET.Services.DialogService"/>), so repeated clicks can't stack loose windows.
/// </summary>
public static class SetPickerDialog
{
    public static void ShowModal(Window dialog)
    {
        var owner = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (owner is not null)
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
        }
    }
}
