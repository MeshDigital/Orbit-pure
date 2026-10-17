using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Track Inspector surface of <see cref="PlaylistTrackViewModel"/> — everything the library
/// sidepanel (<c>LibraryTrackInspector.axaml</c>) binds to that the list rows don't need. Kept in
/// its own file so the inspector contract is readable in one place.
/// </summary>
public partial class PlaylistTrackViewModel
{
    // ── Tabs ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Inspector tab order. The selected tab is shared across tracks (like Rekordbox), so
    /// clicking through the list keeps you on the tab you were reading instead of snapping back.</summary>
    public const int InspectorTabOverview = 0;
    public const int InspectorTabCues = 1;
    public const int InspectorTabFile = 2;

    private static int s_lastInspectorTab = InspectorTabOverview;

    // ── Cues (from the real CuePoints table) ──────────────────────────────────────────────────

    /// <summary>True once <see cref="ApplyStoredCues"/> replaced the legacy CuePointsJson cues with
    /// rows from the CuePoints table — the table Cue Forge, the cue generator and the Rekordbox
    /// export use. The legacy blob is only a fallback for tracks that never got table rows.</summary>
    private bool _cuesFromCueTable;

    private bool _isLoadingCues;
    public bool IsLoadingCues
    {
        get => _isLoadingCues;
        internal set => SetProperty(ref _isLoadingCues, value);
    }

    private string? _cueLoadError;
    public string? CueLoadError
    {
        get => _cueLoadError;
        internal set
        {
            if (SetProperty(ref _cueLoadError, value))
                OnPropertyChanged(nameof(HasCueLoadError));
        }
    }

    public bool HasCueLoadError => !string.IsNullOrEmpty(CueLoadError);

    public int CueCount => _cues.Count;

    public string CuesTabHeader => _cues.Count > 0 ? $"Cues ({_cues.Count})" : "Cues";

    /// <summary>Cues in time order, the way the Cue Forge list shows them.</summary>
    public IReadOnlyList<OrbitCue> CuesByTime => _cues.OrderBy(c => c.Timestamp).ToList();

    /// <summary>Replaces this track's cues with the stored CuePoints rows. Called by
    /// <see cref="LibraryTrackInspectorViewModel"/> after selection settles.</summary>
    internal void ApplyStoredCues(IReadOnlyList<OrbitCue> cues)
    {
        _cues = cues.ToList();
        _cuesFromCueTable = true;
        RaiseCuePropertiesChanged();
    }

    private void RaiseCuePropertiesChanged()
    {
        OnPropertyChanged(nameof(Cues));
        OnPropertyChanged(nameof(CuesByTime));
        OnPropertyChanged(nameof(HasCues));
        OnPropertyChanged(nameof(CueCount));
        OnPropertyChanged(nameof(CuesTabHeader));
        OnPropertyChanged(nameof(HasAnalysisData));
    }

    /// <summary>Raised on the UI thread after a metadata/analysis refresh re-read this track from
    /// the database — the inspector re-pulls cues (analysis regenerates auto cues).</summary>
    public event EventHandler? AnalysisDataRefreshed;

    // ── Header facts ──────────────────────────────────────────────────────────────────────────

    public string BitrateDisplay => Bitrate is { } b && b != "—" ? $"{b} kbps" : "—";

    /// <summary>"FLAC · 1411 kbps" — one compact fact for the header row.</summary>
    public string FormatBitrateDisplay
    {
        get
        {
            var format = FormatDisplay;
            var bitrate = Bitrate is { } b && b != "—" ? b : null;
            if (format == "—" && bitrate is null) return "—";
            if (bitrate is null) return format;
            return format == "—" ? $"{bitrate} kbps" : $"{format} · {bitrate}";
        }
    }

    public bool HasFormatFact => FormatBitrateDisplay != "—";

