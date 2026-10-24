using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services.Shell;

/// <summary>
/// The ORBIT notification-area (tray) icon and its menu, plus the glue to the Windows taskbar
/// button. Left-click toggles the main window; the context menu offers transport, the download
/// engine toggle, show/hide, Settings and Quit, all reflecting live state from
/// <see cref="ShellStateMonitor"/>.
///
/// DI singleton: the constructor only stores dependencies and cannot throw. <see cref="Start"/>
/// runs once the main window is open; any failure there is logged and leaves the app without a
/// tray icon — in which case <see cref="ShouldHideOnClose"/> is false, so the close button quits
/// instead of hiding the window somewhere the user could never get it back from.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private const string IconUri = "avares://ORBIT/app_icon.ico";

    private readonly ShellStateMonitor _monitor;
    private readonly WindowsTaskbarIntegration _taskbar;
    private readonly AppConfig _config;
    private readonly ILogger<TrayIconController> _logger;
    private readonly TaskbarProgressMapper _progressMapper = new();

    private Window? _window;
    private TrayIcon? _trayIcon;
    private WindowState _restoreState = WindowState.Normal;
    private bool _wasShown = true;
    private bool _closeHintShown;
    private DispatcherTimer? _errorFlashTimer;

    private NativeMenuItem? _nowPlayingItem;
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _previousItem;
    private NativeMenuItem? _downloadsItem;
    private NativeMenuItem? _windowItem;

    public TrayIconController(
        ShellStateMonitor monitor,
        WindowsTaskbarIntegration taskbar,
        AppConfig config,
        ILogger<TrayIconController> logger)
    {
        _monitor = monitor;
        _taskbar = taskbar;
        _config = config;
        _logger = logger;
    }

    /// <summary>True once the tray icon is really in the notification area.</summary>
    public bool IsTrayIconActive { get; private set; }

    /// <summary>Close button behaviour: hide to tray only when the setting is on and the tray icon exists.</summary>
    public bool ShouldHideOnClose => _config.CloseToTray && IsTrayIconActive;

    /// <summary>UI thread, once, after the main window has been shown.</summary>
    public void Start(Window mainWindow)
    {
        if (_window is not null) return;
        _window = mainWindow;
        _restoreState = mainWindow.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

        try
        {
            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            IsTrayIconActive = false;
            _logger.LogWarning(ex, "[Tray] Could not create the tray icon; the close button will quit ORBIT this session");
        }

        mainWindow.PropertyChanged += OnWindowPropertyChanged;

        _taskbar.ThumbButtonClicked += OnThumbButtonClicked;
        _taskbar.Attach(mainWindow);

        _monitor.StateChanged += Apply;
        _monitor.Start(IsWindowShown);
        Apply(_monitor.Current);
    }

    private void CreateTrayIcon()
    {
        if (Application.Current is not { } app) return;

        // The multi-resolution .ico (16…256) instead of one 790 px PNG: Windows picks the exact
        // small-icon size for the current DPI, so the tray glyph stays crisp at 16/20/24/32 px.
        WindowIcon icon;
        using (var stream = AssetLoader.Open(new Uri(IconUri)))
            icon = new WindowIcon(stream);

        _nowPlayingItem = new NativeMenuItem("Nothing playing") { IsEnabled = false };
        _playPauseItem = Item("Play", (_, _) => _monitor.TogglePlayPause());
        _nextItem = Item("Next", (_, _) => _monitor.NextTrack());
        _previousItem = Item("Previous", (_, _) => _monitor.PreviousTrack());
        _downloadsItem = Item("Pause downloads", (_, _) => _monitor.ToggleDownloads());
        _windowItem = Item("Hide ORBIT", (_, _) => ToggleWindow());
        var settingsItem = Item("Settings", (_, _) => OpenSettings());
        var quitItem = Item("Quit ORBIT", (_, _) => Quit());

        var menu = new NativeMenu();
        menu.Items.Add(_nowPlayingItem);
        menu.Items.Add(_playPauseItem);
        menu.Items.Add(_nextItem);
        menu.Items.Add(_previousItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_downloadsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_windowItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = ShellStatusFormatter.AppName,
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ToggleWindow();

        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });
        IsTrayIconActive = true;
    }

    private NativeMenuItem Item(string header, EventHandler handler)
    {
        var item = new NativeMenuItem(header);
        item.Click += (s, e) =>
        {
            try
            {
                handler(s, e);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Tray] Menu item '{Header}' failed", item.Header);
            }
        };
        return item;
    }

    // ── State → tray / taskbar ───────────────────────────────────────────────────────────

    private void Apply(ShellState state)
    {
        if (_trayIcon is not null)
        {
            var tip = ShellStatusFormatter.BuildTooltip(state);
            if (_trayIcon.ToolTipText != tip) _trayIcon.ToolTipText = tip;

            var menu = ShellStatusFormatter.BuildMenu(state);
            SetItem(_nowPlayingItem, menu.NowPlayingHeader, enabled: false);
            SetItem(_playPauseItem, menu.PlayPauseLabel, menu.CanPlayPause);
            SetItem(_nextItem, "Next", menu.CanNext);
            SetItem(_previousItem, "Previous", menu.CanPrevious);
            SetItem(_downloadsItem, menu.DownloadsLabel, menu.CanToggleDownloads);
            SetItem(_windowItem, menu.WindowLabel, enabled: true);
        }

        ApplyTaskbar(state);
    }

    private static void SetItem(NativeMenuItem? item, string header, bool enabled)
    {
        if (item is null) return;
        if (item.Header != header) item.Header = header;
        if (item.IsEnabled != enabled) item.IsEnabled = enabled;
    }

    private void ApplyTaskbar(ShellState state)
    {
        if (!_taskbar.IsActive) return;

        var now = DateTime.UtcNow;
        var progress = _progressMapper.Map(state.Downloads, now);
        _taskbar.Apply(ShellStatusFormatter.BuildThumbBar(state.Playback), progress);

        // The red "a download just failed" flash is time-based: re-apply once it has run out even
        // if nothing else changes in the meantime.
        if (_progressMapper.ErrorFlashEndsUtc(now) is { } ends && _errorFlashTimer is null)
        {
            _errorFlashTimer = new DispatcherTimer { Interval = ends - now + TimeSpan.FromMilliseconds(50) };
            _errorFlashTimer.Tick += (_, _) =>
            {
                _errorFlashTimer?.Stop();
                _errorFlashTimer = null;
                ApplyTaskbar(_monitor.Current);
            };
            _errorFlashTimer.Start();
        }
    }

    private void OnThumbButtonClicked(ShellGlyph glyph)
    {
        switch (glyph)
        {
            case ShellGlyph.Previous: _monitor.PreviousTrack(); break;
            case ShellGlyph.Next: _monitor.NextTrack(); break;
            default: _monitor.TogglePlayPause(); break;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
        if (_window is null) return;

        if (_window.WindowState is WindowState.Normal or WindowState.Maximized)
            _restoreState = _window.WindowState;

        var shown = IsWindowShown();
        if (shown && !_wasShown && _window.IsVisible)
            _taskbar.OnWindowShown();
        _wasShown = shown;

        _monitor.Invalidate();
    }

    // ── Window actions ───────────────────────────────────────────────────────────────────

    private bool IsWindowShown() =>
        _window is { IsVisible: true } w && w.WindowState != WindowState.Minimized;

    /// <summary>Tray left-click: hide when on screen, otherwise show, restore and bring to front.</summary>
    public void ToggleWindow()
    {
        if (IsWindowShown()) HideWindow();
        else ShowWindow();
    }

    public void ShowWindow()
    {
        if (_window is not { } w) return;
        try
        {
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = _restoreState;
            w.Activate();
            _taskbar.BringToForeground(w);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Tray] Showing the main window failed");
        }
    }

    public void HideWindow()
    {
        if (_window is not { } w) return;
        try
        {
            w.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Tray] Hiding the main window failed");
        }
    }

    private void OpenSettings()
    {
        ShowWindow();
        if (_window?.DataContext is SLSKDONET.Views.MainViewModel vm && vm.NavigateSettingsCommand.CanExecute(null))
            vm.NavigateSettingsCommand.Execute(null);
    }

    private void Quit()
    {
        if (Application.Current is App app) app.IsExitRequested = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>
    /// Called by the main window after the close button hid it to the tray. Shows the "still
    /// running" hint once per session only.
    /// </summary>
    public bool TryClaimCloseHint()
    {
        if (_closeHintShown) return false;
        _closeHintShown = true;
        return true;
    }

    public void Dispose()
    {
        _monitor.StateChanged -= Apply;
        _taskbar.ThumbButtonClicked -= OnThumbButtonClicked;
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _errorFlashTimer?.Stop();
        _errorFlashTimer = null;
        _monitor.Dispose();
        _taskbar.Dispose();
        if (_trayIcon is not null)
        {
            try
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Tray] Removing the tray icon failed (process is exiting anyway)");
            }
            _trayIcon = null;
        }
    }
}
