using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using SLSKDONET.Services.Rekordbox;
using SLSKDONET.Tests.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

/// <summary>#207 (P6.1): Rekordbox play history → PerformanceSession → live transition evidence.</summary>
public sealed class RekordboxHistoryImportTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("rekordbox-history");

    public void Dispose() => _db.Dispose();

    private static RekordboxHistory History(string id, params string?[] paths) =>
        new(id, $"HISTORY {id}", "2026-01-03 10:59:31",
            paths.Select((p, i) => new RekordboxHistoryTrack(i + 1, p, $"T{i}", "A")).ToList());

    [Fact]
    public void Parse_ReadsTheBridgeJson()
    {
        var json = """
            [{"id":"7","name":"HISTORY 2026-01-03","created":"2026-01-03 10:59:31",
              "tracks":[{"trackNo":1,"path":"C:/Music/a.mp3","title":"One","artist":"X"},
                        {"trackNo":2,"path":null,"title":null,"artist":null}]}]
            """;
        var r = RekordboxLibraryService.ParseHistory(json);
        var h = Assert.Single(r.Histories);
        Assert.Equal("7", h.Id);
        Assert.Equal(2, h.Tracks.Count);
        Assert.Equal("C:/Music/a.mp3", h.Tracks[0].Path);
        Assert.Null(h.Tracks[1].Path);
    }

    [Fact]
    public void BuildSession_RecordsBackToBackPairsOnly_NotAcrossGapsOrRepeats()
    {
        var map = new Dictionary<string, string>
        {
            [RekordboxLibraryService.NormalizePath(@"C:\Music\a.mp3")] = "a",
            [RekordboxLibraryService.NormalizePath(@"C:\Music\b.mp3")] = "b",
            [RekordboxLibraryService.NormalizePath(@"C:\Music\c.mp3")] = "c",
        };
        // a → b (pair), b → b (a repeat, not a transition), unknown (gap), c (no pair across the gap), c → a (pair)
        var s = RekordboxHistoryImportService.BuildSession(
            History("1", "C:/Music/a.mp3", "c:/music/B.mp3", "C:/Music/b.mp3", "D:/elsewhere/x.mp3", "C:/Music/c.mp3", "C:/Music/a.mp3"),
            map, out var matched, out var missing);

        Assert.Equal(5, matched);
        Assert.Equal(1, missing);
        var audibles = PerformanceSessionReconciler.ExtractAudibleTransitions(s.Tracks.ToList());
        Assert.Equal(new[] { ("a", "b"), ("c", "a") }, audibles.Select(a => (a.SourceTrackId, a.TargetTrackId)));
    }

    [Fact]
    public async Task Import_RecordsLiveEvidence_AndIsIdempotent()
    {
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = "a", Artist = "A", Title = "One", FilePath = @"C:\Music\a.mp3" },
                new LibraryEntryEntity { UniqueHash = "b", Artist = "B", Title = "Two", FilePath = @"C:\Music\b.mp3" });
            await db.SaveChangesAsync();
        }

        var histories = new RekordboxHistoryResult(true, "ok", new[]
        {
            History("10", "C:/Music/a.mp3", "C:/Music/b.mp3"),
            History("11", "C:/Music/a.mp3", "D:/not-in-orbit.mp3"), // no transition between ORBIT tracks
        });
        var service = new RekordboxHistoryImportService(_db.Factory, _ => Task.FromResult(histories),
            NullLogger<RekordboxHistoryImportService>.Instance);

        var first = await service.ImportAsync();
        Assert.Null(first.Error);
        Assert.Equal(1, first.SessionsImported);
        Assert.Equal(1, first.SessionsWithoutTransitions);
        Assert.Equal(1, first.TransitionsRecorded);

        await using (var db = _db.Factory.CreateDbContext())
        {
            var edge = await db.TransitionKnowledge.SingleAsync(e => e.SourceTrackId == "a" && e.TargetTrackId == "b");
            Assert.Equal(1, await db.TransitionEvidence.CountAsync(e => e.TransitionKnowledgeId == edge.Id));
        }

        var second = await service.ImportAsync();
        Assert.Equal(0, second.SessionsImported);
        Assert.Equal(1, second.SessionsAlreadyImported);
        await using (var db = _db.Factory.CreateDbContext())
            Assert.Equal(1, await db.PerformanceSessions.CountAsync(s => s.Notes == RekordboxHistoryImportService.NotesPrefix + "10"));
    }

    [Fact]
    public async Task UnreadableRekordbox_IsReportedNotHidden()
    {
        var service = new RekordboxHistoryImportService(_db.Factory,
            _ => Task.FromResult(RekordboxHistoryResult.Fail("No Rekordbox 6/7 library found on this computer.")),
            NullLogger<RekordboxHistoryImportService>.Instance);
        var r = await service.ImportAsync();
        Assert.True(r.Failed);
        Assert.Equal("No Rekordbox 6/7 library found on this computer.", r.Summary);
    }
}
