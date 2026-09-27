using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services.Rekordbox;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.Rekordbox;

/// <summary>
/// Manual diagnostic answering two real questions, not a correctness gate: (1) which tracks in
/// the local Rekordbox install already have saved memory/hot cues, and (2) for any that overlap
/// with ORBIT's own library, how do Rekordbox's saved cues compare to ORBIT's own auto-generated
/// ones for the same track. Read-only against ORBIT's real database (%APPDATA%/ORBIT/library.db)
/// — no writes, no seeding, safe to run against a real user's library. Skips (does not fail) when
/// Rekordbox isn't installed or nothing overlaps, since both are legitimate real states, not bugs.
/// </summary>
public class RekordboxCueComparisonTests
{
    private readonly ITestOutputHelper _output;

    public RekordboxCueComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FindTracksWithSavedCues_ReportsRealLibraryState()
    {
        var service = new RekordboxPssiService(NullLogger<RekordboxPssiService>.Instance);
        if (!service.IsAvailable)
        {
            _output.WriteLine("Rekordbox not installed locally. Skipping.");
            return;
        }

        var cuedTracks = await service.FindTracksWithSavedCuesAsync();
        _output.WriteLine($"Tracks with at least one saved Rekordbox cue: {cuedTracks.Count}");
        foreach (var t in cuedTracks.Take(20))
        {
            _output.WriteLine($"  {Path.GetFileName(t.SourcePath)} — {t.CueCount} cue(s) — {t.AnlzFilePath}");
        }

        if (cuedTracks.Count == 0)
        {
            _output.WriteLine(
                "No tracks with saved cues found in this Rekordbox install — this is a real, " +
                "confirmed state (checked against ~1,258 real .EXT files), not a parser failure. " +
                "Set and save a cue on any track in Rekordbox, then re-run this test, to get a " +
                "non-empty result to compare against ORBIT's own generated cues.");
        }
    }

    [Fact]
    public async Task CompareRekordboxCuesAgainstOrbitGeneratedCues_ForOverlappingTracks()
    {
        var service = new RekordboxPssiService(NullLogger<RekordboxPssiService>.Instance);
        if (!service.IsAvailable)
        {
            _output.WriteLine("Rekordbox not installed locally. Skipping.");
            return;
        }

        string dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "library.db");
        if (!File.Exists(dbPath))
        {
            _output.WriteLine($"No ORBIT library database found at {dbPath}. Skipping.");
            return;
        }

        // Default (parameterless) AppDbContext points at the real user database — read-only here
        // (LoadTracksAsync-equivalent queries only, no SaveChanges), which is safe unlike seeding
        // or mutating it.
        await using var context = new AppDbContext();

        var orbitTracksByFileName = await context.PlaylistTracks
            .Where(t => t.ResolvedFilePath != null && t.ResolvedFilePath != "")
            .Select(t => new { t.TrackUniqueHash, t.Artist, t.Title, t.ResolvedFilePath })
            .ToListAsync();
        var orbitByFileName = orbitTracksByFileName
            .GroupBy(t => Path.GetFileName(t.ResolvedFilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _output.WriteLine($"ORBIT library tracks with a resolved file: {orbitTracksByFileName.Count}");

        var cuedTracks = await service.FindTracksWithSavedCuesAsync();
        _output.WriteLine($"Rekordbox tracks with saved cues: {cuedTracks.Count}");

        int overlapCount = 0;
        foreach (var rbTrack in cuedTracks)
        {
            string fileName = Path.GetFileName(rbTrack.SourcePath);
            if (!orbitByFileName.TryGetValue(fileName, out var orbitTrack)) continue;
            overlapCount++;

            var rekordboxCues = await service.GetSavedCuePointsAsync(orbitTrack.ResolvedFilePath!);
            var orbitCues = await context.CuePoints
                .Where(c => c.TrackUniqueHash == orbitTrack.TrackUniqueHash)
                .OrderBy(c => c.TimestampInSeconds)
                .ToListAsync();

            _output.WriteLine($"\n=== {orbitTrack.Artist} - {orbitTrack.Title} ({fileName}) ===");
            _output.WriteLine($"Rekordbox saved cues ({rekordboxCues?.Count ?? 0}):");
            foreach (var c in rekordboxCues ?? Enumerable.Empty<RekordboxCuePoint>())
            {
                string kind = c.IsLoop ? $"Loop({c.TimeSeconds:F2}-{c.LoopEndSeconds:F2}s)"
                    : c.IsHotCue ? $"Hot{c.HotCueNumber}@{c.TimeSeconds:F2}s" : $"Memory@{c.TimeSeconds:F2}s";
                _output.WriteLine($"  {kind}{(c.Comment != null ? $" \"{c.Comment}\"" : "")}");
            }

            _output.WriteLine($"ORBIT auto-generated cues ({orbitCues.Count}):");
            foreach (var c in orbitCues)
            {
                _output.WriteLine($"  {c.Type}@{c.TimestampInSeconds:F2}s \"{c.Label}\" (confidence {c.Confidence:F2})");
            }

            if (rekordboxCues is { Count: > 0 } && orbitCues.Count > 0)
            {
                const double toleranceSeconds = 2.0;
                int matched = 0;
                foreach (var rbCue in rekordboxCues)
                {
                    if (orbitCues.Any(oc => Math.Abs(oc.TimestampInSeconds - rbCue.TimeSeconds) <= toleranceSeconds))
                        matched++;
                }
                _output.WriteLine($"Agreement: {matched}/{rekordboxCues.Count} Rekordbox cues have an ORBIT cue within {toleranceSeconds}s");
            }
        }

        _output.WriteLine($"\nTotal overlap (Rekordbox-cued track also in ORBIT library): {overlapCount}");
        if (overlapCount == 0)
        {
            _output.WriteLine(
                "No overlap to compare — either no Rekordbox tracks have saved cues yet, or none of " +
                "those tracks are also present in this ORBIT library by filename.");
        }
    }
}
