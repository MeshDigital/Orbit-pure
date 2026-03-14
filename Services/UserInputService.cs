using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace SLSKDONET.Services;

public class UserInputService : IUserInputService
{
    public async Task<string?> GetInputAsync(string prompt, string title, string defaultValue = "")
    {
        var dialog = new InputDialog(title, prompt, defaultValue);
        
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            throw new InvalidOperationException("Cannot show dialog without a main window.");
        }

        await dialog.ShowDialog(desktop.MainWindow);
        
        return dialog.IsConfirmed ? dialog.ResponseText : null;
    }

    // Synchronous wrapper for compatibility
    public string? GetInput(string prompt, string title, string defaultValue = "")
    {
        return GetInputAsync(prompt, title, defaultValue).GetAwaiter().GetResult();
    }
}
