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

        // This constructor runs as part of MainViewModel's DI graph, which App.axaml.cs resolves
        // while desktop.MainWindow is still the splash screen (the real MainWindow isn't assigned
        // until a few lines later, after this constructor has already returned) — attaching here
        // would silently bind every DJ hotkey to a window that closes moments later, forever
        // (KeyboardEventRouter.Attach used to no-op on any later call once _topLevel was non-
        // null). Attach here anyway for any code path that constructs this service after the
        // real window is already up; App.axaml.cs calls AttachToCurrentMainWindow() again right
        // after showing the real MainWindow to cover the startup race.
        AttachToCurrentMainWindow();
    }

    /// <summary>
    /// Re-resolves the app's current TopLevel and (re)attaches the router to it — see the
    /// constructor's comment for why this needs to be callable again after construction.
    /// </summary>
    public void AttachToCurrentMainWindow()
    {
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