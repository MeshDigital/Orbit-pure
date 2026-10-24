using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Shell;

// Pure, UI-free model of what the tray icon and the Windows taskbar button show. Everything in
// this file is deterministic and unit-tested (Tests/.../Services/Shell/ShellStatusTests.cs); the
// controllers that talk to Avalonia / Win32 only copy these values onto the real objects.

/// <summary>Playback facts the shell cares about, snapshotted from the PlayerViewModel.</summary>
public sealed record ShellPlaybackState(
    bool HasTrack,
    bool IsPlaying,
    string? Artist,
    string? Title,
    bool CanPlayPause,
    bool CanNext,
    bool CanPrevious)
{
    public static readonly ShellPlaybackState Empty = new(false, false, null, null, false, false, false);
}

/// <summary>Download-engine facts the shell cares about, snapshotted from the Download Center.</summary>
/// <param name="Progress">Average progress of in-flight transfers, 0..1.</param>
public sealed record ShellDownloadState(
    int Downloading,
    int Searching,
    int Queued,
    int Failed,
    bool EngineRunning,
    bool EnginePaused,
    double Progress,
    bool CanToggleEngine)
{
    public static readonly ShellDownloadState Idle = new(0, 0, 0, 0, false, false, 0, false);

    public bool HasWork => Downloading > 0 || Searching > 0 || Queued > 0;
}

public sealed record ShellState(ShellPlaybackState Playback, ShellDownloadState Downloads, bool IsWindowShown)
{
    public static readonly ShellState Initial = new(ShellPlaybackState.Empty, ShellDownloadState.Idle, true);
}

/// <summary>Labels and enabled-state of every tray context-menu item.</summary>
public sealed record TrayMenuModel(
    string NowPlayingHeader,
    string PlayPauseLabel,
    bool CanPlayPause,
    bool CanNext,
    bool CanPrevious,
    string DownloadsLabel,
    bool CanToggleDownloads,
    string WindowLabel);

/// <summary>Labels and enabled-state of the three taskbar thumbnail-toolbar buttons.</summary>
public sealed record ThumbBarModel(bool CanPrevious, bool CanPlayPause, bool CanNext, bool ShowPauseGlyph, string PlayPauseTip);

public enum ShellProgressKind
{
    None,
    Indeterminate,
    Normal,
    Error,
    Paused,
}

public readonly record struct ShellProgress(ShellProgressKind Kind, double Value);

public static class ShellStatusFormatter
{
    /// <summary>
    /// NOTIFYICONDATA.szTip is 128 UTF-16 units including the terminator, so anything longer is
    /// cut off by Windows mid-word. Keep the tooltip at or under this.
    /// </summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Menu header items are one line of a popup — keep them readable.</summary>
    public const int MaxMenuHeaderLength = 64;

    public const string AppName = "ORBIT";
    private const string Ellipsis = "…";
    private const string Dash = " – ";
    private const string Dot = " · ";

    public static string BuildTooltip(ShellState state)
    {
        var downloads = BuildDownloadsLine(state.Downloads);
        var playback = BuildPlaybackLine(state.Playback);

        // Fixed parts first; the track line (the only unbounded one) gets whatever room is left.
        var fixedLength = AppName.Length + (downloads is null ? 0 : downloads.Length + 1);
        var lines = new List<string>(3) { AppName };
        if (playback is not null)
        {
            var room = MaxTooltipLength - fixedLength - 1; // -1 for the newline before it
            if (room >= 8)
                lines.Add(Truncate(playback, room));
        }
        if (downloads is not null)
            lines.Add(downloads);

        return Truncate(string.Join("\n", lines), MaxTooltipLength);
    }

    /// <summary>"Playing: Artist – Title" / "Paused: Artist – Title", or null when nothing is loaded.</summary>
    public static string? BuildPlaybackLine(ShellPlaybackState playback)
    {
        if (!playback.HasTrack) return null;
        var track = FormatTrack(playback.Artist, playback.Title);
        if (track.Length == 0) return playback.IsPlaying ? "Playing" : "Paused";
        return (playback.IsPlaying ? "Playing: " : "Paused: ") + track;
    }

    /// <summary>"3 downloading · 12 queued · 1 failed" (zero counts omitted), or null when idle.</summary>
    public static string? BuildDownloadsLine(ShellDownloadState d)
    {
        var parts = new List<string>(5);
        if (d.EngineRunning && d.EnginePaused && (d.HasWork || d.Failed > 0)) parts.Add("Downloads paused");
        if (d.Downloading > 0) parts.Add($"{d.Downloading} downloading");
        if (d.Searching > 0) parts.Add($"{d.Searching} searching");
        if (d.Queued > 0) parts.Add($"{d.Queued} queued");
        if (d.Failed > 0) parts.Add($"{d.Failed} failed");
        return parts.Count == 0 ? null : string.Join(Dot, parts);
    }

