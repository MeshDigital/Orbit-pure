using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.Enrichment;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// The <see cref="TagIssueRule.GenreConflict"/> rule (GA9 #254): proposes — never auto-applies — the audio genre as
/// the library genre when the two strongly disagree. Fires when the metadata-first resolver flagged a conflict (a
/// non-user tag against strong audio the drums back, GA8), or when the stored genre is junk ("Electronic", "(52)")
/// and the audio is confident. Never fires against a genre the user set: the user is always right. Empty genres are
/// GenreFill's job, not this rule's. Every proposal starts unticked (<see cref="ProposalConfidence.NeedsReview"/>) and
/// is applied, reversibly, through <see cref="TagChangeService"/>.
/// </summary>
public static class GenreConflictRule
{
    public static readonly string RuleName = nameof(TagIssueRule.GenreConflict);

    /// <summary>The audio facts of the evidence row the rule needs (pulled out of SQLite with json_extract).</summary>
    public sealed class EvidenceRow
    {
        public string Hash { get; set; } = string.Empty;
        public string? Conflict { get; set; }
        public string? AudioLabel { get; set; }
        public double? AudioConfidence { get; set; }
    }

    /// <summary>The proposal for one track, or null when the rule doesn't fire.</summary>
    public static ProposedTagChange? Propose(string hash, string? libraryGenres, string? genreSource,
        string? conflict, string? audioLabel, double? audioConfidence, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(libraryGenres)) return null; // empty: GenreFill proposes
        if (string.Equals(genreSource?.Trim(), MetadataMerger.UserSource, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(audioLabel)) return null;

        var label = audioLabel.Trim();
        // The audio label must itself be a usable genre ("Other" or "Electronic" would only swap one junk for another).
        if (GenreTaxonomy.IsJunkGenre(label)) return null;
        if (string.Equals(label, libraryGenres.Trim(), StringComparison.OrdinalIgnoreCase)) return null;

        var confidence = audioConfidence ?? 0;
        string evidence;
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            evidence = conflict.Trim();
        }
        else if (GenreTaxonomy.MapFreeTextAll(libraryGenres).Count == 0 && confidence >= TrackGenreResolver.StrongAudioConfidence)
        {
            evidence = SLSKDONET.Utils.NumFormat.Ui($"Genre \"{libraryGenres.Trim()}\" says nothing; the audio sounds like {label} ({confidence:P0})");
        }
        else
        {
            return null;
        }

        return new ProposedTagChange(
            Hash: hash,
            Rule: RuleName,
            Changes: new[] { new TagPatchField(TagField.Genre, label) },
            Confidence: ProposalConfidence.NeedsReview,
            Evidence: evidence,
            DefaultSelected: false,
            FilePath: filePath);
    }

    /// <summary>Every proposal across the library: projected queries, the evidence read with json_extract.</summary>
    public static async Task<IReadOnlyList<ProposedTagChange>> ScanAsync(AppDbContext db, CancellationToken ct = default)
    {
        var evidence = await db.Database.SqlQueryRaw<EvidenceRow>(@"
            SELECT ""TrackUniqueHash"" AS ""Hash"",
                   json_extract(""GenreEvidenceJson"", '$.Conflict') AS ""Conflict"",
                   json_extract(""GenreEvidenceJson"", '$.AudioLabel') AS ""AudioLabel"",
                   json_extract(""GenreEvidenceJson"", '$.AudioConfidence') AS ""AudioConfidence""
            FROM ""audio_features""
            WHERE COALESCE(json_valid(""GenreEvidenceJson""), 0) = 1
              AND json_extract(""GenreEvidenceJson"", '$.AudioLabel') IS NOT NULL").ToListAsync(ct).ConfigureAwait(false);
        if (evidence.Count == 0) return Array.Empty<ProposedTagChange>();
        var byHash = new Dictionary<string, EvidenceRow>(StringComparer.Ordinal);
        foreach (var row in evidence) byHash.TryAdd(row.Hash, row);

        var library = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.Genres != null && e.Genres != "")
            .Select(e => new { e.UniqueHash, e.Genres, e.FilePath })
            .ToListAsync(ct).ConfigureAwait(false);
        const string genreField = MetadataMerger.Fields.Genre;
        var sources = await db.MetadataProvenance.AsNoTracking()
            .Where(p => p.Field == genreField)
            .Select(p => new { p.TrackUniqueHash, p.Source })
            .ToListAsync(ct).ConfigureAwait(false);
        var sourceByHash = sources.GroupBy(s => s.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First().Source, StringComparer.Ordinal);

        var proposals = new List<ProposedTagChange>();
        foreach (var entry in library)
        {
            if (!byHash.TryGetValue(entry.UniqueHash, out var row)) continue;
            var proposal = Propose(entry.UniqueHash, entry.Genres, sourceByHash.GetValueOrDefault(entry.UniqueHash),
                row.Conflict, row.AudioLabel, row.AudioConfidence, entry.FilePath);
            if (proposal is not null) proposals.Add(proposal);
        }
        return proposals;
    }
}
