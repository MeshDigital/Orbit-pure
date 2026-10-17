using System;
using System.Globalization;
using System.Reactive;
using ReactiveUI;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;

namespace SLSKDONET.ViewModels.EditLab;

/// <summary>What the shared app player has to offer the Edit Lab: is it playing, and pause it.</summary>
public interface IEditLabPlayerGate
{
    bool IsPlaying { get; }
    IObservable<bool> IsPlayingChanged { get; }
    void Pause();
}

/// <summary>Production gate over the app's <see cref="PlayerViewModel"/>.</summary>
public sealed class PlayerViewModelEditLabGate : IEditLabPlayerGate
{
    private readonly PlayerViewModel _player;

    public PlayerViewModelEditLabGate(PlayerViewModel player)
    {
        _player = player;
        IsPlayingChanged = player.WhenAnyValue(p => p.IsPlaying);
    }

    public bool IsPlaying => _player.IsPlaying;
    public IObservable<bool> IsPlayingChanged { get; }

    public void Pause()
    {
        if (_player.IsPlaying) _player.TogglePlayPauseCommand.Execute(null);
    }
}

public enum StemAvailability { NotSeparated, Separating, Ready, Unavailable }

/// <summary>One track in the source bin.</summary>
public sealed class EditSourceItemViewModel : ReactiveObject
{
    private readonly EditLabViewModel _owner;
    private EditSource _source;
    private StemAvailability _stems;
    private double _separationProgress;
    private bool _isBase;

    public EditSourceItemViewModel(EditLabViewModel owner, EditSource source)
    {
        _owner = owner;
        _source = source;
        SeparateCommand = ReactiveCommand.CreateFromTask(() => _owner.SeparateAsync(this),
            this.WhenAnyValue(x => x.Stems, s => s is StemAvailability.NotSeparated));
        CancelSeparationCommand = ReactiveCommand.Create(() => _owner.CancelSeparation(this),
            this.WhenAnyValue(x => x.Stems, s => s == StemAvailability.Separating));
        SetBaseCommand = ReactiveCommand.Create(() => _owner.SetBase(this), this.WhenAnyValue(x => x.IsBase, b => !b));
        RemoveCommand = ReactiveCommand.Create(() => _owner.RemoveSource(this));
        AddClipCommand = ReactiveCommand.Create<string>(stem => _owner.AddClipFromSource(this, stem));
    }

    public EditSource Source => _source;
    public string Hash => _source.TrackHash;
    public string Title => _source.Title;
    public string Artist => _source.Artist;
    public string BpmDisplay => _source.Bpm > 0 ? _source.Bpm.ToString("0.0#", CultureInfo.InvariantCulture) : "—";
    public string KeyDisplay => EditKeyMath.Normalize(_source.Key) ?? "—";

    public bool IsBase { get => _isBase; set => this.RaiseAndSetIfChanged(ref _isBase, value); }

    public StemAvailability Stems
    {
        get => _stems;
        set
        {
            this.RaiseAndSetIfChanged(ref _stems, value);
            this.RaisePropertyChanged(nameof(StemsText));
            this.RaisePropertyChanged(nameof(StemsReady));
            this.RaisePropertyChanged(nameof(IsSeparating));
            this.RaisePropertyChanged(nameof(CanStartSeparation));
        }
    }

    public bool StemsReady => Stems == StemAvailability.Ready;
    public bool IsSeparating => Stems == StemAvailability.Separating;
    public bool CanStartSeparation => Stems == StemAvailability.NotSeparated;

    public double SeparationProgress
    {
        get => _separationProgress;
        set { this.RaiseAndSetIfChanged(ref _separationProgress, value); this.RaisePropertyChanged(nameof(StemsText)); }
    }

    public string StemsText => Stems switch
    {
        StemAvailability.Ready => "Stems ready",
        StemAvailability.Separating => string.Create(CultureInfo.InvariantCulture, $"Separating {SeparationProgress:P0}"),
        StemAvailability.Unavailable => "Stem model not installed",
        _ => "Stems not separated",
    };

    public ReactiveCommand<Unit, Unit> SeparateCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSeparationCommand { get; }
    public ReactiveCommand<Unit, Unit> SetBaseCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    /// <summary>Parameter: an <see cref="EditStem"/> name ("Vocals", "FullMix"…).</summary>
    public ReactiveCommand<string, Unit> AddClipCommand { get; }

    public void Update(EditSource source, bool isBase)
    {
        _source = source;
        IsBase = isBase;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Artist));
        this.RaisePropertyChanged(nameof(BpmDisplay));
        this.RaisePropertyChanged(nameof(KeyDisplay));
    }
}

