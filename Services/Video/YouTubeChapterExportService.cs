using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Timeline;

namespace SLSKDONET.Services.Video;

/// <summary>
/// Task 8.4 — Generates a YouTube chapter description file from a
/// <see cref="TimelineSession"/> so that video-export recipients can
/// navigate the mix via chapter markers in the YouTube player.
///
/// Output format (plain text, one chapter per line):
/// <code>
/// 00:00 Intro
/// 01:34 Track title – Artist
/// 05:12 Next Track – Artist
/// …
/// </code>
/// The first chapter MUST start at 00:00 for YouTube to recognise the list.
/// Subsequent chapters use <c>HH:MM:SS</c> or <c>MM:SS</c> depending on duration.
/// </summary>
public sealed class YouTubeChapterExportService
{
    private readonly ILogger<YouTubeChapterExportService> _logger;

    public YouTubeChapterExportService(ILogger<YouTubeChapterExportService> logger)
        => _logger = logger;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a chapter string from all clips across all tracks in the session.
    /// Clips are merged and de-duplicated by start time; they are sorted ascending.
    /// </summary>
    /// <param name="session">The timeline session to export.</param>
    /// <param name="trackMetadata">
    /// Optional lookup: track-unique-hash → (Title, Artist).
    /// When supplied, chapter labels use "Title – Artist"; otherwise the clip's
    /// <see cref="TimelineClip.TrackUniqueHash"/> is used as the label.
    /// </param>
    /// <returns>Plain-text YouTube chapter list.</returns>
    public string BuildChapterText(
        TimelineSession session,
        IReadOnlyDictionary<string, (string Title, string Artist)>? trackMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var chapters = CollectChapters(session, trackMetadata);
        return FormatChapters(chapters);
    }

    /// <summary>
    /// Writes a chapter file to <paramref name="outputPath"/>.
    /// </summary>
    public async Task WriteChapterFileAsync(
        TimelineSession session,
        string outputPath,
        IReadOnlyDictionary<string, (string Title, string Artist)>? trackMetadata = null,
        CancellationToken ct = default)
    {
        var text = BuildChapterText(session, trackMetadata);
        await File.WriteAllTextAsync(outputPath, text, Encoding.UTF8, ct);
        _logger.LogInformation("YouTube chapter file written to {Path} ({Count} chapters)",
            outputPath, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private record ChapterEntry(double SessionSeconds, string Label);

    private IReadOnlyList<ChapterEntry> CollectChapters(
        TimelineSession session,
        IReadOnlyDictionary<string, (string Title, string Artist)>? meta)
    {
        const double DedupWindowSeconds = 0.5; // merge clips that start within 500 ms

        var all = new List<ChapterEntry>();

        foreach (var track in session.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                double startSeconds = session.BeatsToSeconds(clip.StartBeat);

                string label;
                if (meta != null && meta.TryGetValue(clip.TrackUniqueHash, out var m))
                    label = string.IsNullOrWhiteSpace(m.Artist)
                        ? m.Title
                        : $"{m.Title} – {m.Artist}";
                else
                    label = clip.TrackUniqueHash;

                // Skip blank hashes (empty placeholder clips)
                if (string.IsNullOrWhiteSpace(label)) continue;

                all.Add(new ChapterEntry(startSeconds, label));
            }
        }

        // Sort + de-duplicate within dedup window
        var sorted = all.OrderBy(c => c.SessionSeconds).ToList();
        var deduped = new List<ChapterEntry>();
        foreach (var c in sorted)
        {
            if (deduped.Count > 0 &&
                Math.Abs(c.SessionSeconds - deduped[^1].SessionSeconds) < DedupWindowSeconds)
                continue;
            deduped.Add(c);
        }

        // Ensure a 00:00 chapter exists (YouTube requirement)
        if (deduped.Count == 0 || deduped[0].SessionSeconds > DedupWindowSeconds)
            deduped.Insert(0, new ChapterEntry(0.0, "Start"));

        return deduped;
    }

    private static string FormatChapters(IReadOnlyList<ChapterEntry> chapters)
    {
        var sb = new StringBuilder();
        double totalSeconds = chapters.Max(c => c.SessionSeconds);

        foreach (var c in chapters)
        {
            sb.AppendLine($"{FormatTimestamp(c.SessionSeconds, totalSeconds)} {c.Label}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns <c>HH:MM:SS</c> when total duration ≥ 1 hour, else <c>MM:SS</c>.
    /// </summary>
    private static string FormatTimestamp(double seconds, double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return totalSeconds >= 3600
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
