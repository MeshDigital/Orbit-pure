using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Services.Matching;

namespace SLSKDONET.Services.Intake;

/// <summary>One library track as Intake compares against it.</summary>
public sealed record IntakeLibraryTrack(
    string TrackHash,
    string FilePath,
    string Artist,
    string Title,
    string Format,
    int Bitrate,
    int? DurationSeconds,
    string? ContentHash);

/// <summary>
/// The library as Intake matches against it (epic #632): first by tag-independent content hash
/// (byte-identical audio), then by artist + title + version with the duration within
/// <see cref="DurationToleranceSeconds"/>. Versions are not duplicates: "Original Mix", "Extended
/// Mix" and "Radio Edit" of one track differ in length, and remixes keep their tag in the
/// normalised title, so neither collapses into the other. Built once per scan, then read-only.
/// </summary>
public sealed class IntakeLibraryIndex
{
    public const int DurationToleranceSeconds = 2;

    private readonly Dictionary<string, IntakeLibraryTrack> _byContentHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IntakeLibraryTrack>> _byKey = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _fileExists;

    public IntakeLibraryIndex(IEnumerable<IntakeLibraryTrack> tracks, Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
        foreach (var t in tracks)
        {
            Count++;
            if (!string.IsNullOrEmpty(t.ContentHash))
                _byContentHash.TryAdd(t.ContentHash, t);
            var key = Key(t.Artist, t.Title);
            if (key == null) continue;
            if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<IntakeLibraryTrack>(1);
            list.Add(t);
        }
    }

    /// <summary>Library tracks in the index.</summary>
    public int Count { get; }

    /// <summary>Library tracks that have a content hash (exact-duplicate checks only reach these).</summary>
    public int HashedCount => _byContentHash.Count;

    /// <summary>Loads every library track that has a file (Ghost entries are search results, not owned files).</summary>
    public static async Task<IntakeLibraryIndex> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.AvailabilityState != SLSKDONET.Models.TrackAvailabilityState.Ghost && e.FilePath != "")
            .Select(e => new IntakeLibraryTrack(
                e.UniqueHash, e.FilePath, e.Artist, e.Title, e.Format, e.Bitrate,
                e.DurationSeconds ?? e.CanonicalDuration, e.AudioContentHash))
            .ToListAsync(ct).ConfigureAwait(false);
        return new IntakeLibraryIndex(rows);
    }

    /// <summary>The library track this file is a copy or another encoding of, or null.</summary>
    public IntakeLibraryMatch? FindMatch(string? contentHash, string? artist, string? title, int durationSeconds)
    {
        if (!string.IsNullOrEmpty(contentHash)
            && _byContentHash.TryGetValue(contentHash, out var exact)
            && _fileExists(exact.FilePath))
            return ToMatch(exact, IntakeMatchKind.ContentHash);

        var key = Key(artist, title);
        if (key == null || !_byKey.TryGetValue(key, out var candidates)) return null;

        var incomingVersion = VersionTag.DerivativeVersionTag(title);
        IntakeLibraryTrack? best = null;
        foreach (var c in candidates)
        {
            if (!SameVersion(incomingVersion, VersionTag.DerivativeVersionTag(c.Title))) continue;
            if (durationSeconds > 0 && c.DurationSeconds is int d && d > 0
                && Math.Abs(d - durationSeconds) > DurationToleranceSeconds) continue;
            if (!_fileExists(c.FilePath)) continue;
            // Several copies already owned: compare against the best one.
            if (best == null || Rank(c) > Rank(best)) best = c;
        }
        return best == null ? null : ToMatch(best, IntakeMatchKind.Metadata);
    }

    /// <summary>Normalised artist|title, or null when either is missing (an untagged file matches nothing by name).</summary>
    internal static string? Key(string? artist, string? title)
    {
        var a = PlaylistDiscoveryService.NormalizeArtist(PlaylistDiscoveryService.PrimaryArtist(artist));
        var t = PlaylistDiscoveryService.NormalizeTitle(title);
        return a.Length == 0 || t.Length == 0 ? null : a + "|" + t;
    }

    private static bool SameVersion(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string? version) =>
        string.IsNullOrWhiteSpace(version) ? string.Empty : version.Trim().ToLowerInvariant();

    private static int Rank(IntakeLibraryTrack t) =>
        Library.Maintenance.ExactDuplicateKeeper.QualityRank(t.Format, t.Bitrate);

    private static IntakeLibraryMatch ToMatch(IntakeLibraryTrack t, IntakeMatchKind kind) =>
        new(t.TrackHash, t.FilePath, t.Format, t.Bitrate, kind);
}
