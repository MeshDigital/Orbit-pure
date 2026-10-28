using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Workstation;
using SLSKDONET.Views.Avalonia.Controls;
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

namespace SLSKDONET.Views.Avalonia;

public partial class WorkstationPage : UserControl
{
    private const string DensityCompactClass = "ws-density-compact";
    private const string DensityNormalClass = "ws-density-normal";
    private const string DensityTouchClass = "ws-density-touch";
    private const string DensityModeAuto = "Auto";
    private const string DensityModeCompact = "Compact";
    private const string DensityModeNormal = "Normal";
    private const string DensityModeTouch = "Touch";

    private const string OverlayCompactClass = "size-compact";
    private const string OverlayComfortClass = "size-comfort";
    private const string OverlayFullClass = "size-full";

    internal const string WorkstationPlaylistTrackFormat = "ORBIT_WorkstationPlaylistTrack";

    private GridLength _lastExpandedDrawerHeight = new(300, GridUnitType.Pixel);
    private GridLength _lastExpandedPrepRowHeight = new(1, GridUnitType.Star);
    private bool? _isCompactLayout;
    private bool _isInspectorDrawerOpen;
    private Point? _flowGridDragStart;
    private bool _overlaySizePinnedByUser;
    private bool _isOverlayResizing;
    private bool _isFlowTransitionResizing;
    private string? _flowTransitionResizeKey;
    private double _flowTransitionResizeInitialLength;
    private Point _flowTransitionResizeStart;
    private bool _isFlowPhraseMarkerResizing;
    private string? _flowPhraseMarkerResizeKey;
    private double _flowPhraseMarkerResizeInitialSeconds;
    private Point _flowPhraseMarkerResizeStart;
    private bool _isFlowPhraseRegionHandleDragging;
    private string? _flowPhraseRegionHandleKey;
    private bool _flowPhraseRegionHandleIsStart;
    private double _flowPhraseRegionHandleInitialSeconds;
    private Point _flowPhraseRegionHandleDragStart;
    private Point _overlayResizeStart;
    private double _overlayStartWidth;
    private double _overlayStartHeight;
    private AppConfig? _appConfig;
    private ConfigManager? _configManager;
    private IEventBus? _eventBus;

    private const string OverlayModeAuto = "Auto";
    private const string OverlayModeCompact = "Compact";
    private const string OverlayModeComfort = "Comfort";
    private const string OverlayModeFull = "Full";
    private const string OverlayModeManual = "Manual";

    private void SetTrackListOverlayVisible(bool isVisible)
    {
        if (this.FindControl<Border>("TrackListOverlay") is { } overlay)
        {
            overlay.IsVisible = isVisible;
        }

        if (_appConfig != null && _configManager != null)
        {
            _appConfig.WorkstationOverlayIsOpen = isVisible;
            _ = _configManager.SaveAsync(_appConfig);
        }
    }

