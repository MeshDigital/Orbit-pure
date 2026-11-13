using System;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>
/// Shell-side owner of the Library Maintenance overlays (exposed as <c>MainViewModel.Maintenance</c>):
/// the maintenance wizard (<see cref="OpenLibraryMaintenanceRequestedEvent"/>) and the exact-duplicate
/// review (<see cref="OpenExactDuplicatesRequestedEvent"/>). Opens a fresh view model per open and
/// disposes it on close, mirroring the setup shell's lifecycle. Raises change notifications: the
/// main window binds <see cref="IsOpen"/>, which never updated before and kept the wizard hidden.
/// </summary>
public sealed class MaintenanceShellViewModel : ReactiveObject, IDisposable
{
    private readonly Func<LibraryMaintenanceViewModel> _wizardFactory;
    private readonly Func<ExactDuplicatesViewModel>? _duplicatesFactory;
    private readonly ILogger<MaintenanceShellViewModel> _logger;
    private readonly IDisposable? _openSubscription;
    private readonly IDisposable? _openDuplicatesSubscription;
    private readonly Action<Action> _post;
    private LibraryMaintenanceViewModel? _wizard;
    private ExactDuplicatesViewModel? _duplicates;

    public MaintenanceShellViewModel(
        Func<LibraryMaintenanceViewModel> wizardFactory,
        ILogger<MaintenanceShellViewModel> logger,
        IEventBus? eventBus = null,
        Action<Action>? postToUi = null,
        Func<ExactDuplicatesViewModel>? duplicatesFactory = null)
    {
        _post = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _wizardFactory = wizardFactory;
        _duplicatesFactory = duplicatesFactory;
        _logger = logger;

        if (eventBus != null)
        {
            _openSubscription = eventBus.GetEvent<OpenLibraryMaintenanceRequestedEvent>()
                .Subscribe(_ => _post(Open));
            _openDuplicatesSubscription = eventBus.GetEvent<OpenExactDuplicatesRequestedEvent>()
                .Subscribe(_ => _post(OpenDuplicates));
        }
    }

    public LibraryMaintenanceViewModel? Wizard
    {
        get => _wizard;
        private set
        {
            this.RaiseAndSetIfChanged(ref _wizard, value);
            this.RaisePropertyChanged(nameof(IsOpen));
            this.RaisePropertyChanged(nameof(IsAnyOpen));
        }
    }

    public ExactDuplicatesViewModel? Duplicates
    {
        get => _duplicates;
        private set
        {
            this.RaiseAndSetIfChanged(ref _duplicates, value);
            this.RaisePropertyChanged(nameof(IsDuplicatesOpen));
            this.RaisePropertyChanged(nameof(IsAnyOpen));
        }
    }

    /// <summary>The maintenance wizard is showing.</summary>
    public bool IsOpen => Wizard != null;

    /// <summary>The exact-duplicate review is showing.</summary>
    public bool IsDuplicatesOpen => Duplicates != null;

    /// <summary>Either overlay is showing (drives the scrim).</summary>
    public bool IsAnyOpen => IsOpen || IsDuplicatesOpen;

    /// <summary>Opens a fresh wizard. No-op while one is already open.</summary>
    public void Open()
    {
        if (Wizard != null) return;
        CloseDuplicates();

        var wizard = _wizardFactory();
        wizard.Closed += OnWizardClosed;
        Wizard = wizard;
        _logger.LogInformation("Library maintenance wizard opened");
    }

    /// <summary>Opens the exact-duplicate review. No-op while it is already open.</summary>
    public void OpenDuplicates()
    {
        if (Duplicates != null) return;
        if (_duplicatesFactory == null)
        {
            _logger.LogWarning("Exact-duplicate review requested, but it isn't registered");
            return;
        }
        if (Wizard != null) Wizard.Close();

        var duplicates = _duplicatesFactory();
        duplicates.Closed += OnDuplicatesClosed;
        Duplicates = duplicates;
        _logger.LogInformation("Exact-duplicate review opened");
        _ = duplicates.LoadAsync();
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

    private void OnDuplicatesClosed(object? sender, EventArgs e) => CloseDuplicates();

    private void CloseDuplicates()
    {
        var duplicates = Duplicates;
        if (duplicates == null) return;
        duplicates.Closed -= OnDuplicatesClosed;
        duplicates.Dispose();
        Duplicates = null;
        _logger.LogInformation("Exact-duplicate review closed");
    }

    public void Dispose()
    {
        _openSubscription?.Dispose();
        _openDuplicatesSubscription?.Dispose();
        if (Wizard != null)
        {
            Wizard.Closed -= OnWizardClosed;
            Wizard.Dispose();
            Wizard = null;
        }
        CloseDuplicates();
    }
}
