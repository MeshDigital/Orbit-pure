using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.Configuration;
using SLSKDONET.Views;
using System;

namespace SLSKDONET.Views.Avalonia
{
    public partial class MainWindow : Window
    {
        private WindowState _preFullScreenWindowState = WindowState.Normal;
        private SystemDecorations _preFullScreenDecorations = SystemDecorations.Full;

        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif

            // Get config from DataContext (MainViewModel will set it)
            this.Opened += OnWindowOpened;
            this.Closing += OnWindowClosing;

            // Responsive layout: auto-collapse navigation on small screens
            this.PropertyChanged += OnWindowPropertyChanged;

            // Global Keyboard Shortcuts
            this.KeyDown += OnKeyDown;

            this.DataContextChanged += (_, _) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged -= OnMainViewModelPropertyChanged; // avoid double-subscribing if DataContext is reassigned
                    vm.PropertyChanged += OnMainViewModelPropertyChanged;
                }
            };
        }

        private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsZenMode) && sender is MainViewModel vm)
                OnZenModeChanged(vm.IsZenMode);
        }

        /// <summary>
        /// Theater Mode used to only hide ORBIT's own chrome (nav/top bar) within whatever size
        /// the window already was — the taskbar and native window border stayed visible even in
        /// "fullscreen visualizer" mode. This makes it cover the whole screen with zero chrome.
        ///
        /// Deliberately NOT WindowState.FullScreen: this window uses
        /// TransparencyLevelHint="Mica, AcrylicBlur", and toggling real FullScreen on top of that
        /// reproduced a hard, silent process-terminating crash on this machine (no managed
        /// exception, no Windows Event Log entry — consistent with a native compositor/swapchain
        /// failure, not a .NET fault). Maximized + no system decorations gets the same visual
        /// result (borderless, edge-to-edge) via a window-state path this app already exercises
        /// safely on every launch that restores a maximized window.
        /// </summary>
        private void OnZenModeChanged(bool isZenMode)
        {
            if (isZenMode)
            {
                _preFullScreenWindowState = WindowState;
                _preFullScreenDecorations = SystemDecorations;
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = _preFullScreenWindowState;
                SystemDecorations = _preFullScreenDecorations;
            }
        }

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            // Never apply window-level media shortcuts while typing in text input controls.
            if (IsTypingIntoTextInput(e))
                return;

            if (DataContext is MainViewModel vm)
            {
                switch (e.Key)
                {
                    case global::Avalonia.Input.Key.Space:
                        // Only handle space if we're not interacting with a button or list item that might need it
                        // But for media apps, Space usually forces Play/Pause unless typing.
                        // We'll set Handled=true to prevent button clicks if we want to enforce Play/Pause
                        // checking modifiers to avoid conflicts (e.g. Ctrl+Space)
                        if (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.None)
                        {
                            if (vm.PlayerViewModel.TogglePlayPauseCommand.CanExecute(null))
                            {
                                vm.PlayerViewModel.TogglePlayPauseCommand.Execute(null);
                                e.Handled = true;
                            }
                        }
                        break;
                        
                    case global::Avalonia.Input.Key.Left:
                        if (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.None)
                        {
                            if (vm.PlayerViewModel.PreviousTrackCommand.CanExecute(null))
                            {
                                vm.PlayerViewModel.PreviousTrackCommand.Execute(null);
                                e.Handled = true;
                            }
                        }
                        break;
                        
                    case global::Avalonia.Input.Key.Right:
                         if (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.None)
                        {
                            if (vm.PlayerViewModel.NextTrackCommand.CanExecute(null))
                            {
                                vm.PlayerViewModel.NextTrackCommand.Execute(null);
                                e.Handled = true;
                            }
                        }
                        break;

                    case global::Avalonia.Input.Key.Escape:
                        // Theater Mode is now real OS fullscreen (see OnZenModeChanged) — Escape
                        // is the expected way out on every platform, so it needs an explicit exit,
                        // not just whatever the window manager's own chrome would normally offer.
                        if (vm.IsZenMode)
                        {
                            vm.PlayerViewModel.ToggleTheaterModeCommand.Execute(null);
                            e.Handled = true;
                        }
                        else if (vm.PlayerViewModel.IsExpandedPlayerOpen)
                        {
                            vm.PlayerViewModel.ToggleExpandedPlayerCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;
                }
            }
        }

        private bool IsTypingIntoTextInput(KeyEventArgs e)
        {
            if (e.Source is TextBox || e.Source is AutoCompleteBox)
                return true;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            return focused is TextBox || focused is AutoCompleteBox;
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Listen for Bounds changes to detect window resize
            if (e.Property == BoundsProperty && DataContext is MainViewModel vm)
            {
                var width = Bounds.Width;
                
                // Auto-collapse navigation below 800px
                if (width < 800 && !vm.IsNavigationCollapsed)
                    vm.IsNavigationCollapsed = true;
                else if (width >= 1200 && vm.IsNavigationCollapsed)
                    vm.IsNavigationCollapsed = false;

                // Responsive breakpoints for Epic 12 #111 / #112
                vm.IsMobileMode  = width < 600;
                vm.IsTabletMode  = width >= 600 && width < 1024;

                var responsiveDisplayMode = MainViewModel.ResolveSidebarDisplayMode(width);
                if (this.FindControl<SplitView>("ShellSplitView") is { } shellSplitView && shellSplitView.DisplayMode != responsiveDisplayMode)
                {
                    shellSplitView.DisplayMode = responsiveDisplayMode;
                }

                // Auto-close Timeline/Overlays panels below tablet threshold
                if (width < 1024)
                {
                    if (vm.IsTimelinePanelOpen)  vm.IsTimelinePanelOpen  = false;
                    if (vm.IsOverlaysPanelOpen) vm.IsOverlaysPanelOpen = false;
                }
            }
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            // Try to get config from app services
            if (App.Current is App app && app.Services != null)
            {
                var config = app.Services.GetService(typeof(AppConfig)) as AppConfig;
                var configManager = app.Services.GetService(typeof(ConfigManager)) as ConfigManager;
                
                if (config != null)
                {
                    // Restore window state
                    if (!double.IsNaN(config.WindowWidth) && config.WindowWidth > 0)
                        Width = config.WindowWidth;
                    
                    if (!double.IsNaN(config.WindowHeight) && config.WindowHeight > 0)
                        Height = config.WindowHeight;
                    
                    if (!double.IsNaN(config.WindowX) && !double.IsNaN(config.WindowY))
                    {
                        Position = new PixelPoint((int)config.WindowX, (int)config.WindowY);
                    }
                    
                    if (config.WindowMaximized)
                    {
                        WindowState = WindowState.Maximized;
                    }

                    // Restore five-column panel state (Epic 12 #110)
                    if (DataContext is MainViewModel vm)
                    {
                        vm.IsTimelinePanelOpen  = config.IsTimelinePanelOpen;
                        vm.TimelinePanelWidth   = config.TimelinePanelWidth > 0 ? config.TimelinePanelWidth : 300;
                        vm.IsOverlaysPanelOpen  = config.IsOverlaysPanelOpen;
                        vm.OverlaysPanelWidth   = config.OverlaysPanelWidth > 0 ? config.OverlaysPanelWidth : 250;
                    }
                }
            }
        }

        private bool _hasShownTrayHintThisSession;

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Closing the window (X button, Alt+F4, taskbar close) hides to the tray instead of
            // exiting, so the download engine keeps running unattended in the background — the
            // tray icon already had Show/Hide/Exit as if this were the design, it just wasn't
            // wired up. Only an explicit "Exit" from the tray menu actually shuts the app down.
            if (App.Current is App exitCheckApp && !exitCheckApp.IsExitRequested)
            {
                e.Cancel = true;
                Hide();

                if (!_hasShownTrayHintThisSession)
                {
                    _hasShownTrayHintThisSession = true;
                    try
                    {
                        if (exitCheckApp.Services?.GetService(typeof(SLSKDONET.Services.WindowsToastService))
                            is SLSKDONET.Services.WindowsToastService toastService)
                        {
                            toastService.ShowIfUnfocused(
                                "ORBIT is still running",
                                "Downloads continue in the background. Right-click the tray icon to reopen or exit.");
                        }
                    }
                    catch { /* Toast is a courtesy hint, never worth failing the close over. */ }
                }

                return;
            }

            // Save window state
            if (App.Current is App app && app.Services != null)
            {
                var config = app.Services.GetService(typeof(AppConfig)) as AppConfig;
                var configManager = app.Services.GetService(typeof(ConfigManager)) as ConfigManager;
                
                if (config != null && configManager != null)
                {
                    config.WindowWidth = Width;
                    config.WindowHeight = Height;
                    config.WindowX = Position.X;
                    config.WindowY = Position.Y;
                    config.WindowMaximized = WindowState == WindowState.Maximized;

                    // Persist five-column panel state (Epic 12 #110)
                    if (DataContext is MainViewModel vm)
                    {
                        config.IsTimelinePanelOpen  = vm.IsTimelinePanelOpen;
                        config.TimelinePanelWidth   = vm.TimelinePanelWidth;
                        config.IsOverlaysPanelOpen  = vm.IsOverlaysPanelOpen;
                        config.OverlaysPanelWidth   = vm.OverlaysPanelWidth;
                    }
                    
                    configManager.Save(config);
                }
            }
        }
    }
}
