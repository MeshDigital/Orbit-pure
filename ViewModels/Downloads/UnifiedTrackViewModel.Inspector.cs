using ReactiveUI;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Track Inspector surface for download rows (<c>TrackInspector.axaml</c>): emoji-free labels and
/// boolean state the view styles with design-system classes instead of view-model hex colours.
/// </summary>
public partial class UnifiedTrackViewModel
{
    /// <summary>"FLAC · 1411 kbps" for the header facts row; empty when neither is known.</summary>
    public string FormatBitrateDisplay
    {
        get
        {
            var format = FileFormat;
            var bitrate = Model.Bitrate is > 0 ? $"{Model.Bitrate} kbps" : null;
            if (string.IsNullOrEmpty(format)) return bitrate ?? string.Empty;
            return bitrate is null ? format : $"{format} · {bitrate}";
        }
    }

    public bool HasFormatFact => FormatBitrateDisplay.Length > 0;

    // Same deterministic tile as the library inspector while artwork loads / when there is none.
    public Avalonia.Media.IBrush FallbackArtBrush => SLSKDONET.Utils.ArtworkFallback.GetBrush($"{Model.Artist}{Model.Title}");
    public string FallbackArtLetter => SLSKDONET.Utils.ArtworkFallback.GetLetter(Model.Title ?? Model.Artist);

    public bool HasPeerName => !string.IsNullOrWhiteSpace(PeerName);

    /// <summary>The download-speed sparkline only means something while bytes are flowing.</summary>
    public bool HasLiveSpeed => IsDownloading;

    /// <summary>Spectral verdict as plain words (the badge text carries emoji).</summary>
    public string SpectralVerdictLabel
    {
        get
        {
            if (!HasSpectralVerdict) return string.Empty;
            return Model.Integrity switch
            {
                SLSKDONET.Data.IntegrityLevel.Gold => "True lossless",
                SLSKDONET.Data.IntegrityLevel.Suspicious => IsHighBitrateTranscode ? "Transcode (HQ)" : "Fake lossless",
                _ => SpectralVerdictText
            };
        }
    }

    public bool IsSpectralGenuine => HasSpectralVerdict && Model.Integrity == SLSKDONET.Data.IntegrityLevel.Gold;
    public bool IsSpectralSuspect => HasSpectralVerdict && Model.Integrity == SLSKDONET.Data.IntegrityLevel.Suspicious;

    private void RaiseInspectorPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(FormatBitrateDisplay));
        this.RaisePropertyChanged(nameof(HasFormatFact));
        this.RaisePropertyChanged(nameof(SpectralVerdictLabel));
        this.RaisePropertyChanged(nameof(IsSpectralGenuine));
        this.RaisePropertyChanged(nameof(IsSpectralSuspect));
        this.RaisePropertyChanged(nameof(HasBpm));
        this.RaisePropertyChanged(nameof(BpmDisplay));
        this.RaisePropertyChanged(nameof(CamelotDisplay));
    }
}
