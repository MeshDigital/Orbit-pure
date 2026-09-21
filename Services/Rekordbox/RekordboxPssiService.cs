using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// Surfaces Rekordbox's own phrase/song-structure analysis (PSSI tag in its ANLZ files) as a
/// <see cref="PhraseSegment"/> source for <see cref="Engine.Cueing.CueGenerationService"/> — the
/// same shape <see cref="Audio.IEdmFormerService"/> provides, so it slots into the exact same
/// Path-1 priority gate. When a track has already been analysed in Rekordbox (a common workflow
/// for working DJs), Rekordbox's own commercial phrase analysis is presumably at least as reliable
/// as ORBIT's own DSP/ML detection — this makes that analysis available to ORBIT for free instead
/// of re-deriving it from scratch, mirroring the approach the open-source "djcues" project takes.
///
/// Entirely optional and best-effort: if Rekordbox isn't installed, hasn't analysed a given track,
/// or the on-disk format doesn't match what's parsed here (a future Rekordbox version, a
/// non-Windows install layout), this returns null and callers fall back to EDMFormer/DSP/heuristic
/// exactly as before. Never throws.
///
/// Format credit: reverse-engineered by the pyrekordbox and Deep Symmetry crate-digger projects
/// (see RekordboxAnlzParser). This reads Rekordbox's local analysis cache only — it never touches
/// master.db (which is SQLCipher-encrypted in Rekordbox 6+) and never writes anything back.
/// </summary>
public sealed class RekordboxPssiService : IRekordboxPssiService
{
    private readonly ILogger<RekordboxPssiService> _logger;

    // Path (normalized, case-insensitive) -> candidate .EXT file(s) that reported this exact
    // source path via their PPTH tag. Confirmed against 15 real .2EX files that PSSI lives
    // exclusively in .EXT (2EX only ever carries PWV6/7/C preview-waveform and PVDI vocal tags),
    // so only .EXT is scanned. Built lazily on first use; Rekordbox's own USBANLZ tree only grows
    // between ORBIT sessions (analysis happens inside Rekordbox, not here), so a one-time-per-session
    // scan is enough.
    private readonly ConcurrentDictionary<string, List<string>> _pathIndex = new(StringComparer.OrdinalIgnoreCase);

    // Secondary index keyed by filename only, populated ONLY for PPTH source paths that look like
    // Rekordbox's "unresolved drive" placeholder — confirmed against ~40 real .EXT files, where
    // PPTH consistently stores paths as e.g. "?/02 - Move Your Body.flac" (a literal '?' where a
    // drive letter/root would be) rather than a real absolute path. That degraded path can never
    // equal ORBIT's own resolved absolute path, so exact-path matching alone silently finds nothing
    // for any such track — which is the common case, not an edge case. Falling back to a filename
    // match trades a small false-positive risk (two different tracks sharing a filename in
    // different folders) for actually finding real matches; acceptable here since a wrong phrase
    // source is a soft failure (Path 1 still tolerates partial/odd label coverage) and this is an
    // optional, best-effort enrichment layer, not a source of truth.
    private readonly ConcurrentDictionary<string, List<string>> _filenameFallbackIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexBuilt;

    public bool IsAvailable => RekordboxAnlzLocator.FindRoots().Count > 0;

    public RekordboxPssiService(ILogger<RekordboxPssiService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<PhraseSegment>?> AnalyzeAsync(
        string audioFilePath, double bpm, double downbeatAnchor,
        IReadOnlyList<double>? beatGridSeconds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || bpm <= 0) return null;

        try
        {
            var candidates = await ResolveCandidatesAsync(audioFilePath, ct).ConfigureAwait(false);
            if (candidates == null) return null;

            // Try each analysis file that claims this source path (.EXT and/or .2EX) until one
            // actually has PSSI data — not every analysis file for a track carries phrase data
            // even when the source path matches (phrase analysis is a separate, optional step in
            // Rekordbox from basic beatgrid/waveform analysis).
            foreach (var extPath in candidates)
            {
                var bytes = await File.ReadAllBytesAsync(extPath, ct).ConfigureAwait(false);
                var parsed = RekordboxAnlzParser.TryParse(bytes);
                if (parsed?.Phrases is not { Count: > 0 }) continue;

                var segments = ConvertToPhraseSegments(parsed.Mood, parsed.Phrases, bpm, downbeatAnchor, beatGridSeconds);
                if (segments.Count == 0) continue;

                _logger.LogInformation(
                    "[RekordboxPssi] Found {Count} phrase segments from Rekordbox analysis for {Path}",
                    segments.Count, audioFilePath);
                return segments;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RekordboxPssi] Lookup failed for {Path}, continuing without it", audioFilePath);
            return null;
        }
    }

