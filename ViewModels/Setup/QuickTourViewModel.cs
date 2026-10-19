using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>
/// One coach mark. <see cref="TargetNames"/> are x:Name values of controls in MainWindow, tried in
/// order (the first visible one is highlighted), so a step can fall back to another control.
/// </summary>
public sealed record TourStep(string Id, string Title, string Body, IReadOnlyList<string> TargetNames);

/// <summary>
/// The optional quick tour: highlights the main navigation items one at a time. The view decides
/// which targets are visible right now (sidebar collapsed, groups hidden or resized), and steps
/// without a visible target are left out.
/// </summary>
public sealed class QuickTourViewModel : SetupObservable
{
    public static readonly IReadOnlyList<TourStep> AllSteps = new[]
    {
        new TourStep("search", "Search", "Search the Soulseek network for a track or album. Results are ranked by quality, so the best file is on top.", new[] { "NavSearch" }),
        new TourStep("downloads", "Downloads", "Everything you queue lands in the Inbox: progress, retries, failures and finished tracks, grouped by playlist.", new[] { "NavInbox" }),
        new TourStep("import", "Import", "Bring in a Spotify playlist, a pasted tracklist or a CSV. You see a preview before anything is downloaded.", new[] { "NavImport" }),
        new TourStep("library", "Library", "Your music and playlists. Tag, rate, colour, edit metadata and export to Rekordbox from here.", new[] { "NavLibrary" }),
        new TourStep("analysis", "Analysis", "BPM, key, energy, loudness and AI genre and mood for every track. The queue runs in the background.", new[] { "NavAnalysis" }),
        new TourStep("cueforge", "Cue Forge", "Generate hot cues and loops from the track's structure, then fine-tune them on the waveform.", new[] { "NavCueForge" }),
        new TourStep("flowbuilder", "Flow Builder", "Get a suggested order for a playlist based on key, tempo and energy, and apply it in one go.", new[] { "NavFlowBuilder" }),
        new TourStep("editlab", "Edit Lab", "Combine tracks and stems into intros and edits, then render them into your library.", new[] { "NavEditLab" }),
        new TourStep("workstation", "Workstation", "Decks, timeline and stem editor for preparing and rehearsing a set.", new[] { "NavWorkstation" }),
        new TourStep("settings", "Settings", "Accounts, folders, audio and everything else. Run setup again or retake this tour under Advanced.", new[] { "NavSettings", "TopBarSettings" }),
    };

    private readonly ISetupStateStore _state;
    private readonly ILogger _logger;
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _backCommand;
    private List<(TourStep Step, string Target)> _active = new();
    private int _index;
    private bool _isOpen;

    public QuickTourViewModel(ISetupStateStore state, ILogger logger)
    {
        _state = state;
        _logger = logger;
        _nextCommand = new RelayCommand(Next, () => IsOpen);
        _backCommand = new RelayCommand(Back, () => IsOpen && !IsFirst);
        NextCommand = _nextCommand;
        BackCommand = _backCommand;
        SkipCommand = new RelayCommand(() => Close(completed: false));
    }

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>"Skip tour" and Esc.</summary>
    public ICommand SkipCommand { get; }

    /// <summary>Asked for by the shell; the view answers by calling <see cref="Start"/> with its visibility check.</summary>
    public event EventHandler? StartRequested;

    /// <summary>Raised when the tour closes (finished or skipped).</summary>
    public event EventHandler? Closed;

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
                RaiseCommandStates();
        }
    }

    public int Index
    {
        get => _index;
        private set
        {
            if (SetProperty(ref _index, value))
            {
                OnPropertyChanged(nameof(Current));
                OnPropertyChanged(nameof(CurrentTarget));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Body));
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(IsFirst));
                OnPropertyChanged(nameof(IsLast));
                OnPropertyChanged(nameof(NextLabel));
                RaiseCommandStates();
            }
        }
    }

    public int Count => _active.Count;
    public TourStep? Current => IsOpen && _index < _active.Count ? _active[_index].Step : null;

    /// <summary>The x:Name of the control to highlight for the current step.</summary>
    public string? CurrentTarget => IsOpen && _index < _active.Count ? _active[_index].Target : null;

    public string Title => Current?.Title ?? string.Empty;
    public string Body => Current?.Body ?? string.Empty;
    public string PositionText => Count == 0 ? string.Empty : $"{Index + 1} / {Count}";
    public bool IsFirst => Index == 0;
    public bool IsLast => Index >= Count - 1;
    public string NextLabel => IsLast ? "Finish" : "Next";

    public void RequestStart() => StartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Starts the tour with the steps whose target is visible. Returns false (and stays closed)
    /// when none is, e.g. with the navigation sidebar hidden.
    /// </summary>
    public bool Start(Func<string, bool> isTargetVisible)
    {
        _active = AllSteps
            .Select(step => (Step: step, Target: step.TargetNames.FirstOrDefault(isTargetVisible)))
            .Where(x => x.Target != null)
            .Select(x => (x.Step, x.Target!))
            .ToList();
        OnPropertyChanged(nameof(Count));

        if (_active.Count == 0)
        {
            _logger.LogInformation("Quick tour: no navigation item is visible, not starting");
            return false;
        }

        _logger.LogInformation("Quick tour started with {Count} of {Total} steps", _active.Count, AllSteps.Count);
        _index = -1;
        IsOpen = true;
        Index = 0;
        return true;
    }

    public void Next()
    {
        if (!IsOpen) return;
        if (IsLast) Close(completed: true);
        else Index++;
    }

    public void Back()
    {
        if (!IsOpen || IsFirst) return;
        Index--;
    }

    public void Close(bool completed)
    {
        if (!IsOpen) return;
        IsOpen = false;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentTarget));
        try
        {
            _state.MarkTourCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quick tour: could not save [Setup] TourCompleted");
        }
        _logger.LogInformation("Quick tour {How} at step {Step} of {Count}", completed ? "finished" : "skipped", Index + 1, Count);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCommandStates()
    {
        _nextCommand.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
    }
}
