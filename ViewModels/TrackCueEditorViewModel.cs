using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis.CueDetr;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One deck (outgoing or incoming track) of the transition editor's "Edit cues" mode, in Flow
/// Builder and the Mix sidepanel. All cue editing is the shared <see cref="CueEditorSession"/>
/// (the same engine as Cue Forge); this host adds what is specific to a transition deck:
/// auditioning through the library preview player (never the main player), holding the main mix
/// while you audition, "use as mix point", and cue generation through the library's
/// "Regenerate Cues" service.
/// </summary>
public sealed class TrackCueEditorViewModel : ReactiveObject, ICueAudition, IDisposable
{
    private readonly ILibraryPreviewPlayer? _previewPlayer;
    private readonly ILogger _logger;

    public TrackCueEditorViewModel(string deckLabel, ICuePointService cueService, ILibraryPreviewPlayer? previewPlayer, ILogger logger,
        CueSessionRegistry? cueSessions = null)
    {
        DeckLabel = deckLabel;
        _previewPlayer = previewPlayer;
        _logger = logger;

        Session = new CueEditorSession(cueService, this, cueSessions, logger)
        {
            EditorName = "Flow Builder",
            // You place cues here by ear: hear every add / move / drop, and snap a drag on release
            // (the transition waveform doesn't snap while dragging).
            AuditionAfterEdits = true,
            SnapOnDragRelease = true,
        };
        Session.SnapResolution = "Bar";
        Session.CueMoved += (_, move) => CueMoved?.Invoke(this, move);
        Session.Saved += (_, _) =>
        {
            EndAudition(); // cue is set — the held mix carries on
            CuesChanged?.Invoke(this, EventArgs.Empty);
        };
        Session.Reloaded += (_, _) => CuesChanged?.Invoke(this, EventArgs.Empty);

        var hasSelection = Session.WhenAnyValue(s => s.SelectedCue).Select(c => c != null);
        AuditionSelectedCommand = ReactiveCommand.Create(() => { if (Session.SelectedCue != null) Audition(Session.SelectedCue.Timestamp); }, hasSelection);
        UseSelectedAsTriggerCommand = ReactiveCommand.Create(() => { if (Session.SelectedCue != null) TriggerRequested?.Invoke(this, Session.SelectedCue.Timestamp); }, hasSelection);
        var canGenerate = Session.WhenAnyValue(s => s.TrackHash).CombineLatest(this.WhenAnyValue(x => x.IsGenerating),
            (h, g) => !string.IsNullOrEmpty(h) && !g);
        GenerateCuesCommand = ReactiveCommand.CreateFromTask<string>(GenerateCuesAsync, canGenerate);
    }

    /// <summary>The cue editor for this deck's track.</summary>
    public CueEditorSession Session { get; }

    /// <summary>"Outgoing" / "Incoming".</summary>
    public string DeckLabel { get; }

    public string? TrackHash => Session.TrackHash;
    public string TrackTitle => Session.TrackTitle;

    /// <summary>A cue was picked as this deck's transition trigger point (seconds).</summary>
    public event EventHandler<double>? TriggerRequested;

    /// <summary>A cue moved from one time to another — the host moves the trigger along with it.</summary>
    public event EventHandler<(double From, double To)>? CueMoved;

    /// <summary>The stored cues changed (saved, regenerated, or saved in another editor) — the host
    /// re-plans the transition from them.</summary>
    public event EventHandler? CuesChanged;

    /// <summary>This deck started playing (a waveform click or a cue) — the host routes the drop keys here.</summary>
    public event EventHandler? Auditioned;

    public ReactiveCommand<Unit, Unit> AuditionSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> UseSelectedAsTriggerCommand { get; }

    public void Load(string trackHash, string title, string? filePath, double bpm, double downbeat, double duration,
        IEnumerable<CuePointEntity> cues, string? genre = null)
    {
        LastAuditionSeconds = 0;
        Session.Load(new CueTrackInfo(trackHash, title, filePath, bpm, downbeat, duration, genre), cues);
        this.RaisePropertyChanged(nameof(TrackHash));
        this.RaisePropertyChanged(nameof(TrackTitle));
    }

    /// <summary>Saves the working set if it has unsaved edits. Returns true when something was written.</summary>
    public Task<bool> SaveIfDirtyAsync() => Session.SaveIfDirtyAsync();

    // ── Generate (same flow as the library's "Regenerate Cues") ────────────