    public static TrayMenuModel BuildMenu(ShellState state)
    {
        var p = state.Playback;
        var d = state.Downloads;

        string header;
        if (!p.HasTrack)
        {
            header = "Nothing playing";
        }
        else
        {
            var track = FormatTrack(p.Artist, p.Title);
            var prefix = p.IsPlaying ? "Now playing: " : "Paused: ";
            header = track.Length == 0 ? prefix.TrimEnd(' ', ':') : Truncate(prefix + track, MaxMenuHeaderLength);
        }

        var downloadsLabel = !d.EngineRunning
            ? "Start downloads"
            : d.EnginePaused ? "Resume downloads" : "Pause downloads";

        return new TrayMenuModel(
            NowPlayingHeader: header,
            PlayPauseLabel: p.IsPlaying ? "Pause" : "Play",
            CanPlayPause: p.CanPlayPause,
            CanNext: p.CanNext,
            CanPrevious: p.CanPrevious,
            DownloadsLabel: downloadsLabel,
            CanToggleDownloads: d.CanToggleEngine,
            WindowLabel: state.IsWindowShown ? "Hide ORBIT" : "Show ORBIT");
    }

    public static ThumbBarModel BuildThumbBar(ShellPlaybackState p) =>
        new(p.CanPrevious, p.CanPlayPause, p.CanNext, ShowPauseGlyph: p.IsPlaying, PlayPauseTip: p.IsPlaying ? "Pause" : "Play");

    public static string FormatTrack(string? artist, string? title)
    {
        var a = artist?.Trim() ?? string.Empty;
        var t = title?.Trim() ?? string.Empty;
        if (a.Length == 0) return t;
        if (t.Length == 0) return a;
        return a + Dash + t;
    }

    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        if (maxLength <= Ellipsis.Length) return value[..maxLength];
        var cut = value[..(maxLength - Ellipsis.Length)].TrimEnd();
        return cut + Ellipsis;
    }
}

/// <summary>
/// Maps download state to the taskbar progress bar. Stateful only for the short red "a download
/// just failed" flash; everything else is a pure function of the snapshot.
/// </summary>
public sealed class TaskbarProgressMapper
{
    public static readonly TimeSpan ErrorFlashDuration = TimeSpan.FromSeconds(4);

    private int _lastFailed = -1;
    private DateTime _errorUntilUtc = DateTime.MinValue;

    /// <summary>When the current error flash ends (so the caller can re-apply), or null if none is active.</summary>
    public DateTime? ErrorFlashEndsUtc(DateTime nowUtc) => nowUtc < _errorUntilUtc ? _errorUntilUtc : null;

    public ShellProgress Map(ShellDownloadState d, DateTime nowUtc)
    {
        // The first snapshot only establishes the baseline: failures from a previous session that
        // are still listed must not flash the taskbar red on startup.
        if (_lastFailed >= 0 && d.Failed > _lastFailed)
            _errorUntilUtc = nowUtc + ErrorFlashDuration;
        _lastFailed = d.Failed;

        if (nowUtc < _errorUntilUtc)
            return new ShellProgress(ShellProgressKind.Error, 1.0);

        return MapSteady(d);
    }

    public static ShellProgress MapSteady(ShellDownloadState d)
    {
        if (!d.EngineRunning || !d.HasWork)
            return new ShellProgress(ShellProgressKind.None, 0);

        var value = Math.Clamp(double.IsFinite(d.Progress) ? d.Progress : 0, 0, 1);

        if (d.EnginePaused)
            // Paused with nothing transferring: a full amber bar reads as "work waiting, paused".
            return new ShellProgress(ShellProgressKind.Paused, d.Downloading > 0 ? Math.Max(value, 0.02) : 1.0);

        if (d.Downloading > 0)
            return new ShellProgress(ShellProgressKind.Normal, Math.Max(value, 0.02));

        if (d.Searching > 0)
            return new ShellProgress(ShellProgressKind.Indeterminate, 0);

        // Only waiting in local/peer queues (can last hours): no animated bar for that.
        return new ShellProgress(ShellProgressKind.None, 0);
    }
}

/// <summary>Leading-edge rate limiter: at most one pass per <c>minInterval</c>.</summary>
public sealed class ShellUpdateThrottle
{
    private readonly TimeSpan _minInterval;
    private DateTime _lastUtc = DateTime.MinValue;

    public ShellUpdateThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    public TimeSpan MinInterval => _minInterval;

    /// <summary>True (and records <paramref name="nowUtc"/>) when enough time has passed since the last pass.</summary>
    public bool TryEnter(DateTime nowUtc)
    {
        // A clock that jumped backwards (nowUtc < last) must not block updates until it catches up.
        if (_lastUtc != DateTime.MinValue && nowUtc >= _lastUtc && nowUtc - _lastUtc < _minInterval)
            return false;
        _lastUtc = nowUtc;
        return true;
    }
}
