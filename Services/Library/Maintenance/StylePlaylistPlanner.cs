using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>One track's style classification, as the planner sees it.</summary>
public sealed record StyleCandidate(string Hash, string? TopStyleKey, float TopStyleConfidence, bool HasGenre);

/// <summary>A playlist the planner wants to create or extend, grouped by top style.</summary>
public sealed record PlannedStylePlaylist(
    string StyleKey,
    string DisplayName,
    IReadOnlyList<string> Hashes,
    Guid? ExistingPlaylistId);

/// <summary>
/// The deterministic grouping result. <see cref="BelowThreshold"/> and <see cref="MissingStyles"/>
/// count tracks; <see cref="TooSmallGroups"/> counts groups that were too small to keep.
/// </summary>
public sealed record StylePlan(
    IReadOnlyList<PlannedStylePlaylist> Playlists,
    int BelowThreshold,
    int TooSmallGroups,
    int MissingStyles);

public sealed record StylePlanOptions(double MinConfidence = 0.35, int MinGroupSize = 5);

/// <summary>
/// Pure planner that groups unclassified tracks by their top Discogs style (explainable and
/// deterministic — not embedding k-means). No I/O.
/// </summary>
public static class StylePlaylistPlanner
{
    /// <summary>Genre-fill proposals use this confidence threshold for High vs Medium.</summary>
    public const double HighGenreConfidence = 0.6;

    public static StylePlan Plan(
        IEnumerable<StyleCandidate> candidates,
        IReadOnlyDictionary<string, Guid> existingStylePlaylists,
        StylePlanOptions options)
    {
        var list = candidates.ToList();
        var groups = new Dictionary<string, (string Name, string Parent, List<(string Hash, float Conf)> Tracks)>(StringComparer.Ordinal);
        int belowThreshold = 0, missingStyles = 0;

        foreach (var c in list)
        {
            if (c.HasGenre) continue;                          // already classified → not "unclassified"
            if (string.IsNullOrWhiteSpace(c.TopStyleKey)) { missingStyles++; continue; }
            if (c.TopStyleConfidence < options.MinConfidence) { belowThreshold++; continue; }

            var (parent, name) = SplitKey(c.TopStyleKey);
            if (!groups.TryGetValue(c.TopStyleKey, out var g))
                groups[c.TopStyleKey] = g = (name, parent, new List<(string, float)>());
            g.Tracks.Add((c.Hash, c.TopStyleConfidence));
        }

        var playlists = new List<PlannedStylePlaylist>();
        int tooSmallGroups = 0;
        foreach (var kv in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var (name, parent, tracks) = kv.Value;
            if (tracks.Count < options.MinGroupSize)
            {
                tooSmallGroups++;
                continue;
            }

            var ordered = tracks
                .OrderByDescending(t => t.Conf)
                .ThenBy(t => t.Hash, StringComparer.Ordinal)
                .Select(t => t.Hash)
                .ToList();

            var displayName = HasParentCollision(name, parent, groups)
                ? $"Style · {name} ({parent})"
                : $"Style · {name}";

            var existing = existingStylePlaylists.TryGetValue(kv.Key, out var pid) ? pid : (Guid?)null;
            playlists.Add(new PlannedStylePlaylist(kv.Key, displayName, ordered, existing));
        }

        return new StylePlan(playlists, belowThreshold, tooSmallGroups, missingStyles);
    }

    /// <summary>
    /// Turns unclassified tracks with a confident top style into <c>GenreFill</c> proposals (name
    /// only, no parent prefix). The wizard resolves each hash's file path before applying, so these
    /// go through the same file + DB, undoable path as tag fixes.
    /// </summary>
    public static IReadOnlyList<ProposedTagChange> GenreFillProposals(IEnumerable<StyleCandidate> candidates)
    {
        var result = new List<ProposedTagChange>();
        foreach (var c in candidates)
        {
            if (c.HasGenre || string.IsNullOrWhiteSpace(c.TopStyleKey)) continue;
            var (_, name) = SplitKey(c.TopStyleKey);
            var confidence = c.TopStyleConfidence >= HighGenreConfidence
                ? ProposalConfidence.High
                : ProposalConfidence.Medium;

            result.Add(new ProposedTagChange(
                Hash: c.Hash,
                Rule: "GenreFill",
                Changes: new[] { new TagPatchField(TagField.Genre, name) },
                Confidence: confidence,
                Evidence: $"Top style {c.TopStyleKey} at {c.TopStyleConfidence:P0}",
                DefaultSelected: confidence == ProposalConfidence.High,
                FilePath: null));
        }
        return result;
    }

    /// <summary>Splits a <c>Parent---Name</c> key. A key without the separator treats the whole key as the name.</summary>
    public static (string Parent, string Name) SplitKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return (string.Empty, string.Empty);
        var sep = key.IndexOf("---", StringComparison.Ordinal);
        return sep <= 0 || sep + 3 >= key.Length
            ? (string.Empty, key)
            : (key[..sep], key[(sep + 3)..]);
    }

    private static bool HasParentCollision(
        string name,
        string parent,
        IReadOnlyDictionary<string, (string Name, string Parent, List<(string, float)>)> groups) =>
        groups.Values.Any(g => string.Equals(g.Name, name, StringComparison.Ordinal)
                               && !string.Equals(g.Parent, parent, StringComparison.Ordinal));
}
