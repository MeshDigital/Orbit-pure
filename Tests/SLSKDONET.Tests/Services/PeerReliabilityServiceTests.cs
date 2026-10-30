using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit coverage for the room-roster reliability badge lookups added for chat integration:
/// <see cref="PeerReliabilityService.TryGetReliabilityScore"/> and
/// <see cref="PeerReliabilityService.GetReliabilityScores"/> must distinguish peers you've actually
/// downloaded from (badged) from search-only or unknown peers (left unbadged), and do it from the
/// in-memory cache so a large room costs no per-row SQLite queries.
/// </summary>
public class PeerReliabilityServiceTests
{
    private static DatabaseService CreateDatabaseService()
    {
        var schemaMigrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        var trackRepository = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var fileWrite = new Mock<IFileWriteService>();
        return new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            schemaMigrator,
            trackRepository,
            fileWrite.Object);
    }

    [Fact]
    public void TryGetReliabilityScore_UnknownPeer_ReturnsNull()
    {
        var sut = new PeerReliabilityService(CreateDatabaseService());

        Assert.Null(sut.TryGetReliabilityScore("never-seen"));
        Assert.Null(sut.TryGetReliabilityScore(null));
        Assert.Null(sut.TryGetReliabilityScore(string.Empty));
    }

    [Fact]
    public void TryGetReliabilityScore_SearchOnlyPeer_ReturnsNull()
    {
        var sut = new PeerReliabilityService(CreateDatabaseService());

        sut.RecordSearchCandidate("search-only-peer");

        // Surfaced only as a search candidate — no download attempt yet — so it must stay unbadged.
        Assert.Null(sut.TryGetReliabilityScore("search-only-peer"));
    }

    [Fact]
    public void TryGetReliabilityScore_PeerWithDownloadAttempt_ReturnsScore()
    {
        var sut = new PeerReliabilityService(CreateDatabaseService());

        sut.RecordDownloadStarted("active-peer");
        sut.RecordDownloadCompleted("active-peer");

        var score = sut.TryGetReliabilityScore("active-peer");

        Assert.NotNull(score);
        Assert.InRange(score!.Value, 0.05, 0.99);
    }

    [Fact]
    public void GetReliabilityScores_ReturnsOnlyPeersWithHistory()
    {
        var sut = new PeerReliabilityService(CreateDatabaseService());

        sut.RecordDownloadStarted("known-a");
        sut.RecordSearchCandidate("search-only");
        // "unknown-b" is never seen at all.

        var scores = sut.GetReliabilityScores(new[] { "known-a", "search-only", "unknown-b" });

        Assert.True(scores.ContainsKey("known-a"));
        Assert.False(scores.ContainsKey("search-only"));
        Assert.False(scores.ContainsKey("unknown-b"));
        Assert.Single(scores);
    }
}
