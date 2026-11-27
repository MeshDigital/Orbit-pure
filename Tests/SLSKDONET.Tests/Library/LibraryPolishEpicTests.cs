using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Services.Playlist;
using Xunit;

namespace SLSKDONET.Tests.Library;

/// <summary>
/// Regression tests for Epic #550 (Library polish):
/// - LP1 (#552): Tag cleanup rule pack + selection-level shared-text pass
/// - LP2 (#553): Bulk relocate folder (re-point mode on FileRelocationService + undo)
/// - LP3 (#554): Playlist comparison, plain-text tracklist export, RFC 4180 CSV export
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class LibraryPolishEpicTests
{
    private sealed class SimpleDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();
    }

    // ── LP1 (#552): Tag cleanup rule pack ────────────────────────────────────

    [Fact]
    public void TagIssueDetector_CleanupPack_HandlesUrlsPromoEncodedGarbageMixParensRemixerAndCasing()
    {
        var emptyIndex = new LibraryIdentityIndex(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // URLs + PromoText + EncodedText
        var p1 = TagIssueDetector.Detect(
            new TagIdentity("h1", "Calibre (site.net)", "Even%20If &amp; Always [www.music.com] (Free Download)", null, null, null),
            emptyIndex,
            new TagIssueOptions());
        Assert.Single(p1);
        Assert.Equal(ProposalConfidence.High, p1[0].Confidence);
        Assert.True(p1[0].DefaultSelected);
        Assert.Equal("Calibre", p1[0].Changes.Single(c => c.Field == TagField.Artist).Value);
        Assert.Equal("Even If & Always", p1[0].Changes.Single(c => c.Field == TagField.Title).Value);

        // Mojibake -> NeedsReview and NOT preselected
        var pMojibake = TagIssueDetector.Detect(
            new TagIdentity("h2", "Caf\u00C3\u00A9", "Track", null, null, null),
            emptyIndex,
            new TagIssueOptions());
        Assert.Single(pMojibake);
        Assert.Equal(ProposalConfidence.NeedsReview, pMojibake[0].Confidence);
        Assert.False(pMojibake[0].DefaultSelected);
        Assert.Equal("Caf\u00E9", pMojibake[0].Changes.Single(c => c.Field == TagField.Artist).Value);

        // GarbageChars (zero-width + repeated separators + stray edge dash)
        var pGarbage = TagIssueDetector.Detect(
            new TagIdentity("h3", "Artist", "--Track\u200B  Name--", null, null, null),
            emptyIndex,
            new TagIssueOptions());
        Assert.Single(pGarbage);
        Assert.Equal("Track Name", pGarbage[0].Changes.Single(c => c.Field == TagField.Title).Value);

        // MixParentheses + RemixerField (version text stays in Title; remixer copied to TagField.Remixer)
        var pMix = TagIssueDetector.Detect(
            new TagIdentity("h4", "Four Tet", "Glue - Bicep Remix", null, null, null, Remixer: null),
            emptyIndex,
            new TagIssueOptions());
        Assert.Single(pMix);
        Assert.Equal("Glue (Bicep Remix)", pMix[0].Changes.Single(c => c.Field == TagField.Title).Value);
        Assert.Equal("Bicep", pMix[0].Changes.Single(c => c.Field == TagField.Remixer).Value);

        // Casing (opt-in, preserves acronyms)
        var pCaseOff = TagIssueDetector.Detect(
            new TagIdentity("h5", "dj shadow", "midnight vip", null, null, null),
            emptyIndex,
            new TagIssueOptions(EnableCasing: false));
        Assert.Empty(pCaseOff);

        var pCaseOn = TagIssueDetector.Detect(
            new TagIdentity("h5", "dj shadow", "midnight vip", null, null, null),
            emptyIndex,
            new TagIssueOptions(EnableCasing: true));
        Assert.Single(pCaseOn);
        Assert.Equal("DJ Shadow", pCaseOn[0].Changes.Single(c => c.Field == TagField.Artist).Value);
        Assert.Equal("Midnight VIP", pCaseOn[0].Changes.Single(c => c.Field == TagField.Title).Value);
    }

    [Fact]
    public void TagIssueDetector_DetectSharedText_ProposesOnlyWhenAll3PlusTracksShareText_AndNeverPreselects()
    {
        var tracks = new[]
        {
            new TagIdentity("h1", "Calibre", "[PROMO] Even If", null, null, null),
            new TagIdentity("h2", "Noisia", "[PROMO] Stigma", null, null, null),
            new TagIdentity("h3", "Bicep", "[PROMO] Glue", null, null, null),
        };

        var proposals = TagIssueDetector.DetectSharedText(tracks);
        Assert.Equal(3, proposals.Count);
        Assert.All(proposals, p =>
        {
            Assert.Equal(TagIssueRule.SharedText.ToString(), p.Rule);
            Assert.Equal(ProposalConfidence.NeedsReview, p.Confidence);
            Assert.False(p.DefaultSelected);
        });
        Assert.Equal("Even If", proposals[0].Changes.Single().Value);

        // Fewer than 3 tracks -> no proposal
        Assert.Empty(TagIssueDetector.DetectSharedText(tracks.Take(2).ToList()));
    }

    // ── LP2 (#553): Bulk relocate folder (re-point mode + undo) ──────────────

    [Fact]
    public async Task FileRelocationService_RepointFolder_UpdatesAll3TablesWithoutMovingFiles_AndUndoRestoresPaths()
    {
        using var scope = new FreshDatabaseScope();
        IDbContextFactory<AppDbContext> factory = new SimpleDbContextFactory();

        var tempDir = Path.Combine(Path.GetTempPath(), $"orbit-lp2-{Guid.NewGuid():N}");
        var subDir = Path.Combine(tempDir, "DnB");
        Directory.CreateDirectory(subDir);

        try
        {
            var newFile1 = Path.Combine(subDir, "track1.flac");
            await File.WriteAllTextAsync(newFile1, "dummy-audio");

            // Track 1 has a missing old POSIX/Windows path whose remapped target EXISTS on disk
            // Track 2 has a missing old path whose remapped target does NOT exist on disk
            var oldPath1 = @"D:\OldMusic\DnB\track1.flac";
            var oldPath2 = @"D:\OldMusic\DnB\still-missing.flac";

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();

                db.LibraryEntries.AddRange(
                    new LibraryEntryEntity { UniqueHash = "rp1", Artist = "A", Title = "T1", FilePath = oldPath1, PathLockedAt = DateTime.UtcNow },
                    new LibraryEntryEntity { UniqueHash = "rp2", Artist = "B", Title = "T2", FilePath = oldPath2 });

                db.PlaylistTracks.AddRange(
                    new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = Guid.NewGuid(), TrackUniqueHash = "rp1", Artist = "A", Title = "T1", ResolvedFilePath = oldPath1 },
                    new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = Guid.NewGuid(), TrackUniqueHash = "rp2", Artist = "B", Title = "T2", ResolvedFilePath = oldPath2 });

                db.Tracks.AddRange(
                    new TrackEntity { GlobalId = "rp1", Artist = "A", Title = "T1", LocalFilePath = oldPath1 },
                    new TrackEntity { GlobalId = "rp2", Artist = "B", Title = "T2", LocalFilePath = oldPath2 });

                await db.SaveChangesAsync();
            }

            var eventBus = new Mock<IEventBus>();
            var svc = new FileRelocationService(factory, eventBus.Object, NullLogger<FileRelocationService>.Instance);

            // Cross-separator prefix remap (POSIX forward slashes matching Windows backslashes)
            Assert.True(FileRelocationService.TryRemapPrefix(oldPath1, "D:/OldMusic", tempDir, out var remapped));
            Assert.Equal(newFile1, remapped);

            // Preview
            var preview = await svc.PlanRepointFolderAsync("D:/OldMusic", tempDir);
            Assert.Equal(2, preview.TotalMissingInLibrary);
            Assert.Equal(1, preview.FoundCount);
            Assert.Equal(1, preview.StillMissingCount);
            Assert.Equal(1, preview.PathLockedCount);
            Assert.NotNull(preview.WarningMessage);

            // Execute re-point
            var outcome = await svc.ExecuteRepointAsync(preview.Plan);
            Assert.Equal(1, outcome.Moved);
            Assert.Empty(outcome.Failures);
            Assert.True(File.Exists(newFile1)); // file stayed in place on disk

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(newFile1, (await db.LibraryEntries.SingleAsync(e => e.UniqueHash == "rp1")).FilePath);
                Assert.Equal(newFile1, (await db.PlaylistTracks.SingleAsync(t => t.TrackUniqueHash == "rp1")).ResolvedFilePath);
                Assert.Equal(newFile1, (await db.Tracks.SingleAsync(t => t.GlobalId == "rp1")).LocalFilePath);

                // Still-missing row left untouched
                Assert.Equal(oldPath2, (await db.LibraryEntries.SingleAsync(e => e.UniqueHash == "rp2")).FilePath);
            }

            // Undo via journal batch restores all 3 path columns without moving/deleting the file
            var reverted = await svc.RevertBatchAsync(outcome.BatchId);
            Assert.Equal(1, reverted);
            Assert.True(File.Exists(newFile1)); // still on disk!

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(oldPath1, (await db.LibraryEntries.SingleAsync(e => e.UniqueHash == "rp1")).FilePath);
                Assert.Equal(oldPath1, (await db.PlaylistTracks.SingleAsync(t => t.TrackUniqueHash == "rp1")).ResolvedFilePath);
                Assert.Equal(oldPath1, (await db.Tracks.SingleAsync(t => t.GlobalId == "rp1")).LocalFilePath);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── LP3 (#554): Compare two playlists + tracklist text & CSV export ──────

    [Fact]
    public void PlaylistToolsService_Compare_HandlesOverlappingAndDisjointPlaylists()
    {
        var a = new[]
        {
            new PlaylistTrack { TrackUniqueHash = "h1", Artist = "A1", Title = "Shared" },
            new PlaylistTrack { TrackUniqueHash = "h2", Artist = "A2", Title = "Only A" },
        };
        var b = new[]
        {
            new PlaylistTrack { TrackUniqueHash = "h1", Artist = "A1", Title = "Shared" },
            new PlaylistTrack { TrackUniqueHash = "h3", Artist = "A3", Title = "Only B" },
        };

        var overlap = PlaylistToolsService.Compare("Set A", a, "Set B", b);
        Assert.Equal(new[] { "h1" }, overlap.InBoth.Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "h2" }, overlap.OnlyInA.Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "h3" }, overlap.OnlyInB.Select(t => t.TrackUniqueHash));

        var disjoint = PlaylistToolsService.Compare("A", new[] { a[1] }, "B", new[] { b[1] });
        Assert.Empty(disjoint.InBoth);
        Assert.Single(disjoint.OnlyInA);
        Assert.Single(disjoint.OnlyInB);
    }

    [Fact]
    public void PlaylistToolsService_FormatTracklist_AndFormatCsv_HandleMissingMetadataAndEscaping()
    {
        var tracks = new[]
        {
            new PlaylistTrack { Artist = "Calibre", Title = "Even If", BPM = 174.0, MusicalKey = "8A", Format = "flac", Bitrate = 1000 },
            new PlaylistTrack { Artist = "Bicep", Title = "Glue, \"Extended\"", BPM = null, MusicalKey = "11A" },
            new PlaylistTrack { Artist = "Burial", Title = "Archangel", BPM = null, MusicalKey = null },
        };

        var text = PlaylistToolsService.FormatTracklist(tracks, includeBpmAndKey: true);
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Assert.Equal(3, lines.Length);
        Assert.Equal("01. Calibre - Even If (174 BPM \u00B7 8A)", lines[0]);
        Assert.Equal("02. Bicep - Glue, \"Extended\" (11A)", lines[1]);
        Assert.Equal("03. Burial - Archangel", lines[2]);

        var csv = PlaylistToolsService.FormatCsv(tracks);
        Assert.Contains("\"Glue, \"\"Extended\"\"\"", csv);
        Assert.StartsWith("#,Artist,Title,Album,BPM,Key,Duration,Format,Bitrate,FilePath", csv);
    }
}
