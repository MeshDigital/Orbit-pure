using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// How a <see cref="CueEditorSession"/> plays its track. Each host supplies its own: Cue Forge
/// borrows the shared player, a Flow Builder deck auditions through the library preview player.
/// The session never touches a player directly, so two hosts never steal each other's audio.
/// </summary>
public interface ICueAudition
{
    /// <summary>Playhead / listening position on this track, in seconds. New cues, drops and
    /// loops that are placed "here" land at this position.</summary>
    double PositionSeconds { get; }

    /// <summary>True while this host is playing this track.</summary>
    bool IsPlaying { get; }

    /// <summary>Plays this track from <paramref name="seconds"/>, taking the host's player if needed.</summary>
    void PlayFrom(double seconds);

    /// <summary>A pad was hit: jump to its cue and play from it.</summary>
    void JumpToCue(OrbitCue cue) => PlayFrom(cue.Timestamp);

    /// <summary>Stops this host's playback of the track (clicking a playing cue again, or Space).
    /// Both production hosts implement it; the default is for hosts that can't stop.</summary>
    void Stop() { }
}

/// <summary>
/// The drop cue template (build-in countdown cues placed before every drop; see
/// <see cref="DropCountdownCues"/>). One app-wide setting, stored in <see cref="AppConfig"/> and
/// shared by every cue editor and by automatic cue generation.
/// </summary>
public sealed class CueTemplateSettings
{
    private readonly AppConfig _config;
    private readonly ConfigManager? _configManager;
    private readonly ILogger _logger;

    public CueTemplateSettings(AppConfig config, ConfigManager? configManager = null, ILogger? logger = null)
    {
        _config = config;
        _configManager = configManager;
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler? Changed;

    public string Mode => DropCountdownCues.Normalize(_config.DropCountdownMode, _config.CustomCountdownBars).Mode;

    /// <summary>Bars before the drop for the Custom template, e.g. "32,16,8".</summary>
    public string CustomBars => DropCountdownCues.Normalize(_config.DropCountdownMode, _config.CustomCountdownBars).CustomBars;

    public void SetMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == Mode) return;
        _config.DropCountdownMode = mode;
        Persist("cue template");
    }

    public void SetCustomBars(string? bars)
    {
        if (bars == null || bars == CustomBars) return;
        _config.CustomCountdownBars = bars;
        Persist("custom cue template");
    }

    /// <summary>Countdowns as memory cues (pads A B D E stay free).</summary>
    public bool CountdownsAsMemoryCues => _config.CountdownsAsMemoryCues;

    public void SetCountdownsAsMemoryCues(bool value)
    {
        if (value == CountdownsAsMemoryCues) return;
        _config.CountdownsAsMemoryCues = value;
        Persist("countdown cue routing");
    }

    public IReadOnlyList<int> ResolveBars(string? genre, double bpm) =>
        DropCountdownCues.ResolveBars(Mode, genre, bpm, CustomBars);

    private void Persist(string what)
    {
        try { _configManager?.Save(_config); }
        catch (Exception ex) { _logger.LogWarning(ex, "Couldn't save the {What}", what); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Links every live <see cref="CueEditorSession"/> (Cue Forge, both Flow Builder decks, the Mix
/// sidepanel decks) so two editors open on the same track can't silently overwrite each other.
///
/// The rule, kept deliberately simple:
///   1. Each session keeps its own working copy, and saves the whole set (replace all rows).
///   2. When a session saves, every other session on the same track is told. A session with no
///      unsaved edits reloads from the store straight away, so the other editor's change shows up
///      live. A session with unsaved edits keeps them and shows a warning: Save keeps its version
///      (it replaces the other one), Revert loads the other one.
///   3. Opening a track that another editor has unsaved edits for shows a warning saying so.
/// Flow Builder also saves its decks before it opens a track in Cue Forge.
///
/// Sessions are held weakly, so a host that is never disposed doesn't leak through this singleton.
/// </summary>
public sealed class CueSessionRegistry
{
    private readonly List<WeakReference<CueEditorSession>> _sessions = new();
    private readonly object _gate = new();

    public CueSessionRegistry(AppConfig config, ConfigManager? configManager = null, ILogger<CueSessionRegistry>? logger = null)
    {
        Templates = new CueTemplateSettings(config, configManager, logger);
        Templates.Changed += (_, _) =>
        {
            foreach (var session in Live()) session.OnTemplateChanged();
        };
    }

    /// <summary>The shared drop cue template.</summary>
    public CueTemplateSettings Templates { get; }

    internal void Add(CueEditorSession session)
    {
        lock (_gate) _sessions.Add(new WeakReference<CueEditorSession>(session));
    }

    internal void Remove(CueEditorSession session)
    {
        lock (_gate) _sessions.RemoveAll(w => !w.TryGetTarget(out var s) || ReferenceEquals(s, session));
    }

    private List<CueEditorSession> Live()
    {
        lock (_gate)
        {
            _sessions.RemoveAll(w => !w.TryGetTarget(out _));
            return _sessions.Select(w => w.TryGetTarget(out var s) ? s : null).OfType<CueEditorSession>().ToList();
        }
    }

    /// <summary>Other sessions currently editing <paramref name="session"/>'s track.</summary>
    public IReadOnlyList<CueEditorSession> PeersOf(CueEditorSession session) =>
        session.TrackHash is not { } hash
            ? Array.Empty<CueEditorSession>()
            : Live().Where(s => !ReferenceEquals(s, session) &&
                                string.Equals(s.TrackHash, hash, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Tells the other sessions on <paramref name="source"/>'s track that the stored cues changed.</summary>
    internal async Task NotifyStoreChangedAsync(CueEditorSession source)
    {
        foreach (var peer in PeersOf(source))
            await peer.OnStoreChangedElsewhereAsync(source);
    }
}
