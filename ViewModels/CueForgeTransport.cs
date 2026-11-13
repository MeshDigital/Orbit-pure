using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>Identity of the track the shared player's queue is on.</summary>
public sealed record CueForgeTrackRef(string Hash, string? Title, string? Artist);

/// <summary>
/// What Cue Forge needs from the app's shared player. Kept narrow so the editor's transport logic
/// (borrowing the player only when you actually audition, and handing it back when you leave)
/// is unit-testable without the full <see cref="PlayerViewModel"/> graph.
/// </summary>
public interface ICueForgeTransport
{
    bool IsPlaying { get; }
    string? CurrentFilePath { get; }
    string? CurrentTrackHash { get; }
    string Title { get; }
    string Artist { get; }
    /// <summary>Playback position as a fraction of the loaded file (0..1).</summary>
    double PositionFraction { get; }
    /// <summary>The file the audio engine actually has open; null when nothing is loaded (e.g. the queue
    /// track restored at startup). Seeking only works on this file.</summary>
    string? LoadedFilePath => CurrentFilePath;
    /// <summary>Length of the loaded file in seconds; 0 when unknown.</summary>
    double LengthSeconds { get; }

    IObservable<bool> IsPlayingChanged { get; }
    IObservable<(double Fraction, double LengthSeconds)> PositionChanged { get; }
    /// <summary>The queue's current track changed (no initial value).</summary>
    IObservable<CueForgeTrackRef?> CurrentTrackChanged { get; }

    void LoadPaused(string filePath, string title, string artist);
    void Play();
    void Pause();
    void SeekFraction(double fraction);
    /// <summary>Enables/disables the metronome click, scheduled from the beat grid (issue #222).</summary>
    void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled);
    Task<string?> ResolveFilePathAsync(string trackHash);

    /// <summary>Remembers where playback is before Cue Forge takes the player over (the top bar then
    /// offers "Resume playlist"). The first interruption wins.</summary>
    void MarkInterrupted() { }

    /// <summary>Returns the player to the remembered point through the queue (re-arming the next
    /// transition). False when nothing was remembered.</summary>
    bool TryResumeInterrupted(bool play) => false;
}

/// <summary>Production <see cref="ICueForgeTransport"/> over the shared <see cref="PlayerViewModel"/>.</summary>
public sealed class PlayerViewModelCueForgeTransport : ICueForgeTransport
{
    private readonly PlayerViewModel _player;
    private readonly DatabaseService? _database;

    public PlayerViewModelCueForgeTransport(PlayerViewModel player, DatabaseService? database)
    {
        _player = player;
        _database = database;
        IsPlayingChanged = player.WhenAnyValue(p => p.IsPlaying);
        PositionChanged = player.WhenAnyValue(p => p.Position, p => p.LengthMs, (pos, len) => ((double)pos, len / 1000.0));
        CurrentTrackChanged = player.WhenAnyValue(p => p.CurrentTrack)
            .Skip(1)
            .Select(t => t?.GlobalId is { Length: > 0 } hash ? new CueForgeTrackRef(hash, t.Title, t.Artist) : null)
            .DistinctUntilChanged(r => r?.Hash);
    }

    public bool IsPlaying => _player.IsPlaying;
    public string? CurrentFilePath => _player.CurrentFilePath;
    public string? LoadedFilePath => _player.LoadedFilePath;
    public string? CurrentTrackHash => _player.CurrentTrack?.GlobalId;
    public string Title => _player.TrackTitle;
    public string Artist => _player.TrackArtist;
    public double PositionFraction => _player.Position;
    public double LengthSeconds => _player.LengthMs / 1000.0;

    public IObservable<bool> IsPlayingChanged { get; }
    public IObservable<(double Fraction, double LengthSeconds)> PositionChanged { get; }
    public IObservable<CueForgeTrackRef?> CurrentTrackChanged { get; }

    public void LoadPaused(string filePath, string title, string artist) => _player.LoadTrackPaused(filePath, title, artist);

    public void Play()
    {
        if (!_player.IsPlaying) _player.TogglePlayPauseCommand.Execute(null);
    }

    public void Pause()
    {
        if (_player.IsPlaying) _player.TogglePlayPauseCommand.Execute(null);
    }

    public void SeekFraction(double fraction) => _player.Seek((float)Math.Clamp(fraction, 0.0, 1.0));

    public void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled) =>
        _player.SetMetronome(beatTimes, downbeatSeconds, enabled);

    public void MarkInterrupted() => _player.MarkInterrupted();

    public bool TryResumeInterrupted(bool play)
    {
        if (!_player.HasResumePoint) return false;
        _player.ResumeInterrupted(play);
        return true;
    }

    public async Task<string?> ResolveFilePathAsync(string trackHash) =>
        _database == null ? null : await _database.GetLocalFilePathByHashAsync(trackHash);
}
