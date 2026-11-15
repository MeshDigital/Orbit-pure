using System;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Tests.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>GA8 (#253) end to end on the isolated test DB: library genre + provenance → resolver → cue inputs.</summary>
public sealed class TrackGenreContextTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("genre-context");

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync(string hash, string? genres, string? source)
    {
        _db.SeedLibrary((hash, "Artist", "Title"));
        await using var db = _db.Factory.CreateDbContext();
        var entry = await db.LibraryEntries.FindAsync(hash);
        entry!.Genres = genres;
        if (source is not null)
            db.MetadataProvenance.Add(new MetadataProvenanceEntity { TrackUniqueHash = hash, Field = "genre", Source = source, Confidence = 1 });
        await db.SaveChangesAsync();
    }

    private async Task<TrackGenreContext?> LoadAsync(string hash)
    {
        await using var db = _db.Factory.CreateDbContext();
        return await TrackGenreContextQuery.LoadAsync(db, hash);
    }

    [Fact]
    public async Task Query_ReadsTheGenreAndItsSource()
    {
        await SeedAsync("h1", "Drum & Bass, Liquid", "user");
        await SeedAsync("h2", "House", null);

        Assert.Equal(new TrackGenreContext("Drum & Bass, Liquid", "user"), await LoadAsync("h1"));
        Assert.Equal(new TrackGenreContext("House", null), await LoadAsync("h2"));
        Assert.Null(await LoadAsync("missing"));
        Assert.Null(await LoadAsync(""));
    }

    [Fact]
    public async Task UserDnbTag_OverTechnoAudio_GivesBreakbeatWithUserTag()
    {
        await SeedAsync("dnb", "Drum & Bass", "user");
        var features = new AudioFeaturesEntity { TrackUniqueHash = "dnb", Bpm = 174, TrackDuration = 300, DetectedSubGenre = "Techno", SubGenreConfidence = 0.46f };

        var genre = TrackGenreResolver.Resolve(await LoadAsync("dnb"), AudioGenreOpinion.FromFeatures(features));
        var result = AnalysisPipelineResultBuilder.Build(features, genre: genre);

        Assert.Equal(GenreSource.UserTag, result.GenreSource);
        Assert.Same(genre, result.ResolvedGenre);
        Assert.Equal(GenreFamily.Breakbeat,
            GenreFamilyClassifier.Classify(result.Genre, result.Bpm, result.GenreConfidence, result.GenreSource).Family);
        Assert.Equal("Techno", features.DetectedSubGenre); // the audio opinion is not overwritten
    }

    [Fact]
    public async Task FileDubstepTag_At140_BeatsATechnoLabelTheTempoCannotCorrect()
    {
        await SeedAsync("step", "Dubstep", "tags");
        var features = new AudioFeaturesEntity { TrackUniqueHash = "step", Bpm = 140, TrackDuration = 300, DetectedSubGenre = "Techno", SubGenreConfidence = 0.6f };

        var audioOnly = AnalysisPipelineResultBuilder.Build(features);
        Assert.Equal(GenreFamily.FourOnTheFloor,
            GenreFamilyClassifier.Classify(audioOnly.Genre, audioOnly.Bpm, audioOnly.GenreConfidence, audioOnly.GenreSource).Family);

        var genre = TrackGenreResolver.Resolve(await LoadAsync("step"), AudioGenreOpinion.FromFeatures(features));
        var resolved = AnalysisPipelineResultBuilder.Build(features, genre: genre);
        Assert.Equal(GenreFamily.Dubstep,
            GenreFamilyClassifier.Classify(resolved.Genre, resolved.Bpm, resolved.GenreConfidence, resolved.GenreSource).Family);
    }

    [Fact]
    public void NoResolvedGenre_KeepsTheAudioLabel()
    {
        var features = new AudioFeaturesEntity { Bpm = 174, DetectedSubGenre = "Techno", SubGenreConfidence = 0.4f };
        var none = TrackGenreResolver.Resolve(null, null);
        var result = AnalysisPipelineResultBuilder.Build(features, genre: none);
        Assert.Equal("Techno", result.Genre);
        Assert.Equal(GenreSource.ModelInferred, result.GenreSource);
        Assert.Null(result.ResolvedGenre);
    }

    [Theory]
    [InlineData("Hardstyle", 175f, GenreFamily.Unknown)]     // a trusted non-club tag is not bracketed by tempo
    [InlineData("Hip Hop", 87f, GenreFamily.Unknown)]
    [InlineData("Neurofunk", 172f, GenreFamily.Breakbeat)]  // the taxonomy knows what the keywords don't
    [InlineData("Techno", 174f, GenreFamily.FourOnTheFloor)] // a user's Techno at 174 is not overruled by tempo
    public void Classifier_TrustsUserTagsOverTempo(string tag, float bpm, GenreFamily expected)
        => Assert.Equal(expected, GenreFamilyClassifier.Classify(tag, bpm, 0.3f, GenreSource.UserTag).Family);

    [Fact]
    public void Classifier_StillDistrustsModelLabelsAtDnbTempo()
        => Assert.Equal(GenreFamily.Breakbeat, GenreFamilyClassifier.Classify("Techno", 174f, 0.6f, GenreSource.ModelInferred).Family);
}
