using System;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>#206 (Sets export): the export carries library metadata, notes are opt-in, duplicate edges don't crash.</summary>
public sealed class SetExportBuilderTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("set-export");

    public void Dispose() => _db.Dispose();

    private async Task<Guid> SeedAsync()
    {
        var setId = Guid.NewGuid();
        await using var db = _db.Factory.CreateDbContext();
        db.LibraryEntries.AddRange(
            new LibraryEntryEntity { UniqueHash = "a", Artist = "Noisia", Title = "One", Album = "Album A", FilePath = "a.flac", BPM = 174, MusicalKey = "8A", Genres = "Drum & Bass", Label = "Vision" },
            new LibraryEntryEntity { UniqueHash = "b", Artist = "Mefjus", Title = "Two", Album = "Album B", FilePath = "b.flac", BPM = 175, MusicalKey = "9A", Comments = "my note" });
        db.SetLists.Add(new SetListEntity { Id = setId, Name = "Friday" });
        db.SetTracks.AddRange(
            new SetTrackEntity { SetListId = setId, TrackUniqueHash = "a", Position = 0 },
            new SetTrackEntity { SetListId = setId, TrackUniqueHash = "b", Position = 1 });
        // Two rows for the same pair (different transition types) are normal.
        db.TransitionKnowledge.AddRange(
            new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", CompatibilityScore = 70, EvidenceCount = 1 },
            new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", TransitionType = KnowledgeTransitionType.Cut, CompatibilityScore = 85, EvidenceCount = 2 });
        await db.SaveChangesAsync();
        return setId;
    }

    [Fact]
    public async Task Export_CarriesLibraryMetadata_InSetOrder_AndSurvivesDuplicateEdges()
    {
        var setId = await SeedAsync();
        await using var db = _db.Factory.CreateDbContext();
        var tracks = await KnowledgeLifecycleRepository.BuildAnnotatedSetExportAsync(db, setId, includeTransitionNotes: false);

        Assert.Equal(new[] { "a", "b" }, tracks.Select(t => t.TrackUniqueHash));
        var first = tracks[0];
        Assert.Equal("Album A", first.Album);
        Assert.Equal(174, first.BPM);
        Assert.Equal("8A", first.MusicalKey);
        Assert.Equal("Drum & Bass", first.Genres);
        Assert.Equal("Vision", first.Label);
        Assert.Equal(1, first.TrackNumber);
        Assert.True(string.IsNullOrEmpty(tracks[0].Comments));
        Assert.Equal("my note", tracks[1].Comments); // the track's own comment, no transition note
    }

    [Fact]
    public async Task TransitionNotes_OnlyWhenAskedFor()
    {
        var setId = await SeedAsync();
        await using var db = _db.Factory.CreateDbContext();
        var tracks = await KnowledgeLifecycleRepository.BuildAnnotatedSetExportAsync(db, setId, includeTransitionNotes: true);
        Assert.True(string.IsNullOrEmpty(tracks[0].Comments)); // the first slot has no incoming transition
        Assert.StartsWith("my note | ", tracks[1].Comments); // appended, never replaced
    }
}
