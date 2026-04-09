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

    /// <summary>Solo Drums stem, mute everything else (toggle).</summary>
    public ReactiveCommand<Unit, Unit> ToggleDrumsOnlyCommand { get; }

    /// <summary>Mute Vocals so only the instrumental plays (toggle).</summary>
    public ReactiveCommand<Unit, Unit> ToggleInstrumentalCommand { get; }

    /// <summary>Trigger ONNX stem separation for the loaded file.</summary>
    public ReactiveCommand<Unit, Unit> SeparateStemsCommand { get; }

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

        // Show fader panel as soon as separation finishes
        this.WhenAnyValue(x => x.Stems.SeparationProgress)
            .Subscribe(p =>
            {
                if (p >= 1f && Stems.VocalsWavPath != null)
                    StemsVisible = true;
            })
            .DisposeWith(_disposables);
    }

    private Task LoadTrackAsync(string filePath)
    {
        Deck.LoadTrack(filePath);
        IsLoaded     = true;
        TrackTitle   = System.IO.Path.GetFileNameWithoutExtension(filePath);
        TrackArtist  = null;
        StemsVisible = false;
        TrackHash    = null;  // No hash available from raw path — use LoadPlaylistTrackCommand for hash
        CueEditor.ClearCues();
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
