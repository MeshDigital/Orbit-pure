using System;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>
/// Shell-side owner of the Library Maintenance wizard (exposed as <c>MainViewModel.Maintenance</c>).
/// Opens the overlay when <see cref="OpenLibraryMaintenanceRequestedEvent"/> is published and
/// disposes the wizard on close, mirroring the setup shell's lifecycle.
/// </summary>
public sealed class MaintenanceShellViewModel : IDisposable
{
    private readonly Func<LibraryMaintenanceViewModel> _wizardFactory;
    private readonly ILogger<MaintenanceShellViewModel> _logger;
    private readonly IDisposable? _openSubscription;
    private readonly Action<Action> _post;
    private LibraryMaintenanceViewModel? _wizard;

    public MaintenanceShellViewModel(
        Func<LibraryMaintenanceViewModel> wizardFactory,
        ILogger<MaintenanceShellViewModel> logger,
        IEventBus? eventBus = null,
        Action<Action>? postToUi = null)
    {
        _post = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _wizardFactory = wizardFactory;
        _logger = logger;

        if (eventBus != null)
        {
            _openSubscription = eventBus.GetEvent<OpenLibraryMaintenanceRequestedEvent>()
                .Subscribe(_ => _post(Open));
        }
    }

    public LibraryMaintenanceViewModel? Wizard { get; private set; }

    public bool IsOpen => Wizard != null;

    /// <summary>Opens a fresh wizard. No-op while one is already open.</summary>
    public void Open()
    {
        if (Wizard != null) return;

        var wizard = _wizardFactory();
        wizard.Closed += OnWizardClosed;
        Wizard = wizard;
        _logger.LogInformation("Library maintenance wizard opened");
    }

    private void OnWizardClosed(object? sender, EventArgs e)
    {
        if (sender is LibraryMaintenanceViewModel wizard)
        {
            wizard.Closed -= OnWizardClosed;
            wizard.Dispose();
        }
        Wizard = null;
        _logger.LogInformation("Library maintenance wizard closed");
    }

    public void Dispose()
    {
        _openSubscription?.Dispose();
        if (Wizard != null)
        {
            Wizard.Closed -= OnWizardClosed;
            Wizard.Dispose();
            Wizard = null;
        }
    }
}
