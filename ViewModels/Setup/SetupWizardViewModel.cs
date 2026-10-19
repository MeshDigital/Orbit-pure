using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

public enum SetupWizardOutcome
{
    /// <summary>"Start using ORBIT" on the Done page.</summary>
    Finished,
    /// <summary>"Take the quick tour" on the Done page.</summary>
    FinishedStartTour,
    /// <summary>"Skip setup" (confirmed). Still marks setup completed.</summary>
    Skipped,
}

/// <summary>
/// The first-run setup wizard: Welcome → Soulseek → Folders → Connections → Audio → Done.
/// Each step keeps its own edits (Back never loses them) and saves only when the user presses
/// Next on it. Finishing or skipping marks [Setup] Completed, so the wizard never reappears unasked;
/// Settings → Advanced → Setup reopens it.
/// </summary>
public sealed class SetupWizardViewModel : SetupObservable, IDisposable
{
    private readonly ISetupStateStore _state;
    private readonly INotificationService? _notifications;
    private readonly ILogger _logger;
    private readonly AsyncRelayCommand _nextCommand;
    private readonly RelayCommand _backCommand;
    private readonly AsyncRelayCommand _skipStepCommand;

    private int _currentIndex;
    private bool _isBusy;
    private bool _isSkipConfirmVisible;
    private bool _isClosed;

    public SetupWizardViewModel(
        ISetupStateStore state,
        ISoulseekSetupGateway soulseek,
        ISetupSettingsGateway settings,
        ILibraryFolderSetupGateway libraryFolders,
        ISetupFolderService folders,
        IFileInteractionService folderPicker,
        ISpotifySetupGateway spotify,
        IAudioSetupGateway audio,
        ILogger<SetupWizardViewModel> logger,
        INotificationService? notifications = null)
    {
        _state = state;
        _notifications = notifications;
        _logger = logger;

        Welcome = new WelcomeStepViewModel();
        Soulseek = new SoulseekStepViewModel(soulseek, logger);
        Folders = new FoldersStepViewModel(settings, libraryFolders, folders, folderPicker, logger);
        Connections = new ConnectionsStepViewModel(spotify, logger);
        Audio = new AudioStepViewModel(audio, settings, logger);
        Done = new DoneStepViewModel(libraryFolders);

        Steps = new ReadOnlyCollection<SetupStepViewModel>(new SetupStepViewModel[] { Welcome, Soulseek, Folders, Connections, Audio, Done });
        Indicators = new ReadOnlyCollection<SetupStepIndicator>(Steps.Select((s, i) => new SetupStepIndicator(i + 1, s)).ToList());

        _nextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy && !IsLast);
        _backCommand = new RelayCommand(Back, () => !IsBusy && !IsFirst);
        _skipStepCommand = new AsyncRelayCommand(SkipStepAsync, () => !IsBusy && CurrentStep.CanSkip);
        NextCommand = _nextCommand;
        BackCommand = _backCommand;
        SkipStepCommand = _skipStepCommand;
        RequestSkipSetupCommand = new RelayCommand(() => IsSkipConfirmVisible = true);
        CancelSkipSetupCommand = new RelayCommand(() => IsSkipConfirmVisible = false);
        ConfirmSkipSetupCommand = new RelayCommand(() => Close(SetupWizardOutcome.Skipped));
        FinishCommand = new RelayCommand(() => Close(SetupWizardOutcome.Finished));
        FinishAndTourCommand = new RelayCommand(() => Close(SetupWizardOutcome.FinishedStartTour));
        EnterCommand = new AsyncRelayCommand(OnEnterAsync);
        EscapeCommand = new RelayCommand(OnEscape);

