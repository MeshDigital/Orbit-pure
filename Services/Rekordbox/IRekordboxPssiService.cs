using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>One track found, during a full-library scan, to already have saved memory/hot cues in
/// Rekordbox — see <see cref="IRekordboxPssiService.FindTracksWithSavedCuesAsync"/>.</summary>
public readonly record struct RekordboxCuedTrack(string SourcePath, string AnlzFilePath, int CueCount);

public interface IRekordboxPssiService
{
    /// <summary>
    /// True once the local Rekordbox analysis folder (share/PIONEER/USBANLZ) has been located.
    /// Does not guarantee any specific track has been analysed in Rekordbox — only that Rekordbox
    /// itself appears to be installed and has analysed at least some tracks.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Looks up Rekordbox's own phrase/song-structure analysis (the PSSI tag from its ANLZ files)
    /// for the given audio file, if Rekordbox has already analysed that exact file. Returns null
    /// if Rekordbox isn't installed, hasn't analysed this file, or the analysis has no phrase data
    /// (PSSI is only present after Rekordbox's "phrase analysis," a separate step from basic
    /// beatgrid/waveform analysis). <paramref name="bpm"/>/<paramref name="downbeatAnchor"/> are
    /// the constant-BPM fallback used when <paramref name="beatGridSeconds"/> is unavailable or
    /// too short to cover a phrase's beat index; when present, the real per-beat timestamps in
    /// <paramref name="beatGridSeconds"/> (ORBIT's own detected beat grid, e.g.
    /// AudioFeaturesEntity.BeatGridJson) are used instead, since Rekordbox's PSSI beat index
    /// assumes the track's actual beat positions, not a constant-BPM extrapolation — a fixed BPM
    /// formula silently drifts on any track with tempo variation.
    /// </summary>
    Task<IReadOnlyList<PhraseSegment>?> AnalyzeAsync(
        string audioFilePath, double bpm, double downbeatAnchor,
        IReadOnlyList<double>? beatGridSeconds = null, CancellationToken ct = default);

    /// <summary>
    /// Looks up Rekordbox's own saved memory/hot cue points (the PCOB/PCO2 tags) for the given
    /// audio file, if Rekordbox has already analysed that file AND a human (or Rekordbox's
    /// auto-cue) has actually placed at least one cue on it — most analysed tracks have none.
    /// Returns null if Rekordbox isn't installed or hasn't analysed this file; returns an empty
    /// list if it has been analysed but has no saved cues.
    /// </summary>
    Task<IReadOnlyList<RekordboxCuePoint>?> GetSavedCuePointsAsync(string audioFilePath, CancellationToken ct = default);

    /// <summary>
    /// Scans Rekordbox's entire local analysis cache and returns every track that has at least one
    /// saved memory or hot cue — the "which of my tracks already have cues set in Rekordbox"
    /// question. This is a full-library disk scan (re-parsing every .EXT file), so it's meant for
    /// occasional/manual use (a comparison tool, a library audit), not a hot path.
    /// </summary>
    Task<IReadOnlyList<RekordboxCuedTrack>> FindTracksWithSavedCuesAsync(CancellationToken ct = default);
}