    public async Task<IReadOnlyList<RekordboxCuePoint>?> GetSavedCuePointsAsync(string audioFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath)) return null;

        try
        {
            var candidates = await ResolveCandidatesAsync(audioFilePath, ct).ConfigureAwait(false);
            if (candidates == null) return null;

            foreach (var extPath in candidates)
            {
                var bytes = await File.ReadAllBytesAsync(extPath, ct).ConfigureAwait(false);
                var parsed = RekordboxAnlzParser.TryParse(bytes);
                if (parsed == null) continue;
                // Even zero saved cues is a meaningful, real answer (most tracks have none) — return
                // it rather than falling through to the next candidate file.
                return parsed.CuePoints;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RekordboxPssi] Cue lookup failed for {Path}, continuing without it", audioFilePath);
            return null;
        }
    }

    public async Task<IReadOnlyList<RekordboxCuedTrack>> FindTracksWithSavedCuesAsync(CancellationToken ct = default)
    {
        var results = new List<RekordboxCuedTrack>();
        var roots = RekordboxAnlzLocator.FindRoots();
        if (roots.Count == 0) return results;

        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var extFile in Directory.EnumerateFiles(root, "*.EXT", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var parsed = RekordboxAnlzParser.TryParse(File.ReadAllBytes(extFile));
                        if (parsed?.SourcePath is { Length: > 0 } sourcePath && parsed.CuePoints.Count > 0)
                        {
                            results.Add(new RekordboxCuedTrack(sourcePath, extFile, parsed.CuePoints.Count));
                        }
                    }
                    catch
                    {
                        // One unreadable/malformed file must not abort the scan.
                    }
                }
            }
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("[RekordboxPssi] Found {Count} track(s) with saved Rekordbox cue points", results.Count);
        return results;
    }

    /// <summary>
    /// Resolves an ORBIT audio file path to its matching Rekordbox .EXT file(s), by exact resolved
    /// path first, then by filename (see <see cref="_filenameFallbackIndex"/>'s doc comment for
    /// why the fallback is needed). Returns null if no candidate is found at all — shared by
    /// <see cref="AnalyzeAsync"/> and <see cref="GetSavedCuePointsAsync"/> since both key off the
    /// same PPTH-built index.
    /// </summary>
    private async Task<List<string>?> ResolveCandidatesAsync(string audioFilePath, CancellationToken ct)
    {
        await EnsureIndexBuiltAsync(ct).ConfigureAwait(false);

        string normalized = Path.GetFullPath(audioFilePath);
        if (_pathIndex.TryGetValue(normalized, out var candidates) && candidates.Count > 0)
            return candidates;

        string fileName = Path.GetFileName(audioFilePath);
        if (!string.IsNullOrEmpty(fileName) &&
            _filenameFallbackIndex.TryGetValue(fileName, out candidates) && candidates.Count > 0)
            return candidates;

        return null;
    }

    // ── Phrase kind -> ORBIT label mapping ──────────────────────────────────
    // Rekordbox's own vocabulary varies by "mood" (1=high/EDM, 2=mid, 3=low). Only kinds with an
    // obvious structural analog to ORBIT's Intro/Build/Drop/Breakdown/Outro vocabulary are mapped;
    // unmapped kinds (e.g. mid/low's numbered Verses) are simply omitted rather than guessed at —
    // CueGenerationService's Path 1 already tolerates partial label coverage.
    private static readonly Dictionary<int, string> HighMoodLabels = new()
    {
        [1] = "Intro", [2] = "Build" /* "Up" */, [3] = "Breakdown" /* "Down" */, [5] = "Drop" /* "Chorus" */, [6] = "Outro",
    };
    private static readonly Dictionary<int, string> MidLowMoodLabels = new()
    {
        [1] = "Intro", [8] = "Breakdown" /* "Bridge" */, [9] = "Drop" /* "Chorus" */, [10] = "Outro",
    };

    // Internal (not private) so RekordboxPssiServiceTests can verify the beat-grid-vs-constant-BPM
    // conversion logic directly, without needing to fake a real USBANLZ folder on disk.
    internal static List<PhraseSegment> ConvertToPhraseSegments(
        int mood, IReadOnlyList<RekordboxPhraseEntry> phrases, double bpm, double downbeatAnchor,
        IReadOnlyList<double>? beatGridSeconds)
    {
        var labels = mood == 1 ? HighMoodLabels : MidLowMoodLabels;
        double beatDuration = 60.0 / bpm;
        bool hasGrid = beatGridSeconds is { Count: > 0 };

        // Prefer the real per-beat timestamp at this index (ORBIT's own detected beat grid) over
        // constant-BPM extrapolation from the downbeat anchor — a fixed-BPM formula silently
        // drifts on any track with genuine tempo variation, while the real grid doesn't. Falls
        // back to the constant-BPM formula for any beat index beyond the grid's length (Rekordbox
        // and ORBIT's beat trackers can disagree slightly on total beat count near the track end).
        double BeatToSeconds(int beat)
        {
            int idx = Math.Max(0, beat - 1);
            if (hasGrid && idx < beatGridSeconds!.Count) return beatGridSeconds[idx];
            return downbeatAnchor + idx * beatDuration;
        }

        var ordered = phrases.OrderBy(p => p.Beat).ToList();
        var result = new List<PhraseSegment>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!labels.TryGetValue(ordered[i].Kind, out var label)) continue;

            double start = BeatToSeconds(ordered[i].Beat);
            double next = i + 1 < ordered.Count
                ? BeatToSeconds(ordered[i + 1].Beat)
                : start + beatDuration * 32; // last segment: default to an 8-bar span

            result.Add(new PhraseSegment
            {
                Label = label,
                Start = (float)start,
                Duration = (float)Math.Max(0, next - start),
                Confidence = 0.9f, // Rekordbox's own commercial analysis — treated on par with EDMFormer
            });
        }
        return result;
    }

    // ── Discovery: locate USBANLZ root, index by PPTH ───────────────────────

    private async Task EnsureIndexBuiltAsync(CancellationToken ct)
    {
        if (_indexBuilt) return;

        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_indexBuilt) return;

            var roots = RekordboxAnlzLocator.FindRoots();
            if (roots.Count == 0) { _indexBuilt = true; return; }

            await Task.Run(() =>
            {
                foreach (var root in roots)
                    ScanRoot(root, ct);
            }, ct).ConfigureAwait(false);
            _indexBuilt = true;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private void ScanRoot(string root, CancellationToken ct)
    {
        int scanned = 0, matched = 0;
        try
        {
            // PSSI lives exclusively in .EXT — confirmed against 15 real .2EX files, all of which
            // carry only PWV6/7/C (3-band preview waveform) and occasionally PVDI (vocal detection)
            // tags, never PSSI. .DAT is skipped for the same reason: beatgrid/waveform/basic cue
            // data only.
            var candidateFiles = Directory.EnumerateFiles(root, "*.EXT", SearchOption.AllDirectories);
            foreach (var extFile in candidateFiles)
            {
                if (ct.IsCancellationRequested) return;
                scanned++;
                try
                {
                    var bytes = File.ReadAllBytes(extFile);
                    var parsed = RekordboxAnlzParser.TryParse(bytes);
                    if (parsed?.SourcePath is { Length: > 0 } sourcePath)
                    {
                        var normalized = Path.GetFullPath(sourcePath);
                        _pathIndex.AddOrUpdate(normalized,
                            _ => new List<string> { extFile },
                            (_, list) => { lock (list) { list.Add(extFile); } return list; });

                        // Rekordbox sometimes can't resolve the drive/root for a track's PPTH entry
                        // and stores it as e.g. "?/02 - Move Your Body.flac" instead of a real
                        // absolute path (confirmed against real files — see _filenameFallbackIndex's
                        // doc comment). That degraded form is detectable as "doesn't look like a
                        // real rooted path", so index it by filename too for the fallback lookup.
                        if (!Path.IsPathRooted(sourcePath))
                        {
                            var fileName = Path.GetFileName(sourcePath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                _filenameFallbackIndex.AddOrUpdate(fileName,
                                    _ => new List<string> { extFile },
                                    (_, list) => { lock (list) { list.Add(extFile); } return list; });
                            }
                        }
                        matched++;
                    }
                }
                catch
                {
                    // One unreadable/malformed file must not abort indexing the rest.
                }
            }
            _logger.LogInformation(
                "[RekordboxPssi] Indexed {Matched}/{Scanned} analysed tracks from {Root}", matched, scanned, root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RekordboxPssi] Failed to scan {Root}", root);
        }
    }

}
