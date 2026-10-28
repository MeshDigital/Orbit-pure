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
        Assert.Equal(170, result.BpmBracketMin);
        Assert.Equal(180, result.BpmBracketMax);
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
