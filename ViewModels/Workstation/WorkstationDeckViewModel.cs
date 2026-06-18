using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.Models.Stem;
using SLSKDONET.ViewModels;
using System.Collections.Generic;
using System.Globalization;

namespace SLSKDONET.ViewModels.Workstation;

public enum WorkstationTrackEligibilityIssue
{
    None,
    NoTrack,
    NotDownloaded,
    MissingFile,
    MissingHash,
    MissingWaveform,
    MissingCues,
    MissingAnalysis,
}

/// <summary>
/// Thin wrap of <see cref="DeckSlotViewModel"/> that adds workstation-specific
/// state: deck label, loaded track metadata, stems, and stem preset shortcuts.
/// </summary>
public sealed class WorkstationDeckViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly StemPreferenceService _stemPrefService;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;

    /// <summary>
    /// Callback invoked whenever a track finishes loading into this deck.
    /// Set by <see cref="WorkstationViewModel"/> to trigger a session autosave.
    /// </summary>
    public Func<Task>? OnTrackLoaded { get; set; }

    /// <summary>
    /// Raised when deck state changes that affect transition and harmonic guidance.
    /// </summary>
    public Action? OnDeckStateChanged { get; set; }

    // ── Wrapped deck engine VM ────────────────────────────────────────────────
    public DeckSlotViewModel Deck { get; }

    // ── Deck identity ─────────────────────────────────────────────────────────
    public string DeckLabel { get; }  // "A", "B", "C" …

    // ── Loaded track metadata (set when a track is dropped/loaded) ────────────
    private string? _trackTitle;
    public string? TrackTitle
    {
        get => _trackTitle;
        set => this.RaiseAndSetIfChanged(ref _trackTitle, value);
    }

    private string? _trackArtist;
    public string? TrackArtist
    {
        get => _trackArtist;
        set => this.RaiseAndSetIfChanged(ref _trackArtist, value);
    }

    private string? _trackKey;
    public string? TrackKey
    {
        get => _trackKey;
        set => this.RaiseAndSetIfChanged(ref _trackKey, value);
    }

    private double _displayBpm;
    public double DisplayBpm
    {
        get => _displayBpm;
        set => this.RaiseAndSetIfChanged(ref _displayBpm, value);
    }

    private bool _isLoaded;
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => this.RaiseAndSetIfChanged(ref _isLoaded, value);
    }

    /// <summary>Set when a track load fails (file not found, codec error, etc). Null when healthy.</summary>
    private string? _trackLoadError;
    public string? TrackLoadError
    {
        get => _trackLoadError;
        set => this.RaiseAndSetIfChanged(ref _trackLoadError, value);
    }

    // ── Stem mixer ────────────────────────────────────────────────────────────
    public StemMixerViewModel Stems { get; }

    /// <summary>Waveform rows for the four separated stems, shown in Stems mode.</summary>
    public StemWaveformViewModel StemWaveforms { get; }

    private bool _stemsVisible;
    /// <summary>True after stem separation completes — shows the fader panel.</summary>
    public bool StemsVisible
    {
        get => _stemsVisible;
        private set => this.RaiseAndSetIfChanged(ref _stemsVisible, value);
    }

    // ── Waveform data (populated when track loads) ────────────────────────────
    private WaveformAnalysisData? _waveformData;
    public WaveformAnalysisData? WaveformData
    {
        get => _waveformData;
        set => this.RaiseAndSetIfChanged(ref _waveformData, value);
    }

    /// <summary>Playback position as 0..1 fraction for WaveformControl.Progress.</summary>
    public float PlaybackProgress =>
        Deck.DurationSeconds > 0
            ? (float)(Deck.PositionSeconds / Deck.DurationSeconds)
            : 0f;

    /// <summary>
    /// Track gain in dB for the deck output. Internally mapped to DeckEngine.VolumeLevel.
    /// Note: DeckEngine currently caps at +6 dB equivalent.
    /// </summary>
    private double _trackGainDb;
    public double TrackGainDb
    {
        get => _trackGainDb;
        set
        {
            var clamped = Math.Clamp(value, -12.0, 12.0);
            this.RaiseAndSetIfChanged(ref _trackGainDb, clamped);
            var linear = (float)Math.Pow(10.0, clamped / 20.0);
            Deck.Engine.VolumeLevel = Math.Clamp(linear, 0f, 2f);
            this.RaisePropertyChanged(nameof(VuLevel));
        }
    }

    /// <summary>Placeholder meter signal until full metering is integrated.</summary>
    public double VuLevel
    {
        get
        {
            if (!Deck.IsPlaying)
            {
                return 0;
            }

            var movement = 0.15 + (PlaybackProgress * 0.75);
            return Math.Clamp(movement, 0, 1);
        }
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set => this.RaiseAndSetIfChanged(ref _isLocked, value);
    }

    private bool _isFocusedDeck;
    public bool IsFocusedDeck
    {
        get => _isFocusedDeck;
        set => this.RaiseAndSetIfChanged(ref _isFocusedDeck, value);
    }

    // Stem-reactive waveform overlays
    public byte[] LowBandForWaveform =>
        WaveformData == null || Stems.Bass.IsMuted
            ? Array.Empty<byte>()
            : WaveformData.LowData;

    public byte[] MidBandForWaveform =>
        WaveformData == null || Stems.Drums.IsMuted
            ? Array.Empty<byte>()
            : WaveformData.MidData;

    public byte[] HighBandForWaveform =>
        WaveformData == null || (Stems.Vocals.IsMuted && Stems.Other.IsMuted)
            ? Array.Empty<byte>()
            : WaveformData.HighData;

    public IEnumerable<PhraseSegment> PhraseSegments => BuildPhraseSegments();

    public IEnumerable<float>? EnergyCurveForWaveform => BuildEnergyCurve();

    public IReadOnlyList<double>? EnergyCurveBarPoints => BuildEnergyCurveDouble();

    private double _waveformZoomLevel = 1.0;
    public double WaveformZoomLevel
    {
        get => _waveformZoomLevel;
        set
        {
            var clamped = Math.Clamp(value, 1.0, 16.0);
            this.RaiseAndSetIfChanged(ref _waveformZoomLevel, clamped);
            this.RaisePropertyChanged(nameof(IsSemanticDetailMode));
            this.RaisePropertyChanged(nameof(WaveformDetailSummary));
        }
    }

    private double _waveformViewOffset;
    public double WaveformViewOffset
    {
        get => _waveformViewOffset;
        set
        {
            this.RaiseAndSetIfChanged(ref _waveformViewOffset, Math.Clamp(value, 0.0, 1.0));
            this.RaisePropertyChanged(nameof(WaveformDetailSummary));
        }
    }

    public bool IsSemanticDetailMode => WaveformZoomLevel >= 3.0;

    // ── Live position / state display ─────────────────────────────────────────
    public string PositionDisplayText =>
        IsLoaded ? $"{FormatSeconds(Deck.PositionSeconds)} / {FormatSeconds(Deck.DurationSeconds)}" : "–:– / –:–";

    public bool IsLoopActive => Deck.IsLoopActive;

    public string HotCueSummary => BuildCuePrepSummary(Deck.HotCues);
    public string MixReadinessText => BuildMixReadinessText(TrackKey, DisplayBpm, CueEditor.Cues);
    public string PerformanceStatusSummary => BuildPerformanceStatusSummary(DeckLabel, IsLoaded, Deck.IsPlaying, Deck.IsLoopActive, Deck.KeyLock, Deck.TempoPercent);
    public string SectionJumpSummary => BuildSectionJumpSummary(CueEditor.Cues);
    public string StemControlSummary => BuildStemControlSummary(StemsVisible, Stems.IsSeparating, Stems.SeparationProgress, Stems.Vocals.IsMuted, Stems.Drums.IsSoloed);
    public string WaveformDetailSummary => BuildWaveformDetailSummary(IsSemanticDetailMode, WaveformZoomLevel, WaveformViewOffset);
    public string CurrentKeyShiftText => Deck.SemitoneShift == 0 ? "0 st" : $"{Deck.SemitoneShift:+#;-#;0} st";

    private string _harmonicSuggestionText = "Native key • load another deck for harmonic advice";
    public string HarmonicSuggestionText
    {
        get => _harmonicSuggestionText;
        private set => this.RaiseAndSetIfChanged(ref _harmonicSuggestionText, value);
    }

    private string _transitionStatusText = "Load another deck for live transition guidance";
    public string TransitionStatusText
    {
        get => _transitionStatusText;
        private set => this.RaiseAndSetIfChanged(ref _transitionStatusText, value);
    }

    public string HotCue1Text => BuildHotCuePadText(GetHotCue(0), 0);
    public string HotCue2Text => BuildHotCuePadText(GetHotCue(1), 1);
    public string HotCue3Text => BuildHotCuePadText(GetHotCue(2), 2);
    public string HotCue4Text => BuildHotCuePadText(GetHotCue(3), 3);
    public string HotCue5Text => BuildHotCuePadText(GetHotCue(4), 4);
    public string HotCue6Text => BuildHotCuePadText(GetHotCue(5), 5);
    public string HotCue7Text => BuildHotCuePadText(GetHotCue(6), 6);
    public string HotCue8Text => BuildHotCuePadText(GetHotCue(7), 7);

    public string HotCue1Tooltip => BuildHotCueTooltip(GetHotCue(0), 0);
    public string HotCue2Tooltip => BuildHotCueTooltip(GetHotCue(1), 1);
    public string HotCue3Tooltip => BuildHotCueTooltip(GetHotCue(2), 2);
    public string HotCue4Tooltip => BuildHotCueTooltip(GetHotCue(3), 3);
    public string HotCue5Tooltip => BuildHotCueTooltip(GetHotCue(4), 4);
    public string HotCue6Tooltip => BuildHotCueTooltip(GetHotCue(5), 5);
    public string HotCue7Tooltip => BuildHotCueTooltip(GetHotCue(6), 6);
    public string HotCue8Tooltip => BuildHotCueTooltip(GetHotCue(7), 7);

    // ── Cue editor ────────────────────────────────────────────────────────────
    public CueEditorViewModel CueEditor { get; }

    /// <summary>The hash of the currently loaded track (used for cue DB lookups).</summary>
    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set => this.RaiseAndSetIfChanged(ref _trackHash, value);
    }

    // ── Audio intelligence fields (populated from PlaylistTrack on load) ───────
    private string? _moodTag;
    private double? _energy;
    private float? _instrumentalProbability;
    private float? _bpmStability;

    public bool HasMoodTag => IsLoaded && !string.IsNullOrWhiteSpace(_moodTag);
    public string MoodTagText => _moodTag ?? string.Empty;

    public bool HasEnergyBadge => IsLoaded && _energy.HasValue;
    private int ComputedEnergyScore => (int)Math.Round((_energy ?? 0) * 10);
    public string EnergyBadgeText => HasEnergyBadge ? $"E{ComputedEnergyScore}" : string.Empty;
    public string EnergyBadgeColor => ComputedEnergyScore switch
    {
        >= 8 => "#E74C3C",
        >= 5 => "#F39C12",
        _    => "#27AE60",
    };

    public bool ShowInstrumentalBadge => IsLoaded && _instrumentalProbability >= 0.75f;
    public bool HasBpmDriftWarning => IsLoaded && _bpmStability.HasValue && _bpmStability.Value < 0.70f;

    private void ApplyTrackIntelligence(PlaylistTrack track)
    {
        _moodTag = track.MoodTag;
        _energy  = track.Energy;
        _instrumentalProbability = (float?)track.InstrumentalProbability;
        _bpmStability = track.BpmStability;
        this.RaisePropertyChanged(nameof(HasMoodTag));
        this.RaisePropertyChanged(nameof(MoodTagText));
        this.RaisePropertyChanged(nameof(HasEnergyBadge));
        this.RaisePropertyChanged(nameof(EnergyBadgeText));
        this.RaisePropertyChanged(nameof(EnergyBadgeColor));
        this.RaisePropertyChanged(nameof(ShowInstrumentalBadge));
        this.RaisePropertyChanged(nameof(HasBpmDriftWarning));
    }

    private void ClearTrackIntelligence()
    {
        _moodTag = null; _energy = null; _instrumentalProbability = null; _bpmStability = null;
        this.RaisePropertyChanged(nameof(HasMoodTag));
        this.RaisePropertyChanged(nameof(HasEnergyBadge));
        this.RaisePropertyChanged(nameof(ShowInstrumentalBadge));
        this.RaisePropertyChanged(nameof(HasBpmDriftWarning));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Load a file path into this deck (drops old track first).</summary>
    public ReactiveCommand<string, Unit> LoadTrackCommand { get; }

    /// <summary>Load a <see cref="PlaylistTrack"/> — provides both file path and track hash for cue loading.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadPlaylistTrackCommand { get; }

    /// <summary>Instantly mute Vocals stem (or unmute if already muted).</summary>
    public ReactiveCommand<Unit, Unit> ToggleVocalOffCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleVocalsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDrumsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBassCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleOtherCommand { get; }

    /// <summary>Solo Drums stem, mute everything else (toggle).</summary>
    public ReactiveCommand<Unit, Unit> ToggleDrumsOnlyCommand { get; }

    /// <summary>Mute Vocals so only the instrumental plays (toggle).</summary>
    public ReactiveCommand<Unit, Unit> ToggleInstrumentalCommand { get; }

    /// <summary>Trigger ONNX stem separation for the loaded file.</summary>
    public ReactiveCommand<Unit, Unit> SeparateStemsCommand { get; }

    // ── Hot-cue pad commands (slot 1–8 ≃ engine slots 0–7) ───────────────────────
    // CDJ behaviour: empty slot → set+persist cue; filled slot → jump.
    public ReactiveCommand<Unit, Unit> HotCue1Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue2Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue3Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue4Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue5Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue6Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue7Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue8Command { get; }

    // ── Loop controls ─────────────────────────────────────────────────────────
    private double? _loopInSeconds;
    public double? LoopInSeconds
    {
        get => _loopInSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _loopInSeconds, value);
            this.RaisePropertyChanged(nameof(LoopRegionText));
            this.RaisePropertyChanged(nameof(LoopIsReady));
        }
    }

    private double? _loopOutSeconds;
    public double? LoopOutSeconds
    {
        get => _loopOutSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _loopOutSeconds, value);
            this.RaisePropertyChanged(nameof(LoopRegionText));
            this.RaisePropertyChanged(nameof(LoopIsReady));
        }
    }

    public bool LoopIsReady => _loopInSeconds.HasValue && _loopOutSeconds.HasValue
                               && _loopOutSeconds.Value > _loopInSeconds.Value;

    public string LoopRegionText
    {
        get
        {
            if (!_loopInSeconds.HasValue && !_loopOutSeconds.HasValue) return "– No loop set –";
            var inStr  = _loopInSeconds.HasValue  ? FormatSeconds(_loopInSeconds.Value)  : "?";
            var outStr = _loopOutSeconds.HasValue ? FormatSeconds(_loopOutSeconds.Value) : "?";
            var lenStr = LoopIsReady
                ? $" ({FormatSeconds(_loopOutSeconds!.Value - _loopInSeconds!.Value)} long)"
                : string.Empty;
            return $"{inStr} → {outStr}{lenStr}";
        }
    }

    public string LoopLengthBarsText
    {
        get
        {
            if (!LoopIsReady || DisplayBpm <= 0) return string.Empty;
            double barLength = 240.0 / DisplayBpm; // 4/4 bar in seconds
            double loopBars = (_loopOutSeconds!.Value - _loopInSeconds!.Value) / barLength;
            return loopBars >= 0.5 ? $"{loopBars:0.##} bar{(Math.Abs(loopBars - 1.0) < 0.01 ? "" : "s")}" : string.Empty;
        }
    }

    /// <summary>Set loop in-point to current playhead position.</summary>
    public ReactiveCommand<Unit, Unit> SetLoopInCommand { get; }

    /// <summary>Set loop out-point to current playhead position.</summary>
    public ReactiveCommand<Unit, Unit> SetLoopOutCommand { get; }

    /// <summary>Commit the staged loop in/out to the CueEditor (persists to DB).</summary>
    public ReactiveCommand<Unit, Unit> CommitLoopCommand { get; }

    /// <summary>Clear the staged loop in/out and remove any saved loop cue from DB.</summary>
    public ReactiveCommand<Unit, Unit> ClearStagedLoopCommand { get; }

    /// <summary>Set a loop of N bars from the current position (auto-loop). 0.5 = half bar.</summary>
    public ReactiveCommand<double, Unit> AutoLoopBarsCommand { get; }

    /// <summary>Drop a memory cue (Num=-1) at the current playhead, persisted to DB.</summary>
    public ReactiveCommand<Unit, Unit> AddMemoryCueCommand { get; }

    /// <summary>Remove the cue assigned to hot-cue pad slot 0–7. Clears pad + removes from DB.</summary>
    public ReactiveCommand<int, Unit> DeleteHotCueAtSlotCommand { get; }

    public ReactiveCommand<Unit, Unit> ViewWaveformCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToIntroCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToBuildCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToDropCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToOutroCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCueAtPlayheadCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCuePrepCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseKeyShiftCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetKeyShiftCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseKeyShiftCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public WorkstationDeckViewModel(string deckLabel, DeckSlotViewModel deck,
        CachedStemSeparator stemSeparator, ICuePointService cueService,
        StemPreferenceService stemPrefService,
        IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        DeckLabel  = deckLabel;
        Deck          = deck;
        Stems         = new StemMixerViewModel(stemSeparator);
        StemWaveforms = new StemWaveformViewModel();
        CueEditor     = new CueEditorViewModel(cueService);
        _stemPrefService = stemPrefService;
        _dbFactory = dbFactory;

        LoadTrackCommand         = ReactiveCommand.CreateFromTask<string>(LoadTrackAsync);
        LoadPlaylistTrackCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(LoadPlaylistTrackAsync);

        // Swallow load errors so the ReactiveCommand pipeline never propagates
        // an unhandled exception that kills the app. Expose the message via TrackLoadError.
        LoadTrackCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex => TrackLoadError = ex.InnerException?.Message ?? ex.Message)
            .DisposeWith(_disposables);
        LoadPlaylistTrackCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex => TrackLoadError = ex.InnerException?.Message ?? ex.Message)
            .DisposeWith(_disposables);

        ToggleVocalOffCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Vocals.IsMuted = !Stems.Vocals.IsMuted;
            await SaveStemPrefsAsync();
        });

        ToggleVocalsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Vocals.IsMuted = !Stems.Vocals.IsMuted;
            RaiseWaveformBandPropertiesChanged();
            await SaveStemPrefsAsync();
        });

        ToggleDrumsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Drums.IsMuted = !Stems.Drums.IsMuted;
            RaiseWaveformBandPropertiesChanged();
            await SaveStemPrefsAsync();
        });

        ToggleBassCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Bass.IsMuted = !Stems.Bass.IsMuted;
            RaiseWaveformBandPropertiesChanged();
            await SaveStemPrefsAsync();
        });

        ToggleOtherCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Other.IsMuted = !Stems.Other.IsMuted;
            RaiseWaveformBandPropertiesChanged();
            await SaveStemPrefsAsync();
        });

        ToggleDrumsOnlyCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            bool solo = !Stems.Drums.IsSoloed;
            Stems.Vocals.IsSoloed = false;
            Stems.Bass.IsSoloed   = false;
            Stems.Other.IsSoloed  = false;
            Stems.Drums.IsSoloed  = solo;
            await SaveStemPrefsAsync();
        });

        ToggleInstrumentalCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Stems.Vocals.IsMuted = !Stems.Vocals.IsMuted;
            await SaveStemPrefsAsync();
        });

        var canSeparate = this.WhenAnyValue(
            x => x.IsLoaded, x => x.Stems.IsSeparating,
            (loaded, separating) => loaded && !separating);

        SeparateStemsCommand = ReactiveCommand.CreateFromTask(
            SeparateStemsAsync, canSeparate);

        HotCue1Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(0));
        HotCue2Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(1));
        HotCue3Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(2));
        HotCue4Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(3));
        HotCue5Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(4));
        HotCue6Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(5));
        HotCue7Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(6));
        HotCue8Command = ReactiveCommand.CreateFromTask(() => TriggerHotCuePadAsync(7));

        var hasTrack = this.WhenAnyValue(x => x.IsLoaded);

        SetLoopInCommand = ReactiveCommand.Create(() =>
        {
            LoopInSeconds = Deck.PositionSeconds;
        }, hasTrack);

        SetLoopOutCommand = ReactiveCommand.Create(() =>
        {
            LoopOutSeconds = Deck.PositionSeconds;
        }, hasTrack);

        var canCommitLoop = this.WhenAnyValue(
            x => x.LoopIsReady, x => x.IsLoaded,
            (ready, loaded) => ready && loaded);

        CommitLoopCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!LoopIsReady) return;
            await CueEditor.SetLoopCommand.Execute((_loopInSeconds!.Value, _loopOutSeconds!.Value)).FirstAsync();
        }, canCommitLoop);

        ClearStagedLoopCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            LoopInSeconds  = null;
            LoopOutSeconds = null;
            await CueEditor.ClearLoopCommand.Execute().FirstAsync();
        }, hasTrack);

        AutoLoopBarsCommand = ReactiveCommand.CreateFromTask<double>(async bars =>
        {
            if (!IsLoaded || DisplayBpm <= 0) return;
            double barLength = 240.0 / DisplayBpm;
            double loopLength = bars * barLength;
            double inPt  = Deck.PositionSeconds;
            double outPt = inPt + loopLength;
            LoopInSeconds  = inPt;
            LoopOutSeconds = outPt;
            await CueEditor.SetLoopCommand.Execute((inPt, outPt)).FirstAsync();
        }, hasTrack);

        AddMemoryCueCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!IsLoaded) return;
            await CueEditor.AddCueAtPositionCommand.Execute(Deck.PositionSeconds).FirstAsync();
        }, hasTrack);

        DeleteHotCueAtSlotCommand = ReactiveCommand.CreateFromTask<int>(async slot =>
        {
            await CueEditor.DeleteHotCueAtSlotCommand.Execute(slot).FirstAsync();
            ApplySuggestedHotCues(CueEditor.Cues);
        }, hasTrack);

        ViewWaveformCommand = ReactiveCommand.Create(ToggleWaveformView);
        JumpToIntroCommand = ReactiveCommand.Create(() => JumpToPhrase(CueRole.Intro, 0.08d));
        JumpToBuildCommand = ReactiveCommand.Create(() => JumpToPhrase(CueRole.Build, 0.35d));
        JumpToDropCommand = ReactiveCommand.Create(() => JumpToPhrase(CueRole.Drop, 0.55d));
        JumpToOutroCommand = ReactiveCommand.Create(() => JumpToPhrase(CueRole.Outro, 0.82d));
        AddCueAtPlayheadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!IsLoaded)
                return;

            await CueEditor.AddCueAtPositionCommand.Execute(Deck.PositionSeconds).FirstAsync();
        });
        ResetCuePrepCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!IsLoaded)
                return;

            await CueEditor.ResetToAutoCommand.Execute().FirstAsync();
            ApplySuggestedHotCues(CueEditor.Cues);
        });
        DecreaseKeyShiftCommand = ReactiveCommand.Create(() => AdjustSemitoneShift(-1));
        ResetKeyShiftCommand = ReactiveCommand.Create(() => SetSemitoneShift(0));
        IncreaseKeyShiftCommand = ReactiveCommand.Create(() => AdjustSemitoneShift(1));

        // Show fader panel as soon as separation finishes
        this.WhenAnyValue(x => x.Stems.SeparationProgress)
            .Subscribe(p =>
            {
                if (p >= 1f && Stems.VocalsWavPath != null)
                    StemsVisible = true;

                this.RaisePropertyChanged(nameof(StemControlSummary));
            })
            .DisposeWith(_disposables);

        // Keep waveform progress/meter responsive during playback.
        Deck.WhenAnyValue(x => x.PositionSeconds, x => x.DurationSeconds, x => x.DeckState)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(PlaybackProgress));
                this.RaisePropertyChanged(nameof(VuLevel));
                this.RaisePropertyChanged(nameof(PerformanceStatusSummary));
                this.RaisePropertyChanged(nameof(PositionDisplayText));
            })
            .DisposeWith(_disposables);

        Deck.WhenAnyValue(x => x.IsLoopActive, x => x.KeyLock, x => x.TempoPercent)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(PerformanceStatusSummary));
                this.RaisePropertyChanged(nameof(IsLoopActive));
            })
            .DisposeWith(_disposables);

        Deck.WhenAnyValue(x => x.HotCues)
            .Subscribe(_ => RaiseHotCueDisplayPropertiesChanged())
            .DisposeWith(_disposables);

        CueEditor.Cues.CollectionChanged += (_, _) =>
        {
            ApplySuggestedHotCues(CueEditor.Cues);
            RaiseHotCueDisplayPropertiesChanged();
            this.RaisePropertyChanged(nameof(PhraseSegments));
            OnDeckStateChanged?.Invoke();
        };

        // Stem mute changes directly affect the rendered waveform color bands.
        Stems.Vocals.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Drums.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Bass.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Other.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
    }

    private async Task LoadTrackAsync(string filePath)
    {
        TrackLoadError = null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            TrackLoadError = "Track file not found. Re-download or rescan it before loading into the workstation.";
            return;
        }

        try
        {
            Deck.LoadTrack(filePath);
        }
        catch (Exception ex)
        {
            TrackLoadError = ex.InnerException?.Message ?? ex.Message;
            return;
        }

        IsLoaded     = true;
        TrackTitle   = Path.GetFileNameWithoutExtension(filePath);
        TrackArtist  = null;
        StemsVisible = false;
        WaveformData = new WaveformAnalysisData();
        TrackHash    = null;  // No hash available from raw path — use LoadPlaylistTrackCommand for hash
        ClearTrackIntelligence();
        CueEditor.ClearCues();
        LoopInSeconds  = null;
        LoopOutSeconds = null;
        ApplySuggestedHotCues(Array.Empty<OrbitCue>());
        UpdateHarmonicGuidance(BuildHarmonicSuggestionText(TrackKey, null, Deck.SemitoneShift));
        RaiseWaveformBandPropertiesChanged();
        OnDeckStateChanged?.Invoke();
        if (OnTrackLoaded != null) _ = OnTrackLoaded();
    }

    private async Task LoadPlaylistTrackAsync(PlaylistTrack track)
    {
        TrackLoadError = null;

        if (track != null && track.AvailabilityState == TrackAvailabilityState.Ghost)
        {
            if (App.Current is App app && app.Services != null)
            {
                var notificationService = app.Services.GetService(typeof(SLSKDONET.Views.INotificationService)) as SLSKDONET.Views.INotificationService;
                notificationService?.Show("Track Not Local", $"'{track.Artist} - {track.Title}' is a cloud/ghost track. Queuing for download...", SLSKDONET.Views.NotificationType.Information);

                track.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                track.Status = TrackStatus.Missing;
                track.SearchRetryCount = 0;
                track.NotFoundRestartCount = 0;

                try
                {
                    await using var dbContext = _dbFactory != null
                        ? _dbFactory.CreateDbContext()
                        : new AppDbContext();
                    var dbTrack = await dbContext.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.Id);
                    if (dbTrack != null)
                    {
                        dbTrack.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                        dbTrack.Status = TrackStatus.Missing;
                        dbTrack.SearchRetryCount = 0;
                        dbTrack.NotFoundRestartCount = 0;
                    }
                    var masterTrack = await dbContext.Tracks.FirstOrDefaultAsync(t => t.GlobalId == track.TrackUniqueHash);
                    if (masterTrack != null)
                    {
                        masterTrack.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                        masterTrack.SearchRetryCount = 0;
                        masterTrack.NotFoundRestartCount = 0;
                    }
                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to persist database state: {ex.Message}");
                }

                var downloadManager = app.Services.GetService(typeof(SLSKDONET.Services.DownloadManager)) as SLSKDONET.Services.DownloadManager;
                downloadManager?.QueueTracks(new List<PlaylistTrack> { track });
            }

            TrackLoadError = "Track not local. Queuing for download...";
            return;
        }

        var readinessMessage = GetTrackLoadReadinessMessage(track);
        if (!string.IsNullOrWhiteSpace(readinessMessage))
        {
            TrackLoadError = readinessMessage;
            return;
        }

        try
        {
            Deck.LoadTrack(track.ResolvedFilePath, track.BPM.HasValue ? (double)track.BPM.Value : 0);
        }
        catch (Exception ex)
        {
            TrackLoadError = ex.InnerException?.Message ?? ex.Message;
            return;
        }

        IsLoaded     = true;
        TrackTitle   = track.Title;
        TrackArtist  = track.Artist;
        TrackKey     = track.Key;
        DisplayBpm   = track.BPM ?? 0;
        StemsVisible = false;
        WaveformData = track.WaveformDataObj;
        TrackHash    = track.TrackUniqueHash;
        ApplyTrackIntelligence(track);

        if (!string.IsNullOrEmpty(TrackHash))
        {
            await CueEditor.LoadCuesAsync(TrackHash);
            ApplySuggestedHotCues(CueEditor.Cues);
            // Restore staged loop from saved loop cue (if any)
            var savedLoop = CueEditor.Cues.FirstOrDefault(c => c.IsLoop);
            LoopInSeconds  = savedLoop?.Timestamp;
            LoopOutSeconds = savedLoop?.LoopEndSeconds;
            var pref = await _stemPrefService.GetPreferenceAsync(TrackHash);
            ApplyStemPrefs(pref);
        }
        else
        {
            CueEditor.ClearCues();
            LoopInSeconds  = null;
            LoopOutSeconds = null;
            ApplySuggestedHotCues(Array.Empty<OrbitCue>());
        }

        if (OnTrackLoaded != null) await OnTrackLoaded();

        RaiseWaveformBandPropertiesChanged();
    }

    public static bool IsTrackReadyForWorkstation(PlaylistTrack? track) =>
        string.IsNullOrWhiteSpace(GetTrackLoadReadinessMessage(track));

    public static WorkstationTrackEligibilityIssue GetTrackEligibilityIssue(PlaylistTrack? track)
    {
        if (track == null)
        {
            return WorkstationTrackEligibilityIssue.NoTrack;
        }

        if (track.Status != TrackStatus.Downloaded)
        {
            return WorkstationTrackEligibilityIssue.NotDownloaded;
        }

        if (string.IsNullOrWhiteSpace(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
        {
            return WorkstationTrackEligibilityIssue.MissingFile;
        }

        if (string.IsNullOrWhiteSpace(track.TrackUniqueHash))
        {
            return WorkstationTrackEligibilityIssue.MissingHash;
        }

        if (!HasWaveformPrepData(track))
        {
            return WorkstationTrackEligibilityIssue.MissingWaveform;
        }

        if (!HasCuePrepData(track))
        {
            return WorkstationTrackEligibilityIssue.MissingCues;
        }

        if (!HasWorkstationPrepData(track))
        {
            return WorkstationTrackEligibilityIssue.MissingAnalysis;
        }

        return WorkstationTrackEligibilityIssue.None;
    }

    public static string GetTrackLoadReadinessMessage(PlaylistTrack? track)
    {
        switch (GetTrackEligibilityIssue(track))
        {
            case WorkstationTrackEligibilityIssue.NoTrack:
                return "No track was selected for workstation loading.";
            case WorkstationTrackEligibilityIssue.NotDownloaded:
                return "Track is not local yet. Download it first, then load it into the workstation.";
            case WorkstationTrackEligibilityIssue.MissingFile:
                return "Track file not found. Re-download or rescan it before loading into the workstation.";
            case WorkstationTrackEligibilityIssue.MissingHash:
                return "Track is missing a stable library hash. Rebuild library metadata before workstation loading.";
            case WorkstationTrackEligibilityIssue.MissingWaveform:
                return "Waveform analysis is missing. Analyze the track to generate waveform data before workstation loading.";
            case WorkstationTrackEligibilityIssue.MissingCues:
                return "Cue analysis is incomplete. Generate cue points before workstation loading.";
            case WorkstationTrackEligibilityIssue.MissingAnalysis:
                return "Track analysis is incomplete. Analyze the track before loading it into the workstation.";
            default:
                return string.Empty;
        }
    }

    private static bool HasWorkstationPrepData(PlaylistTrack track)
    {
        return !string.IsNullOrWhiteSpace(track.TrackUniqueHash)
            && HasWaveformPrepData(track)
            && HasCuePrepData(track);
    }

    private static bool HasWaveformPrepData(PlaylistTrack track)
    {
        return (track.WaveformData?.Length ?? 0) > 0
            || (track.LowData?.Length ?? 0) > 0
            || (track.MidData?.Length ?? 0) > 0
            || (track.HighData?.Length ?? 0) > 0;
    }

    private static bool HasCuePrepData(PlaylistTrack track)
    {
        return !string.IsNullOrWhiteSpace(track.CuePointsJson);
    }

    private void RaiseWaveformBandPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(LowBandForWaveform));
        this.RaisePropertyChanged(nameof(MidBandForWaveform));
        this.RaisePropertyChanged(nameof(HighBandForWaveform));
        this.RaisePropertyChanged(nameof(EnergyCurveForWaveform));
        this.RaisePropertyChanged(nameof(EnergyCurveBarPoints));
    }

    private void ToggleWaveformView()
    {
        if (!IsLoaded)
            return;

        if (IsSemanticDetailMode)
        {
            WaveformZoomLevel = 1.0;
            WaveformViewOffset = 0.0;
            return;
        }

        WaveformZoomLevel = 4.0;
        CenterViewportAround(Deck.PositionSeconds);
    }

    private HotCue? GetHotCue(int index) =>
        (uint)index < Deck.HotCues.Length ? Deck.HotCues[index] : null;

    /// <summary>
    /// CDJ-style hot cue pad: jump if slot is occupied; set + persist if empty.
    /// </summary>
    private async Task TriggerHotCuePadAsync(int slot)
    {
        if (!IsLoaded) return;
        var existing = GetHotCue(slot);
        if (existing != null)
        {
            Deck.Engine.JumpToHotCue(slot);
        }
        else
        {
            await CueEditor.AddHotCueAtSlotCommand.Execute((Deck.PositionSeconds, slot)).FirstAsync();
        }
    }

    private static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    private void RaiseHotCueDisplayPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(HotCueSummary));
        this.RaisePropertyChanged(nameof(MixReadinessText));
        this.RaisePropertyChanged(nameof(SectionJumpSummary));
        this.RaisePropertyChanged(nameof(StemControlSummary));
        this.RaisePropertyChanged(nameof(CurrentKeyShiftText));
        this.RaisePropertyChanged(nameof(HotCue1Text));
        this.RaisePropertyChanged(nameof(HotCue2Text));
        this.RaisePropertyChanged(nameof(HotCue3Text));
        this.RaisePropertyChanged(nameof(HotCue4Text));
        this.RaisePropertyChanged(nameof(HotCue5Text));
        this.RaisePropertyChanged(nameof(HotCue6Text));
        this.RaisePropertyChanged(nameof(HotCue7Text));
        this.RaisePropertyChanged(nameof(HotCue8Text));
        this.RaisePropertyChanged(nameof(HotCue1Tooltip));
        this.RaisePropertyChanged(nameof(HotCue2Tooltip));
        this.RaisePropertyChanged(nameof(HotCue3Tooltip));
        this.RaisePropertyChanged(nameof(HotCue4Tooltip));
        this.RaisePropertyChanged(nameof(HotCue5Tooltip));
        this.RaisePropertyChanged(nameof(HotCue6Tooltip));
        this.RaisePropertyChanged(nameof(HotCue7Tooltip));
        this.RaisePropertyChanged(nameof(HotCue8Tooltip));
    }

    public void UpdateTransitionStatus(string text)
    {
        TransitionStatusText = string.IsNullOrWhiteSpace(text)
            ? "Load another deck for live transition guidance"
            : text;
    }

    public void UpdateHarmonicGuidance(string text)
    {
        HarmonicSuggestionText = string.IsNullOrWhiteSpace(text)
            ? "Native key • load another deck for harmonic advice"
            : text;
        this.RaisePropertyChanged(nameof(CurrentKeyShiftText));
    }

    private void AdjustSemitoneShift(int delta)
    {
        SetSemitoneShift(Deck.SemitoneShift + delta);
    }

    private void SetSemitoneShift(int value)
    {
        Deck.SemitoneShift = Math.Clamp(value, -12, 12);
        this.RaisePropertyChanged(nameof(CurrentKeyShiftText));
        OnDeckStateChanged?.Invoke();
    }

    private void JumpToPhrase(CueRole role, double fallbackRatio)
    {
        if (!IsLoaded)
            return;

        var target = ResolvePhraseJumpTarget(CueEditor.Cues, role, Deck.DurationSeconds, fallbackRatio);
        Deck.Engine.Seek(target);
        CenterViewportAround(target);
    }

    private void ApplySuggestedHotCues(IEnumerable<OrbitCue> cues)
    {
        Deck.Engine.LoadHotCues(BuildSuggestedHotCues(cues));
        this.RaisePropertyChanged(nameof(HotCueSummary));
    }

    public static IReadOnlyList<HotCue> BuildSuggestedHotCues(IEnumerable<OrbitCue>? cues)
    {
        var ordered = cues?
            .Where(c => c != null)
            .OrderByDescending(c => c.Source == CueSource.User)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.Timestamp)
            .ToList() ?? new List<OrbitCue>();

        var selected = new List<OrbitCue>();
        var rolePriority = new[]
        {
            CueRole.Intro,
            CueRole.Build,
            CueRole.Drop,
            CueRole.Breakdown,
            CueRole.Breakdown2,
            CueRole.Bridge,
            CueRole.Climax,
            CueRole.Outro,
            CueRole.PhraseStart,
            CueRole.KickIn,
            CueRole.Vocals,
            CueRole.Custom
        };

        const double minimumSpacingSeconds = 3.0;

        foreach (var role in rolePriority)
        {
            var candidate = ordered
                .Where(c => c.Role == role)
                .OrderByDescending(c => c.Source == CueSource.User)
                .ThenByDescending(c => c.Confidence)
                .ThenBy(c => c.Timestamp)
                .FirstOrDefault(c => selected.All(existing => Math.Abs(existing.Timestamp - c.Timestamp) >= minimumSpacingSeconds));

            if (candidate != null)
                selected.Add(candidate);

            if (selected.Count >= 8)
                break;
        }

        foreach (var candidate in ordered)
        {
            if (selected.Count >= 8)
                break;

            if (selected.All(existing => Math.Abs(existing.Timestamp - candidate.Timestamp) >= minimumSpacingSeconds))
                selected.Add(candidate);
        }

        return selected
            .OrderBy(c => c.Timestamp)
            .Take(8)
            .Select((c, index) => new HotCue
            {
                Slot = index,
                PositionSeconds = Math.Max(0d, c.Timestamp),
                Label = string.IsNullOrWhiteSpace(c.Name) ? FriendlyRoleLabel(c.Role) : c.Name,
                Color = string.IsNullOrWhiteSpace(c.Color) ? "#FFFFFF" : c.Color
            })
            .ToList();
    }

    public static string BuildHotCuePadText(HotCue? cue, int slotIndex)
    {
        if (cue == null || string.IsNullOrWhiteSpace(cue.Label))
            return (slotIndex + 1).ToString();

        var label = cue.Label.Trim();
        if (label.Contains("intro", StringComparison.OrdinalIgnoreCase)) return "IN";
        if (label.Contains("build", StringComparison.OrdinalIgnoreCase) || label.Contains("riser", StringComparison.OrdinalIgnoreCase)) return "BLD";
        if (label.Contains("drop", StringComparison.OrdinalIgnoreCase) || label.Contains("climax", StringComparison.OrdinalIgnoreCase)) return "DRP";
        if (label.Contains("break", StringComparison.OrdinalIgnoreCase) || label.Contains("bridge", StringComparison.OrdinalIgnoreCase)) return "BRK";
        if (label.Contains("outro", StringComparison.OrdinalIgnoreCase) || label.Contains("end", StringComparison.OrdinalIgnoreCase)) return "OUT";

        return new string(label
            .Where(char.IsLetterOrDigit)
            .Take(3)
            .ToArray())
            .ToUpperInvariant()
            .PadRight(1, (slotIndex + 1).ToString()[0]);
    }

    public static string BuildCuePrepSummary(IEnumerable<HotCue?>? hotCues)
    {
        var labels = hotCues?
            .Where(h => h != null)
            .Select(h => string.IsNullOrWhiteSpace(h!.Label) ? $"Cue {h.Slot + 1}" : h.Label)
            .Take(4)
            .ToList() ?? new List<string>();

        return labels.Count == 0
            ? "Auto-prep cues will appear here"
            : $"{labels.Count} prep pads • {string.Join(" • ", labels)}";
    }

    public static string BuildMixReadinessText(string? trackKey, double bpm, IEnumerable<OrbitCue>? cues)
    {
        var cueList = cues?.ToList() ?? new List<OrbitCue>();
        var hasIntro = cueList.Any(c => c.Role == CueRole.Intro);
        var hasDrop = cueList.Any(c => c.Role == CueRole.Drop);
        var hasOutro = cueList.Any(c => c.Role == CueRole.Outro);
        var coreCount = (hasIntro ? 1 : 0) + (hasDrop ? 1 : 0) + (hasOutro ? 1 : 0);

        var keyText = string.IsNullOrWhiteSpace(trackKey) ? "— Key" : trackKey;
        var bpmText = bpm > 0 ? $"{bpm.ToString("F1", CultureInfo.InvariantCulture)} BPM" : "BPM pending";
        var status = coreCount >= 2 ? "Mix-ready" : "Prep cues needed";

        return $"{status} • {keyText} • {bpmText}";
    }

    public static string BuildPerformanceStatusSummary(string? deckLabel, bool isLoaded, bool isPlaying, bool loopActive, bool keyLock, double tempoPercent)
    {
        if (!isLoaded || string.IsNullOrWhiteSpace(deckLabel))
            return "Load a track to unlock live transport and loop tools";

        var state = isPlaying ? "Live" : "Cued";
        var loop = loopActive ? "loop on" : "loop open";
        var key = keyLock ? "key lock" : "free pitch";
        var tempo = $"{tempoPercent.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture)}%";
        return $"Deck {deckLabel} • {state} • {loop} • {key} • {tempo}";
    }

    public static string BuildSectionJumpSummary(IEnumerable<OrbitCue>? cues)
    {
        var cueList = cues?.ToList() ?? new List<OrbitCue>();
        if (cueList.Count == 0)
            return "Quick jumps: guided Intro / Build / Drop / Outro";

        var labels = new List<string>();
        if (cueList.Any(c => c.Role == CueRole.Intro || c.Role == CueRole.PhraseStart)) labels.Add("Intro");
        if (cueList.Any(c => c.Role == CueRole.Build || c.Role == CueRole.Bridge)) labels.Add("Build");
        if (cueList.Any(c => c.Role == CueRole.Drop || c.Role == CueRole.Climax || c.Role == CueRole.KickIn)) labels.Add("Drop");
        if (cueList.Any(c => c.Role == CueRole.Outro || c.Role == CueRole.Breakdown || c.Role == CueRole.Breakdown2)) labels.Add("Outro");

        return labels.Count == 0
            ? "Quick jumps: guided Intro / Build / Drop / Outro"
            : $"Quick jumps: {string.Join(" • ", labels)}";
    }

    public static string BuildWaveformDetailSummary(bool isSemanticDetailMode, double waveformZoomLevel, double waveformViewOffset)
    {
        var zoom = waveformZoomLevel.ToString("0.0", CultureInfo.InvariantCulture);
        var offset = Math.Round(Math.Clamp(waveformViewOffset, 0.0, 1.0) * 100.0);

        return isSemanticDetailMode
            ? $"Semantic detail • {zoom}× zoom • window {offset:0}%"
            : $"Full waveform • {zoom}× zoom • widen for phrase detail";
    }

    public static string BuildStemControlSummary(bool stemsVisible, bool isSeparating, float separationProgress, bool vocalsMuted, bool drumsSoloed)
    {
        if (isSeparating)
            return $"Separating stems • {(int)Math.Round(Math.Clamp(separationProgress, 0f, 1f) * 100f)}%";

        if (!stemsVisible)
            return "Separate stems to unlock vocal-off, drums-only, and instrumental presets";

        var states = new List<string>();
        if (vocalsMuted) states.Add("Vocal off");
        if (drumsSoloed) states.Add("Drums solo");

        return states.Count == 0
            ? "Stem mixer ready for live isolation"
            : $"Stem mixer active • {string.Join(" • ", states)}";
    }

    public static string BuildHarmonicSuggestionText(string? currentKey, string? referenceKey, int semitoneShift)
    {
        var shiftText = semitoneShift == 0 ? "0 st" : $"{semitoneShift:+#;-#;0} st";

        if (string.IsNullOrWhiteSpace(currentKey))
            return $"Key pending • shift {shiftText}";

        if (string.IsNullOrWhiteSpace(referenceKey))
        {
            return semitoneShift == 0
                ? "Native key • load another deck for harmonic advice"
                : $"Native key shifted {shiftText} • load another deck for harmonic advice";
        }

        var harmonicDistance = CamelotDistance(currentKey, referenceKey);
        var status = harmonicDistance switch
        {
            <= 1d => "Harmonic blend ready",
            <= 2d => "Energy lift is safe",
            <= 4d => "Tension mix • use the breakdown",
            _ => "Key clash risk • blend drums first"
        };

        return $"{status} • shift {shiftText}";
    }

    public static string BuildTransitionStatus(
        string? currentDeckLabel,
        string? currentKey,
        double currentBpm,
        IEnumerable<OrbitCue>? currentCues,
        string? referenceDeckLabel,
        string? referenceKey,
        double referenceBpm,
        IEnumerable<OrbitCue>? referenceCues)
    {
        if (string.IsNullOrWhiteSpace(referenceDeckLabel))
            return "Load another deck for live transition guidance";

        var bpmDiff = referenceBpm > 0 && currentBpm > 0 ? referenceBpm - currentBpm : 0d;
        var bpmDelta = bpmDiff == 0d
            ? "±0.0 BPM"
            : bpmDiff > 0
                ? $"+{bpmDiff.ToString("F1", CultureInfo.InvariantCulture)} BPM"
                : $"{bpmDiff.ToString("F1", CultureInfo.InvariantCulture)} BPM";

        var harmonicDistance = CamelotDistance(currentKey, referenceKey);
        var harmonicLabel = harmonicDistance switch
        {
            <= 1d => "Smooth blend",
            <= 2d => "Safe energy lift",
            <= 4d => "Stretch mix",
            _ => "Risky clash"
        };

        var outCue = FindLatestCueByRoles(currentCues, CueRole.Outro, CueRole.Breakdown, CueRole.PhraseStart);
        var inCue = FindFirstCueByRoles(referenceCues, CueRole.Intro, CueRole.PhraseStart, CueRole.Build);
        var anchorText = outCue != null && inCue != null
            ? $" • {outCue.Role} → {inCue.Role}"
            : string.Empty;

        return $"Deck {referenceDeckLabel} • {harmonicLabel} • {bpmDelta}{anchorText}";
    }

    private static double CamelotDistance(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a == "—" || b == "—")
            return 3.0;

        if (!TryParseCamelot(a, out int na, out bool minA) ||
            !TryParseCamelot(b, out int nb, out bool minB))
            return 3.0;

        int raw = Math.Abs(na - nb);
        int dist = Math.Min(raw, 12 - raw);
        return dist + (minA == minB ? 0 : 1);
    }

    private static bool TryParseCamelot(string key, out int number, out bool isMinor)
    {
        number = 0;
        isMinor = false;
        if (key.Length < 2) return false;

        char suffix = char.ToUpperInvariant(key[^1]);
        if (suffix != 'A' && suffix != 'B') return false;

        isMinor = suffix == 'A';
        return int.TryParse(key[..^1], out number) && number >= 1 && number <= 12;
    }

    private static OrbitCue? FindFirstCueByRoles(IEnumerable<OrbitCue>? cues, params CueRole[] roles)
    {
        var cueList = cues?.ToList() ?? new List<OrbitCue>();
        foreach (var role in roles)
        {
            var match = cueList.Where(c => c.Role == role).OrderBy(c => c.Timestamp).FirstOrDefault();
            if (match != null)
                return match;
        }

        return cueList.OrderBy(c => c.Timestamp).FirstOrDefault();
    }

    private static OrbitCue? FindLatestCueByRoles(IEnumerable<OrbitCue>? cues, params CueRole[] roles)
    {
        var cueList = cues?.ToList() ?? new List<OrbitCue>();
        foreach (var role in roles)
        {
            var match = cueList.Where(c => c.Role == role).OrderByDescending(c => c.Timestamp).FirstOrDefault();
            if (match != null)
                return match;
        }

        return cueList.OrderByDescending(c => c.Timestamp).FirstOrDefault();
    }

    private static string BuildHotCueTooltip(HotCue? cue, int slotIndex)
    {
        if (cue == null)
            return $"Hot Cue {slotIndex + 1} — click to store the current playhead";

        var time = TimeSpan.FromSeconds(Math.Max(0d, cue.PositionSeconds));
        var label = string.IsNullOrWhiteSpace(cue.Label) ? $"Cue {slotIndex + 1}" : cue.Label;
        return $"Hot Cue {slotIndex + 1} — {label} at {time:mm\\:ss}";
    }

    private static string FriendlyRoleLabel(CueRole role) => role switch
    {
        CueRole.PhraseStart => "Phrase",
        CueRole.KickIn => "Kick In",
        CueRole.Breakdown2 => "Breakdown 2",
        _ => role.ToString()
    };

    public static double ResolvePhraseJumpTarget(
        IEnumerable<OrbitCue>? cues,
        CueRole preferredRole,
        double trackDurationSeconds,
        double fallbackRatio)
    {
        var ordered = cues?
            .Where(c => c != null)
            .OrderBy(c => c.Timestamp)
            .ToList() ?? new List<OrbitCue>();

        var exact = ordered.FirstOrDefault(c => c.Role == preferredRole);
        if (exact != null)
            return Math.Max(0d, exact.Timestamp);

        string[] hints = preferredRole switch
        {
            CueRole.Intro => ["intro", "start"],
            CueRole.Build => ["build", "riser"],
            CueRole.Drop => ["drop", "climax"],
            CueRole.Outro => ["outro", "end"],
            _ => [preferredRole.ToString().ToLowerInvariant()]
        };

        var namedMatch = ordered.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.Name) &&
            hints.Any(h => c.Name.Contains(h, StringComparison.OrdinalIgnoreCase)));

        if (namedMatch != null)
            return Math.Max(0d, namedMatch.Timestamp);

        if (trackDurationSeconds <= 0)
            return 0d;

        return Math.Clamp(trackDurationSeconds * fallbackRatio, 0d, Math.Max(0d, trackDurationSeconds - 0.1d));
    }

    private void CenterViewportAround(double seconds)
    {
        if (Deck.DurationSeconds <= 0 || WaveformZoomLevel <= 1.0)
        {
            WaveformViewOffset = 0.0;
            return;
        }

        var visibleFraction = Math.Min(1.0, 1.0 / WaveformZoomLevel);
        var rawOffset = (seconds / Deck.DurationSeconds) - (visibleFraction / 2.0);
        WaveformViewOffset = Math.Clamp(rawOffset, 0.0, Math.Max(0.0, 1.0 - visibleFraction));
    }

    private void OnStemMuteStateChanged()
    {
        RaiseWaveformBandPropertiesChanged();
        _ = SaveStemPrefsAsync();
    }

    public void UpdateWaveformViewport(double windowSeconds, double timelineOffsetSeconds)
    {
        windowSeconds = Math.Max(1.0, windowSeconds);

        // 240 seconds on screen = zoom 1.0 baseline. Smaller windows zoom in.
        WaveformZoomLevel = Math.Clamp(240.0 / windowSeconds, 1.0, 16.0);

        if (Deck.DurationSeconds <= 0)
        {
            WaveformViewOffset = 0;
            return;
        }

        var visibleFraction = Math.Min(1.0, windowSeconds / Deck.DurationSeconds);
        var maxOffset = Math.Max(0.0, 1.0 - visibleFraction);
        var raw = timelineOffsetSeconds / Deck.DurationSeconds;
        WaveformViewOffset = Math.Clamp(raw, 0.0, maxOffset);
    }

    private void ApplyStemPrefs(StemPreference pref)
    {
        Stems.Vocals.IsMuted  = pref.AlwaysMuted.Contains(StemType.Vocals);
        Stems.Drums.IsMuted   = pref.AlwaysMuted.Contains(StemType.Drums);
        Stems.Bass.IsMuted    = pref.AlwaysMuted.Contains(StemType.Bass);
        Stems.Other.IsMuted   = pref.AlwaysMuted.Contains(StemType.Other);
        Stems.Vocals.IsSoloed = pref.AlwaysSolo.Contains(StemType.Vocals);
        Stems.Drums.IsSoloed  = pref.AlwaysSolo.Contains(StemType.Drums);
        Stems.Bass.IsSoloed   = pref.AlwaysSolo.Contains(StemType.Bass);
        Stems.Other.IsSoloed  = pref.AlwaysSolo.Contains(StemType.Other);
    }

    private async Task SaveStemPrefsAsync()
    {
        if (string.IsNullOrEmpty(TrackHash)) return;
        var muted  = new List<StemType>();
        var soloed = new List<StemType>();
        if (Stems.Vocals.IsMuted)  muted.Add(StemType.Vocals);
        if (Stems.Drums.IsMuted)   muted.Add(StemType.Drums);
        if (Stems.Bass.IsMuted)    muted.Add(StemType.Bass);
        if (Stems.Other.IsMuted)   muted.Add(StemType.Other);
        if (Stems.Vocals.IsSoloed) soloed.Add(StemType.Vocals);
        if (Stems.Drums.IsSoloed)  soloed.Add(StemType.Drums);
        if (Stems.Bass.IsSoloed)   soloed.Add(StemType.Bass);
        if (Stems.Other.IsSoloed)  soloed.Add(StemType.Other);
        await _stemPrefService.SavePreferenceAsync(TrackHash, new StemPreference
        {
            AlwaysMuted = muted,
            AlwaysSolo  = soloed
        });
    }

    private async Task SeparateStemsAsync()
    {
        if (Deck.LoadedFilePath is not { } path) return;

        await Stems.LoadAndSeparateCommand.Execute(path).FirstAsync();

        // After separation, also extract per-stem waveforms for the visual strip
        if (Stems.VocalsWavPath != null)
        {
            await StemWaveforms.LoadStemWaveformsAsync(
                Stems.VocalsWavPath,
                Stems.DrumsWavPath,
                Stems.BassWavPath,
                Stems.OtherWavPath);
        }
    }

    private IEnumerable<PhraseSegment> BuildPhraseSegments()
    {
        var duration = WaveformData?.DurationSeconds ?? 0.0;
        var orderedCues = CueEditor.Cues.OrderBy(c => c.Timestamp).ToList();

        if (orderedCues.Count == 0)
        {
            if (duration <= 0) return Array.Empty<PhraseSegment>();
            var introEnd = Math.Max(8.0, duration * 0.15);
            var outroStart = Math.Max(introEnd, duration * 0.82);
            return new[]
            {
                new PhraseSegment { Label = "Intro", Start = 0f, Duration = (float)Math.Max(4.0, introEnd), Color = "#1E3A5F" },
                new PhraseSegment { Label = "Main",  Start = (float)introEnd, Duration = (float)Math.Max(4.0, outroStart - introEnd), Color = "#6A0DAD" },
                new PhraseSegment { Label = "Outro", Start = (float)outroStart, Duration = (float)Math.Max(4.0, duration - outroStart), Color = "#708090" }
            };
        }

        var segments = new List<PhraseSegment>(orderedCues.Count);
        for (var i = 0; i < orderedCues.Count; i++)
        {
            var current = orderedCues[i];
            var nextTime = i < orderedCues.Count - 1
                ? orderedCues[i + 1].Timestamp
                : Math.Max(duration, current.Timestamp + 8.0);
            segments.Add(new PhraseSegment
            {
                Label      = string.IsNullOrWhiteSpace(current.Name) ? $"Cue {i + 1}" : current.Name,
                Start      = (float)Math.Max(0.0, current.Timestamp),
                Duration   = (float)Math.Max(2.0, nextTime - current.Timestamp),
                Confidence = (float)Math.Clamp(current.Confidence, 0.0, 1.0),
                Color      = string.IsNullOrWhiteSpace(current.Color) ? "#708090" : current.Color
            });
        }
        return segments;
    }

    private IEnumerable<float>? BuildEnergyCurve()
    {
        var data = WaveformData;
        if (data == null) return null;
        var low  = data.LowData;
        var mid  = data.MidData;
        var high = data.HighData;
        var len  = Math.Max(low.Length, Math.Max(mid.Length, high.Length));
        if (len == 0) return null;

        var points = new float[len];
        for (var i = 0; i < len; i++)
        {
            double sum = 0; int count = 0;
            if (i < low.Length)  { sum += low[i]  / 255.0; count++; }
            if (i < mid.Length)  { sum += mid[i]  / 255.0; count++; }
            if (i < high.Length) { sum += high[i] / 255.0; count++; }
            points[i] = count > 0 ? (float)(sum / count) : 0f;
        }
        return points;
    }

    private IReadOnlyList<double>? BuildEnergyCurveDouble()
    {
        var data = WaveformData;
        if (data == null) return null;
        var low  = data.LowData;
        var mid  = data.MidData;
        var high = data.HighData;
        var len  = Math.Max(low.Length, Math.Max(mid.Length, high.Length));
        if (len == 0) return null;

        var points = new double[len];
        for (var i = 0; i < len; i++)
        {
            double sum = 0; int count = 0;
            if (i < low.Length)  { sum += low[i]  / 255.0; count++; }
            if (i < mid.Length)  { sum += mid[i]  / 255.0; count++; }
            if (i < high.Length) { sum += high[i] / 255.0; count++; }
            points[i] = count > 0 ? sum / count : 0.0;
        }
        return points;
    }

    public void Dispose()
    {
        _disposables.Dispose();
        CueEditor.Dispose();
        Stems.Dispose();
        Deck.Dispose();
    }
}