        UpdateIndicators();
    }

    public WelcomeStepViewModel Welcome { get; }
    public SoulseekStepViewModel Soulseek { get; }
    public FoldersStepViewModel Folders { get; }
    public ConnectionsStepViewModel Connections { get; }
    public AudioStepViewModel Audio { get; }
    public DoneStepViewModel Done { get; }

    public IReadOnlyList<SetupStepViewModel> Steps { get; }
    public IReadOnlyList<SetupStepIndicator> Indicators { get; }

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SkipStepCommand { get; }
    public ICommand RequestSkipSetupCommand { get; }
    public ICommand CancelSkipSetupCommand { get; }
    public ICommand ConfirmSkipSetupCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand FinishAndTourCommand { get; }

    /// <summary>Enter: the page's primary action (Next / Start using ORBIT), or "keep setting up" on the skip prompt.</summary>
    public ICommand EnterCommand { get; }

    /// <summary>Esc: ask to skip setup; Esc again keeps setting up.</summary>
    public ICommand EscapeCommand { get; }

    /// <summary>Raised once, when the wizard closes.</summary>
    public event EventHandler<SetupWizardOutcome>? Closed;

    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (SetProperty(ref _currentIndex, value))
            {
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(IsFirst));
                OnPropertyChanged(nameof(IsLast));
                OnPropertyChanged(nameof(IsWelcome));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(ShowFooterNavigation));
                OnPropertyChanged(nameof(StepPositionText));
                OnPropertyChanged(nameof(NextLabel));
                UpdateIndicators();
                RaiseCommandStates();
            }
        }
    }

    public SetupStepViewModel CurrentStep => Steps[CurrentIndex];
    public bool IsFirst => CurrentIndex == 0;
    public bool IsLast => CurrentIndex == Steps.Count - 1;
    public bool IsWelcome => CurrentStep == Welcome;
    public bool IsDone => CurrentStep == Done;

    /// <summary>Back / Skip this step / Next row (the Welcome and Done pages have their own buttons).</summary>
    public bool ShowFooterNavigation => !IsWelcome && !IsDone;

    /// <summary>"Step 2 of 4" across the four setup steps between Welcome and Done.</summary>
    public string StepPositionText => IsWelcome || IsDone ? string.Empty : $"Step {CurrentIndex} of {Steps.Count - 2}";

    public string NextLabel => CurrentStep == Soulseek && !Soulseek.ResultIsSuccess ? "Sign in and continue" : "Next";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public bool IsSkipConfirmVisible
    {
        get => _isSkipConfirmVisible;
        set => SetProperty(ref _isSkipConfirmVisible, value);
    }

    public bool IsClosed => _isClosed;

    /// <summary>Loads current values into every step (folders, account, audio).</summary>
    public async Task InitializeAsync()
    {
        foreach (var step in Steps)
        {
            try
            {
                await step.InitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Setup: initialising step {Step} failed", step.ShortTitle);
            }
        }
        Soulseek.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SoulseekStepViewModel.ResultIsSuccess) or nameof(SoulseekStepViewModel.LastResult))
                OnPropertyChanged(nameof(NextLabel));
        };
    }

    public async Task NextAsync()
    {
        if (IsBusy || IsLast || _isClosed) return;
        IsBusy = true;
        try
        {
            var step = CurrentStep;
            if (!await step.CommitAsync())
                return;
            if (step != Welcome) step.Status = SetupStepStatus.Configured;
            MoveTo(CurrentIndex + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: confirming step {Step} failed", CurrentStep.ShortTitle);
            _notifications?.Show("Setup step failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Back()
    {
        if (IsBusy || IsFirst) return;
        IsSkipConfirmVisible = false;
        CurrentIndex--;
    }

    public Task SkipStepAsync()
    {
        if (IsBusy || !CurrentStep.CanSkip) return Task.CompletedTask;
        CurrentStep.Status = SetupStepStatus.Skipped;
        MoveTo(CurrentIndex + 1);
        return Task.CompletedTask;
    }

    private void MoveTo(int index)
    {
        if (Steps[index] == Done)
        {
            Done.NewLibraryFolderCount = Folders.AddedLibraryFolderCount;
            Done.SetSummary(Steps.Where(s => s.AppearsInSummary)
                .Select(s => new SetupSummaryItem(s.SummaryTitle, s.SummaryDetail, s.Status == SetupStepStatus.Configured
                                                                                   || (s == Connections && Connections.IsSpotifyConnected))));
        }
        CurrentIndex = index;
    }

    /// <summary>Closes the wizard. Every outcome marks setup completed so it doesn't reappear on the next start.</summary>
    public void Close(SetupWizardOutcome outcome)
    {
        if (_isClosed) return;
        _isClosed = true;
        IsSkipConfirmVisible = false;
        try
        {
            _state.MarkSetupCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: could not save [Setup] Completed");
            _notifications?.Show("Couldn't save setup state", "The setup wizard may open again next time ORBIT starts. " + ex.Message, NotificationType.Warning);
        }

        if (outcome == SetupWizardOutcome.Skipped)
            _notifications?.Show("Setup skipped", "You can run it again any time from Settings → Advanced → Setup.", NotificationType.Information, TimeSpan.FromSeconds(8));

        _logger.LogInformation("Setup wizard closed: {Outcome} (steps: {Steps})", outcome,
            string.Join(", ", Steps.Where(s => s.AppearsInSummary).Select(s => $"{s.ShortTitle}={s.Status}")));
        Closed?.Invoke(this, outcome);
    }

    private async Task OnEnterAsync()
    {
        if (IsSkipConfirmVisible)
        {
            IsSkipConfirmVisible = false;
            return;
        }
        if (IsDone)
        {
            Close(SetupWizardOutcome.Finished);
            return;
        }
        await NextAsync();
    }

    private void OnEscape()
    {
        IsSkipConfirmVisible = !IsSkipConfirmVisible;
    }

    private void UpdateIndicators()
    {
        foreach (var indicator in Indicators)
        {
            indicator.IsCurrent = indicator.Step == CurrentStep;
            indicator.IsDone = indicator.Number - 1 < CurrentIndex;
        }
    }

    private void RaiseCommandStates()
    {
        _nextCommand.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
        _skipStepCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        Soulseek.Dispose();
        Connections.Dispose();
    }
}
