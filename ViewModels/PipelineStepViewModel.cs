using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Status of a pipeline step in the Command Center UI.
/// </summary>
public enum StepStatus
{
    Pending,
    Active,
    Complete,
    Error
}

/// <summary>
/// ViewModel for a single step in the export pipeline.
/// Designed for the "Command Center" UI with visual state transitions.
/// </summary>
public class PipelineStepViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private StepStatus _status = StepStatus.Pending;
    private string _stepName = string.Empty;
    private int _stepIndex;
    private string? _errorMessage;
    private string? _recoveryHint;

    /// <summary>
    /// Display name for the step (DJ-facing copy).
    /// </summary>
    public string StepName
    {
        get => _stepName;
        set { _stepName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Zero-based index in the pipeline sequence.
    /// </summary>
    public int StepIndex
    {
        get => _stepIndex;
        set { _stepIndex = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Current status of this step.
    /// </summary>
    public StepStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsComplete));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }
    
    /// <summary>
    /// DJ-readable error message (e.g., "USB disconnected").
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }
    
    /// <summary>
    /// Recovery action hint (e.g., "Reinsert the USB drive and click Retry").
    /// </summary>
    public string? RecoveryHint
    {
        get => _recoveryHint;
        set { _recoveryHint = value; OnPropertyChanged(); }
    }

    // Convenience properties for XAML binding
    public bool IsPending => Status == StepStatus.Pending;
    public bool IsActive => Status == StepStatus.Active;
    public bool IsComplete => Status == StepStatus.Complete;
    public bool IsError => Status == StepStatus.Error;
    public bool HasError => Status == StepStatus.Error && !string.IsNullOrEmpty(ErrorMessage);

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Factory method to create a pipeline step.
    /// </summary>
    public static PipelineStepViewModel Create(int index, string name) => new()
    {
        StepIndex = index,
        StepName = name,
        Status = StepStatus.Pending
    };
    
    /// <summary>
    /// Sets the step to error state with DJ-readable messaging.
    /// </summary>
    public void SetError(string errorMessage, string recoveryHint)
    {
        ErrorMessage = errorMessage;
        RecoveryHint = recoveryHint;
        Status = StepStatus.Error;
    }
    
    /// <summary>
    /// Clears error state and resets to pending (for retry).
    /// </summary>
    public void Reset()
    {
        ErrorMessage = null;
        RecoveryHint = null;
        Status = StepStatus.Pending;
    }
}
