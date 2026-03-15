using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Service for handling global hotkeys with focus-awareness.
/// Intercepts key events to provide shortcuts that don't interfere with text input.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly TopLevel? _topLevel;
    private bool _isDisposed;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger)
    {
        _logger = logger;

        // Get the main window (TopLevel) for global key interception
        _topLevel = Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (_topLevel != null)
        {
            // Use tunnel routing to intercept keys before they reach focused controls
            _topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _logger.LogInformation("GlobalHotkeyService initialized with tunnel key interception");
        }
        else
        {
            _logger.LogWarning("GlobalHotkeyService: Could not find TopLevel for key interception");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Check if the focused element is a text input control
        var focusManager = TopLevel.GetTopLevel(_topLevel)?.FocusManager;
        var focusedElement = focusManager?.GetFocusedElement();
        bool isTextInputFocused = focusedElement is TextBox;

        // Handle media keys that should work globally but not interfere with text input
        if (e.Key == Key.Space && !isTextInputFocused)
        {
            // Space for play/pause - let the KeyBinding handle it
            // We don't consume it here, just log for debugging
            _logger.LogDebug("Space key intercepted globally (not consuming, letting KeyBinding handle)");
            return;
        }

        // Add more global keys here as needed
        // For example, media keys like Play, Next, Previous, VolumeUp, VolumeDown
        // But for now, we rely on KeyBindings for most shortcuts
    }

    public void Dispose()
    {
        if (!_isDisposed && _topLevel != null)
        {
            _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _isDisposed = true;
        }
    }
}