    /// <summary>Quality tier text for a small badge (Gold / Verified / Review), empty when unknown.</summary>
    public string QualityTierDisplay => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "Gold",
        Data.IntegrityLevel.Verified => "Verified",
        Data.IntegrityLevel.Suspicious => "Review",
        _ => string.Empty
    };

    public bool HasQualityTier => QualityTierDisplay.Length > 0;
    public bool IsQualitySuspicious => Model.Integrity == Data.IntegrityLevel.Suspicious;

    // ── Overview: AI classification as ranked bars ────────────────────────────────────────────

    /// <summary>The five mood classifier scores, strongest first, zero scores dropped. Rendered by
    /// an ItemsControl exactly like <see cref="TopGenres"/> — the genre bars always refreshed live
    /// after analysis while the five hand-placed mood ProgressBars (sitting in a panel that was
    /// collapsed until the first ONNX run) did not, so mood now uses the proven path.</summary>
    public IReadOnlyList<GenreScore> MoodScores => BuildMoodScores(
        MoodPartyNorm, MoodAggressiveNorm, MoodHappyNorm, MoodRelaxedNorm, MoodSadNorm);

    internal static IReadOnlyList<GenreScore> BuildMoodScores(
        double party, double aggressive, double happy, double relaxed, double sad)
    {
        var all = new[]
        {
            new GenreScore("Party", party),
            new GenreScore("Aggressive", aggressive),
            new GenreScore("Happy", happy),
            new GenreScore("Relaxed", relaxed),
            new GenreScore("Sad", sad),
        };
        return all.Where(m => m.Confidence > 0).OrderByDescending(m => m.Confidence).ToArray();
    }

    /// <summary>Show the Genre &amp; Mood block whenever any classifier output exists. The old gate
    /// (<see cref="HasVibeData"/> = text genre or a non-"Neutral" mood tag) hid real mood bars on
    /// tracks whose winner-takes-all mood fell under the 0.35 threshold.</summary>
    public bool HasAiClassification => HasTopGenres || HasMoodBreakdown || ShowLegacyGenreFallback;

    public bool IsAwaitingAnalysis => !IsGhost && !HasAnalysisData;

    // ── File tab ──────────────────────────────────────────────────────────────────────────────

    public string FilePathDisplay => string.IsNullOrWhiteSpace(Model.ResolvedFilePath) ? "—" : Model.ResolvedFilePath;
    public bool HasFilePath => !string.IsNullOrWhiteSpace(Model.ResolvedFilePath);

    public string SpectralVerdictDisplay => Model.SpectralVerdictText ?? string.Empty;
    public bool HasSpectralVerdict => !string.IsNullOrWhiteSpace(Model.SpectralVerdictText);

    /// <summary>Everything after the verdict in QualityDetails ("verdict | detail | detail").</summary>
    public string QualityDetailsDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.QualityDetails)) return string.Empty;
            var parts = Model.QualityDetails.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? string.Join(" · ", parts.Skip(1)) : string.Empty;
        }
    }

    public bool HasQualityDetails => QualityDetailsDisplay.Length > 0;

    public string FrequencyCutoffDisplay => Model.FrequencyCutoff is > 0
        ? $"{Model.FrequencyCutoff.Value / 1000.0:0.0} kHz"
        : "—";

    public bool HasForensics => HasQualityTier || HasSpectralVerdict || Model.FrequencyCutoff is > 0;

    // ── Commands routed to the library (same handlers as the track list context menu) ─────────

    private ICommand? _openInCueForgeCommand;
    public ICommand OpenInCueForgeCommand => _openInCueForgeCommand ??=
        new RelayCommand(() => _eventBus?.Publish(new OpenInCueForgeFromInspectorEvent(GlobalId)), () => IsCompleted);

    private ICommand? _openInEditLabCommand;
    /// <summary>New Edit Lab project with this track as the base (tempo/key).</summary>
    public ICommand OpenInEditLabCommand => _openInEditLabCommand ??=
        new RelayCommand(() => _eventBus?.Publish(new OpenInEditLabFromInspectorEvent(GlobalId)), () => IsCompleted);

    // ── Persistence feedback ──────────────────────────────────────────────────────────────────

    /// <summary>Writes a rating/like change and, on failure, reverts the in-memory value and
    /// shows a toast — the old fire-and-forget left the stars showing a value that never saved.</summary>
    private async Task PersistEngagementAsync(Func<Task> write, string what, Action revert)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to save {What} for track {GlobalId}", what, GlobalId);
            Avalonia.Threading.Dispatcher.UIThread.Post(revert);
            _eventBus?.Publish(new ToastRequestedEvent(
                $"Couldn't save {what}",
                $"'{TrackTitle}' was not updated. {ex.Message}",
                NotificationType.Error,
                null));
        }
    }

    private void RaiseInspectorDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(MoodScores));
        OnPropertyChanged(nameof(HasAiClassification));
        OnPropertyChanged(nameof(IsAwaitingAnalysis));
        OnPropertyChanged(nameof(BitrateDisplay));
        OnPropertyChanged(nameof(FormatBitrateDisplay));
        OnPropertyChanged(nameof(HasFormatFact));
        OnPropertyChanged(nameof(QualityTierDisplay));
        OnPropertyChanged(nameof(HasQualityTier));
        OnPropertyChanged(nameof(IsQualitySuspicious));
        OnPropertyChanged(nameof(FilePathDisplay));
        OnPropertyChanged(nameof(HasFilePath));
        OnPropertyChanged(nameof(SpectralVerdictDisplay));
        OnPropertyChanged(nameof(HasSpectralVerdict));
        OnPropertyChanged(nameof(QualityDetailsDisplay));
        OnPropertyChanged(nameof(HasQualityDetails));
        OnPropertyChanged(nameof(FrequencyCutoffDisplay));
        OnPropertyChanged(nameof(HasForensics));
        OnPropertyChanged(nameof(HasAnalysisData));
        OnPropertyChanged(nameof(BpmDisplay));
        OnPropertyChanged(nameof(HasBpm));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(EnergyRating));
        OnPropertyChanged(nameof(LastAnalyzedDisplay));
        OnPropertyChanged(nameof(HasWaveformData));
        OnPropertyChanged(nameof(HasPhraseSegments));
    }
}
