using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input; // For ICommand
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand
using SLSKDONET.Data; // For IntegrityLevel

namespace SLSKDONET.ViewModels;



/// <summary>
/// ViewModel representing a track in the download queue.
/// Manages state, progress, and updates for the UI.
/// </summary>
public class PlaylistTrackViewModel : INotifyPropertyChanged, Library.ILibraryNode, IDisposable
{
    private PlaylistTrackState _state;
    private double _progress;
    private string _currentSpeed = string.Empty;
    private string? _errorMessage;
    private string? _coverArtUrl;
    private ArtworkProxy _artwork; // Replaces _artworkBitmap
    private bool _isAnalyzing; // New field for analysis feedback
    private bool _isEnriching; // New field for metadata enrichment feedback
    private bool _isSelected;
    private List<OrbitCue> _cues = new();
    
    // NEW Phase 12.1: Live Console Log for granular updates
    public System.Collections.ObjectModel.ObservableCollection<string> LiveConsoleLog { get; } = new();

    private bool _isConsoleOpen;
    public bool IsConsoleOpen
    {
        get => _isConsoleOpen;
        set => SetProperty(ref _isConsoleOpen, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                // Trigger the lazy-load of waveform data from DB
                _ = LoadTechnicalDataAsync();
            }
        }
    }

    private int _sortOrder;
    public DateTime AddedAt => Model?.AddedAt ?? DateTime.MinValue;

    public DateTime? ReleaseDate => Model?.ReleaseDate;
    public string ReleaseYear => Model?.ReleaseDate?.Year.ToString() ?? "";
    public string YearDisplay => ReleaseYear; // Alias for StandardTrackRow compatibility

    public string? PrimaryGenre => Model.PrimaryGenre;

    public bool IsPrepared => Model.IsPrepared;
    public string PreparationStatus => IsPrepared ? "Prepared" : "Raw";
    public Avalonia.Media.IBrush PreparationColor => IsPrepared ? Avalonia.Media.Brushes.DodgerBlue : Avalonia.Media.Brushes.Gray;

    public Avalonia.Media.IBrush QualityColor 
    {
        get
        {
            // Use MetadataForensicService if available, otherwise fallback to bitrate
            var tier = MetadataForensicService.CalculateTier(Model);
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(MetadataForensicService.GetTierColor(tier)));
        }
    }

    public string TierBadge => MetadataForensicService.GetTierBadge(MetadataForensicService.CalculateTier(Model));
    public string Tier => MetadataForensicService.CalculateTier(Model).ToString();
    


    public int SortOrder 
    {
        get => _sortOrder;
        set
        {
             if (_sortOrder != value)
             {
                 _sortOrder = value;
                 OnPropertyChanged();
                 // Propagate to Model
                 if (Model != null) Model.SortOrder = value;
             }
        }
    }

    public Guid SourceId { get; set; } // Project ID (PlaylistJob.Id)
    public Guid Id => Model.Id;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set => SetProperty(ref _isAnalyzing, value);
    }

    public bool IsEnriching
    {
        get => _isEnriching;
        set => SetProperty(ref _isEnriching, value);
    }

    private bool _isExpanded;
    private bool _technicalDataLoaded = false;
    private Data.Entities.TrackTechnicalEntity? _technicalEntity;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                if (_isExpanded && !_technicalDataLoaded)
                {
                    _ = LoadTechnicalDataAsync();
                }
            }
        }
    }

    // Integrity Level
    public IntegrityLevel IntegrityLevel
    {
        get => Model.Integrity;
        set
        {
            if (Model.Integrity != value)
            {
                Model.Integrity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IntegrityBadge));
                OnPropertyChanged(nameof(IntegrityColor));
                OnPropertyChanged(nameof(IntegrityTooltip));
            }
        }
    }

    public string IntegrityBadge => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "🥇",
        Data.IntegrityLevel.Verified => "🛡️",
        Data.IntegrityLevel.Suspicious => "📉",
        _ => ""
    };

    public string IntegrityColor => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "#FFD700",      // Gold
        Data.IntegrityLevel.Verified => "#32CD32",  // LimeGreen
        Data.IntegrityLevel.Suspicious => "#FFA500",// Orange
        _ => "Transparent"
    };

    public string IntegrityTooltip => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "Perfect Match (Gold)",
        Data.IntegrityLevel.Verified => "Verified Log/Hash",
        Data.IntegrityLevel.Suspicious => "Suspicious (Upscale/Transcode)",
        _ => "Not Analyzed"
    };

    public double Energy
    {
        get => Model.Energy ?? 0.0;
        set
        {
            Model.Energy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SonicProfile));
        }
    }

    public double Danceability
    {
        get => Model.Danceability ?? 0.0;
        set
        {
            Model.Danceability = value;
            OnPropertyChanged();
        }
    }

    public int? ManualEnergy
    {
        get => Model.ManualEnergy;
        set
        {
            if (Model.ManualEnergy != value)
            {
                Model.ManualEnergy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnergyRating));
            }
        }
    }

    public string EnergyRating => ManualEnergy?.ToString() ?? (Energy > 0 ? $"{(int)(Energy * 10):0}" : "—");

    public double? DropTimestamp
    {
        get => Model.DropTimestamp;
        set
        {
            if (Model.DropTimestamp != value)
            {
                Model.DropTimestamp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DropDisplay));
            }
        }
    }

    public string DropDisplay => DropTimestamp.HasValue ? TimeSpan.FromSeconds(DropTimestamp.Value).ToString(@"mm\:ss") : "—";

    public double Valence
    {
        get => Model.Valence ?? 0.0;
        set
        {
            Model.Valence = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SonicProfile));
        }
    }

    public string? MoodTag
    {
        get => Model.MoodTag;
        set
        {
            Model.MoodTag = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMood));
        }
    }
    
    public bool HasMood => !string.IsNullOrEmpty(MoodTag) && MoodTag != "Neutral";

    public Models.SonicProfileData SonicProfile => new Models.SonicProfileData(
        Energy, 
        Valence, 
        Model.InstrumentalProbability ?? 0.0);
    
    public double InstrumentalProbability => Model.InstrumentalProbability ?? 0.0;
    
    public double BPM => Model.BPM ?? 0.0;
    public string MusicalKey => Model.MusicalKey ?? "—";
    
    public string GlobalId { get; set; } // TrackUniqueHash
    
    // Properties linked to Model and Notification
    public string Artist 
    { 
        get => Model.Artist ?? string.Empty;
        set
        {
            if (Model.Artist != value)
            {
                Model.Artist = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ArtistName));
            }
        }
    }

    public string Title 
    { 
        get => Model.Title ?? string.Empty;
        set
        {
            if (Model.Title != value)
            {
                Model.Title = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TrackTitle));
            }
        }
    }

    public string Album
    {
        get => Model.Album ?? string.Empty;
        set
        {
            if (Model.Album != value)
            {
                Model.Album = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlbumName));
            }
        }
    }
    
    // Aliases for StandardTrackRow.axaml compatibility (binds to ArtistName/TrackTitle)
    public string ArtistName => !string.IsNullOrWhiteSpace(Artist) ? Artist : "Unknown Artist";
    public string TrackTitle => !string.IsNullOrWhiteSpace(Title) ? Title : "Unknown Title";
    public string AlbumName => !string.IsNullOrWhiteSpace(Album) ? Album : "Unknown Album";
    
    public string? Genres => GenresDisplay;
    public int Popularity => Model.Popularity ?? 0;
    public string? Duration => DurationDisplay;
    public string? DurationFormatted => DurationDisplay; // Alias for DataGrid
    
    // Phase 5: Fixed Bitrate (ensure it doesn't show BPM values)
    public string? Bitrate 
    {
        get
        {
            var val = Model.Bitrate ?? Model.BitrateScore ?? 0;
            if (val > 0 && val < 100 && BPM > 0) return "—"; // Likely a swapped BPM value
            return val > 0 ? $"{val}" : "—";
        }
    }
    public string? BitrateFormatted => Bitrate; // Alias for DataGrid
    public string? Status => StatusText;

    public string? Label
    {
        get => Model.Label;
        set
        {
            if (Model.Label != value)
            {
                Model.Label = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Comments
    {
        get => Model.Comments;
        set
        {
            if (Model.Comments != value)
            {
                Model.Comments = value;
                OnPropertyChanged();
            }
        }
    }

    public string Source => Model.SourceProvenance ?? Model.Source.ToString();
    public string? SourceProvenance => Model.SourceProvenance;

    public ArtworkProxy Artwork => _artwork;
    
    public Avalonia.Media.Imaging.Bitmap? ArtworkBitmap => _artwork?.Image;

    // Status Properties for StandardTrackRow
    public bool IsActive => (State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Searching || State == PlaylistTrackState.Queued || State == PlaylistTrackState.Pending) && State != PlaylistTrackState.Stalled;
    public bool IsFailed => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;
    public bool IsCompleted => State == PlaylistTrackState.Completed;
    public bool IsStalled => State == PlaylistTrackState.Stalled;
    public string? StalledReason => Model.StalledReason;
    public bool IsOnHold => Model.Status == TrackStatus.OnHold;
    
    // UI Layout Bools (For clean XAML)
    public bool IsSearching => State == PlaylistTrackState.Searching || State == PlaylistTrackState.Pending;
    public bool IsDownloading => State == PlaylistTrackState.Downloading;
    public bool HasBpm => BPM > 0;
    public bool HasKey => !string.IsNullOrEmpty(MusicalKey) && MusicalKey != "—";
    public bool HasGenre => !string.IsNullOrEmpty(DetectedSubGenre) || !string.IsNullOrEmpty(Genres) || !string.IsNullOrEmpty(PrimaryGenre);

    public string StatusText => State switch
    {
        PlaylistTrackState.Completed => "Ready",
        PlaylistTrackState.Downloading => Progress > 0 ? $"{(int)Progress}%" : "Downloading...",
        PlaylistTrackState.Searching => "Searching...",
        PlaylistTrackState.Queued => "Queued",
        PlaylistTrackState.Failed => !string.IsNullOrEmpty(ErrorMessage) ? ErrorMessage : "Failed",
        PlaylistTrackState.Paused => Model.Status == TrackStatus.OnHold ? "On Hold (Pending MP3 Search)" : "Paused",
        PlaylistTrackState.Stalled => $"Stalled: {StalledReason ?? "Waiting for data"}",
        _ => State.ToString()
    };

    public Avalonia.Media.IBrush StatusColor => State switch
    {
        PlaylistTrackState.Completed => Avalonia.Media.Brushes.LimeGreen,
        PlaylistTrackState.Failed => Avalonia.Media.Brushes.OrangeRed,
        PlaylistTrackState.Cancelled => Avalonia.Media.Brushes.Gray,
        PlaylistTrackState.Downloading => Avalonia.Media.Brushes.Cyan,
        PlaylistTrackState.Searching => Avalonia.Media.Brushes.Yellow,
        PlaylistTrackState.Stalled => Avalonia.Media.Brushes.Orange,
        PlaylistTrackState.Paused => Model.Status == TrackStatus.OnHold ? Avalonia.Media.Brushes.MediumSlateBlue : Avalonia.Media.Brushes.LightGray,
        _ => Avalonia.Media.Brushes.LightGray
    };

    public string DetailedStatusText => IsFailed 
        ? $"Failed: {ErrorMessage ?? "Unknown Error"}" 
        : StatusText;

    public string TechnicalSummary
    {
        get
        {
            var parts = new List<string>();
            if (Model.Bitrate.HasValue) parts.Add($"{Model.Bitrate}k");
            if (!string.IsNullOrEmpty(Format)) parts.Add(Format.ToUpper());
            if (FileSizeBytes > 0) parts.Add($"{FileSizeBytes / 1024.0 / 1024.0:F1}MB");
            return string.Join(" • ", parts);
        }
    }

    public string Format => Model.Format ?? "Unknown";
    public IEnumerable<OrbitCue> Cues => _cues;

    public string FileSizeDisplay => FileSizeBytes > 0 ? $"{FileSizeBytes / 1024.0 / 1024.0:F1} MB" : "—";
    public string BpmDisplay => Model.BPM.HasValue ? $"{Model.BPM:0}" : "—";
    public string KeyDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(Model.MusicalKey)) return "—";
            
            var camelot = Utils.KeyConverter.ToCamelot(Model.MusicalKey);
            // Show both: "G minor (6A)" or just "6A" if already in Camelot format
            if (camelot == Model.MusicalKey)
                return camelot; // Already Camelot
            
            return $"{Model.MusicalKey} ({camelot})";
        }
    }

    public string CamelotDisplay => !string.IsNullOrEmpty(Model.MusicalKey) ? Utils.KeyConverter.ToCamelot(Model.MusicalKey) : "—";


    public Avalonia.Media.IBrush ColorBrush
    {
        get
        {
            if (string.IsNullOrEmpty(Model.MusicalKey)) return Avalonia.Media.Brushes.Transparent;
            var camelot = Utils.KeyConverter.ToCamelot(Model.MusicalKey);
            return GetHarmonicColor(camelot);
        }
    }

    private static Avalonia.Media.IBrush GetHarmonicColor(string camelot)
    {
        if (string.IsNullOrEmpty(camelot) || camelot.Length < 2) return Avalonia.Media.Brushes.Transparent;

        bool isMinor = camelot.EndsWith("A", StringComparison.OrdinalIgnoreCase);
        string numPart = camelot.Substring(0, camelot.Length - 1);

        if (isMinor)
        {
            return numPart switch
            {
                "1" => Avalonia.Media.Brushes.Teal,
                "2" => Avalonia.Media.Brushes.SteelBlue,
                "3" => Avalonia.Media.Brushes.RoyalBlue,
                "4" => Avalonia.Media.Brushes.Indigo,
                "5" => Avalonia.Media.Brushes.DarkViolet,
                "6" => Avalonia.Media.Brushes.MediumVioletRed,
                "7" => Avalonia.Media.Brushes.Crimson,
                "8" => Avalonia.Media.Brushes.DarkOrange,
                "9" => Avalonia.Media.Brushes.Gold,
                "10" => Avalonia.Media.Brushes.YellowGreen,
                "11" => Avalonia.Media.Brushes.MediumSeaGreen,
                "12" => Avalonia.Media.Brushes.DarkCyan,
                _ => Avalonia.Media.Brushes.SlateGray
            };
        }
        else
        {
            return numPart switch
            {
                "1" => Avalonia.Media.Brushes.Aquamarine,
                "2" => Avalonia.Media.Brushes.LightSkyBlue,
                "3" => Avalonia.Media.Brushes.DodgerBlue,
                "4" => Avalonia.Media.Brushes.SlateBlue,
                "5" => Avalonia.Media.Brushes.Plum,
                "6" => Avalonia.Media.Brushes.HotPink,
                "7" => Avalonia.Media.Brushes.LightCoral,
                "8" => Avalonia.Media.Brushes.Orange,
                "9" => Avalonia.Media.Brushes.Khaki,
                "10" => Avalonia.Media.Brushes.PaleGreen,
                "11" => Avalonia.Media.Brushes.MediumSpringGreen,
                "12" => Avalonia.Media.Brushes.Turquoise,
                _ => Avalonia.Media.Brushes.LightSlateGray
            };
        }
    }
    public string DurationDisplay => Model.CanonicalDuration.HasValue ? TimeSpan.FromMilliseconds(Model.CanonicalDuration.Value).ToString(@"mm\:ss") : "—";

    // Curation & Trust
    // Phase 0.6: Truth in UI (Must be completed to be verified)
    public bool IsSecure => Model.Status == TrackStatus.Downloaded && Model.QualityConfidence > 0.9 && !string.IsNullOrEmpty(Model.ResolvedFilePath);
    public string CurationIcon => Model.CurationConfidence switch
    {
        Data.Entities.CurationConfidence.Manual => "🛡️",
        Data.Entities.CurationConfidence.High => "🏅",
        Data.Entities.CurationConfidence.Medium => "🥈",
        Data.Entities.CurationConfidence.Low => "📉",
        _ => string.Empty
    };
    
    public Avalonia.Media.IBrush CurationColor => Model.CurationConfidence switch
    {
        Data.Entities.CurationConfidence.Manual => Avalonia.Media.Brushes.LimeGreen,
        Data.Entities.CurationConfidence.High => Avalonia.Media.Brushes.Gold,
        Data.Entities.CurationConfidence.Medium => Avalonia.Media.Brushes.Silver,
        Data.Entities.CurationConfidence.Low => Avalonia.Media.Brushes.OrangeRed,
        _ => Avalonia.Media.Brushes.Transparent
    };

    public string ProvenanceTooltip => $"Confidence: {Model.CurationConfidence}\nSource: {Model.Source}";

    public string? DetectedSubGenre => Model.DetectedSubGenre;
    public Avalonia.Media.IBrush VibeColor => GetGenreColor(DetectedSubGenre);

    public string VibeTooltip
    {
        get
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(DetectedSubGenre)) list.Add($"Sub-Genre: {DetectedSubGenre}");
            if (!string.IsNullOrEmpty(PrimaryGenre)) list.Add($"Primary Genre: {PrimaryGenre}");
            if (!string.IsNullOrEmpty(MoodTag)) list.Add($"Mood: {MoodTag}");
            if (Energy > 0) list.Add($"Energy: {Energy:P0}");
            if (Valence > 0) list.Add($"Valence: {Valence:P0}");
            if (Model.InstrumentalProbability > 0) list.Add($"Instrumental: {Model.InstrumentalProbability:P0}");
            
            return list.Count > 0 ? string.Join("\n", list) : "No AI analysis data";
        }
    }

    // public record VibePill(string Icon, string Label, Avalonia.Media.IBrush Color); // Moved to VibePillRecord.cs

    // 1. Define the colors (Helper)
    private static Avalonia.Media.IBrush GetGenreColor(string? genre)
    {
        return genre?.ToLower() switch
        {
            "techno" => Avalonia.Media.Brushes.MediumPurple,
            "house" => Avalonia.Media.Brushes.DeepPink,
            "dnb" or "drum and bass" => Avalonia.Media.Brushes.OrangeRed,
            "ambient" => Avalonia.Media.Brushes.Teal,
            "dubstep" => Avalonia.Media.Brushes.Indigo,
            _ => Avalonia.Media.Brushes.SlateGray
        };
    }

    public IEnumerable<VibePill> VibePills
    {
        get
        {
            var list = new List<VibePill>();
            if (!string.IsNullOrEmpty(DetectedSubGenre))
            {
                 list.Add(new VibePill("🎵", DetectedSubGenre, GetGenreColor(DetectedSubGenre)));
            }

            // Energy/Mood Pills
            // Refined thresholds (normalized 0-1)
            if (Energy > 0.85) list.Add(new VibePill("⚡", "High Energy", Avalonia.Media.Brushes.Gold));
            else if (Energy < 0.3 && Energy > 0.05) list.Add(new VibePill("🌙", "Chill", Avalonia.Media.Brushes.CornflowerBlue));

            if (Valence > 0.75) list.Add(new VibePill("😎", "Positive", Avalonia.Media.Brushes.LimeGreen));
            else if (Valence < 0.25 && Valence > 0.05) list.Add(new VibePill("💀", "Dark", Avalonia.Media.Brushes.DarkSlateGray));
            
            if (!string.IsNullOrEmpty(MoodTag) && MoodTag != "Neutral")
            {
                list.Add(new VibePill("🎭", MoodTag, Avalonia.Media.Brushes.MediumSlateBlue));
            }

            if (Model.InstrumentalProbability > 0.85)
            {
                list.Add(new VibePill("🎤", "Instrumental", Avalonia.Media.Brushes.DarkCyan));
            }

            return list;
        }
    }

    private WaveformAnalysisData? _cachedWaveformData;

    public WaveformAnalysisData WaveformData
    {
        get
        {
            if (_cachedWaveformData != null) return _cachedWaveformData;

             // Use lazy loaded entity if available, checking cached array logic
             var waveData = _technicalEntity?.WaveformData ?? Model.WaveformData ?? Array.Empty<byte>();
             
             _cachedWaveformData = new WaveformAnalysisData 
             { 
                 PeakData = waveData, 
                 RmsData = _technicalEntity?.RmsData ?? Model.RmsData ?? Array.Empty<byte>(),
                 LowData = _technicalEntity?.LowData ?? Model.LowData ?? Array.Empty<byte>(),
                 MidData = _technicalEntity?.MidData ?? Model.MidData ?? Array.Empty<byte>(),
                 HighData = _technicalEntity?.HighData ?? Model.HighData ?? Array.Empty<byte>(),
                 DurationSeconds = (Model.CanonicalDuration ?? 0) / 1000.0
             };

             return _cachedWaveformData;
        }
    }

    // Band extraction for legacy WaveformControl bindings
    public byte[] LowData => WaveformData.LowData;
    public byte[] MidData => WaveformData.MidData;
    public byte[] HighData => WaveformData.HighData;
    
    // Technical Stats
    public int SampleRate => Model.BitrateScore ?? 0; // Or add SampleRate to Model
    // Fix: LoudnessDisplay was previously incorrectly bound to QualityConfidence
    public string ConfidenceDisplay => Model.QualityConfidence.HasValue ? $"{Model.QualityConfidence:P0} Confidence" : "—";
    
    public double MatchConfidence => (Model.QualityConfidence ?? 0) * 100;
    
    public string MatchConfidenceColor => MatchConfidence switch
    {
        >= 90 => "#1DB954", // Spotify Green
        >= 70 => "#FFD700", // Gold/Yellow
        _ => "#E91E63"      // Pink/Red
    };

    // Phase 12.1: Only show High Risk badge if it's a final heuristic score, not while searching
    public bool IsHighRisk => Model.IsFlagged && State != PlaylistTrackState.Searching && State != PlaylistTrackState.Queued && State != PlaylistTrackState.Pending;
    public string? FlagReason => Model.FlagReason;
    
    public string LoudnessDisplay => Model.Loudness.HasValue ? $"{Model.Loudness:F1} LUFS" : "—";
    public string TruePeakDisplay => Model.TruePeak.HasValue ? $"{Model.TruePeak:F1} dBTP" : "—";
    public string DynamicRangeDisplay => Model.DynamicRange.HasValue ? $"{Model.DynamicRange:F1} LU" : "—";
    
    public string IntegritySymbol => IntegrityBadge;
    public string IntegrityText => IntegrityTooltip;
    
    // Phase 21: Stem Separation Support
    private bool? _hasStems;
    public bool HasStems
    {
        get
        {
            // Phase 0.6: Truth in UI (Must be completed to have stems)
            if (Model.Status != TrackStatus.Downloaded) return false;

            if (!_hasStems.HasValue)
            {
                // Initial check
                _hasStems = false; // Default
                _ = CheckStemsAsync();
            }
            return _hasStems.Value;
        }
        private set => SetProperty(ref _hasStems, value);
    }

    private async Task CheckStemsAsync()
    {
        if (string.IsNullOrEmpty(Model.ResolvedFilePath)) 
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HasStems = false);
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var trackDir = System.IO.Path.GetDirectoryName(Model.ResolvedFilePath);
                var trackName = System.IO.Path.GetFileNameWithoutExtension(Model.ResolvedFilePath);
                
                if (string.IsNullOrEmpty(trackDir)) return;

                // Strategy A: /Music/Techno/Track.mp3 -> /Music/Techno/Stems/Track/
                var stemPathA = System.IO.Path.Combine(trackDir, "Stems", trackName);
                
                // Strategy B: /Music/Techno/Track.mp3 -> /Music/Techno/Track_Stems/
                var stemPathB = System.IO.Path.Combine(trackDir, $"{trackName}_Stems");
                
                // Strategy C: Check for _stems folder (Legacy)
                var stemPathC = System.IO.Path.Combine(trackDir, "_stems");

                bool found = (System.IO.Directory.Exists(stemPathA) && System.IO.Directory.GetFiles(stemPathA).Length > 0) || 
                             (System.IO.Directory.Exists(stemPathB) && System.IO.Directory.GetFiles(stemPathB).Length > 0) ||
                             (System.IO.Directory.Exists(stemPathC) && System.IO.Directory.GetFiles(stemPathC).Length > 0);

                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    _hasStems = found;
                    OnPropertyChanged(nameof(HasStems));
                });
            });
        }
        catch { /* Fail silently */ }
    }
    // AlbumArtPath and Progress are already present in this class.

    // Reference to the underlying model if needed for persistence later
    public PlaylistTrack Model { get; private set; }

    // Cancellation token source for this specific track's operation
    public System.Threading.CancellationTokenSource? CancellationTokenSource { get; set; }

    // User engagement
    public int Rating
    {
        get => Model.Rating;
        set
        {
            if (Model.Rating != value)
            {
                Model.Rating = value;
                OnPropertyChanged();
                
                // Persistence
                if (_libraryService != null)
                {
                    _ = _libraryService.UpdateRatingAsync(GlobalId, value);
                }
            }
        }
    }

    public bool IsLiked
    {
        get => Model.IsLiked;
        set
        {
            if (Model.IsLiked != value)
            {
                Model.IsLiked = value;
                OnPropertyChanged();
                
                // Persistence
                if (_libraryService != null)
                {
                    _ = _libraryService.UpdateLikeStatusAsync(GlobalId, value);
                }
            }
        }
    }

    public int PlayCount
    {
        get => Model.PlayCount;
        set
        {
            if (Model.PlayCount != value)
            {
                Model.PlayCount = value;
                OnPropertyChanged();
            }
        }
    }

    // Commands
    public ICommand RetryCommand { get; }
    public ICommand HardRetryCommand { get; }
    public ICommand ForceStartCommand { get; }
    public ICommand BumpToTopCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FindNewVersionCommand { get; }
    public ICommand AnalyzeTrackCommand { get; }
    public ICommand SeparateStemsCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand AddToProjectCommand { get; }
    public ICommand ToggleLikeCommand { get; }

    private readonly IEventBus? _eventBus;
    private readonly ILibraryService? _libraryService;
    private readonly ArtworkCacheService? _artworkCacheService;

    // Disposal
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
    private bool _isDisposed;

    public PlaylistTrackViewModel(
        PlaylistTrack track, 
        IEventBus? eventBus = null,
        ILibraryService? libraryService = null,
        ArtworkCacheService? artworkCacheService = null)
    {
        _eventBus = eventBus;
        _libraryService = libraryService;
        _artworkCacheService = artworkCacheService;
        Model = track;
        SourceId = track.PlaylistId;
        GlobalId = track.TrackUniqueHash;
        Artist = track.Artist;
        Title = track.Title;
        SortOrder = track.TrackNumber; // Initialize SortOrder
        State = PlaylistTrackState.Pending;
        
        // Map initial status from model
        if (track.Status == TrackStatus.Downloaded)
        {
            State = PlaylistTrackState.Completed;
            Progress = 1.0;
            // PERFORMANCE FIX: Defer disk I/O to background thread
            // Don't block constructor with file system calls
            _ = Task.Run(LoadFileSizeFromDisk);
        }

        PauseCommand = new RelayCommand(Pause, () => CanPause);
        ResumeCommand = new RelayCommand(Resume, () => CanResume);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        FindNewVersionCommand = new RelayCommand(FindNewVersion, () => CanHardRetry);
        AnalyzeTrackCommand = new RelayCommand(AnalyzeTrack, () => State == PlaylistTrackState.Completed); // Enable only if available locally
        SeparateStemsCommand = new RelayCommand(SeparateStems, () => State == PlaylistTrackState.Completed && !HasStems);
        PlayCommand = new RelayCommand(PlayTrack, () => IsCompleted);
        RevealFileCommand = new RelayCommand(RevealFile, () => IsCompleted);
        AddToProjectCommand = new RelayCommand(() => _eventBus?.Publish(new Models.AddToProjectRequestEvent(new[] { Model })), () => IsCompleted);
        RetryCommand = new RelayCommand(FindNewVersion, () => CanHardRetry);
        HardRetryCommand = RetryCommand;
        ForceStartCommand = new RelayCommand(ForceStart, () => CanForceStart);
        BumpToTopCommand = new RelayCommand(BumpToTop, () => CanBumpToTop);
        ToggleLikeCommand = new RelayCommand(() => IsLiked = !IsLiked);
        
        // REMOVED: 8000+ redundant event listeners eliminated.
        // Centralized dispatch moved to VirtualizedTrackCollection.
        
        // Initialize ArtworkProxy
        _artwork = new ArtworkProxy(_artworkCacheService!, track.AlbumArtUrl);
            
            // PERFORMANCE FIX: Don't eagerly trigger artwork load in constructor
            // Let UI trigger it via property access when visible (lazy loading)
            // This was causing 1600+ async operations on library open
        }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            CancellationTokenSource?.Cancel();
            CancellationTokenSource?.Dispose();
            
            // Shared Bitmap: Do NOT dispose. 
            // _artwork is a proxy, does not own the bitmap resource (cache does).
            // _artworkBitmap = null;
        }

        _isDisposed = true;
    }

    // Event Handlers (Internal for centralized update from collection)
    internal void OnMetadataUpdated(Models.TrackMetadataUpdatedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
             _isAnalyzing = false; // Clear analyzing flag

             // Reload track data from database to get updated metadata
             if (_libraryService != null)
             {
                 var updatedTrack = await _libraryService.GetPlaylistTrackByHashAsync(Model.PlaylistId, GlobalId);
                 
                 if (updatedTrack != null)
                 {
                     // Update model with fresh data
                     Model.AlbumArtUrl = updatedTrack.AlbumArtUrl;
                     Model.SpotifyTrackId = updatedTrack.SpotifyTrackId;
                     Model.SpotifyAlbumId = updatedTrack.SpotifyAlbumId;
                     Model.SpotifyArtistId = updatedTrack.SpotifyArtistId;
                     Model.IsEnriched = updatedTrack.IsEnriched;
                     Model.Album = updatedTrack.Album;
                     
                     // Sync Audio Features & Extended Metadata
                     Model.BPM = updatedTrack.BPM;
                     Model.MusicalKey = updatedTrack.MusicalKey;
                     Model.Energy = updatedTrack.Energy;
                     Model.Danceability = updatedTrack.Danceability;
                     Model.Valence = updatedTrack.Valence;
                     Model.Loudness = updatedTrack.Loudness;
                     Model.TruePeak = updatedTrack.TruePeak;
                     Model.DynamicRange = updatedTrack.DynamicRange;
                     Model.MoodTag = updatedTrack.MoodTag;
                     Model.InstrumentalProbability = updatedTrack.InstrumentalProbability;
                     
                     // Update Analysis info if available
                     Model.Popularity = updatedTrack.Popularity;
                     Model.Popularity = updatedTrack.Popularity;
                     Model.Genres = updatedTrack.Genres;
                     Model.IsReviewNeeded = updatedTrack.IsReviewNeeded; // Phase 10.4
                     Model.Label = updatedTrack.Label;
                     Model.Comments = updatedTrack.Comments;
                     Model.DetectedSubGenre = updatedTrack.DetectedSubGenre;
                     Model.PrimaryGenre = updatedTrack.PrimaryGenre;
                     
                     // NEW: Sync Waveform and Technical Analysis results
                     Model.WaveformData = updatedTrack.WaveformData;
                     Model.RmsData = updatedTrack.RmsData;
                     Model.LowData = updatedTrack.LowData;
                     Model.MidData = updatedTrack.MidData;
                     Model.HighData = updatedTrack.HighData;
                     Model.CanonicalDuration = updatedTrack.CanonicalDuration;
                     Model.Bitrate = updatedTrack.Bitrate;
                     Model.QualityConfidence = updatedTrack.QualityConfidence;
                     Model.IsTrustworthy = updatedTrack.IsTrustworthy;
                     
                     // Technical Audio
                     Model.Loudness = updatedTrack.Loudness;
                     Model.TruePeak = updatedTrack.TruePeak;
                     Model.DynamicRange = updatedTrack.DynamicRange;
                     
                      // Load artwork if URL is available
                      if (!string.IsNullOrWhiteSpace(updatedTrack.AlbumArtUrl))
                      {
                          // Refresh proxy
                          _artwork = new ArtworkProxy(_artworkCacheService!, updatedTrack.AlbumArtUrl);
                          OnPropertyChanged(nameof(Artwork));
                          OnPropertyChanged(nameof(AlbumArtPath));
                      }
                 }
             }
             
             OnPropertyChanged(nameof(Artist));
             OnPropertyChanged(nameof(Title));
             OnPropertyChanged(nameof(Album));
             OnPropertyChanged(nameof(ArtistName)); // Alias for StandardTrackRow
             OnPropertyChanged(nameof(TrackTitle)); // Alias for StandardTrackRow
             OnPropertyChanged(nameof(AlbumName));  // Alias for StandardTrackRow
             OnPropertyChanged(nameof(CoverArtUrl));
             OnPropertyChanged(nameof(AlbumArtPath));
             OnPropertyChanged(nameof(SpotifyTrackId));
             OnPropertyChanged(nameof(IsEnriched));
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
             
             // Notify Extended Props
             OnPropertyChanged(nameof(BPM));
             OnPropertyChanged(nameof(MusicalKey));
             OnPropertyChanged(nameof(CamelotDisplay));
             OnPropertyChanged(nameof(ColorBrush));
             OnPropertyChanged(nameof(LoudnessDisplay));
             OnPropertyChanged(nameof(Energy));
             OnPropertyChanged(nameof(Danceability));
             OnPropertyChanged(nameof(Valence));
             OnPropertyChanged(nameof(MoodTag));
             OnPropertyChanged(nameof(HasMood));
             OnPropertyChanged(nameof(SonicProfile));
             OnPropertyChanged(nameof(Genres));
             OnPropertyChanged(nameof(Genres));
             OnPropertyChanged(nameof(Popularity));
             OnPropertyChanged(nameof(Label));
             OnPropertyChanged(nameof(Comments));
             OnPropertyChanged(nameof(Source));
             OnPropertyChanged(nameof(DurationFormatted));
             OnPropertyChanged(nameof(BitrateFormatted));
             OnPropertyChanged(nameof(DetectedSubGenre));
             OnPropertyChanged(nameof(PrimaryGenre));
             OnPropertyChanged(nameof(VibePills));
             OnPropertyChanged(nameof(MatchConfidence));
             OnPropertyChanged(nameof(VibeColor));
             OnPropertyChanged(nameof(VibeTooltip));
             OnPropertyChanged(nameof(InstrumentalProbability));
             
             // NEW: Notify Waveform and technical props
             OnPropertyChanged(nameof(WaveformData));
             OnPropertyChanged(nameof(Bitrate));
             OnPropertyChanged(nameof(IntegritySymbol));
             OnPropertyChanged(nameof(IntegrityText));
             
             // CRITICAL: Reload technical data (waveforms) from TechnicalDetails table
             _ = LoadTechnicalDataAsync();
             OnPropertyChanged(nameof(Duration));

             OnPropertyChanged(nameof(LoudnessDisplay));
             OnPropertyChanged(nameof(TruePeakDisplay));
             OnPropertyChanged(nameof(DynamicRangeDisplay));
             OnPropertyChanged(nameof(ConfidenceDisplay));

             // Dates
             OnPropertyChanged(nameof(ReleaseDate));
             OnPropertyChanged(nameof(ReleaseYear));
              OnPropertyChanged(nameof(IsReviewNeeded)); // Phase 10.4
              OnPropertyChanged(nameof(PrimaryGenre)); // New
              OnPropertyChanged(nameof(IsPrepared)); // New
              OnPropertyChanged(nameof(PreparationStatus)); // New
              OnPropertyChanged(nameof(PreparationColor)); // New
              OnPropertyChanged(nameof(QualityColor)); // New
              OnPropertyChanged(nameof(StatusColor)); // New
              OnPropertyChanged(nameof(YearDisplay)); // New
             
              // Phase 11.5
              OnPropertyChanged(nameof(CurationIcon));
              OnPropertyChanged(nameof(CurationColor));
              OnPropertyChanged(nameof(ProvenanceTooltip));
        });
    }

    internal void OnStateChanged(TrackStateChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Marshal to UI Thread
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             State = evt.State;
             if (evt.Error != null) ErrorMessage = evt.Error;
             OnPropertyChanged(nameof(DetailedStatusText)); // Update tooltip
             OnPropertyChanged(nameof(IsHighRisk)); // Notify IsHighRisk changes based on state
             
             // NEW: Load file size from disk when track completes
             if (evt.State == PlaylistTrackState.Completed && FileSizeBytes == 0)
             {
                 LoadFileSizeFromDisk();
             }
        });
    }
    
    // Phase 12.1: Live Console Updates
    internal void OnDetailedStatus(Events.TrackDetailedStatusEvent evt)
    {
        if (evt.TrackHash != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             // Add to log (max 100 items to prevent memory leaks)
             if (LiveConsoleLog.Count >= 100)
             {
                 LiveConsoleLog.RemoveAt(0);
             }
             
             string timeStamp = DateTime.Now.ToString("HH:mm:ss");
             string prefix = evt.IsError ? "❌" : "ℹ️";
             LiveConsoleLog.Add($"[{timeStamp}] {prefix} {evt.Message}");
             
             // If we get an error detailed status and we are searching, we could optionally update the main StatusText too,
             // but staying focused on LiveConsoleLog for the granular details is preferred.
        });
    }
    
    /// <summary>
    /// Loads file size from disk for existing completed tracks (fallback when event didn't provide TotalBytes)
    /// </summary>
    private void LoadFileSizeFromDisk()
    {
        if (string.IsNullOrEmpty(Model.ResolvedFilePath))
            return;
            
        try
        {
            if (System.IO.File.Exists(Model.ResolvedFilePath))
            {
                var fileInfo = new System.IO.FileInfo(Model.ResolvedFilePath);
                FileSizeBytes = fileInfo.Length;
            }
        }
        catch { /* Fail silently */ }
    }

    private void AnalyzeTrack()
    {
        if (_eventBus == null || string.IsNullOrEmpty(GlobalId)) return;
        
        // Publish event to request analysis
        // The AnalysisOrchestrator or AnalysisQueueService should listen to this.
        _eventBus.Publish(new Models.TrackAnalysisRequestedEvent(GlobalId));
        
        // Optimistic UI update
        _isAnalyzing = true;
        OnPropertyChanged(nameof(MetadataStatus));
        OnPropertyChanged(nameof(MetadataStatusSymbol));
    }

    internal void OnProgressChanged(TrackProgressChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Throttling could be added here if needed, but for now we rely on simple marshaling
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             Progress = evt.Progress;
             
             // NEW: Capture file size during download
             if (evt.TotalBytes > 0)
             {
                 FileSizeBytes = evt.TotalBytes;
             }
        });
    }

    internal void OnAnalysisStarted(Models.TrackAnalysisStartedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             _isAnalyzing = true;
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
        });
    }

    internal void OnAnalysisFailed(Models.TrackAnalysisFailedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             _isAnalyzing = false;
             // Could update ErrorMessage here if desired
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
        });
    }

    public PlaylistTrackState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusText));
                
                // Notify command availability
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanHardRetry));
                OnPropertyChanged(nameof(CanDeleteFile));
                
                // Visual distinctions
                OnPropertyChanged(nameof(IsCompleted));
                
                // CommandManager.InvalidateRequerySuggested() happens automatically or via interaction
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.001)
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string CurrentSpeed
    {
        get => _currentSpeed;
        set
        {
            if (_currentSpeed != value)
            {
                _currentSpeed = value;
                OnPropertyChanged();
            }
        }
    }
    
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DetailedStatusText));
            }
        }
    }

    public string? CoverArtUrl
    {
        get => _coverArtUrl;
        set
        {
            if (_coverArtUrl != value)
            {
                _coverArtUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public string? AlbumArtPath => Model.AlbumArtUrl;

    public string? AlbumArtUrl => Model.AlbumArtUrl;
    
    // Phase 3.1: Expose Spotify Metadata ID
    public string? SpotifyTrackId
    {
        get => Model.SpotifyTrackId;
        set
        {
            if (Model.SpotifyTrackId != value)
            {
                Model.SpotifyTrackId = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SpotifyAlbumId => Model.SpotifyAlbumId;

    public bool IsEnriched
    {
        get => Model.IsEnriched;
        set
        {
            if (Model.IsEnriched != value)
            {
                Model.IsEnriched = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MetadataStatus));
            }
        }
    }
    

    // Phase 10.4: Industrial Prep
    public bool IsReviewNeeded
    {
        get => Model.IsReviewNeeded;
        set
        {
            if (Model.IsReviewNeeded != value)
            {
                Model.IsReviewNeeded = value;
                OnPropertyChanged();
            }
        }
    }

    public string MetadataStatus
    {
        get
        {
            if (_isAnalyzing) return "Analyzing";
            if (Model.IsEnriched) return "Enriched";
            if (!string.IsNullOrEmpty(Model.SpotifyTrackId)) return "Identified"; // Partial state
            return "Pending"; // Waiting for enrichment worker
        }

    }

    // Phase 1: UI Metadata
    
    public string GenresDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Genres)) return string.Empty;
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(Model.Genres);
                return list != null ? string.Join(", ", list) : string.Empty;
            }
            catch
            {
                return Model.Genres ?? string.Empty;
            }
        }
    }





    /// <summary>
    /// Raw file size in bytes (populated during download via event or from disk for existing files)
    /// </summary>
    private long _fileSizeBytes = 0;
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        private set
        {
            if (_fileSizeBytes != value)
            {
                _fileSizeBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileSizeDisplay));
            }
        }
    }










    // Computed Properties for Logic
    public bool CanPause => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Queued || State == PlaylistTrackState.Searching;
    public bool CanResume => State == PlaylistTrackState.Paused;
    public bool CanCancel => State != PlaylistTrackState.Completed && State != PlaylistTrackState.Cancelled;
    public bool CanHardRetry => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled; // Or Completed if we want to re-download
    public bool CanDeleteFile => State == PlaylistTrackState.Completed || State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;
    public bool CanForceStart => State == PlaylistTrackState.Pending || State == PlaylistTrackState.Stalled || State == PlaylistTrackState.Paused;

    public string MetadataStatusColor => MetadataStatus switch
    {
        "Analyzing" => "#00BFFF", // Deep Sky Blue
        "Enriched" => "#FFD700", // Gold
        "Identified" => "#1E90FF", // DodgerBlue
        _ => "#505050"
    };

    public string MetadataStatusSymbol => MetadataStatus switch
    {

        "Analyzing" => "⚙️",
        "Enriched" => "✨",
        "Identified" => "🆔",
        _ => "⏳"
    };

    // Actions
    public void Pause()
    {
        if (CanPause)
        {
            // Cancel current work but set state to Paused instead of Cancelled
            CancellationTokenSource?.Cancel();
            State = PlaylistTrackState.Paused;
            CurrentSpeed = "Paused";
        }
    }

    public void Resume()
    {
        if (CanResume)
        {
            State = PlaylistTrackState.Pending; // Back to queue
        }
    }

    public void Cancel()
    {
        if (CanCancel)
        {
            CancellationTokenSource?.Cancel();
            State = PlaylistTrackState.Cancelled;
            CurrentSpeed = "Cancelled";
        }
    }

    public void FindNewVersion()
    {
        if (CanHardRetry)
        {
            // Similar to Hard Retry, we reset to Pending to allow new search
            Reset(); 
        }
    }
    
    public void Reset()
    {
        CancellationTokenSource?.Cancel();
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = null;
        State = PlaylistTrackState.Pending;
        Progress = 0;
        CurrentSpeed = "";
        ErrorMessage = null;
    }

    // ArtworkBitmap and LoadAlbumArtworkAsync removed. 
    // Replaced by ArtworkProxy logic (see Artwork property).

    /// <summary>
    /// Lazy loads heavy technical data (Waveforms, etc.) from the database.
    /// Triggered when the track is expanded or viewed in Inspector.
    /// </summary>
    public async Task LoadTechnicalDataAsync()
    {
        if (_technicalDataLoaded || _libraryService == null) return;
        
        try
        {
             // Fetch from LibraryService (which calls DB)
              _technicalEntity = await _libraryService.GetTechnicalDetailsAsync(this.Id);
              
            if (_technicalEntity != null)
            {
                // Parse Cues
                if (!string.IsNullOrEmpty(_technicalEntity.CuePointsJson))
                {
                    try
                    {
                        var cues = System.Text.Json.JsonSerializer.Deserialize<List<OrbitCue>>(_technicalEntity.CuePointsJson);
                        if (cues != null)
                        {
                            _cues = cues;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse cues: {ex.Message}");
                    }
                }

                _technicalDataLoaded = true;
                _cachedWaveformData = null; // Invalidate cache

                // Notify UI
                OnPropertyChanged(nameof(WaveformData));
                OnPropertyChanged(nameof(LowData));
                OnPropertyChanged(nameof(MidData));
                OnPropertyChanged(nameof(HighData));
                OnPropertyChanged(nameof(TechnicalSummary));
                OnPropertyChanged(nameof(Cues));
                OnPropertyChanged(nameof(FileSizeBytes));
                OnPropertyChanged(nameof(LoudnessDisplay));
                OnPropertyChanged(nameof(Format));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load technical data: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void SeparateStems()
    {
        if (_eventBus == null || string.IsNullOrEmpty(GlobalId)) return;
        _eventBus.Publish(new Models.StemSeparationRequestedEvent(GlobalId, Model.ResolvedFilePath ?? ""));
        
        // UI notification or temporary state if needed
        ErrorMessage = "Stem separation queued...";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailedStatusText));
    }

    private void PlayTrack()
    {
        _eventBus?.Publish(new Models.PlayTrackRequestEvent(this));
    }

    private void RevealFile()
    {
        if (!string.IsNullOrEmpty(Model.ResolvedFilePath))
        {
            _eventBus?.Publish(new Models.RevealFileRequestEvent(Model.ResolvedFilePath));
        }
    }

    public void ForceStart()
    {
        if (CanForceStart && _eventBus != null)
        {
            _eventBus.Publish(new Models.ForceStartRequestEvent(GlobalId));
        }
    }

    public void BumpToTop()
    {
        if (CanBumpToTop && _eventBus != null)
        {
            _eventBus.Publish(new Models.BumpToTopRequestEvent(GlobalId));
        }
    }

    public bool CanBumpToTop => (State == PlaylistTrackState.Pending || State == PlaylistTrackState.Paused || State == PlaylistTrackState.Stalled) && !IsCompleted;
}
