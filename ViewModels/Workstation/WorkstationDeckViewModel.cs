using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.Models.Stem;
using SLSKDONET.ViewModels;
using System.Collections.Generic;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// Thin wrap of <see cref="DeckSlotViewModel"/> that adds workstation-specific
/// state: deck label, loaded track metadata, stems, and stem preset shortcuts.
/// </summary>
public sealed class WorkstationDeckViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly StemPreferenceService _stemPrefService;

    /// <summary>
    /// Callback invoked whenever a track finishes loading into this deck.
    /// Set by <see cref="WorkstationViewModel"/> to trigger a session autosave.
    /// </summary>
    public Func<Task>? OnTrackLoaded { get; set; }

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

    private double _waveformZoomLevel = 1.0;
    public double WaveformZoomLevel
    {
        get => _waveformZoomLevel;
        set
        {
            var clamped = Math.Clamp(value, 1.0, 16.0);
            this.RaiseAndSetIfChanged(ref _waveformZoomLevel, clamped);
            this.RaisePropertyChanged(nameof(IsSemanticDetailMode));
        }
    }

    private double _waveformViewOffset;
    public double WaveformViewOffset
    {
        get => _waveformViewOffset;
        set => this.RaiseAndSetIfChanged(ref _waveformViewOffset, Math.Clamp(value, 0.0, 1.0));
    }

    public bool IsSemanticDetailMode => WaveformZoomLevel >= 3.0;

    // ── Cue editor ────────────────────────────────────────────────────────────
    public CueEditorViewModel CueEditor { get; }

    /// <summary>The hash of the currently loaded track (used for cue DB lookups).</summary>
    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set => this.RaiseAndSetIfChanged(ref _trackHash, value);
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
    // Set cue at current position if empty; jump if already set (matches CDJ behaviour).
    public ReactiveCommand<Unit, Unit> HotCue1Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue2Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue3Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue4Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue5Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue6Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue7Command { get; }
    public ReactiveCommand<Unit, Unit> HotCue8Command { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public WorkstationDeckViewModel(string deckLabel, DeckSlotViewModel deck,
        CachedStemSeparator stemSeparator, ICuePointService cueService,
        StemPreferenceService stemPrefService)
    {
        DeckLabel  = deckLabel;
        Deck          = deck;
        Stems         = new StemMixerViewModel(stemSeparator);
        StemWaveforms = new StemWaveformViewModel();
        CueEditor     = new CueEditorViewModel(cueService);
        _stemPrefService = stemPrefService;

        LoadTrackCommand = ReactiveCommand.CreateFromTask<string>(LoadTrackAsync);

        LoadPlaylistTrackCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(LoadPlaylistTrackAsync);

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

        HotCue1Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(0));
        HotCue2Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(1));
        HotCue3Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(2));
        HotCue4Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(3));
        HotCue5Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(4));
        HotCue6Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(5));
        HotCue7Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(6));
        HotCue8Command = ReactiveCommand.Create(() => Deck.Engine.JumpToHotCue(7));

        // Show fader panel as soon as separation finishes
        this.WhenAnyValue(x => x.Stems.SeparationProgress)
            .Subscribe(p =>
            {
                if (p >= 1f && Stems.VocalsWavPath != null)
                    StemsVisible = true;
            })
            .DisposeWith(_disposables);

        // Keep waveform progress/meter responsive during playback.
        Deck.WhenAnyValue(x => x.PositionSeconds, x => x.DurationSeconds, x => x.DeckState)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(PlaybackProgress));
                this.RaisePropertyChanged(nameof(VuLevel));
            })
            .DisposeWith(_disposables);

        // Stem mute changes directly affect the rendered waveform color bands.
        Stems.Vocals.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Drums.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Bass.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
        Stems.Other.WhenAnyValue(x => x.IsMuted).Subscribe(_ => OnStemMuteStateChanged()).DisposeWith(_disposables);
    }

    private Task LoadTrackAsync(string filePath)
    {
        Deck.LoadTrack(filePath);
        IsLoaded     = true;
        TrackTitle   = System.IO.Path.GetFileNameWithoutExtension(filePath);
        TrackArtist  = null;
        StemsVisible = false;
        WaveformData = new WaveformAnalysisData();
        TrackHash    = null;  // No hash available from raw path — use LoadPlaylistTrackCommand for hash
        CueEditor.ClearCues();
        RaiseWaveformBandPropertiesChanged();
        if (OnTrackLoaded != null) _ = OnTrackLoaded();
        return Task.CompletedTask;
    }

    private async Task LoadPlaylistTrackAsync(PlaylistTrack track)
    {
        Deck.LoadTrack(track.ResolvedFilePath, track.BPM.HasValue ? (double)track.BPM.Value : 0);
        IsLoaded     = true;
        TrackTitle   = track.Title;
        TrackArtist  = track.Artist;
        TrackKey     = track.Key;
        DisplayBpm   = track.BPM ?? 0;
        StemsVisible = false;
        WaveformData = track.WaveformDataObj;
        TrackHash    = track.TrackUniqueHash;

        if (!string.IsNullOrEmpty(TrackHash))
        {
            await CueEditor.LoadCuesAsync(TrackHash);
            var pref = await _stemPrefService.GetPreferenceAsync(TrackHash);
            ApplyStemPrefs(pref);
        }
        else
        {
            CueEditor.ClearCues();
        }

        if (OnTrackLoaded != null) await OnTrackLoaded();

        RaiseWaveformBandPropertiesChanged();
    }

    private void RaiseWaveformBandPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(LowBandForWaveform));
        this.RaisePropertyChanged(nameof(MidBandForWaveform));
        this.RaisePropertyChanged(nameof(HighBandForWaveform));
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

    public void Dispose()
    {
        _disposables.Dispose();
        CueEditor.Dispose();
        Stems.Dispose();
        Deck.Dispose();
    }
}