    /// <summary>Regenerates this track's auto cues and returns (ok, message). Set by the host; the
    /// same service as the library's right-click "Regenerate Cues" (ORBIT / + CUE-DETR / CUE-DETR only).</summary>
    public Func<string, string?, CueSourceMode, Task<(bool Ok, string Message)>>? CueGenerator { get; set; }

    /// <summary>Parameter "orbit", "compare" or "ai".</summary>
    public ReactiveCommand<string, Unit> GenerateCuesCommand { get; }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; private set => this.RaiseAndSetIfChanged(ref _isGenerating, value); }

    private async Task GenerateCuesAsync(string? modeText)
    {
        if (Session.TrackHash is not { } hash || CueGenerator == null) return;
        var mode = modeText switch
        {
            "compare" => CueSourceMode.Compare,
            "ai" => CueSourceMode.AiOnly,
            _ => CueSourceMode.Orbit,
        };
        // Unsaved edits are saved first: they become your own cues, which generation never replaces.
        if (Session.IsDirty && !await Session.SaveAsync()) return;
        IsGenerating = true;
        Session.IsLocked = true;
        Session.SetStatus(mode == CueSourceMode.Orbit ? "Generating cues…" : "Generating cues with CUE-DETR (~10 s the first time)…");
        try
        {
            var (ok, message) = await CueGenerator(hash, Session.FilePath, mode);
            if (!ok) { Session.SetStatus(message, isError: true); return; }
            if (Session.TrackHash != hash) return;
            if (await Session.ReloadFromStoreAsync(message, undoable: true))
                await Session.NotifyStoreChangedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cue generation failed for {Hash}", hash);
            Session.SetStatus("Couldn't generate cues", isError: true);
        }
        finally
        {
            Session.IsLocked = false;
            IsGenerating = false;
        }
    }

    // ── Audition (ICueAudition) ────────────────────────────────────────────

    /// <summary>Where this deck was last auditioned.</summary>
    public double LastAuditionSeconds { get; set; }

    private bool IsPreviewingThisTrack =>
        _previewPlayer is { IsPreviewPlaying: true } p && p.CurrentPreviewPath == Session.FilePath;

    /// <summary>Where you are listening on this deck: the preview's live position when it is
    /// playing this track, else the last audition point. "Here" for cues, drops and loops.</summary>
    public double ListenSeconds =>
        IsPreviewingThisTrack && _previewPlayer!.PositionSeconds is { } pos ? pos : LastAuditionSeconds;

    double ICueAudition.PositionSeconds => ListenSeconds;
    bool ICueAudition.IsPlaying => IsPreviewingThisTrack;
    void ICueAudition.PlayFrom(double seconds) => Audition(seconds);
    void ICueAudition.Stop() => EndAudition();

    /// <summary>Host hook: pause the main player if it's playing; return true if it was paused
    /// (so it can be resumed later). Auditioning a cue holds the main mix until the cues are saved.</summary>
    public Func<bool>? HoldMainPlayback { get; set; }
    /// <summary>Host hook: resume the main player after a hold.</summary>
    public Action? ResumeMainPlayback { get; set; }
    private bool _holdingMainPlayback;

    /// <summary>Plays this deck from <paramref name="seconds"/> through the preview player, pausing
    /// the main player first if it's playing (resumed on save or <see cref="EndAudition"/>).</summary>
    public void Audition(double seconds)
    {
        if (string.IsNullOrEmpty(Session.FilePath) || _previewPlayer == null) return;
        LastAuditionSeconds = seconds;
        Auditioned?.Invoke(this, EventArgs.Empty);
        if (!_holdingMainPlayback && HoldMainPlayback?.Invoke() == true) _holdingMainPlayback = true;
        // startSeconds 0 means "hover preview" (debounced) to the preview player — use a hair above.
        // The status line keeps the last edit's result; the transition editor shows what's previewing.
        _previewPlayer.RequestPreview(Session.FilePath, Session.Bpm > 0 ? Session.Bpm : null, Math.Max(seconds, 0.001));
    }

    /// <summary>Stops this deck's audition and resumes the main player if the audition paused it.</summary>
    public void EndAudition()
    {
        _previewPlayer?.StopPreview();
        if (_holdingMainPlayback)
        {
            _holdingMainPlayback = false;
            ResumeMainPlayback?.Invoke();
        }
    }

    public void Dispose() => Session.Dispose();
}
