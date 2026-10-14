using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Views.Avalonia.Controls;

namespace SLSKDONET.Services;

/// <summary>
/// Text-prompt service backed by the shared Avalonia <see cref="PromptDialog"/>.
/// (Previously used a hand-built InputDialog whose OK button looked up its TextBox via
/// FindControl on code-created controls with no name scope, so OK silently did nothing.)
/// </summary>
public class UserInputService : IUserInputService
{
    public async Task<string?> GetInputAsync(string prompt, string title, string defaultValue = "")
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            throw new InvalidOperationException("Cannot show dialog without a main window.");
        }

        var dialog = new PromptDialog(title, prompt, defaultValue);
        return await dialog.ShowDialog<string?>(desktop.MainWindow);
    }

    // Synchronous wrapper for compatibility
    public string? GetInput(string prompt, string title, string defaultValue = "")
    {
        return GetInputAsync(prompt, title, defaultValue).GetAwaiter().GetResult();
    }
}
