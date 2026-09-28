using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Discovery;

/// <summary>One playlist's saved Discover results.</summary>
public sealed class DiscoveryCacheEntry
{
    public int Version { get; set; } = 1;
    public Guid PlaylistId { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Identity of every track in the playlist when these suggestions were made — compared
    /// with the playlist now to decide whether they're still representative.</summary>
    public List<string> Fingerprint { get; set; } = new();

    public string Summary { get; set; } = string.Empty;
    public List<DiscoveryCandidate> Suggestions { get; set; } = new();

    /// <summary>Suggestions the user hid (✕), by <see cref="DiscoveryCache.SuggestionKey"/>. Kept
    /// across refreshes so they don't come back.</summary>
    public List<string> Dismissed { get; set; } = new();
}

/// <summary>Whether saved suggestions should be re-evaluated, and why.</summary>
public sealed record DiscoveryDrift(bool Refresh, string Reason, int Added, int Removed);

/// <summary>
/// Keeps Discover results on disk per playlist (%APPDATA%\ORBIT\DiscoverCache) so the tab opens
/// instantly and survives restarts, and decides when a playlist has changed enough since to be
/// worth re-evaluating.
/// </summary>
public sealed class DiscoveryCache
{
    /// <summary>Re-evaluate when at least this share of the playlist changed (and <see cref="MinChanged"/> tracks)…</summary>
    public const double ChangedShareThreshold = 0.20;
    public const int MinChanged = 3;
    /// <summary>…or this many tracks changed regardless of playlist size…</summary>
    public const int AbsoluteChangedThreshold = 10;
    /// <summary>…or the results are this old (new releases, chart movement).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _directory;
    private readonly ILogger<DiscoveryCache>? _logger;

    public DiscoveryCache(ILogger<DiscoveryCache>? logger = null, string? directoryOverride = null)
    {
        _logger = logger;
        _directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "DiscoverCache");
    }

    public DiscoveryCacheEntry? Load(Guid playlistId)
    {
        var path = PathFor(playlistId);
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<DiscoveryCacheEntry>(File.ReadAllText(path), Json);
            return entry?.PlaylistId == playlistId ? entry : null;
        }
        catch (Exception ex)
        {
            // A damaged cache file just means "no cache" — it gets rewritten on the next refresh.
            _logger?.LogWarning(ex, "[Discover] Ignoring unreadable cache file {Path}", path);
            return null;
        }
    }

    public void Save(DiscoveryCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(entry.PlaylistId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
            File.Move(temp, path, overwrite: true); // never leave a half-written file behind
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Discover] Could not save cache for {Id}", entry.PlaylistId);
        }
    }

    private string PathFor(Guid playlistId) => Path.Combine(_directory, $"{playlistId:N}.json");

    /// <summary>Compares the playlist when suggestions were made with the playlist now.</summary>
    public static DiscoveryDrift Evaluate(
        IReadOnlyCollection<string> cachedFingerprint,
        IReadOnlyCollection<string> currentFingerprint,
        DateTime createdUtc,
        DateTime nowUtc)
    {
        var before = new HashSet<string>(cachedFingerprint, StringComparer.Ordinal);
        var now = new HashSet<string>(currentFingerprint, StringComparer.Ordinal);
        int added = now.Count(k => !before.Contains(k));
        int removed = before.Count(k => !now.Contains(k));
        int changed = added + removed;
        double share = changed / (double)Math.Max(1, Math.Max(before.Count, now.Count));

        if (before.Count == 0 && now.Count > 0)
            return new DiscoveryDrift(true, "no saved playlist snapshot", added, removed);
        if (changed >= AbsoluteChangedThreshold || (changed >= MinChanged && share >= ChangedShareThreshold))
            return new DiscoveryDrift(true, $"playlist changed (+{added} / −{removed})", added, removed);
        if (nowUtc - createdUtc > MaxAge)
            return new DiscoveryDrift(true, $"suggestions are {(int)(nowUtc - createdUtc).TotalDays} days old", added, removed);
        return new DiscoveryDrift(false, string.Empty, added, removed);
    }

    /// <summary>Stable identity of a suggestion (artist + title incl. version), for dismissals.</summary>
    public static string SuggestionKey(DiscoveryCandidate c) =>
        PlaylistDiscoveryService.NormalizeArtist(PlaylistDiscoveryService.PrimaryArtist(c.Artist)) + "|" +
        PlaylistDiscoveryService.NormalizeTitle(c.SearchTitle);
}
