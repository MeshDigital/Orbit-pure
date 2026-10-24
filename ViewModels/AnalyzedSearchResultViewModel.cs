using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using ReactiveUI;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels
{
    /// <summary>
    /// One row on the ad-hoc Search page (and the SearchInspector detail panel). Pure
    /// presentation over <see cref="SearchResult"/>: formatted, tabular-friendly strings plus
    /// flags the view turns into style classes. No colours live here — the view maps flags
    /// to design-system tokens.
    /// </summary>
    public class AnalyzedSearchResultViewModel : ReactiveObject
    {
        

        private readonly SearchResult _result;
        private bool _isFilteredOut;
        private string? _filteredOutReason;

        public SearchResult RawResult => _result;

        // Base properties
        public string Filename => Path.GetFileName(_result.Filename);
        public string FullPath => _result.Filename;
        public string FolderPath
        {
            get
            {
                var full = _result.Filename ?? string.Empty;
                var idx = full.LastIndexOfAny(new[] { '\\', '/' });
                return idx > 0 ? full[..idx] : string.Empty;
            }
        }

        public string ArtistName => !string.IsNullOrWhiteSpace(_result.Model.Artist) ? _result.Model.Artist : "Unknown Artist";
        public string TrackTitle => !string.IsNullOrWhiteSpace(_result.Model.Title) ? _result.Model.Title : Path.GetFileNameWithoutExtension(_result.Filename);
        public string DisplayName => $"{ArtistName} - {TrackTitle}";

        public long Size => _result.Size;
        public int BitRate => _result.Bitrate;
        public int? Length => _result.Length;
        public string User => _result.Username;
        public int UploadSpeed => _result.UploadSpeed;
        /// <summary>Peer upload speed. Soulseek reports bytes per second.</summary>
        public string UploadSpeedDisplay => FormatSpeed(UploadSpeed);
        public int QueueLength => _result.QueueLength;
        public int? SampleRate => _result.SampleRate;
        public int? BitDepth => _result.BitDepth;

        public string SampleRateDisplay => SampleRate.HasValue ? NumFormat.Ui($"{SampleRate.Value / 1000.0:0.#} kHz") : "";
        public string BitDepthDisplay => BitDepth.HasValue ? $"{BitDepth.Value}-bit" : "";
        public string AudioQualityDisplay => string.Join(" / ", new[] { BitDepthDisplay, SampleRateDisplay }.Where(s => !string.IsNullOrEmpty(s)));
        public bool HasAudioQualityDisplay => AudioQualityDisplay.Length > 0;

        public bool SlotFree => _result.SlotFree;

        /// <summary>"Free slot" / "Queue 12" — short peer availability label.</summary>
        public string PeerAvailabilityText => SlotFree
            ? (QueueLength > 0 ? $"Free slot, queue {QueueLength}" : "Free slot")
            : $"No free slot, queue {QueueLength}";

        /// <summary>Second line of the peer column: "1.2 MB/s · Q 3".</summary>
        public string PeerStatsText => $"{UploadSpeedDisplay} · Q {QueueLength}";

        // Quality tier (format + bitrate) — a small fixed-width badge on the row.
        public string FormatLabel => string.IsNullOrWhiteSpace(_result.FileFormat) ? "?" : _result.FileFormat;
        public bool IsLossless => SLSKDONET.Services.Audio.AudioFormats.IsLossless(FormatLabel);
        /// <summary>Known lossy bitrate under 256 kbps.</summary>
        public bool IsLowQuality => !IsLossless && BitRate > 0 && BitRate < 256;
        public string BitrateDisplay => BitRate > 0 ? BitRate.ToString() : "—";
        public string QualityTooltip
        {
            get
            {
                var parts = new[]
                {
                    FormatLabel,
                    BitRate > 0 ? $"{BitRate} kbps" : "bitrate not reported",
                    AudioQualityDisplay
                };
                var text = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
                return IsLowQuality ? text + " (below 256 kbps)" : text;
            }
        }

        // Match score + reason
        public string? MatchReason => _result.PreferredReason;
        public bool HasMatchReason => _result.HasPreferredReason;
        public double MatchConfidence => Math.Clamp(_result.CurrentRank, 0, 100);
        public string MatchScoreText => $"{MatchConfidence:0}";
        public string MatchTooltip => HasMatchReason
            ? $"Match {MatchConfidence:0}% — {MatchReason}"
            : $"Match {MatchConfidence:0}%";

        // Formatted values
        public string DisplayLength => Length.HasValue ? TimeSpan.FromSeconds(Length.Value).ToString(@"m\:ss") : "—";
        public string DisplaySize => Size > 0 ? NumFormat.Ui($"{Size / 1024.0 / 1024.0:0.0} MB") : "—";

        // Safety
        public bool IsFake { get; }
        public bool IsSuspicious => IsFake;
        public bool IsHighRisk => _result.Model.IsFlagged;
        public bool ShowHighRiskBadge => IsHighRisk;
        public string? FlagReason => _result.Model.FlagReason;

        public bool IsFilteredOut
        {
            get => _isFilteredOut;
            private set => this.RaiseAndSetIfChanged(ref _isFilteredOut, value);
        }

        public string? FilteredOutReason
        {
            get => _filteredOutReason;
            private set => this.RaiseAndSetIfChanged(ref _filteredOutReason, value);
        }

        public bool HasFilteredOutReason => !string.IsNullOrWhiteSpace(FilteredOutReason);

        /// <summary>Ghosting for rows that fail the current filters or are flagged.</summary>
        public double Opacity => IsFilteredOut ? 0.45 : IsFake ? 0.5 : 1.0;

        // Download state (kept live by SearchViewModel from TrackStateChangedEvent)
        public bool IsQueued => _result.Status == Models.TrackStatus.Pending;
        public bool IsDownloaded => _result.Status == Models.TrackStatus.Downloaded;
        public bool IsDownloadFailed => _result.Status == Models.TrackStatus.Failed;
        public bool HasDownloadStatus => IsQueued || IsDownloaded || IsDownloadFailed
                                         || _result.Status is Models.TrackStatus.Skipped or Models.TrackStatus.OnHold;
        public string DownloadStatusText => _result.Status switch
        {
            Models.TrackStatus.Pending    => "Queued for download",
            Models.TrackStatus.Downloaded => "Downloaded",
            Models.TrackStatus.Failed     => "Download failed",
            Models.TrackStatus.Skipped    => "Skipped",
            Models.TrackStatus.OnHold     => "On hold",
            _                             => ""
        };

        public AnalyzedSearchResultViewModel(SearchResult result)
        {
            _result = result;
            _result.PropertyChanged += OnRawResultPropertyChanged;

            IsFake = result.Model.IsFlagged;
            // Feeds SearchFilterViewModel's "Hide suspects" rule.
            _result.IntegrityStatus = IsFake ? "Suspect" : "";
        }

        public static string FormatSpeed(int bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "—";
            return SLSKDONET.Utils.NumFormat.Speed(bytesPerSecond);
        }

        private void OnRawResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SearchResult.Status):
                case nameof(SearchResult.StatusIcon):
                    this.RaisePropertyChanged(nameof(HasDownloadStatus));
                    this.RaisePropertyChanged(nameof(IsQueued));
                    this.RaisePropertyChanged(nameof(IsDownloaded));
                    this.RaisePropertyChanged(nameof(IsDownloadFailed));
                    this.RaisePropertyChanged(nameof(DownloadStatusText));
                    break;
                case nameof(SearchResult.CurrentRank):
                case nameof(SearchResult.PreferredReason):
                    this.RaisePropertyChanged(nameof(MatchConfidence));
                    this.RaisePropertyChanged(nameof(MatchScoreText));
                    this.RaisePropertyChanged(nameof(MatchReason));
                    this.RaisePropertyChanged(nameof(HasMatchReason));
                    this.RaisePropertyChanged(nameof(MatchTooltip));
                    break;
            }
        }

        public void SetFilterVisibility(bool isFilteredOut, string? reason)
        {
            IsFilteredOut = isFilteredOut;
            FilteredOutReason = reason;
            this.RaisePropertyChanged(nameof(HasFilteredOutReason));
            this.RaisePropertyChanged(nameof(Opacity));
        }
    }
}
