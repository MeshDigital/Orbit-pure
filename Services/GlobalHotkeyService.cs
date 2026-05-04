using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Input;

namespace SLSKDONET.Services;

/// <summary>
/// Service for handling global hotkeys with focus-awareness.
/// Attaches a tunnel handler to the main window and delegates to
/// <see cref="KeyboardEventRouter"/> for all DJ-action dispatch.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly KeyboardEventRouter          _router;
    private bool _isDisposed;

    public GlobalHotkeyService(
        ILogger<GlobalHotkeyService> logger,
        KeyboardEventRouter router)
    {
        _logger = logger;
        _router = router;

        // Attach the router to the main window TopLevel
        var topLevel = Application.Current?.ApplicationLifetime is ClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as TopLevel
            : null;

        if (topLevel != null)
        {
            _router.Attach(topLevel);
            _logger.LogInformation("GlobalHotkeyService: router attached to TopLevel");
        }
        else
        {
            _logger.LogWarning("GlobalHotkeyService: could not find TopLevel – router not attached");
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _router.Dispose();
            _isDisposed = true;
        }
    }
}