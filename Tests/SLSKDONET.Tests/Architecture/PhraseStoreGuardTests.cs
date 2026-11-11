using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Issue #225: TrackPhrases is the single phrase store (User > RekordboxPSSI > Heuristic). The legacy
/// <c>AudioFeaturesEntity.PhraseSegmentsJson</c> column never saw the user's phrase-strip edits and could
/// hold boundaries from an older, wrong-grid analysis, so production code must not <b>read</b> it any more.
/// Writes (kept for older builds) and the one-off migration that moved it into TrackPhrases are allowed.
/// </summary>
public class PhraseStoreGuardTests
{
    private static readonly string[] ScannedDirs =
        { "Services", "ViewModels", "Views", "Engine", "Data", "Database", "Models", "Utils", "Configuration" };

    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data/Entities/AudioFeaturesEntity.cs",                 // the column itself
        "Services/Data/SchemaMigratorService.cs",               // schema
        "Services/AudioAnalysis/PhraseBarMigrationService.cs",  // the migration into TrackPhrases
    };

    private static readonly Regex Assignment = new(@"PhraseSegmentsJson\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void NoProductionCode_ReadsTheLegacyPhraseColumn()
    {
        var root = RepoGuard.Root;
        var offenders = new List<string>();
        foreach (var file in ScannedDirs.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                     .SelectMany(d => RepoGuard.EnumerateFiles(d, "*.cs", skipTestData: true))
                     .Where(f => !RepoGuard.IsLegacy(f)))
        {
            var rel = RepoGuard.Rel(file);
            if (Allowlist.Contains(rel)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("PhraseSegmentsJson", StringComparison.Ordinal)) continue;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                if (Assignment.IsMatch(line)) continue;
                offenders.Add($"{rel}:{i + 1}: {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Read TrackPhrases (PhraseSourceResolver.Resolve) instead of the legacy PhraseSegmentsJson column:\n  " +
            string.Join("\n  ", offenders));
    }
}
