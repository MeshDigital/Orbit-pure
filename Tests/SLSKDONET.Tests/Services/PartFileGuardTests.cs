using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using SLSKDONET.Services.Downloads;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>#328: a .part is only resumed from the same source, and every cleanup path deletes the real .part.</summary>
public sealed class PartFileGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "part-" + Guid.NewGuid().ToString("N"));

    public PartFileGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: best-effort cleanup of the isolated test folder */ }
    }

    [Fact]
    public void PartFromSourceA_WithNewSourceB_RestartsFromZero()
    {
        var decision = PartFileGuard.DecideResume("peerA", @"music\Down (Bad Legs Remix).flac", 40_000_000, 22_839_397,
            "peerB", @"share\Frannabik - Down.flac", 38_000_000);
        Assert.False(decision.Resume);
        Assert.Equal(0, decision.ConfirmedBytes);
        Assert.Contains("peerA", decision.Reason);
    }

    [Theory]
    [InlineData(null, null, 0, "no record")]                       // no journal proof of origin
    [InlineData("peerA", "other.flac", 1000, "different remote file")]
    [InlineData("peerA", "a.flac", 999, "999")]                     // same file name, different size
    public void UnprovenOrDifferentSources_AreNotResumed(string? user, string? file, long size, string reasonPart)
    {
        var decision = PartFileGuard.DecideResume(user, file, size, 500, "peerA", "a.flac", 1000);
        Assert.False(decision.Resume);
        Assert.Contains(reasonPart, decision.Reason);
    }

    [Fact]
    public void SameSource_ResumesFromTheConfirmedBytes()
    {
        var decision = PartFileGuard.DecideResume("PeerA", "a.flac", 1000, 640, "peera", "a.flac", 1000);
        Assert.True(decision.Resume);
        Assert.Equal(640, decision.ConfirmedBytes);
    }

    [Fact]
    public void CleanupDeletesTheRealPartFile_AndNeverInventsFolders()
    {
        var final = Path.Combine(_dir, "Artist - Title.flac");
        File.WriteAllText(final + ".part", "partial");

        Assert.Equal(new[] { final + ".part" }, PartFileGuard.CandidatePartPaths(null, final));
        Assert.Equal(new[] { final + ".part" }, PartFileGuard.CandidatePartPaths(final + ".part", final)); // deduped
        Assert.Equal(new[] { final + ".part" }, PartFileGuard.CandidatePartPaths(null, final + ".part")); // already a .part

        var deleted = PartFileGuard.DeletePartFiles(null, final);
        Assert.Equal(new[] { final + ".part" }, deleted);
        Assert.False(File.Exists(final + ".part"));
        Assert.Empty(PartFileGuard.DeletePartFiles(null, null));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir))); // no Artist/Album folders created
        Assert.Empty(Directory.GetDirectories(_dir));
    }

    [Fact]
    public void StagingPartPath_IsIncludedInCandidatesAndCleanup()
    {
        // #450: after a restart PartFilePath is null, but the deterministic staging path still
        // protects/deletes the in-flight .part.
        var final = Path.Combine(_dir, "Artist - Title.flac");
        var staging = Path.Combine(_dir, ".orbit-staging", "hash.part");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllText(staging, "partial");

        Assert.Contains(staging, PartFileGuard.CandidatePartPaths(null, final, staging));
        Assert.Contains(staging, PartFileGuard.DeletePartFiles(null, final, staging));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task Journal_ReturnsTheSourceThatWroteThePart()
    {
        var journal = new CrashRecoveryJournal(NullLogger<CrashRecoveryJournal>.Instance);
        await journal.InitAsync();
        var id = "part-guard-" + Guid.NewGuid().ToString("N");
        var state = new DownloadCheckpointState
        {
            TrackGlobalId = id, SoulseekUsername = "peerA", SoulseekFilename = "a.flac", ExpectedSize = 1000, BytesDownloaded = 640,
        };
        await journal.LogCheckpointAsync(new RecoveryCheckpoint
        {
            Id = id, OperationType = OperationType.Download, TargetPath = "x", StateJson = JsonSerializer.Serialize(state),
        });

        var read = await journal.GetDownloadStateAsync(id);
        Assert.Equal("peerA", read!.SoulseekUsername);
        Assert.Equal(640, read.BytesDownloaded);
        Assert.Null(await journal.GetDownloadStateAsync("missing-" + id));
        await journal.CompleteCheckpointAsync(id);
        // Not disposed: the journal's lock is shared process-wide, and other journal tests run in parallel.
    }
}
