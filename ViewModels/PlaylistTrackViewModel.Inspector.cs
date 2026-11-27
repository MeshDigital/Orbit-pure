using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>One playlist containing the inspected track (LP3 #554 "In playlists").</summary>
public sealed record TrackPlaylistMembership(Guid PlaylistId, string Title);

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

    // ── Metadata sources (library enrichment provenance) ─────────────────────────────────────

    private string? _metadataSourcesText;
    private bool _metadataSourcesRequested;

    /// <summary>"Cover Deezer · Label MusicBrainz · Genre Discogs", loaded on first read.</summary>
    public string? MetadataSourcesText
    {
        get
        {
            if (!_metadataSourcesRequested && _libraryService != null && !string.IsNullOrEmpty(GlobalId))
            {
                _metadataSourcesRequested = true;
                _ = LoadMetadataSourcesAsync();
            }
            return _metadataSourcesText;
        }
    }

    public bool HasMetadataSources => !string.IsNullOrEmpty(_metadataSourcesText);

    /// <summary>Re-read on the next binding pass (after enrichment changed the track).</summary>
    internal void InvalidateMetadataSources()
    {
        _metadataSourcesRequested = false;
        OnPropertyChanged(nameof(MetadataSourcesText));
    }

    private async Task LoadMetadataSourcesAsync()
    {
        try
        {
            var service = _libraryService!;
            var hash = GlobalId;
            var provenance = await Task.Run(() => service.GetMetadataProvenanceAsync(hash)).ConfigureAwait(false);
            var text = FormatMetadataSources(provenance);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _metadataSourcesText = text;
                OnPropertyChanged(nameof(MetadataSourcesText));
                OnPropertyChanged(nameof(HasMetadataSources));
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load metadata sources for track {GlobalId}", GlobalId);
        }
    }

    private static readonly (string Field, string Label)[] SourceFields =
    {
        ("artwork", "Cover"), ("album", "Album"), ("label", "Label"), ("genre", "Genre"), ("releaseDate", "Year"), ("isrc", "ISRC"),
        ("artist", "Artist"), ("title", "Title"),
    };

    /// <summary>Provenance rows → one line. "user" and file-tag sources are named plainly.</summary>
    internal static string? FormatMetadataSources(IReadOnlyDictionary<string, string> provenance)
    {
        var parts = new List<string>();
        foreach (var (field, label) in SourceFields)
        {
            if (!provenance.TryGetValue(field, out var source) || string.IsNullOrWhiteSpace(source)) continue;
            parts.Add($"{label} {SourceDisplayName(source)}");
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    internal static string SourceDisplayName(string source) => source.ToLowerInvariant() switch
    {
        "tags" => "file tags",
        "user" => "you",
        "deezer" => "Deezer",
        "musicbrainz" => "MusicBrainz",
        "discogs" => "Discogs",
        "acoustid" => "AcoustID",
        "spotify" => "Spotify",
        "beatport" => "Beatport",
        _ => source,
    };

    // ── Discogs top style + "Use as genre" ──────────────────────────────────────

    private string? _topStyleKey;
    private double _topStyleConfidence;
    private bool _stylesRequested;

    /// <summary>The top Discogs style name (e.g. "Deep House"), loaded on first read.</summary>
    public string? TopStyleDisplay
    {
        get
        {
            EnsureStylesRequested();
            return string.IsNullOrWhiteSpace(_topStyleKey) ? null : StylePlaylistPlanner.SplitKey(_topStyleKey).Name;
        }
    }

    public bool HasStyleScores => !string.IsNullOrWhiteSpace(_topStyleKey);

    public double TopStyleConfidence => _topStyleConfidence;

    internal void InvalidateStyles()
    {
        _stylesRequested = false;
        OnPropertyChanged(nameof(TopStyleDisplay));
        OnPropertyChanged(nameof(GenreEvidence));
    }

    private void EnsureStylesRequested()
    {
        if (_stylesRequested || _libraryService == null || string.IsNullOrEmpty(GlobalId)) return;
        _stylesRequested = true;
        IsLoadingGenreEvidence = true;
        _ = LoadStylesAsync();
    }

    private async Task LoadStylesAsync()
    {
        try
        {
            var features = await _libraryService!.GetAudioFeaturesByHashAsync(GlobalId);
            var (evidence, error) = ParseGenreEvidence(features?.GenreEvidenceJson, GlobalId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _topStyleKey = features?.TopStyle;
                _topStyleConfidence = features?.TopStyleConfidence ?? 0f;
                OnPropertyChanged(nameof(TopStyleDisplay));
                OnPropertyChanged(nameof(HasStyleScores));
                OnPropertyChanged(nameof(TopStyleConfidence));
                SetGenreEvidence(evidence, error);
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load style for track {GlobalId}", GlobalId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                SetGenreEvidence(null, "Couldn't load the genre evidence."));
        }
    }

    // ── Genre evidence (GA9 #254) ────────────────────────────────────────────────────────────

    private GenreEvidencePresentation? _genreEvidence;
    /// <summary>What the genre engine decided and why; loaded with the AI style on first read.</summary>
    public GenreEvidencePresentation? GenreEvidence
    {
        get
        {
            EnsureStylesRequested();
            return _genreEvidence;
        }
    }

    public bool HasGenreEvidence => _genreEvidence is not null;

    private bool _isLoadingGenreEvidence;
    public bool IsLoadingGenreEvidence
    {
        get => _isLoadingGenreEvidence;
        private set
        {
            if (SetProperty(ref _isLoadingGenreEvidence, value))
                OnPropertyChanged(nameof(ShowGenreEvidenceEmpty));
        }
    }

    private string? _genreEvidenceError;
    public string? GenreEvidenceError
    {
        get => _genreEvidenceError;
        private set
        {
            if (SetProperty(ref _genreEvidenceError, value))
            {
                OnPropertyChanged(nameof(HasGenreEvidenceError));
                OnPropertyChanged(nameof(ShowGenreEvidenceEmpty));
            }
        }
    }

    public bool HasGenreEvidenceError => !string.IsNullOrEmpty(GenreEvidenceError);

    /// <summary>Loaded, readable, and no evidence yet: "not analysed with the new genre engine".</summary>
    public bool ShowGenreEvidenceEmpty => !IsLoadingGenreEvidence && !HasGenreEvidence && !HasGenreEvidenceError && _stylesRequested;

    private string? _genreReanalyseStatus;
    /// <summary>"Queued for analysis" after Re-analyse, so the button gives honest feedback.</summary>
    public string? GenreReanalyseStatus { get => _genreReanalyseStatus; private set => SetProperty(ref _genreReanalyseStatus, value); }

    /// <summary>Old rows and malformed JSON become the empty state (logged), never a crash.</summary>
    internal static (GenreEvidencePresentation? Evidence, string? Error) ParseGenreEvidence(string? json, string? hash = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        var record = Engine.Analysis.GenreFusion.GenreEvidenceRecord.TryParse(json);
        if (record is null)
        {
            Serilog.Log.Warning("Unreadable GenreEvidenceJson for track {Hash}; showing the not-analysed state", hash);
            return (null, null);
        }
        try
        {
            return (GenreEvidencePresentation.From(record), null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not present the genre evidence for track {Hash}", hash);
            return (null, "Couldn't read the genre evidence.");
        }
    }

    private void SetGenreEvidence(GenreEvidencePresentation? evidence, string? error)
    {
        _genreEvidence = evidence;
        GenreEvidenceError = error;
        IsLoadingGenreEvidence = false;
        if (evidence is not null) GenreReanalyseStatus = null;
        OnPropertyChanged(nameof(GenreEvidence));
        OnPropertyChanged(nameof(HasGenreEvidence));
        OnPropertyChanged(nameof(ShowGenreEvidenceEmpty));
    }

    /// <summary>Re-analyse is offered for tracks with a file; ghosts have nothing to decode.</summary>
    public bool CanReanalyseGenre => _eventBus is not null && !IsGhost && !string.IsNullOrEmpty(GlobalId);

    private ICommand? _reanalyseGenreCommand;
    /// <summary>Queues the AI genre/mood pass (Mood engines: style scores, rhythm, fusion, evidence) for this track.</summary>
    public ICommand ReanalyseGenreCommand => _reanalyseGenreCommand ??= new RelayCommand(ReanalyseGenre, () => CanReanalyseGenre);

    private void ReanalyseGenre()
    {
        if (!CanReanalyseGenre) return;
        _eventBus!.Publish(new TrackAnalysisRequestedEvent(GlobalId, IsHighPriority: true, Engines: AnalysisEngine.Mood));
        GenreReanalyseStatus = "Queued for AI genre analysis.";
    }

    private ICommand? _useAsGenreCommand;
    public ICommand UseAsGenreCommand => _useAsGenreCommand ??= new RelayCommand(UseAsGenre, () => HasStyleScores);

    private void UseAsGenre()
    {
        if (!HasStyleScores) return;
        _eventBus?.Publish(new UseAsGenreRequestedEvent(GlobalId));
    }

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
        RaisePreparationPropertiesChanged();
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
        ? (Model.FrequencyCutoff.Value / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " kHz"
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

    // ── Loudness "Measure" link (tracks analysed before real LUFS metering) ───────────────────

    private bool _isMeasuringLoudness;

    /// <summary>True while this track's loudness is being measured (the link shows "Measuring…").</summary>
    public bool IsMeasuringLoudness
    {
        get => _isMeasuringLoudness;
        private set
        {
            if (_isMeasuringLoudness == value) return;
            _isMeasuringLoudness = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMeasureLoudness));
        }
    }

    /// <summary>Loudness shows "—" (legacy or never measured) but the file is here to measure.</summary>
    public bool CanMeasureLoudness =>
        !IsMeasuringLoudness
        && IsCompleted
        && Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(Model.Loudness) is null;

    private ICommand? _measureLoudnessCommand;
    /// <summary>Measures just this track's integrated loudness now (LoudnessBackfillService), without
    /// a full re-analysis, and updates the inspector when it lands.</summary>
    public ICommand MeasureLoudnessCommand => _measureLoudnessCommand ??=
        new RelayCommand(MeasureLoudness);

    private void MeasureLoudness()
    {
        if (_eventBus == null || string.IsNullOrEmpty(GlobalId) || !CanMeasureLoudness) return;
        IsMeasuringLoudness = true;

        var hash = GlobalId;
        ListenForLoudnessResult(hash);
        _eventBus.Publish(new MeasureTrackLoudnessRequestedEvent(hash));
    }

    private void ApplyLoudnessResult(TrackLoudnessMeasuredEvent result)
    {
        if (result.Success) Model.Loudness = result.Lufs;
        IsMeasuringLoudness = false;
        OnPropertyChanged(nameof(LoudnessDisplay));
        OnPropertyChanged(nameof(CanMeasureLoudness));
    }

    // ── LF4: Path protection (lock state & FileOrigin) ───────────────────────────────────────

    private bool _isPathLocked;
    private DateTime? _pathLockedAt;
    private string? _fileOrigin;
    private Func<bool, Task<Services.Library.Files.PathProtectionState>>? _togglePathLockCallback;
    private ICommand? _togglePathLockCommand;

    public bool IsPathLocked => _isPathLocked;
    public DateTime? PathLockedAt => _pathLockedAt;
    public string? FileOrigin => _fileOrigin;
    public bool HasPathProtectionAction => _togglePathLockCallback is not null;

    public string FileOriginDisplay => _fileOrigin switch
    {
        FileOriginDeriver.Downloaded => "Downloaded",
        FileOriginDeriver.LinkedFolder => "Linked folder",
        FileOriginDeriver.Imported => "Imported",
        _ => "Unknown",
    };

    public string FileOriginTooltip => _fileOrigin switch
    {
        FileOriginDeriver.LinkedFolder =>
            "Lives in a linked folder — ORBIT never moves or renames linked-folder files automatically.",
        FileOriginDeriver.Downloaded =>
            "Downloaded by ORBIT into your download directory.",
        FileOriginDeriver.Imported =>
            "Imported into your library from an external location.",
        _ => "File origin has not been classified yet.",
    };

    public string PathLockStatusText => _isPathLocked
        ? (_pathLockedAt is DateTime lockedAt
            ? $"Locked ({lockedAt.ToLocalTime():yyyy-MM-dd}) — protected from automatic moves"
            : "Locked — protected from automatic moves")
        : "Unlocked";

    public string PathLockButtonText => _isPathLocked ? "Unlock path" : "Lock path";

    public ICommand TogglePathLockCommand =>
        _togglePathLockCommand ??= new AsyncRelayCommand(TogglePathLockAsync, () => _togglePathLockCallback is not null);

    internal void ApplyPathProtection(
        Services.Library.Files.PathProtectionState state,
        Func<bool, Task<Services.Library.Files.PathProtectionState>>? toggleCallback = null)
    {
        _isPathLocked = state.IsLocked;
        _pathLockedAt = state.PathLockedAt;
        _fileOrigin = state.FileOrigin;
        if (toggleCallback is not null)
            _togglePathLockCallback = toggleCallback;

        OnPropertyChanged(nameof(IsPathLocked));
        OnPropertyChanged(nameof(PathLockedAt));
        OnPropertyChanged(nameof(FileOrigin));
        OnPropertyChanged(nameof(HasPathProtectionAction));
        OnPropertyChanged(nameof(FileOriginDisplay));
        OnPropertyChanged(nameof(FileOriginTooltip));
        OnPropertyChanged(nameof(PathLockStatusText));
        OnPropertyChanged(nameof(PathLockButtonText));
    }

    // ── In playlists (LP3 #554) ───────────────────────────────────────────────

    private Action<Guid>? _openMemberPlaylistCallback;
    public ObservableCollection<TrackPlaylistMembership> MemberPlaylists { get; } = new();
    public bool HasMemberPlaylists => MemberPlaylists.Count > 0;

    private ICommand? _openMemberPlaylistCommand;
    public ICommand OpenMemberPlaylistCommand => _openMemberPlaylistCommand ??= new RelayCommand<Guid>(id =>
    {
        if (id != Guid.Empty)
            _openMemberPlaylistCallback?.Invoke(id);
    });

    internal void ApplyMemberPlaylists(IReadOnlyList<TrackPlaylistMembership> memberships, Action<Guid>? onOpen)
    {
        _openMemberPlaylistCallback = onOpen;
        MemberPlaylists.Clear();
        foreach (var m in memberships)
            MemberPlaylists.Add(m);
        OnPropertyChanged(nameof(HasMemberPlaylists));
    }

    private async Task TogglePathLockAsync()
    {
        var callback = _togglePathLockCallback;
        if (callback is null) return;

        var target = !_isPathLocked;
        try
        {
            var updated = await callback(target);
            ApplyPathProtection(updated);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to toggle path lock for track {GlobalId}", GlobalId);
            _eventBus?.Publish(new ToastRequestedEvent(
                "Couldn't update path lock",
                $"'{TrackTitle}' was not updated. {ex.Message}",
                NotificationType.Error,
                null));
        }
    }

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
        OnPropertyChanged(nameof(TimeTillDropSeconds));
        OnPropertyChanged(nameof(TimeTillDropBars));
        OnPropertyChanged(nameof(HasTimeTillDrop));
        OnPropertyChanged(nameof(TimeTillDropDisplay));
        OnPropertyChanged(nameof(TimeTillDropBarsDisplay));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(EnergyRating));
        OnPropertyChanged(nameof(LastAnalyzedDisplay));
        OnPropertyChanged(nameof(HasWaveformData));
        OnPropertyChanged(nameof(HasPhraseSegments));
    }
}
