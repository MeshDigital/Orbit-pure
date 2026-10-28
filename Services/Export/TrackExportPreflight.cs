using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Raised when an export cannot proceed because no tracks are usable, or when a strict exporter
/// requires every requested track to pass validation.
/// </summary>
public sealed class TrackExportValidationException : Exception
{
    public IReadOnlyList<string> Problems { get; }

    public TrackExportValidationException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems;
    }

    private static string BuildMessage(IReadOnlyList<string> problems)
    {
        var lines = problems.Take(20).Select(problem => $" • {problem}").ToList();
        if (problems.Count > lines.Count)
            lines.Add($" • …and {problems.Count - lines.Count} more problem(s).");

        return $"Export blocked: {problems.Count} track(s) need attention before they can be exported."
            + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Checks whether tracks' effective export paths exist, can be opened for reading, and can be
/// decoded by the same PlayableAudio pipeline used by the decks and library preview.
/// </summary>
public static class TrackExportPreflight
{
    public sealed record ScanResult(
        IReadOnlyList<PlaylistTrack> ValidTracks,
        IReadOnlyList<string> Problems);

    public static async Task ValidateAsync(
        IReadOnlyCollection<PlaylistTrack> tracks,
        CancellationToken cancellationToken = default,
        Func<PlaylistTrack, string?>? pathSelector = null)
    {
        var result = await ScanAsync(tracks, cancellationToken, pathSelector);
        if (result.Problems.Count > 0)
            throw new TrackExportValidationException(result.Problems);
    }

    public static async Task<ScanResult> ScanAsync(
        IReadOnlyCollection<PlaylistTrack> tracks,
        CancellationToken cancellationToken = default,
        Func<PlaylistTrack, string?>? pathSelector = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        return await Task.Run(() =>
        {
            var validTracks = new List<PlaylistTrack>();
            var problems = new List<string>();
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pathSelector is null ? track.ResolvedFilePath : pathSelector(track);
                var label = $"{track.Artist ?? "Unknown Artist"} - {track.Title ?? "Unknown Title"}";

                if (string.IsNullOrWhiteSpace(path))
                {
                    problems.Add($"{label}: no usable export file location (not downloaded or copy unavailable).");
                    continue;
                }

                try
                {
                    // Opening and reading the path checks permissions/availability; decoding one
                    // block catches corrupt or unsupported files while allowing PlayableAudio's
                    // existing ffmpeg fallback for supported library formats.
                    using (var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length == 0)
                            throw new InvalidDataException("the file is empty");
                        _ = stream.ReadByte();
                    }

                    using var reader = PlayableAudio.Open(path, out _);
                    var samples = new float[8192];
                    if (reader.Read(samples, 0, samples.Length) <= 0)
                        throw new InvalidDataException("the decoder returned no audio samples");

                    validTracks.Add(track);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"{label} [{path}]: {ex.Message}");
                }
            }

            return new ScanResult(validTracks, problems);
        }, cancellationToken);
    }
}
