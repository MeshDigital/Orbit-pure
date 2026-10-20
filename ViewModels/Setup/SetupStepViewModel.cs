using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>Minimal INotifyPropertyChanged base for the setup wizard (no dispatcher, so tests run headless).</summary>
public abstract class SetupObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public enum SetupStepStatus
{
    NotVisited,
    Configured,
    Skipped,
}

/// <summary>
/// One wizard page. Edits live on the step (so Back keeps them) and are only written when the
/// user confirms the step with Next (<see cref="CommitAsync"/>).
/// </summary>
public abstract class SetupStepViewModel : SetupObservable
{
    private SetupStepStatus _status;
    private string? _errorMessage;

    /// <summary>Page heading.</summary>
    public abstract string Title { get; }

    /// <summary>Label in the step indicator.</summary>
    public abstract string ShortTitle { get; }

    /// <summary>One line under the heading.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Shown as "Recommended" next to the title.</summary>
    public virtual bool IsRecommended => false;

    /// <summary>Whether "Skip this step" is offered.</summary>
    public virtual bool CanSkip => false;

    /// <summary>Why skipping matters, shown next to the skip button.</summary>
    public virtual string? SkipNote => null;

    public SetupStepStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsConfigured));
                OnPropertyChanged(nameof(IsSkipped));
            }
        }
    }

    public bool IsConfigured => Status == SetupStepStatus.Configured;
    public bool IsSkipped => Status == SetupStepStatus.Skipped;

    /// <summary>Validation / save error for this step (shown as an error callout).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Loads current values (called once when the wizard opens).</summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Validates and saves the step. Returns false to stay on the step (with <see cref="ErrorMessage"/> set).</summary>
    public virtual Task<bool> CommitAsync() => Task.FromResult(true);

    /// <summary>Called by the wizard each time this step becomes the current page.</summary>
    public virtual void OnShown() { }

    /// <summary>One line for the Done summary.</summary>
    public virtual string SummaryTitle => ShortTitle;

    public virtual string SummaryDetail => Status switch
    {
        SetupStepStatus.Configured => "Set up",
        SetupStepStatus.Skipped => "Skipped",
        _ => "Not changed",
    };

    /// <summary>Whether this step appears in the Done summary.</summary>
    public virtual bool AppearsInSummary => true;

    protected void ClearError() => ErrorMessage = null;
}

/// <summary>A row in the step indicator.</summary>
public sealed class SetupStepIndicator : SetupObservable
{
    private bool _isCurrent;
    private bool _isDone;

    public SetupStepIndicator(int number, SetupStepViewModel step)
    {
        Number = number;
        Step = step;
    }

    public int Number { get; }
    public SetupStepViewModel Step { get; }
    public string Label => Step.ShortTitle;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }
}

/// <summary>A line on the Done page.</summary>
public sealed record SetupSummaryItem(string Title, string Detail, bool IsConfigured);
