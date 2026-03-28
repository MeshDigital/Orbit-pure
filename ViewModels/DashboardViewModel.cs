using System;
using System.Reactive.Disposables;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Coordinates the three-column shell state (left navigation, center content, right panel)
/// and persists layout preferences so operators return to their last working configuration.
///
/// This ViewModel acts as a thin coordinator: it does not own any business logic and only
/// bridges the layout surface of the shell (column widths, visibility) with the
/// <see cref="IRightPanelService"/> and the persistent <see cref="AppConfig"/>.
/// </summary>
public class DashboardViewModel : ReactiveObject, IDisposable
{
    private const double MinRightPanelWidth = 200;

    private readonly IRightPanelService _rightPanelService;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly CompositeDisposable _disposables = new();

    // ── Right-panel width (persisted) ──────────────────────────────────────
    private double _rightPanelWidth;

    /// <summary>
    /// Width of the right inspector/player panel in device-independent pixels.
    /// Persisted across sessions via <see cref="SaveLayout"/>.
    /// </summary>
    public double RightPanelWidth
    {
        get => _rightPanelWidth;
        set => this.RaiseAndSetIfChanged(ref _rightPanelWidth, Math.Max(MinRightPanelWidth, value));
    }

    // ── Navigation visibility ──────────────────────────────────────────────
    private bool _isNavigationCollapsed;

    /// <summary>
    /// <see langword="true"/> when the left navigation column is collapsed to its minimal width.
    /// Persisted across sessions via <see cref="SaveLayout"/>.
    /// </summary>
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set => this.RaiseAndSetIfChanged(ref _isNavigationCollapsed, value);
    }

    // ── Right-panel visibility (delegated to IRightPanelService) ───────────

    /// <summary>
    /// <see langword="true"/> when the right inspector panel is open.
    /// Delegates to <see cref="IRightPanelService.IsPanelOpen"/>.
    /// </summary>
    public bool IsRightPanelOpen
    {
        get => _rightPanelService.IsPanelOpen;
        set => _rightPanelService.IsPanelOpen = value;
    }

    // ── Commands ───────────────────────────────────────────────────────────

    /// <summary>Toggles the left navigation column between collapsed and expanded.</summary>
    public ICommand ToggleNavigationCommand { get; }

    /// <summary>Toggles the right inspector panel open or closed.</summary>
    public ICommand ToggleRightPanelCommand { get; }

    // ── Constructor ────────────────────────────────────────────────────────

    public DashboardViewModel(
        IRightPanelService rightPanelService,
        AppConfig config,
        ConfigManager configManager)
    {
        _rightPanelService = rightPanelService ?? throw new ArgumentNullException(nameof(rightPanelService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

        // Restore persisted layout
        RestoreLayout();

        // Mirror IsPanelOpen changes from the service into IsRightPanelOpen so bound UIs update.
        // This follows the same reactive-proxy pattern used by SidebarViewModel.
        this.WhenAnyValue(x => x._rightPanelService.IsPanelOpen)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsRightPanelOpen)))
            .DisposeWith(_disposables);

        ToggleNavigationCommand = ReactiveCommand.Create(ToggleNavigation);
        ToggleRightPanelCommand = ReactiveCommand.Create(ToggleRightPanel);
    }

    // ── Layout persistence ─────────────────────────────────────────────────

    /// <summary>
    /// Restores the three-column layout from the last persisted state in <see cref="AppConfig"/>.
    /// Called once during construction.
    /// </summary>
    public void RestoreLayout()
    {
        _rightPanelWidth = Math.Max(MinRightPanelWidth, _config.DashboardRightPanelWidth);
        _isNavigationCollapsed = _config.DashboardIsNavigationCollapsed;
        _rightPanelService.IsPanelOpen = _config.DashboardIsRightPanelOpen;

        this.RaisePropertyChanged(nameof(RightPanelWidth));
        this.RaisePropertyChanged(nameof(IsNavigationCollapsed));
        this.RaisePropertyChanged(nameof(IsRightPanelOpen));
    }

    /// <summary>
    /// Persists the current three-column layout state to <see cref="AppConfig"/> and
    /// writes it to disk via <see cref="ConfigManager.Save"/>.
    /// Call this when the shell is closing or when the user explicitly locks in a layout.
    /// </summary>
    public void SaveLayout()
    {
        _config.DashboardRightPanelWidth = _rightPanelWidth;
        _config.DashboardIsNavigationCollapsed = _isNavigationCollapsed;
        _config.DashboardIsRightPanelOpen = _rightPanelService.IsPanelOpen;
        _configManager.Save(_config);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private void ToggleNavigation() => IsNavigationCollapsed = !IsNavigationCollapsed;

    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose() => _disposables.Dispose();
}
