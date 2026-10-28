using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// Centralizes the "does this track have a separated-stems folder on disk" check that both
/// <see cref="SLSKDONET.ViewModels.PlaylistTrackViewModel"/> and
/// <see cref="SLSKDONET.ViewModels.Downloads.UnifiedTrackViewModel"/> need for their HasStems
/// badge. Previously each row's property getter fired its own independent Task.Run doing raw
/// Directory.Exists/Directory.GetFiles calls — on a library grid with thousands of completed
/// tracks, realizing (or re-realizing on scroll) many rows at once could queue thousands of
/// concurrent disk probes, saturating the thread pool. This bounds concurrency and caches
/// results briefly so repeated row realizations don't re-hit the disk.
/// </summary>
public static class StemAvailabilityProbe
{
    private const int MaxConcurrentProbes = 4;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim Gate = new(MaxConcurrentProbes, MaxConcurrentProbes);
    private static readonly ConcurrentDictionary<string, (bool Found, DateTime CachedAtUtc)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> HasStemsAsync(string? resolvedFilePath)
    {
        if (string.IsNullOrEmpty(resolvedFilePath)) return false;

        if (Cache.TryGetValue(resolvedFilePath, out var cached) && DateTime.UtcNow - cached.CachedAtUtc < CacheTtl)
        {
            return cached.Found;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Another caller may have already populated this while we were waiting on the gate.
            if (Cache.TryGetValue(resolvedFilePath, out cached) && DateTime.UtcNow - cached.CachedAtUtc < CacheTtl)
            {
                return cached.Found;
            }

            var found = await Task.Run(() => ProbeDisk(resolvedFilePath)).ConfigureAwait(false);
            Cache[resolvedFilePath] = (found, DateTime.UtcNow);
            return found;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool ProbeDisk(string resolvedFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(resolvedFilePath);
            var name = Path.GetFileNameWithoutExtension(resolvedFilePath);
            if (string.IsNullOrEmpty(dir)) return false;

            // Strategy A: /Music/Techno/Track.mp3 -> /Music/Techno/Stems/Track/
            var stemPathA = Path.Combine(dir, "Stems", name);
            // Strategy B: /Music/Techno/Track.mp3 -> /Music/Techno/Track_Stems/
            var stemPathB = Path.Combine(dir, $"{name}_Stems");
            // Strategy C: /Music/Techno/_stems/ (legacy)
            var stemPathC = Path.Combine(dir, "_stems");

            return HasFiles(stemPathA) || HasFiles(stemPathB) || HasFiles(stemPathC);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasFiles(string path) => Directory.Exists(path) && Directory.GetFiles(path).Length > 0;
}
