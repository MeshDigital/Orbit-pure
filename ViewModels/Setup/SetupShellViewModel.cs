using System;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>
/// Shell-side owner of the setup wizard and the quick tour (exposed as <c>MainViewModel.Setup</c>).
/// Opens the wizard on a genuine first run or when Settings asks for it, and keeps the rest of the
/// app in step with what the wizard saved.
/// </summary>
public sealed class SetupShellViewModel : SetupObservable, IDisposable
{
    private readonly Func<SetupWizardViewModel> _wizardFactory;
    private readonly ILogger<SetupShellViewModel> _logger;
    private readonly IDisposable? _openSubscription;
    private readonly IDisposable? _tourSubscription;
    private readonly Action? _onWizardOpened;
    private readonly Action? _onWizardClosed;
    private SetupWizardViewModel? _wizard;

    public SetupShellViewModel(
        Func<SetupWizardViewModel> wizardFactory,
        ISetupStateStore state,
        ILogger<SetupShellViewModel> logger,
        IEventBus? eventBus = null,
        Action? onWizardOpened = null,
        Action? onWizardClosed = null)
    {
        _wizardFactory = wizardFactory;
        _logger = logger;
        _onWizardOpened = onWizardOpened;
        _onWizardClosed = onWizardClosed;
        Tour = new QuickTourViewModel(state, logger);

        if (eventBus != null)
        {
            _openSubscription = eventBus.GetEvent<OpenSetupWizardRequestedEvent>()
                .Subscribe(_ => Dispatcher.UIThread.Post(OpenWizard));
            _tourSubscription = eventBus.GetEvent<StartTourRequestedEvent>()
                .Subscribe(_ => Dispatcher.UIThread.Post(StartTour));
        }
    }

    public QuickTourViewModel Tour { get; }

    public SetupWizardViewModel? Wizard
    {
        get => _wizard;
        private set
        {
            if (SetProperty(ref _wizard, value))
                OnPropertyChanged(nameof(IsWizardOpen));
        }
    }

    public bool IsWizardOpen => Wizard != null;

    /// <summary>Opens a fresh wizard (current values loaded from config). No-op while one is open.</summary>
    public void OpenWizard()
    {
        if (Wizard != null) return;
        if (Tour.IsOpen) Tour.Close(completed: false);

        var wizard = _wizardFactory();
        wizard.Closed += OnWizardClosed;
        Wizard = wizard;
        _logger.LogInformation("Setup wizard opened");
        _onWizardOpened?.Invoke();
        _ = InitializeWizardAsync(wizard);
    }

    /// <summary>Asks the view to start the tour (it knows which navigation items are visible).</summary>
    public void StartTour()
    {
        if (Wizard != null) return;
        Tour.RequestStart();
    }

    private async System.Threading.Tasks.Task InitializeWizardAsync(SetupWizardViewModel wizard)
    {
        try
        {
            await wizard.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup wizard failed to load current settings");
        }
    }

    private void OnWizardClosed(object? sender, SetupWizardOutcome outcome)
    {
        if (sender is SetupWizardViewModel wizard)
        {
            wizard.Closed -= OnWizardClosed;
            wizard.Dispose();
        }
        Wizard = null;
        try
        {
            _onWizardClosed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: refreshing the shell after the wizard closed failed");
        }

        if (outcome == SetupWizardOutcome.FinishedStartTour)
            Dispatcher.UIThread.Post(StartTour);
    }

    public void Dispose()
    {
        _openSubscription?.Dispose();
        _tourSubscription?.Dispose();
        if (_wizard != null)
        {
            _wizard.Closed -= OnWizardClosed;
            _wizard.Dispose();
        }
    }
}
