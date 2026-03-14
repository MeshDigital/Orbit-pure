using System;
using System.IO;
using Avalonia.Media;
using Soulseek;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class AnalyzedSearchResultViewModel : ReactiveObject
    {
        private readonly SearchResult _result;
        public Models.SearchTier Tier { get; } // Phase 19



        public SearchResult RawResult => _result;

        // Base Properties
        public string Filename => Path.GetFileName(_result.Filename);
        public string FullPath => _result.Filename;

        // Clean Metadata (Phase 19.5: UI Refinement)
        public string ArtistName => !string.IsNullOrWhiteSpace(_result.Model.Artist) ? _result.Model.Artist : "Unknown Artist";
        public string TrackTitle => !string.IsNullOrWhiteSpace(_result.Model.Title) ? _result.Model.Title : Path.GetFileNameWithoutExtension(_result.Filename);
        public string DisplayName => $"{ArtistName} - {TrackTitle}";

        public long Size => _result.Size;
        public int BitRate => _result.Bitrate;
        public int? Length => _result.Length;
        public string User => _result.Username;
        public int UploadSpeed => _result.UploadSpeed;
        public string UploadSpeedDisplay => UploadSpeed > 0 ? $"{(double)UploadSpeed / 1024.0:F1}MB/s" : "Slow";
        public int QueueLength => _result.QueueLength;
        public bool SlotFree => _result.SlotFree;
        
        // Phase 19: Sonic Match Reason
        public string? MatchReason => _result.Model.MatchReason;
        public bool HasMatchReason => !string.IsNullOrEmpty(MatchReason);
        
        // Formatted Values
        public string DisplayLength => Length.HasValue ? TimeSpan.FromSeconds(Length.Value).ToString(@"mm\:ss") : "--:--";
        public string DisplaySize => $"{Size / 1024.0 / 1024.0:F1} MB";
        
        // Search 2.0 Forensic Properties
        public int TrustScore { get; }
        public string ForensicAssessment { get; }
        public bool IsGoldenMatch { get; }
        public bool IsFake { get; }
        public bool IsSuspicious => IsFake;
        
        public double MatchConfidence => Math.Clamp(_result.CurrentRank, 0, 100);
        
        public string MatchConfidenceColor => MatchConfidence switch
        {
            >= 90 => "#1DB954", // Green
            >= 70 => "#FFD700", // Yellow/Gold
            _ => "#E91E63"      // Red
        };

        public bool IsHighRisk => _result.Model.IsFlagged;
        public string? FlagReason => _result.Model.FlagReason;

        public IBrush ItemBackground
        {
            get
            {
                if (IsFake) return Brushes.Transparent; // Will look slightly dimmed due to text color
                if (IsGoldenMatch) return new SolidColorBrush(Color.Parse("#1A201A")); // Very subtle green tint
                return Brushes.Transparent;
            }
        }
        
        public IBrush ForegroundColor
        {
            get
            {
                if (IsFake) return new SolidColorBrush(Color.Parse("#666666")); // Dimmed
                if (IsGoldenMatch) return Brushes.White;
                return new SolidColorBrush(Color.Parse("#DDDDDD"));
            }
        }

        public string TrustColor
        {
            get
            {
                if (TrustScore >= 90) return "#1DB954"; // Green
                if (TrustScore >= 70) return "#2196F3"; // Blue
                if (TrustScore >= 50) return "#FFC107"; // Amber
                return "#F44336"; // Red
            }
        }
        
        // Trust Bar Visualization (Width 0-100)
        public double TrustBarWidth => TrustScore;

        // Opacity for Ghosting (The Bouncer Phase 14A)
        public double Opacity => IsFake ? 0.3 : 1.0;

        public AnalyzedSearchResultViewModel(SearchResult result)
        {
            _result = result;

            // Calculate Metrics
            TrustScore = MetadataForensicService.CalculateTrustScore(result.Model);
            ForensicAssessment = MetadataForensicService.GetForensicAssessment(result.Model);
            IsGoldenMatch = MetadataForensicService.IsGoldenMatch(result.Model);
            
            // Phase 14A: The Bouncer Integration
            // Combine existing forensic checks with new SafetyFilter flags
            IsFake = MetadataForensicService.IsFake(result.Model) || result.Model.IsFlagged;
            
            if (result.Model.IsFlagged)
            {
                ForensicAssessment = result.Model.FlagReason ?? "Flagged by Bouncer";
            }
            
            // Sync with base SearchResult for Filter & Badge logic
            // Sync with base SearchResult for Filter & Badge logic
            if (IsFake) _result.IntegrityStatus = "Suspect";
            else if (IsGoldenMatch) _result.IntegrityStatus = "Verified";
            else _result.IntegrityStatus = "";
            
            // Phase 19: Search 2.0 Tier Calculation
            Tier = MetadataForensicService.CalculateTier(result.Model);
        }

        public string TierBadge => MetadataForensicService.GetTierBadge(Tier);

        public string TierDescription => MetadataForensicService.GetTierDescription(Tier);

        public IBrush TierColor => new SolidColorBrush(Color.Parse(MetadataForensicService.GetTierColor(Tier)));
    }
}
