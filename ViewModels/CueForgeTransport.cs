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
    Task<string?> ResolveFilePathAsync(string trackHash);
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

    public async Task<string?> ResolveFilePathAsync(string trackHash) =>
        _database == null ? null : await _database.GetLocalFilePathByHashAsync(trackHash);
}
