using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class SearchResult : INotifyPropertyChanged
{
    public Track Model { get; }

    /// <summary>
    /// Phase 2.8: Constructor now uses Null Object Pattern.
    /// Never allows null tracks - uses Track.Null as fallback.
    /// </summary>
    public SearchResult(Track? track)
    {
        Model = track ?? Track.Null;
    }

    public string Artist => Model.Artist ?? "Unknown Artist";
    public string Title => Model.Title ?? "Unknown Track";
    public string Album => Model.Album ?? "Unknown Album";
    public int Bitrate => Model.Bitrate;
    // Alias for compatibility if needed, but better to fix consumers.
    // public int BitRate => Bitrate; 
    
    public string Filename => Model.Filename ?? "";
    public int? Length => Model.Length;
    public int UploadSpeed => Model.UploadSpeed;
    public bool SlotFree => Model.HasFreeUploadSlot;

    public long Size => Model.Size ?? 0;
    public string Username => Model.Username ?? "Unknown";
    public bool HasFreeUploadSlot => Model.HasFreeUploadSlot;
    public int QueueLength => Model.QueueLength;
    
    // Rank is updated on the Model by ResultSorter, we just expose it
    public double CurrentRank => Model.CurrentRank;
    public string ScoreBreakdown => Model.ScoreBreakdown ?? $"Rank: {CurrentRank:F1}";

    public void RefreshRank()
    {
        OnPropertyChanged(nameof(CurrentRank));
        OnPropertyChanged(nameof(ScoreBreakdown));
    }

    // Phase 12.6: Multi-line row template properties
    public string PrimaryDisplay => $"{Artist} - {Title}";
    public string FileFormat => System.IO.Path.GetExtension(Model.Filename ?? "")?.TrimStart('.').ToUpperInvariant() ?? "MP3";
    public string TechnicalMetadata => $"{Bitrate} kbps {FileFormat} • @{Username} • {Size / 1024.0 / 1024.0:F1} MB • Q:{QueueLength}";

    // Phase 12.6: Percentile-based scoring for visual hierarchy
    private double _percentile;
    public double Percentile
    {
        get => _percentile;
        set
        {
            if (Math.Abs(_percentile - value) > 0.001)
            {
                _percentile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGoldenMatch));
                OnPropertyChanged(nameof(HeatMapOpacity));
                OnPropertyChanged(nameof(RowBackground));
            }
        }
    }

    // Phase 12.6: Visual hierarchy properties
    public bool IsGoldenMatch => Percentile <= 0.05; // Top 5%
    
    public double HeatMapOpacity => Percentile switch
    {
        <= 0.05 => 1.0,      // Top 5%: Full opacity
        <= 0.75 => 0.85,     // Middle 70%: Standard
        _ => 0.6             // Bottom 25%: Faded
    };
    
    public string RowBackground => Percentile switch
    {
        <= 0.05 => "#2A2D2E",  // Top tier: Slightly lighter
        _ => "#1E1E1E"         // Standard
    };

    // Phase 12.6: Integrity badges
    private string _integrityStatus = "";
    public string IntegrityStatus
    {
        get => _integrityStatus;
        set
        {
            if (_integrityStatus != value)
            {
                _integrityStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IntegrityBadge));
                OnPropertyChanged(nameof(IntegrityTooltip));
            }
        }
    }

    public string IntegrityBadge => IntegrityStatus switch
    {
        "Verified" => "✅",
        "Warning" => "⚠️",
        "Suspect" => "🚫",
        "HarmonicMatch" => "🔥",
        _ => ""
    };

    public string IntegrityTooltip => IntegrityStatus switch
    {
        "Verified" => "High-confidence quality",
        "Warning" => "Duration mismatch (likely radio edit)",
        "Suspect" => "Potential upscale/fake detected",
        "HarmonicMatch" => "Key/BPM match with current track",
        _ => ""
    };

    // Phase 12.6: "Add to Project" workflow state
    private bool _isAddedToProject;
    public bool IsAddedToProject
    {
        get => _isAddedToProject;
        set
        {
            if (_isAddedToProject != value)
            {
                _isAddedToProject = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AddButtonText));
                OnPropertyChanged(nameof(AddButtonColor));
            }
        }
    }

    public string AddButtonText => IsAddedToProject ? "✓ Added" : "Add to Project";
    public string AddButtonColor => IsAddedToProject ? "#4EC9B0" : "#007ACC";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    private TrackStatus _status = TrackStatus.Missing;
    public TrackStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(IsDownloaded));
            }
        }
    }

    public bool IsDownloaded => Status == TrackStatus.Downloaded;

    public string StatusIcon => Status switch
    {
        TrackStatus.Downloaded => "✅",
        TrackStatus.Failed => "❌",
        TrackStatus.Skipped => "⏭",
        TrackStatus.Pending => "⏳",
        _ => ""

    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
