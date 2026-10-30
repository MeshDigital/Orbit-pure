using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class GenreFamilyClassifierTests
{
    [Theory]
    [InlineData("Drum and Bass")]
    [InlineData("DnB")]
    [InlineData("Jungle")]
    public void Classify_BreakbeatGenreText_ReturnsBreakbeat(string genre)
    {
        var result = GenreFamilyClassifier.Classify(genre, bpm: 174f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
        Assert.Equal(160, result.BpmBracketMin);
        Assert.Equal(190, result.BpmBracketMax);
    }

    [Fact]
    public void Classify_ModelTechnoAtDnbTempo_IsTempoOverriddenEvenWhenConfident()
    {
        var result = GenreFamilyClassifier.Classify(
            "Techno", bpm: 174f, genreConfidence: 0.9f, source: GenreSource.ModelInferred);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
        Assert.True(result.WasTempoOverride);
    }

    [Fact]
    public void Classify_ModelTechnoAtHalfTimeDnbTempo_IsTempoOverridden()
    {
        var result = GenreFamilyClassifier.Classify(
            "Techno", bpm: 87f, genreConfidence: 0.9f, source: GenreSource.ModelInferred);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
        Assert.True(result.WasTempoOverride);
    }

    [Fact]
    public void Classify_UserTechnoTagAtDnbTempo_IsNotOverridden()
    {
        var result = GenreFamilyClassifier.Classify(
            "Techno", bpm: 174f, genreConfidence: 0.9f, source: GenreSource.UserTag);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.False(result.WasTempoOverride);
    }

    [Fact]
    public void Classify_ModelTechnoAtHouseTempo_IsNotOverridden()
    {
        var result = GenreFamilyClassifier.Classify(
            "Techno", bpm: 128f, genreConfidence: 0.9f, source: GenreSource.ModelInferred);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.False(result.WasTempoOverride);
    }

    [Fact]
    public void Classify_DubstepTag_ReturnsDubstepEvenAtHouseTempo()
    {
        var result = GenreFamilyClassifier.Classify("Dubstep", bpm: 128f);

        Assert.Equal(GenreFamily.Dubstep, result.Family);
        Assert.Equal(138, result.BpmBracketMin);
        Assert.Equal(150, result.BpmBracketMax);
    }

    [Fact]
    public void Classify_StoredGenres_PrefersDetectedSubGenreOverRawTagAndBpm()
    {
        var result = GenreFamilyClassifier.Classify(
            detectedSubGenre: "Dubstep", primaryGenre: "Electronic", genres: "House", bpm: 128f);

        Assert.Equal(GenreFamily.Dubstep, result.Family);
    }

    [Fact]
    public void Classify_HalfTimeReading_FallsBackToBreakbeat()
    {
        var result = GenreFamilyClassifier.Classify(genreHint: null, bpm: 87f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }

    [Fact]
    public void Classify_LowConfidenceTechnoAtDnbTempo_FallsBackToBreakbeat()
    {
        // Real library case: a 175 BPM DnB track is ONNX-tagged "Techno" at 0.18 confidence.
        var result = GenreFamilyClassifier.Classify("Techno", bpm: 175f, genreConfidence: 0.18f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }

    [Fact]
    public void Classify_BorderlineConfidenceTechnoAtDnbTempo_FallsBackToBreakbeat()
    {
        var result = GenreFamilyClassifier.Classify("Techno", bpm: 174f, genreConfidence: 0.62f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }

    [Fact]
    public void Classify_HighConfidenceTechnoAtDnbTempo_KeepsTheTag()
    {
        var result = GenreFamilyClassifier.Classify("Techno", bpm: 174f, genreConfidence: 0.9f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
    }

    [Fact]
    public void Classify_HighConfidenceTechnoAtHouseTempo_StaysFourOnTheFloor()
    {
        var result = GenreFamilyClassifier.Classify("Techno", bpm: 128f, genreConfidence: 0.9f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
    }

    [Fact]
    public void Classify_160BpmWithoutGenre_FallsBackToBreakbeat()
    {
        var result = GenreFamilyClassifier.Classify(genreHint: null, bpm: 160f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }

    [Fact]
    public void Classify_HouseGenreText_ReturnsFourOnTheFloorHouse()
    {
        var result = GenreFamilyClassifier.Classify("House", bpm: 124f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.Equal(FourOnTheFloorSubgenre.House, result.Subgenre);
    }

    [Fact]
    public void Classify_TechHouseGenreText_ReturnsTechHouseTechno()
    {
        var result = GenreFamilyClassifier.Classify("Tech House", bpm: 128f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.Equal(FourOnTheFloorSubgenre.TechHouseTechno, result.Subgenre);
    }

    [Fact]
    public void Classify_TranceGenreText_ReturnsTrance()
    {
        var result = GenreFamilyClassifier.Classify("Uplifting Trance", bpm: 138f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.Equal(FourOnTheFloorSubgenre.Trance, result.Subgenre);
    }

    [Fact]
    public void Classify_EmptyGenre_FallsBackToBreakbeatBpmBracket()
    {
        var result = GenreFamilyClassifier.Classify(null, bpm: 174f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }

    [Fact]
    public void Classify_EmptyGenre_FallsBackToHouseBpmBracket()
    {
        var result = GenreFamilyClassifier.Classify(genreHint: null, bpm: 122f);

        Assert.Equal(GenreFamily.FourOnTheFloor, result.Family);
        Assert.Equal(FourOnTheFloorSubgenre.House, result.Subgenre);
    }

    [Fact]
    public void Classify_AmbiguousGenreAndBpm_ReturnsUnknown()
    {
        var result = GenreFamilyClassifier.Classify(genreHint: null, bpm: 100f);

        Assert.Equal(GenreFamily.Unknown, result.Family);
    }

    [Fact]
    public void Classify_UnrecognizedGenreText_FallsBackToBpmBracket()
    {
        // Genre text present but not matched by any branch — should still fall back to BPM.
        var result = GenreFamilyClassifier.Classify("Ambient", bpm: 174f);

        Assert.Equal(GenreFamily.Breakbeat, result.Family);
    }
}
