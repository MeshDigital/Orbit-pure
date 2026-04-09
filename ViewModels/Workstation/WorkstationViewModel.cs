using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>The creative canvas inside the Workstation page.</summary>
public enum WorkstationMode
{
    /// <summary>Multi-deck waveform view: zoom, cue editing, beat-matching.</summary>
    Waveform,
    /// <summary>Flow Builder: playlist sequence, transition scoring, energy arc.</summary>
    Flow,
    /// <summary>Stem separation: per-instrument faders, mute/solo.</summary>
    Stems,
    /// <summary>Mixdown + export: normalisation, dither, export profiles.</summary>
    Export,
}

/// <summary>
/// Root ViewModel for the Workstation page — modelled after the DJ.Studio layout.
/// Manages decks, timeline position, global BPM, and the active playlist.
/// </summary>
public sealed class WorkstationViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ILibraryService          _library;
    private readonly DeckViewModel            _deckPair;
    private readonly CachedStemSeparator      _stemSeparator;
    private readonly ICuePointService         _cueService;
    private readonly StemPreferenceService    _stemPrefService;
    private readonly MixdownService           _mixdown;
    private readonly WorkstationSessionService _sessionService;
    private readonly BpmSyncService           _bpmSync = new();

    // ── Decks ─────────────────────────────────────────────────────────────────

    public ObservableCollection<WorkstationDeckViewModel> Decks { get; } = new();

    private WorkstationDeckViewModel? _focusedDeck;
    public WorkstationDeckViewModel? FocusedDeck
    {
        get => _focusedDeck;
        set => this.RaiseAndSetIfChanged(ref _focusedDeck, value);
    }

    // ── Playlist selector ─────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    private PlaylistJob? _activePlaylist;
    public PlaylistJob? ActivePlaylist
    {
        get => _activePlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _activePlaylist, value);
            if (value != null)
                _ = LoadPlaylistTracksAsync(value);
        }
    }

    // Track rows shown in the bottom track list
    public ObservableCollection<PlaylistTrack> PlaylistTracks { get; } = new();

    // ── Timeline / scroll ─────────────────────────────────────────────────────

    /// <summary>Visible timeline window start in seconds.</summary>
    private double _timelineOffsetSeconds;
    public double TimelineOffsetSeconds
    {
        get => _timelineOffsetSeconds;
        set => this.RaiseAndSetIfChanged(ref _timelineOffsetSeconds, value);
    }

    /// <summary>Seconds visible in the timeline viewport (zoom level).</summary>
    private double _timelineWindowSeconds = 60.0;
    public double TimelineWindowSeconds
    {
        get => _timelineWindowSeconds;
        set => this.RaiseAndSetIfChanged(ref _timelineWindowSeconds,
                   Math.Clamp(value, 10.0, 3600.0));
    }

    // ── Global BPM (master) ───────────────────────────────────────────────────

    private double _masterBpm;
    public double MasterBpm
    {
        get => _masterBpm;
        set => this.RaiseAndSetIfChanged(ref _masterBpm, value);
    }

    public string MasterBpmDisplay => MasterBpm > 0 ? $"{MasterBpm:F1}" : "—";

    // ── Active mode ───────────────────────────────────────────────────────────

    private WorkstationMode _activeMode = WorkstationMode.Waveform;
    public WorkstationMode ActiveMode
    {
        get => _activeMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeMode, value);
            this.RaisePropertyChanged(nameof(IsWaveformMode));
            this.RaisePropertyChanged(nameof(IsFlowMode));
            this.RaisePropertyChanged(nameof(IsStemsMode));
            this.RaisePropertyChanged(nameof(IsExportMode));
        }
    }

    public bool IsWaveformMode => ActiveMode == WorkstationMode.Waveform;
    public bool IsFlowMode     => ActiveMode == WorkstationMode.Flow;
    public bool IsStemsMode    => ActiveMode == WorkstationMode.Stems;
    public bool IsExportMode   => ActiveMode == WorkstationMode.Export;

    // ── Inline Export panel (shown in Export mode) ────────────────────────────

    /// <summary>
    /// Shared Export configuration shown in the inline Export mode panel.
    /// The same instance also populates the popup Export dialog when
    /// <see cref="ExportMixCommand"/> is invoked from the toolbar.
    /// </summary>
    public ExportDialogViewModel ExportPanel { get; }

    // ── Master play transport ─────────────────────────────────────────────────

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>          PlayPauseAllCommand   { get; }
    public ReactiveCommand<Unit, Unit>          StopAllCommand        { get; }
    public ReactiveCommand<Unit, Unit>          AddDeckCommand        { get; }
    public ReactiveCommand<WorkstationDeckViewModel, Unit> RemoveDeckCommand { get; }
    public ReactiveCommand<PlaylistJob, Unit>   LoadPlaylistCommand   { get; }
    public ReactiveCommand<Unit, Unit>          ZoomInCommand         { get; }
    public ReactiveCommand<Unit, Unit>          ZoomOutCommand        { get; }
    /// <summary>Opens the Export Mix dialog for the active decks.</summary>
    public ReactiveCommand<Unit, Unit>          ExportMixCommand      { get; }
    /// <summary>Load a playlist track into the focused deck (or Deck A if none focused).</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToFocusedDeckCommand { get; }
    /// <summary>Load a playlist track into Deck A.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToDeckACommand    { get; }
    /// <summary>Load a playlist track into Deck B.</summary>
    public ReactiveCommand<PlaylistTrack, Unit> LoadToDeckBCommand    { get; }
    /// <summary>Beat-match + phase-align Deck B to Deck A.</summary>
    public ReactiveCommand<Unit, Unit>          SyncBpmCommand        { get; }
    /// <summary>Toggle loop on the focused deck.</summary>
    public ReactiveCommand<Unit, Unit>          ToggleLoopCommand     { get; }
    /// <summary>Switch to a Workstation mode (Waveform / Flow / Stems / Export).</summary>
    public ReactiveCommand<WorkstationMode, Unit> SetModeCommand      { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public WorkstationViewModel(ILibraryService library, DeckViewModel deckPair,
        CachedStemSeparator stemSeparator, ICuePointService cueService,
        StemPreferenceService stemPrefService, MixdownService mixdown,
        WorkstationSessionService sessionService)
    {
        _library         = library;
        _deckPair        = deckPair;
        _stemSeparator   = stemSeparator;
        _cueService      = cueService;
        _stemPrefService = stemPrefService;
        _mixdown         = mixdown;
        _sessionService  = sessionService;

        ExportPanel = new ExportDialogViewModel(mixdown);

        // Wrap existing DeckA / DeckB
        var deckA = new WorkstationDeckViewModel("A", deckPair.DeckA, stemSeparator, cueService, stemPrefService);
        var deckB = new WorkstationDeckViewModel("B", deckPair.DeckB, stemSeparator, cueService, stemPrefService);
        deckA.OnTrackLoaded = SaveSessionAsync;
        deckB.OnTrackLoaded = SaveSessionAsync;
        Decks.Add(deckA);
        Decks.Add(deckB);

        FocusedDeck = Decks.FirstOrDefault();

        PlayPauseAllCommand = ReactiveCommand.Create(() =>
        {
            IsPlaying = !IsPlaying;
            foreach (var d in Decks)
            {
                if (IsPlaying) d.Deck.Engine.Play();
                else           d.Deck.Engine.Pause();
            }
        });

        StopAllCommand = ReactiveCommand.Create(() =>
        {
            IsPlaying = false;
            foreach (var d in Decks)
            {
                d.Deck.Engine.Pause();
                d.Deck.Engine.Cue();
            }
        });

        AddDeckCommand = ReactiveCommand.Create(() =>
        {
            // Max 4 decks
            if (Decks.Count >= 4) return;
            string label = Decks.Count switch { 2 => "C", 3 => "D", _ => "?" };
            var engine = new DeckEngine();
            var slot   = new DeckSlotViewModel(label, engine);
            var newDeck = new WorkstationDeckViewModel(label, slot, _stemSeparator, _cueService, _stemPrefService);
            newDeck.OnTrackLoaded = SaveSessionAsync;
            Decks.Add(newDeck);
        });

        RemoveDeckCommand = ReactiveCommand.Create<WorkstationDeckViewModel>(deck =>
        {
            if (Decks.Count <= 1) return;
            Decks.Remove(deck);
            deck.Dispose();
            FocusedDeck = Decks.FirstOrDefault();
        });

        LoadPlaylistCommand = ReactiveCommand.CreateFromTask<PlaylistJob>(async job =>
        {
            ActivePlaylist = job;
        });

        ZoomInCommand  = ReactiveCommand.Create(() => { TimelineWindowSeconds /= 1.5; });
        ZoomOutCommand = ReactiveCommand.Create(() => { TimelineWindowSeconds *= 1.5; });

        ExportMixCommand = ReactiveCommand.CreateFromTask(OpenExportDialogAsync);

        LoadToFocusedDeckCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var target = FocusedDeck ?? Decks.FirstOrDefault();
            if (target != null) await target.LoadPlaylistTrackCommand.Execute(t).FirstAsync();
        });

        LoadToDeckACommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var deck = Decks.FirstOrDefault(d => d.DeckLabel == "A");
            if (deck != null) await deck.LoadPlaylistTrackCommand.Execute(t).FirstAsync();
        });

        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<PlaylistTrack>(async t =>
        {
            var deck = Decks.FirstOrDefault(d => d.DeckLabel == "B");
            if (deck != null) await deck.LoadPlaylistTrackCommand.Execute(t).FirstAsync();
        });

        SyncBpmCommand = ReactiveCommand.Create(() =>
        {
            var deckA = Decks.FirstOrDefault(d => d.DeckLabel == "A");
            var deckB = Decks.FirstOrDefault(d => d.DeckLabel == "B");
            if (deckA == null || deckB == null) return;
            if (deckA.DisplayBpm <= 0 || deckB.DisplayBpm <= 0) return;
            _bpmSync.BeatMatch(deckA.Deck.Engine, deckA.DisplayBpm, deckB.Deck.Engine, deckB.DisplayBpm);
            _bpmSync.PhaseAlign(deckA.Deck.Engine, deckA.DisplayBpm, deckB.Deck.Engine, deckB.DisplayBpm);
        });

        ToggleLoopCommand = ReactiveCommand.Create(() =>
        {
            var deck = FocusedDeck?.Deck;
            if (deck == null) return;
            if (deck.IsLoopActive) deck.ExitLoopCommand.Execute().Subscribe();
            else                   deck.SetLoopCommand.Execute().Subscribe();
        });

        SetModeCommand = ReactiveCommand.Create<WorkstationMode>(mode =>
        {
            ActiveMode = mode;
            _ = SaveSessionAsync();
        });

        // Update MasterBpm from focused deck
        this.WhenAnyValue(x => x.FocusedDeck)
            .Subscribe(d =>
            {
                if (d != null)
                    MasterBpm = d.Deck.TrackBpm;
            })
            .DisposeWith(_disposables);

        _ = LoadPlaylistsAsync();
        _ = RestoreSessionAsync();
    }

    // ── Session persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Captures current deck state and writes it atomically to disk.
    /// Safe to fire-and-forget — errors are silently swallowed.
    /// </summary>
    public async Task SaveSessionAsync()
    {
        try
        {
            var session = new WorkstationSession
            {
                ActiveModeIndex     = (int)ActiveMode,
                TimelineOffsetSeconds = TimelineOffsetSeconds,
                TimelineWindowSeconds = TimelineWindowSeconds,
            };

            foreach (var deck in Decks)
            {
                session.Decks.Add(new WorkstationDeckState
                {
                    DeckLabel       = deck.DeckLabel,
                    FilePath        = deck.Deck.LoadedFilePath,
                    TrackUniqueHash = deck.TrackHash,
                    TrackTitle      = deck.TrackTitle,
                    TrackArtist     = deck.TrackArtist,
                    Bpm             = deck.DisplayBpm,
                    Key             = deck.TrackKey,
                    PositionSeconds = deck.Deck.PositionSeconds,
                });
            }

            await _sessionService.SaveAsync(session);
        }
        catch { /* Never crash the UI thread over a save failure */ }
    }

    /// <summary>
    /// Called once on startup. Restores the last session if one exists.
    /// Tracks are reloaded by file path; cue points are re-fetched from the DB
    /// by <see cref="WorkstationDeckViewModel.LoadPlaylistTrackCommand"/>.
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        var session = await _sessionService.LoadAsync();
        if (session == null) return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                ActiveMode            = (WorkstationMode)session.ActiveModeIndex;
                TimelineOffsetSeconds = session.TimelineOffsetSeconds;
                TimelineWindowSeconds = session.TimelineWindowSeconds;

                foreach (var deckState in session.Decks)
                {
                    if (string.IsNullOrEmpty(deckState.FilePath)) continue;
                    if (!System.IO.File.Exists(deckState.FilePath)) continue;

                    var deck = Decks.FirstOrDefault(d => d.DeckLabel == deckState.DeckLabel)
                               ?? Decks.FirstOrDefault();
                    if (deck == null) continue;

                    // Use raw path load; cue points load via hash when available
                    var track = new Models.PlaylistTrack
                    {
                        Title            = deckState.TrackTitle ?? string.Empty,
                        Artist           = deckState.TrackArtist ?? string.Empty,
                        ResolvedFilePath = deckState.FilePath,
                        TrackUniqueHash  = deckState.TrackUniqueHash ?? string.Empty,
                        BPM              = deckState.Bpm > 0 ? deckState.Bpm : null,
                        MusicalKey       = deckState.Key,
                    };
                    await deck.LoadPlaylistTrackCommand.Execute(track).FirstAsync();
                }
            }
            catch { /* Corrupt session — proceed with blank workstation */ }
        });
    }

    private async Task LoadPlaylistsAsync()
    {
        var jobs = await _library.LoadAllPlaylistJobsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Playlists.Clear();
            foreach (var j in jobs) Playlists.Add(j);
            if (Playlists.Count > 0 && ActivePlaylist == null)
                ActivePlaylist = Playlists[0];
        });
    }

    private async Task LoadPlaylistTracksAsync(PlaylistJob job)
    {
        var tracks = await _library.GetPagedPlaylistTracksAsync(
            job.Id, skip: 0, take: 200);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlaylistTracks.Clear();
            foreach (var t in tracks) PlaylistTracks.Add(t);
        });
    }

    private async Task OpenExportDialogAsync()
    {
        var vm = new ExportDialogViewModel(_mixdown);
        vm.SetDecks(Decks);
        // Show from UI thread — caller must be on UI thread (ReactiveCommand is)
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
            ? lt.MainWindow : null;
        if (owner != null)
            await Views.Avalonia.Workstation.ExportDialog.ShowForWorkstationAsync(owner, vm);
        vm.Dispose();
    }

    public void Dispose()
    {
        // Synchronously block for a brief window so the session is flushed
        // even when the OS terminates the process after the window closes.
        SaveSessionAsync().GetAwaiter().GetResult();
        ExportPanel.Dispose();
        _disposables.Dispose();
        foreach (var d in Decks) d.Dispose();
    }
}
