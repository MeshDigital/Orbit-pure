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
            if (item.Stage < CurrentStage)
            {
                item.Color = Avalonia.Media.Brushes.LimeGreen;
                item.Opacity = 1.0;
            }
            else if (item.Stage == CurrentStage)
            {
                item.Color = Avalonia.Media.Brushes.Cyan;
                item.Opacity = 1.0;
            }
            else
            {
                item.Color = Avalonia.Media.Brushes.Gray;
                item.Opacity = 0.3;
            }
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
        
        private Avalonia.Media.IBrush _color = Avalonia.Media.Brushes.Gray;
        public Avalonia.Media.IBrush Color { get => _color; set { _color = value; OnPropertyChanged(); OnPropertyChanged(nameof(GlowColor)); } }
        
        private double _opacity = 0.3;
        public double Opacity { get => _opacity; set { _opacity = value; OnPropertyChanged(); } }

        public Avalonia.Media.Color GlowColor => (Color as Avalonia.Media.ISolidColorBrush)?.Color ?? Avalonia.Media.Colors.Transparent;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