    private void OnDrawerTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_appConfig == null || _configManager == null || sender is not TabControl tabs)
        {
            return;
        }

        _appConfig.WorkstationDrawerTabIndex = Math.Max(0, tabs.SelectedIndex);
        _ = _configManager.SaveAsync(_appConfig);
    }

    private void SetOverlaySizeClass(string className)
    {
        if (this.FindControl<Border>("TrackListOverlayPanel") is not { } panel)
        {
            return;
        }

        panel.Width = double.NaN;
        panel.Height = double.NaN;
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.VerticalAlignment = VerticalAlignment.Stretch;

        panel.Classes.Set(OverlayCompactClass, className == OverlayCompactClass);
        panel.Classes.Set(OverlayComfortClass, className == OverlayComfortClass);
        panel.Classes.Set(OverlayFullClass, className == OverlayFullClass);
    }

    private string GetOverlayModeFromClass(string className)
    {
        return className switch
        {
            OverlayCompactClass => OverlayModeCompact,
            OverlayFullClass => OverlayModeFull,
            _ => OverlayModeComfort
        };
    }

    private string GetClassFromOverlayMode(string mode)
    {
        return mode switch
        {
            OverlayModeCompact => OverlayCompactClass,
            OverlayModeFull => OverlayFullClass,
            _ => OverlayComfortClass
        };
    }

    private void HideOverlaySnapGuide()
    {
        if (this.FindControl<Border>("OverlayResizeGuidePill") is { } pill)
        {
            pill.IsVisible = false;
        }
    }

    private void ShowOverlaySnapGuide(string text)
    {
        if (this.FindControl<TextBlock>("OverlayResizeGuideText") is { } guideText)
        {
            guideText.Text = text;
        }

        if (this.FindControl<Border>("OverlayResizeGuidePill") is { } pill)
        {
            pill.IsVisible = true;
        }
    }

    private void PersistOverlayLayout(string mode, double width = 0, double height = 0)
    {
        if (_appConfig == null || _configManager == null)
        {
            return;
        }

        _appConfig.WorkstationOverlaySizeMode = mode;
        _appConfig.WorkstationOverlayManualWidth = width;
        _appConfig.WorkstationOverlayManualHeight = height;
        _ = _configManager.SaveAsync(_appConfig);
    }

    private void RestoreOverlayLayoutFromConfig()
    {
        if (_appConfig == null)
        {
            return;
        }

        var mode = _appConfig.WorkstationOverlaySizeMode ?? OverlayModeAuto;
        switch (mode)
        {
            case OverlayModeManual when _appConfig.WorkstationOverlayManualWidth > 0 && _appConfig.WorkstationOverlayManualHeight > 0:
                _overlaySizePinnedByUser = true;
                SetOverlayManualSize(_appConfig.WorkstationOverlayManualWidth, _appConfig.WorkstationOverlayManualHeight);
                break;

            case OverlayModeCompact:
            case OverlayModeComfort:
            case OverlayModeFull:
                _overlaySizePinnedByUser = true;
                SetOverlaySizeClass(GetClassFromOverlayMode(mode));
                break;

            default:
                _overlaySizePinnedByUser = false;
                ApplyAutoOverlaySize();
                break;
        }
    }

    private void SetOverlayManualSize(double width, double height)
    {
        if (this.FindControl<Border>("TrackListOverlayPanel") is not { } panel)
        {
            return;
        }

        panel.Classes.Set(OverlayCompactClass, false);
        panel.Classes.Set(OverlayComfortClass, false);
        panel.Classes.Set(OverlayFullClass, false);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.Width = width;
        panel.Height = height;
    }

    private string GetAutoOverlaySizeClass(double width)
    {
        if (width < 1320)
        {
            return OverlayCompactClass;
        }

        if (width > 1900)
        {
            return OverlayFullClass;
        }

        return OverlayComfortClass;
    }

    private void ApplyAutoOverlaySize()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : 1400;
        SetOverlaySizeClass(GetAutoOverlaySizeClass(width));
    }

    private void OnSetOverlayAutoSizeClick(object? sender, RoutedEventArgs e)
    {
        _overlaySizePinnedByUser = false;
        ApplyAutoOverlaySize();
        PersistOverlayLayout(OverlayModeAuto);
    }

    private void OnSetOverlayCompactSizeClick(object? sender, RoutedEventArgs e)
    {
        _overlaySizePinnedByUser = true;
        SetOverlaySizeClass(OverlayCompactClass);
        PersistOverlayLayout(OverlayModeCompact);
    }

    private void OnSetOverlayComfortSizeClick(object? sender, RoutedEventArgs e)
    {
        _overlaySizePinnedByUser = true;
        SetOverlaySizeClass(OverlayComfortClass);
        PersistOverlayLayout(OverlayModeComfort);
    }

    private void OnSetOverlayFullSizeClick(object? sender, RoutedEventArgs e)
    {
        _overlaySizePinnedByUser = true;
        SetOverlaySizeClass(OverlayFullClass);
        PersistOverlayLayout(OverlayModeFull);
    }

    private void OnWorkstationSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyDensityMode();
        ApplyResponsiveLayout();

        if (_overlaySizePinnedByUser)
        {
            return;
        }

        ApplyAutoOverlaySize();
    }

    /// <summary>
    /// Uses the arranged shell for breakpoint decisions, then lets the page fill that shell naturally.
    /// Larger desktops get more useful waveform and playlist space; high DPI never reduces density.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        if (this.FindControl<Grid>("CockpitGrid") is not { } grid ||
            this.FindControl<Grid>("PrepWorkspaceGrid") is not { } workspace ||
            this.FindControl<Border>("PrepInspector") is not { } inspector ||
            this.FindControl<Button>("CompactInspectorToggle") is not { } inspectorToggle)
        {
            return;
        }

        // TransitioningContentControl can measure a page with infinite space. The named shell
        // is the real arranged viewport; its bounds already exclude navigation and timeline lanes.
        var window = TopLevel.GetTopLevel(this) as MainWindow;
        var mainLayout = window?.FindControl<Grid>("MainLayoutGrid");
        var shell = window?.FindControl<SplitView>("ShellSplitView");
        var shellWidth = shell?.Bounds.Width ?? 0;
        var availableWidth = shellWidth > 0
            ? shellWidth
            : mainLayout?.ColumnDefinitions[2].ActualWidth
                ?? (Parent as Control)?.Bounds.Width
                ?? Bounds.Width;
        var availableHeight = shell?.Bounds.Height > 0
            ? shell.Bounds.Height
            : mainLayout?.Bounds.Height ?? Bounds.Height;
        if (shell is { IsPaneOpen: true, DisplayMode: SplitViewDisplayMode.Inline })
        {
            availableWidth -= shell.OpenPaneLength;
        }

        availableWidth = Math.Max(0, availableWidth);

        // Do not cap the page to a previously measured size. Star-sized rows and columns must
        // consume the actual desktop viewport, including when the window or a side panel resizes.
        grid.Width = double.NaN;
        grid.Height = double.NaN;
        grid.MaxWidth = double.PositiveInfinity;
        grid.MaxHeight = double.PositiveInfinity;
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.VerticalAlignment = VerticalAlignment.Stretch;

        var isCompact = availableWidth > 0 && availableWidth < 1000;
        var isSpacious = !isCompact && availableWidth >= 1500 && availableHeight >= 750;
        var breakpointChanged = _isCompactLayout != isCompact;
        _isCompactLayout = isCompact;
        Classes.Set("ws-layout-compact", isCompact);
        Classes.Set("ws-layout-spacious", isSpacious);
        if (this.FindControl<Border>("PrepWorkflowStrip") is { } workflowStrip)
        {
            workflowStrip.IsVisible = availableWidth <= 0 || availableWidth >= 1180;
        }

        // Workstation-local token overrides: modestly larger type and controls on roomy desktops,
        // with taller waveform and playlist rows. Compact screens retain the standard theme scale.
        Resources["TextSizeCaption"] = isSpacious ? 12d : 11d;
        Resources["TextSizeBodySm"] = isSpacious ? 13d : 12d;
        Resources["TextSizeBody"] = isSpacious ? 14d : 13d;
        Resources["TextSizeMono"] = isSpacious ? 13d : 12d;
        Resources["TextSizeH2"] = isSpacious ? 18d : 16d;
        Resources["TextSizeH3"] = isSpacious ? 15d : 14d;
        Resources["ControlHeight"] = isSpacious ? 36d : 32d;
        Resources["ControlHeightSm"] = isSpacious ? 30d : 28d;
        Resources["WsFontSm"] = isSpacious ? 11d : 10d;
        Resources["WsDeckRowHeight"] = isSpacious ? 80d : 64d;
        Resources["WsPlaylistRowHeight"] = isSpacious ? 40d : 34d;
        Resources["WsPlaylistActionHeight"] = isSpacious ? 30d : 26d;

        var inspectorWidth = availableWidth > 0
            ? Math.Clamp(availableWidth * 0.18, 276, 360)
            : 276;
        if (this.FindControl<Canvas>("TimelineTickCanvas") is { } tickCanvas && availableWidth > 0)
        {
            tickCanvas.Width = Math.Max(0, availableWidth - (isCompact ? 216 : inspectorWidth + 216));
        }

        if (breakpointChanged)
        {
            _isInspectorDrawerOpen = false;
        }

        inspectorToggle.IsVisible = isCompact;
        inspectorToggle.Content = _isInspectorDrawerOpen ? "Close inspector" : "Inspector";
        workspace.ColumnDefinitions[1].Width = new GridLength(isCompact ? 0 : inspectorWidth);

        if (isCompact)
        {
            Grid.SetColumn(inspector, 0);
            Grid.SetRowSpan(inspector, 3);
            inspector.Width = 300;
            inspector.MaxWidth = availableWidth > 24 ? Math.Max(280, availableWidth - 24) : 300;
            inspector.HorizontalAlignment = HorizontalAlignment.Right;
            inspector.IsVisible = _isInspectorDrawerOpen;
            inspector.ZIndex = 50;
        }
        else
        {
            Grid.SetColumn(inspector, 1);
            Grid.SetRowSpan(inspector, 2);
            inspector.Width = double.NaN;
            inspector.MaxWidth = double.PositiveInfinity;
            inspector.HorizontalAlignment = HorizontalAlignment.Stretch;
            inspector.IsVisible = true;
            inspector.ZIndex = 0;
        }

        if (grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var drawerRow = grid.RowDefinitions[3];
        if (IsDrawerRowCollapsed(drawerRow.Height))
        {
            return;
        }

        var drawerWasAutoSized = Math.Abs(drawerRow.Height.Value - _lastExpandedDrawerHeight.Value) < 1;
        if (!breakpointChanged && !drawerWasAutoSized)
        {
            return;
        }

        var defaultDrawerHeight = availableHeight > 0 && availableHeight < 520
            ? 110
            : availableHeight > 0 && availableHeight < 600
                ? 150
                : isCompact
                    ? 180
                    : availableHeight > 0
                        ? Math.Clamp(availableHeight * 0.28, 220, 360)
                        : 280;
        _lastExpandedDrawerHeight = new GridLength(defaultDrawerHeight);
        drawerRow.Height = _lastExpandedDrawerHeight;
    }

    private void OnToggleInspectorClick(object? sender, RoutedEventArgs e)
    {
        if (_isCompactLayout != true)
        {
            return;
        }

        _isInspectorDrawerOpen = !_isInspectorDrawerOpen;
        ApplyResponsiveLayout();
    }

    private void OnOverlayResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (this.FindControl<Border>("TrackListOverlayPanel") is not { } panel ||
            sender is not IInputElement grip)
        {
            return;
        }

        _overlaySizePinnedByUser = true;
        _isOverlayResizing = true;
        _overlayResizeStart = e.GetPosition(this);
        _overlayStartWidth = double.IsNaN(panel.Width) || panel.Width <= 0 ? panel.Bounds.Width : panel.Width;
        _overlayStartHeight = double.IsNaN(panel.Height) || panel.Height <= 0 ? panel.Bounds.Height : panel.Height;
        HideOverlaySnapGuide();
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnOverlayResizeGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_isOverlayResizing)
        {
            return;
        }

        var current = e.GetPosition(this);
        var deltaX = current.X - _overlayResizeStart.X;
        var deltaY = current.Y - _overlayResizeStart.Y;

        const double minWidth = 780;
        const double minHeight = 460;
        var maxWidth = Math.Max(minWidth, (Bounds.Width > 0 ? Bounds.Width : 1600) - 24);
        var maxHeight = Math.Max(minHeight, (Bounds.Height > 0 ? Bounds.Height : 900) - 24);

        var nextWidth = Math.Clamp(_overlayStartWidth + deltaX, minWidth, maxWidth);
        var nextHeight = Math.Clamp(_overlayStartHeight + deltaY, minHeight, maxHeight);

        var compactTarget = Math.Max(minWidth, (Bounds.Width > 0 ? Bounds.Width : 1600) - 152);
        var comfortTarget = Math.Max(minWidth, (Bounds.Width > 0 ? Bounds.Width : 1600) - 88);
        var fullTarget = Math.Max(minWidth, (Bounds.Width > 0 ? Bounds.Width : 1600) - 32);
        const double snapThreshold = 18;

        if (Math.Abs(nextWidth - compactTarget) <= snapThreshold)
        {
            nextWidth = compactTarget;
            ShowOverlaySnapGuide("Snap: Compact");
        }
        else if (Math.Abs(nextWidth - comfortTarget) <= snapThreshold)
        {
            nextWidth = comfortTarget;
            ShowOverlaySnapGuide("Snap: Comfort");
        }
        else if (Math.Abs(nextWidth - fullTarget) <= snapThreshold)
        {
            nextWidth = fullTarget;
            ShowOverlaySnapGuide("Snap: Full");
        }
        else
        {
            HideOverlaySnapGuide();
        }

        SetOverlayManualSize(nextWidth, nextHeight);
        e.Handled = true;
    }

    private void OnOverlayResizeGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isOverlayResizing)
        {
            return;
        }

        e.Pointer.Capture(null);

        _isOverlayResizing = false;
        HideOverlaySnapGuide();

        if (this.FindControl<Border>("TrackListOverlayPanel") is { } panel)
        {
            var width = double.IsNaN(panel.Width) || panel.Width <= 0 ? panel.Bounds.Width : panel.Width;
            var height = double.IsNaN(panel.Height) || panel.Height <= 0 ? panel.Bounds.Height : panel.Height;
            PersistOverlayLayout(OverlayModeManual, width, height);
        }

        e.Handled = true;
    }

    private void OnOverlayResizeGripDoubleTapped(object? sender, TappedEventArgs e)
    {
        OnSetOverlayAutoSizeClick(this, new RoutedEventArgs());
        HideOverlaySnapGuide();
        e.Handled = true;
    }

    private void ApplyDensityMode()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var configuredMode = _appConfig?.WorkstationDensityMode ?? DensityModeAuto;

        // Density follows available logical space, never physical resolution or DPI. A large
        // high-DPI desktop has more room for readable controls, not a reason to miniaturize them.
        var compactFromSpace = configuredMode == DensityModeAuto &&
                               ((width > 0 && width < 1000) || (height > 0 && height < 600));
        var isCompact = configuredMode == DensityModeCompact || compactFromSpace;
        var isTouch = configuredMode == DensityModeTouch && !isCompact;
        var isNormal = !isCompact && !isTouch;

        Classes.Set(DensityCompactClass, isCompact);
        Classes.Set(DensityTouchClass, isTouch);
        Classes.Set(DensityNormalClass, isNormal);
    }

    private void SetDensityMode(string mode)
    {
        if (_appConfig != null && _configManager != null)
        {
            _appConfig.WorkstationDensityMode = mode;
            _ = _configManager.SaveAsync(_appConfig);
        }

        ApplyDensityMode();
    }

    private void OnSetDensityAutoClick(object? sender, RoutedEventArgs e) => SetDensityMode(DensityModeAuto);
    private void OnSetDensityCompactClick(object? sender, RoutedEventArgs e) => SetDensityMode(DensityModeCompact);
    private void OnSetDensityNormalClick(object? sender, RoutedEventArgs e) => SetDensityMode(DensityModeNormal);
    private void OnSetDensityTouchClick(object? sender, RoutedEventArgs e) => SetDensityMode(DensityModeTouch);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDensityMode();

        if (DataContext is WorkstationViewModel vm)
        {
            vm.PropertyChanged += OnWorkstationViewModelPropertyChanged;
            ApplyModeLayout(vm.IsFlowMode);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is WorkstationViewModel vm)
        {
            vm.PropertyChanged -= OnWorkstationViewModelPropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnWorkstationViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkstationViewModel.IsFlowMode) && sender is WorkstationViewModel vm)
        {
            ApplyModeLayout(vm.IsFlowMode);
        }
    }

    /// <summary>
    /// Legacy mode notifications keep the prep content row and resizable playlist drawer intact.
    /// Flow Builder is a separate page and no longer changes this layout.
    /// </summary>
    private void ApplyModeLayout(bool isFlowMode)
    {
        if (this.FindControl<Grid>("CockpitGrid") is not { } grid || grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var contentRow = grid.RowDefinitions[1];
        var drawerRow = grid.RowDefinitions[3];
        contentRow.Height = new GridLength(1, GridUnitType.Star);

        if (isFlowMode)
        {
            if (!IsDrawerRowCollapsed(drawerRow.Height) && drawerRow.Height.GridUnitType == GridUnitType.Pixel)
            {
                _lastExpandedDrawerHeight = drawerRow.Height;
            }

            drawerRow.Height = new GridLength(0, GridUnitType.Pixel);
        }
        else
        {
            drawerRow.Height = _lastExpandedDrawerHeight.Value > 1
                ? _lastExpandedDrawerHeight
                : new GridLength(280, GridUnitType.Pixel);
        }
    }

    private void OnOpenTrackListOverlayClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<TabControl>("DrawerTabs") is { } tabs)
        {
            tabs.SelectedIndex = 0;
        }

        if (!_overlaySizePinnedByUser)
        {
            ApplyAutoOverlaySize();
        }

        SetTrackListOverlayVisible(true);
    }

    private void OnCloseTrackListOverlayClick(object? sender, RoutedEventArgs e)
    {
        SetTrackListOverlayVisible(false);
    }

    private void OnTrackListOverlayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Border)
        {
            SetTrackListOverlayVisible(false);
            e.Handled = true;
        }
    }

    public WorkstationPage()
    {
        InitializeComponent();
        SizeChanged += OnWorkstationSizeChanged;
        ApplyDensityMode();
        if (!Design.IsDesignMode &&
            Application.Current is App appWithServices &&
            appWithServices.Services != null)
        {
            _appConfig = appWithServices.Services.GetService(typeof(AppConfig)) as AppConfig;
            _configManager = appWithServices.Services.GetService(typeof(ConfigManager)) as ConfigManager;
            _eventBus = appWithServices.Services.GetService(typeof(IEventBus)) as IEventBus;
        }

        // The modal track browser was removed; never restore its legacy open state.
        if (_appConfig?.WorkstationOverlayIsOpen == true && _configManager != null)
        {
            _appConfig.WorkstationOverlayIsOpen = false;
            _ = _configManager.SaveAsync(_appConfig);
        }

        if (!Design.IsDesignMode &&
            Application.Current is App app && app.Services != null)
        {
            DataContext = app.Services.GetService(typeof(WorkstationViewModel))
                          as WorkstationViewModel;
        }
    }

    /// <summary>
    /// True only for a genuinely collapsed (near-zero pixel) drawer row — a Star-sized row (as
    /// used by Set Plan mode, see <see cref="ApplyModeLayout"/>) has <c>Value == 1</c> too, but
    /// is not collapsed, so unit type must be checked alongside the value.
    /// </summary>
    private static bool IsDrawerRowCollapsed(GridLength height) =>
        height.GridUnitType == GridUnitType.Pixel && height.Value <= 1;

    private void OnDrawerSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.FindControl<Grid>("CockpitGrid") is not { } grid || grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var drawerRow = grid.RowDefinitions[3];
        if (!IsDrawerRowCollapsed(drawerRow.Height))
        {
            _lastExpandedDrawerHeight = drawerRow.Height;
            drawerRow.Height = new GridLength(0, GridUnitType.Pixel);
            return;
        }

        drawerRow.Height = _lastExpandedDrawerHeight.Value > 1
            ? _lastExpandedDrawerHeight
            : new GridLength(300, GridUnitType.Pixel);
    }

    private void OnToggleDrawerClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Grid>("CockpitGrid") is not { } grid || grid.RowDefinitions.Count < 4)
        {
            return;
        }

        var drawerRow = grid.RowDefinitions[3];
        if (!IsDrawerRowCollapsed(drawerRow.Height))
        {
            _lastExpandedDrawerHeight = drawerRow.Height;
            drawerRow.Height = new GridLength(0, GridUnitType.Pixel);
            return;
        }

        drawerRow.Height = _lastExpandedDrawerHeight.Value > 1
            ? _lastExpandedDrawerHeight
            : new GridLength(220, GridUnitType.Pixel);
    }

    private void OnFlowTrackGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _flowGridDragStart = e.GetPosition(this);
    }

    private void OnPrepTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: PlaylistTrack track } ||
            DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        // Let a button's own click command run without also triggering the row gesture.
        if (e.Source is Visual source &&
            (source is Button || source.FindAncestorOfType<Button>() != null))
        {
            return;
        }

        var command = WorkstationDeckViewModel.IsTrackReadyForWorkstation(track)
            ? vm.LoadToFocusedDeckCommand
            : vm.RepairTrackCommand;
        command.Execute(track).Subscribe();
        e.Handled = true;
    }

    private void OnAnalyzeSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkstationViewModel vm) return;

        var grid = this.FindControl<VirtualGrid>("FlowTrackGrid");
        if (grid == null) return;

        var selected = grid.SelectedItems
            .OfType<PlaylistTrack>()
            .ToList();

        if (selected.Count == 0) return;

        vm.AnalyzeSelectedCuesCommand.Execute(selected).Subscribe();
    }

    private void OnDownloadTracksCtaClick(object? sender, RoutedEventArgs e)
    {
        _eventBus?.Publish(new NavigateToPageEvent("Projects"));
    }

    private void OnImportLocalFilesCtaClick(object? sender, RoutedEventArgs e)
    {
        _eventBus?.Publish(new NavigateToPageEvent("Import"));
    }

    private void OnFocusPlaylistCtaClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Grid>("CockpitGrid") is { } grid && grid.RowDefinitions.Count >= 4)
        {
            var drawerRow = grid.RowDefinitions[3];
            if (IsDrawerRowCollapsed(drawerRow.Height))
            {
                drawerRow.Height = _lastExpandedDrawerHeight.Value > 1
                    ? _lastExpandedDrawerHeight
                    : new GridLength(220, GridUnitType.Pixel);
            }
        }

        if (this.FindControl<ComboBox>("PlaylistSelector") is { } playlistSelector)
        {
            playlistSelector.Focus();
            playlistSelector.IsDropDownOpen = true;
        }
    }

    private void OnLoadIntoWorkstationCtaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkstationViewModel vm && vm.HasActivePlaylist)
        {
            vm.SetModeCommand.Execute(WorkstationMode.Flow).Subscribe();
        }
    }

    private async void OnFlowTrackGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_flowGridDragStart == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _flowGridDragStart.Value.X) < 6 &&
            Math.Abs(current.Y - _flowGridDragStart.Value.Y) < 6)
        {
            return;
        }

        if (e.Source is not Visual sourceVisual)
        {
            return;
        }

        var row = sourceVisual.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not PlaylistTrack track)
        {
            return;
        }

        var data = new DataObject();
        data.Set(WorkstationPlaylistTrackFormat, track);
        data.Set(DataFormats.Text, $"{track.Artist} - {track.Title}");

        _flowGridDragStart = null;
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private void OnFlowTransitionHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm || sender is not InputElement handle)
        {
            return;
        }

        if (handle.DataContext is not FlowTransitionOverlayViewModel transition)
        {
            return;
        }

        _isFlowTransitionResizing = true;
        _flowTransitionResizeKey = transition.TransitionKey;
        _flowTransitionResizeInitialLength = transition.LengthSeconds;
        _flowTransitionResizeStart = e.GetPosition(this);

        vm.SelectFlowTransitionCommand.Execute(transition).Subscribe();
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnFlowTransitionHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFlowTransitionResizing || string.IsNullOrWhiteSpace(_flowTransitionResizeKey))
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        var current = e.GetPosition(this);
        var deltaX = current.X - _flowTransitionResizeStart.X;
        vm.PreviewFlowTransitionLengthFromCanvasDelta(_flowTransitionResizeKey, _flowTransitionResizeInitialLength, deltaX);
        e.Handled = true;
    }

    private void OnFlowTransitionHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isFlowTransitionResizing || string.IsNullOrWhiteSpace(_flowTransitionResizeKey))
        {
            return;
        }

        if (DataContext is WorkstationViewModel vm)
        {
            var current = e.GetPosition(this);
            var deltaX = current.X - _flowTransitionResizeStart.X;
            vm.CommitFlowTransitionLengthFromCanvasDelta(_flowTransitionResizeKey, _flowTransitionResizeInitialLength, deltaX);
        }

        if (sender is InputElement handle)
        {
            e.Pointer.Capture(null);
        }

        _isFlowTransitionResizing = false;
        _flowTransitionResizeKey = null;
        _flowTransitionResizeInitialLength = 0;
        e.Handled = true;
    }

    private void OnFlowPhraseMarkerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm || sender is not InputElement marker)
        {
            return;
        }

        if (marker.DataContext is not FlowTransitionOverlayViewModel transition)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            vm.ToggleFlowPhraseMarkerEditState(transition.TransitionKey);
            e.Handled = true;
            return;
        }

        _isFlowPhraseMarkerResizing = true;
        _flowPhraseMarkerResizeKey = transition.TransitionKey;
        _flowPhraseMarkerResizeInitialSeconds = transition.PhraseGuideSeconds;
        _flowPhraseMarkerResizeStart = e.GetPosition(this);

        vm.SelectFlowTransitionCommand.Execute(transition).Subscribe();
        e.Pointer.Capture(marker);
        e.Handled = true;
    }

    private void OnFlowPhraseMarkerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFlowPhraseMarkerResizing || string.IsNullOrWhiteSpace(_flowPhraseMarkerResizeKey))
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        var current = e.GetPosition(this);
        var deltaX = current.X - _flowPhraseMarkerResizeStart.X;
        vm.PreviewFlowPhraseMarkerFromCanvasDelta(_flowPhraseMarkerResizeKey, _flowPhraseMarkerResizeInitialSeconds, deltaX);
        e.Handled = true;
    }

    private void OnFlowPhraseMarkerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isFlowPhraseMarkerResizing || string.IsNullOrWhiteSpace(_flowPhraseMarkerResizeKey))
        {
            return;
        }

        if (DataContext is WorkstationViewModel vm)
        {
            var current = e.GetPosition(this);
            var deltaX = current.X - _flowPhraseMarkerResizeStart.X;
            vm.CommitFlowPhraseMarkerFromCanvasDelta(_flowPhraseMarkerResizeKey, _flowPhraseMarkerResizeInitialSeconds, deltaX);
        }

        if (sender is InputElement marker)
        {
            e.Pointer.Capture(null);
        }

        _isFlowPhraseMarkerResizing = false;
        _flowPhraseMarkerResizeKey = null;
        _flowPhraseMarkerResizeInitialSeconds = 0;
        e.Handled = true;
    }

    private void OnPhraseRegionStartHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control handle || handle.Tag is not FlowPhraseRegion region || string.IsNullOrWhiteSpace(region.TransitionKey))
        {
            return;
        }

        _isFlowPhraseRegionHandleDragging = true;
        _flowPhraseRegionHandleIsStart = true;
        _flowPhraseRegionHandleKey = region.TransitionKey;
        _flowPhraseRegionHandleInitialSeconds = region.StartSeconds;
        _flowPhraseRegionHandleDragStart = e.GetPosition(this);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnPhraseRegionEndHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control handle || handle.Tag is not FlowPhraseRegion region || string.IsNullOrWhiteSpace(region.TransitionKey))
        {
            return;
        }

        _isFlowPhraseRegionHandleDragging = true;
        _flowPhraseRegionHandleIsStart = false;
        _flowPhraseRegionHandleKey = region.TransitionKey;
        _flowPhraseRegionHandleInitialSeconds = region.EndSeconds;
        _flowPhraseRegionHandleDragStart = e.GetPosition(this);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnPhraseRegionCreatePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm || sender is not Control regionEl)
        {
            return;
        }

        if (regionEl.Tag is not FlowPhraseRegion region || string.IsNullOrWhiteSpace(region.TransitionKey))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            vm.RemoveFlowPhraseRegionForTransition(region.TransitionKey);
            e.Handled = true;
        }
    }

    private void OnPhraseRegionStartHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFlowPhraseRegionHandleDragging || !_flowPhraseRegionHandleIsStart || string.IsNullOrWhiteSpace(_flowPhraseRegionHandleKey))
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        if (sender is InputElement handle && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            var deltaX = e.GetPosition(this).X - _flowPhraseRegionHandleDragStart.X;
            vm.PreviewFlowPhraseRegionBoundary(_flowPhraseRegionHandleKey, isStartHandle: true, _flowPhraseRegionHandleInitialSeconds, deltaX);
            e.Handled = true;
        }
    }

    private void OnPhraseRegionStartHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isFlowPhraseRegionHandleDragging || !_flowPhraseRegionHandleIsStart || string.IsNullOrWhiteSpace(_flowPhraseRegionHandleKey))
        {
            return;
        }

        if (DataContext is WorkstationViewModel vm)
        {
            var deltaX = e.GetPosition(this).X - _flowPhraseRegionHandleDragStart.X;
            vm.CommitFlowPhraseRegionBoundary(_flowPhraseRegionHandleKey, isStartHandle: true, _flowPhraseRegionHandleInitialSeconds, deltaX);
        }

        if (sender is InputElement handle)
        {
            e.Pointer.Capture(null);
        }

        _isFlowPhraseRegionHandleDragging = false;
        _flowPhraseRegionHandleKey = null;
        e.Handled = true;
    }

    private void OnPhraseRegionEndHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFlowPhraseRegionHandleDragging || _flowPhraseRegionHandleIsStart || string.IsNullOrWhiteSpace(_flowPhraseRegionHandleKey))
        {
            return;
        }

        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        if (sender is InputElement handle && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            var deltaX = e.GetPosition(this).X - _flowPhraseRegionHandleDragStart.X;
            vm.PreviewFlowPhraseRegionBoundary(_flowPhraseRegionHandleKey, isStartHandle: false, _flowPhraseRegionHandleInitialSeconds, deltaX);
            e.Handled = true;
        }
    }

    private void OnPhraseRegionEndHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isFlowPhraseRegionHandleDragging || _flowPhraseRegionHandleIsStart || string.IsNullOrWhiteSpace(_flowPhraseRegionHandleKey))
        {
            return;
        }

        if (DataContext is WorkstationViewModel vm)
        {
            var deltaX = e.GetPosition(this).X - _flowPhraseRegionHandleDragStart.X;
            vm.CommitFlowPhraseRegionBoundary(_flowPhraseRegionHandleKey, isStartHandle: false, _flowPhraseRegionHandleInitialSeconds, deltaX);
        }

        if (sender is InputElement handle)
        {
            e.Pointer.Capture(null);
        }

        _isFlowPhraseRegionHandleDragging = false;
        _flowPhraseRegionHandleKey = null;
        e.Handled = true;
    }

    private async void OnCockpitKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
        if (DataContext is not WorkstationViewModel vm)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control &&
            (e.Key == Key.D1 || e.Key == Key.NumPad1))
        {
            if (this.FindControl<TabControl>("DrawerTabs") is { } tabs)
            {
                tabs.SelectedIndex = 0;
            }

            e.Handled = true;
            return;
        }

        if (!vm.IsFlowMode && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.D)
        {
            OnToggleDrawerClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.P)
        {
            OnDownloadTracksCtaClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.I)
        {
            OnImportLocalFilesCtaClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.L)
        {
            OnLoadIntoWorkstationCtaClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Do not trigger invisible deck, drawer or timeline commands from Build Set.
        if (vm.IsFlowMode) return;

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z)
        {
            vm.UndoCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y)
        {
            vm.RedoCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            vm.PlayPauseAllCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Add || e.Key == Key.OemPlus)
        {
            vm.ZoomInCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
        {
            vm.ZoomOutCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            vm.PanLeftCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            vm.PanRightCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && vm.FocusedDeck != null)
        {
            if (e.Key == Key.V)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleVocalsCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.D)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleDrumsCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.B)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleBassCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.O)
            {
                await ExecuteStemToggle(vm.FocusedDeck.ToggleOtherCommand);
                e.Handled = true;
            }
        }
        }
        catch (Exception ex)
        {
            // Fires on nearly every cockpit keystroke — a bad command execution must not
            // be allowed to crash the whole app.
            Serilog.Log.Error(ex, "WorkstationPage: cockpit key handler failed for key {Key}", e.Key);
        }
    }

    private static async System.Threading.Tasks.Task ExecuteStemToggle(ReactiveCommand<Unit, Unit> command)
    {
        await command.Execute().FirstAsync();
    }
}