/// <summary>A lane header (name, mute, solo, remove).</summary>
public sealed class EditLaneItemViewModel : ReactiveObject
{
    private readonly EditProjectEditor _editor;
    private EditLane _lane;

    public EditLaneItemViewModel(EditProjectEditor editor, EditLane lane)
    {
        _editor = editor;
        _lane = lane;
        RemoveCommand = ReactiveCommand.Create(() => _editor.RemoveLane(_lane.Id));
    }

    public Guid Id => _lane.Id;

    public string Name
    {
        get => _lane.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _lane.Name) return;
            _editor.SetLane(_lane.Id, l => l.Name = value.Trim(), "lane-name-" + _lane.Id);
        }
    }

    public bool Muted
    {
        get => _lane.Muted;
        set { if (value != _lane.Muted) _editor.SetLane(_lane.Id, l => l.Muted = value); }
    }

    public bool Solo
    {
        get => _lane.Solo;
        set { if (value != _lane.Solo) _editor.SetLane(_lane.Id, l => l.Solo = value); }
    }

    public int ClipCount => _lane.Clips.Count;

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public void Update(EditLane lane)
    {
        _lane = lane;
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(Muted));
        this.RaisePropertyChanged(nameof(Solo));
        this.RaisePropertyChanged(nameof(ClipCount));
    }
}

/// <summary>The inspector for the selected clip. Every setter is an undoable edit through the editor
/// (repeated changes of one field coalesce into one undo step).</summary>
public sealed class EditClipInspectorViewModel : ReactiveObject
{
    private readonly EditProjectEditor _editor;
    private EditClip _clip;

    public EditClipInspectorViewModel(EditProjectEditor editor, EditClip clip)
    {
        _editor = editor;
        _clip = clip;
        UseSuggestedShiftCommand = ReactiveCommand.Create(() =>
        {
            if (Match.SuggestedSemitones is { } s) Set(c => { c.KeyMode = ClipKeyMode.Manual; c.ManualSemitones = s; });
        });
        ResetShiftCommand = ReactiveCommand.Create(() => Set(c => { c.KeyMode = ClipKeyMode.Auto; c.ManualSemitones = 0; }));
    }

    public Guid Id => _clip.Id;
    private EditProject Project => _editor.Project;
    private EditSource? Source => Project.FindSource(_clip.SourceHash);
    private ClipMatch Match => EditMatching.Resolve(Project, _clip, Source);

    private void Set(Action<EditClip> change, string? field = null) =>
        _editor.SetClip(_clip.Id, change, field == null ? null : $"clip-{_clip.Id}-{field}");

    public string SourceTitle => Source?.DisplayName ?? "Missing track";
    public string StemName
    {
        get => _clip.Stem.ToString();
        set { if (Enum.TryParse<EditStem>(value, out var s) && s != _clip.Stem) Set(c => c.Stem = s); }
    }
    public static string[] StemOptions { get; } = Enum.GetNames<EditStem>();

    // NumericUpDown binds decimal?; the model stays double.

    /// <summary>1-based timeline bar.</summary>
    public decimal? StartBar
    {
        get => (decimal)Math.Round(_clip.StartBar + 1, 4);
        set { if (value is not { } d) return; var v = Math.Max(0, (double)d - 1); if (Math.Abs(v - _clip.StartBar) > 1e-9) Set(c => c.StartBar = v, "start"); }
    }

    public decimal? LengthBars
    {
        get => (decimal)Math.Round(_clip.LengthBars, 4);
        set { if (value is not { } d) return; var v = Math.Max(EditProjectEditor.MinClipBars, (double)d); if (Math.Abs(v - _clip.LengthBars) > 1e-9) Set(c => c.LengthBars = v, "length"); }
    }

    /// <summary>In-point as a 1-based bar of the source (its own tempo and downbeat).</summary>
    public decimal? SourceInBar
    {
        get => Source == null ? 1m : (decimal)Math.Round(EditMatching.SourceSecondsToBar(Source, _clip.SourceInSeconds, Project.BeatsPerBar), 3);
        set
        {
            if (Source == null || value is not { } d) return;
            var seconds = EditMatching.SourceBarToSeconds(Source, (double)d, Project.BeatsPerBar);
            if (Math.Abs(seconds - _clip.SourceInSeconds) > 1e-6) Set(c => c.SourceInSeconds = seconds, "in");
        }
    }

    public string SourceInTime => CueForgeGrid.FormatTime(_clip.SourceInSeconds);

