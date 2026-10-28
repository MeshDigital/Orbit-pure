using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models;

/// <summary>
/// Represents the current state of an analysis worker thread.
/// </summary>
public class ActiveThreadInfo : INotifyPropertyChanged
{
    private int _threadId;
    private string _currentTrack = string.Empty;
    private string _status = "Idle";
    private double _progress;
    private DateTime? _startTime;
    private Guid? _databaseId; // Phase 21: Database visibility

    public int ThreadId
    {
        get => _threadId;
        set => SetField(ref _threadId, value);
    }

    public string CurrentTrack
    {
        get => _currentTrack;
        set => SetField(ref _currentTrack, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public DateTime? StartTime
    {
        get => _startTime;
        set => SetField(ref _startTime, value);
    }

    public Guid? DatabaseId
    {
        get => _databaseId;
        set => SetField(ref _databaseId, value);
    }

    private float _bpmConfidence;
    public float BpmConfidence
    {
        get => _bpmConfidence;
        set => SetField(ref _bpmConfidence, value);
    }

    private float _keyConfidence;
    public float KeyConfidence
    {
        get => _keyConfidence;
        set => SetField(ref _keyConfidence, value);
    }

    private float _integrityScore;
    public float IntegrityScore
    {
        get => _integrityScore;
        set => SetField(ref _integrityScore, value);
    }

    private SLSKDONET.Data.AnalysisStage _currentStage;
    public SLSKDONET.Data.AnalysisStage CurrentStage
    {
        get => _currentStage;
        set 
        {
            if (SetField(ref _currentStage, value))
            {
                UpdateStages();
            }
        }
    }

    public ObservableCollection<StageIndicator> ActiveStages { get; } = new();

    private void UpdateStages()
    {
        var stages = Enum.GetValues<SLSKDONET.Data.AnalysisStage>();
        if (ActiveStages.Count == 0)
        {
            foreach (var s in stages.Where(x => x != SLSKDONET.Data.AnalysisStage.Complete))
            {
                ActiveStages.Add(new StageIndicator { Label = s.ToString(), Stage = s });
            }
        }

        foreach (var item in ActiveStages)
        {
            item.IsDone = item.Stage < CurrentStage;
            item.IsCurrent = item.Stage == CurrentStage;
            item.Opacity = item.Stage <= CurrentStage ? 1.0 : 0.3;
        }
    }

    public string ElapsedTime
    {
        get
        {
            if (!StartTime.HasValue) return "--:--";
            var elapsed = DateTime.Now - StartTime.Value;
            return elapsed.ToString(@"mm\:ss");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public class StageIndicator : INotifyPropertyChanged
    {
        public string Label { get; set; } = string.Empty;
        public SLSKDONET.Data.AnalysisStage Stage { get; set; }
        
        // State flags; the view maps them to status-dot classes (success = done, accent = current),
        // so the model carries no colours.
        private bool _isDone;
        public bool IsDone { get => _isDone; set { if (_isDone == value) return; _isDone = value; OnPropertyChanged(); } }

        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set { if (_isCurrent == value) return; _isCurrent = value; OnPropertyChanged(); } }

        private double _opacity = 0.3;
        public double Opacity { get => _opacity; set { _opacity = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