    public double GainDb
    {
        get => _clip.GainDb;
        set { var v = Math.Clamp(value, -24, 12); if (Math.Abs(v - _clip.GainDb) > 1e-6) Set(c => c.GainDb = v, "gain"); }
    }
    public string GainDisplay => string.Create(CultureInfo.InvariantCulture, $"{_clip.GainDb:+0.0;-0.0;0.0} dB");

    public decimal? FadeInBars
    {
        get => (decimal)Math.Round(_clip.FadeInBars, 4);
        set { if (value is not { } d) return; var v = Math.Clamp((double)d, 0, _clip.LengthBars); if (Math.Abs(v - _clip.FadeInBars) > 1e-9) Set(c => c.FadeInBars = v, "fadein"); }
    }

    public decimal? FadeOutBars
    {
        get => (decimal)Math.Round(_clip.FadeOutBars, 4);
        set { if (value is not { } d) return; var v = Math.Clamp((double)d, 0, _clip.LengthBars); if (Math.Abs(v - _clip.FadeOutBars) > 1e-9) Set(c => c.FadeOutBars = v, "fadeout"); }
    }

    public bool Muted { get => _clip.Muted; set { if (value != _clip.Muted) Set(c => c.Muted = value); } }
    public bool Solo { get => _clip.Solo; set { if (value != _clip.Solo) Set(c => c.Solo = value); } }

    // Tempo: Project / Follow / Native (radio chips)
    public bool TempoFollowsProject { get => _clip.TempoMode == ClipTempoMode.Project; set { if (value) Set(c => c.TempoMode = ClipTempoMode.Project); } }
    public bool TempoAlwaysFollow { get => _clip.TempoMode == ClipTempoMode.Follow; set { if (value) Set(c => c.TempoMode = ClipTempoMode.Follow); } }
    public bool TempoNative { get => _clip.TempoMode == ClipTempoMode.Native; set { if (value) Set(c => c.TempoMode = ClipTempoMode.Native); } }

    // Key: Auto / Manual
    public bool KeyAuto { get => _clip.KeyMode == ClipKeyMode.Auto; set { if (value) Set(c => c.KeyMode = ClipKeyMode.Auto); } }
    public bool KeyManual { get => _clip.KeyMode == ClipKeyMode.Manual; set { if (value) Set(c => { c.KeyMode = ClipKeyMode.Manual; }); } }

    public decimal? ManualSemitones
    {
        get => _clip.ManualSemitones;
        set { if (value is not { } d) return; var v = Math.Clamp((int)Math.Round(d), -12, 12); if (v != _clip.ManualSemitones || _clip.KeyMode != ClipKeyMode.Manual) Set(c => { c.ManualSemitones = v; c.KeyMode = ClipKeyMode.Manual; }, "semis"); }
    }

    // Read-outs
    public string StretchDisplay => Match.FollowsTempo ? EditMatching.FormatStretch(Match.StretchPercent) : "Native tempo";
    public bool StretchWarning => Match.StretchWarning;
    public string StretchWarningText => string.Create(CultureInfo.InvariantCulture,
        $"Stretched more than {EditMatching.StretchWarningPercent:0} %: expect smeared transients. Consider a closer tempo.");
    public string SourceTempoDisplay => Source is { Bpm: > 0 } s
        ? string.Create(CultureInfo.InvariantCulture, $"{Match.EffectiveSourceBpm:0.0#} → {(Match.FollowsTempo ? Project.Bpm : Match.EffectiveSourceBpm):0.0#}")
        : "Not analysed";
    public string ShiftDisplay => EditMatching.FormatSemitones(Match.Semitones);
    public string KeyDisplay => Match.SourceKey == null ? "Key unknown" : $"{Match.SourceKey} → {Match.ResultKey}";
    public string SuggestionDisplay => Match.SuggestedSemitones is { } s ? $"Suggested {EditMatching.FormatSemitones(s)}" : "No suggestion (key unknown)";
    public bool HasSuggestion => Match.SuggestedSemitones is { } s && s != Match.Semitones;
    public bool StemMissing => EditRenderPlan.FilesFor(_clip.Stem).Any(s => Source == null || EditRenderPlan.DefaultAudioPath(Source, s) == null);

    private static readonly string[] RefreshedProperties = typeof(EditClipInspectorViewModel)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => !p.Name.EndsWith("Command", StringComparison.Ordinal))
        .Select(p => p.Name).ToArray();

    public ReactiveCommand<Unit, Unit> UseSuggestedShiftCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetShiftCommand { get; }

    /// <summary>Re-reads the (possibly replaced, after undo) clip and refreshes every binding.</summary>
    public void Update(EditClip clip)
    {
        _clip = clip;
        foreach (var property in RefreshedProperties) this.RaisePropertyChanged(property);
    }
}
