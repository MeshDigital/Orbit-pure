using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class GenreTaxonomyTests
{
    // ---------------------------------------------------------------------------------------
    // Discogs-400: every key resolves, and the club member lists are exactly the plan's table.
    // ---------------------------------------------------------------------------------------

    private static readonly Dictionary<string, float> DnB = new(StringComparer.Ordinal)
    {
        ["Electronic---Drum n Bass"] = 1f,
        ["Electronic---Jungle"]     = 1f,
        ["Electronic---Halftime"]   = 1f,
    };

    private static readonly Dictionary<string, float> Dubstep = new(StringComparer.Ordinal)
    {
        ["Electronic---Dubstep"] = 1f,
        ["Electronic---Grime"]   = 1f,
        ["Hip Hop---Grime"]      = 1f,
    };

    private static readonly Dictionary<string, float> Techno = new(StringComparer.Ordinal)
    {
        ["Electronic---Techno"]         = 1f,
        ["Electronic---Hard Techno"]    = 1f,
        ["Electronic---Minimal Techno"] = 1f,
        ["Electronic---Deep Techno"]    = 1f,
        ["Electronic---Dub Techno"]     = 1f,
        ["Electronic---Schranz"]        = 1f,
        ["Electronic---Acid"]           = 0.5f,
        ["Electronic---Industrial"]     = 0.5f,
        ["Electronic---EBM"]            = 0.3f,
        ["Electronic---Minimal"]        = 0.5f,
    };

    private static readonly Dictionary<string, float> House = new(StringComparer.Ordinal)
    {
        ["Electronic---House"]             = 1f,
        ["Electronic---Tech House"]        = 1f,
        ["Electronic---Deep House"]        = 1f,
        ["Electronic---Progressive House"] = 1f,
        ["Electronic---Acid House"]        = 1f,
        ["Electronic---Garage House"]      = 1f,
        ["Electronic---Ghetto House"]      = 1f,
        ["Electronic---Tribal House"]      = 1f,
        ["Electronic---Hip-House"]         = 1f,
        ["Electronic---Italo House"]       = 1f,
        ["Electronic---Euro House"]        = 1f,
        ["Electronic---Electro House"]     = 0.6f,
        ["Electronic---Tribal"]            = 0.5f,
        ["Electronic---Nu-Disco"]          = 0.4f,
    };

    [Fact]
    public void MapDiscogsStyle_All400Keys_ResolveToExactlyThePlanBuckets()
    {
        var keys = LoadDiscogsKeys();
        Assert.Equal(400, keys.Length);

        foreach (var key in keys)
        {
            var m = GenreTaxonomy.MapDiscogsStyle(key);
            if (DnB.TryGetValue(key, out var wDnb))
                AssertBucket(key, m, GenreBucket.DrumAndBass, wDnb);
            else if (Dubstep.TryGetValue(key, out var wDub))
                AssertBucket(key, m, GenreBucket.Dubstep, wDub);
            else if (Techno.TryGetValue(key, out var wTech))
                AssertBucket(key, m, GenreBucket.Techno, wTech);
            else if (House.TryGetValue(key, out var wHouse))
                AssertBucket(key, m, GenreBucket.House, wHouse);
            else
                AssertBucket(key, m, GenreBucket.Other, 1f);
        }

        // Every expected member must exist in the loaded key set — guards against typos in the
        // expected lists and against a drifted label file.
        var keySet = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var k in DnB.Keys.Concat(Dubstep.Keys).Concat(Techno.Keys).Concat(House.Keys))
            Assert.Contains(k, keySet);
    }

    [Fact]
    public void MapDiscogsStyle_Confusers_LandInOther()
    {
        string[] confusers =
        {
            "Electronic---Hardcore", "Electronic---Gabber", "Electronic---Hardstyle",
            "Electronic---Happy Hardcore", "Electronic---Speedcore", "Electronic---Jumpstyle",
            "Electronic---Hard Trance", "Electronic---Trance", "Electronic---Psy-Trance",
            "Electronic---Goa Trance", "Electronic---Progressive Trance", "Electronic---Tech Trance",
            "Electronic---Breaks", "Electronic---Breakbeat", "Electronic---Progressive Breaks",
            "Electronic---Big Beat", "Electronic---Electro", "Electronic---UK Garage",
            "Electronic---Speed Garage", "Electronic---Bassline", "Electronic---Juke",
            "Hip Hop---Bass Music", "Electronic---Trip Hop", "Electronic---Downtempo",
            "Electronic---Ambient", "Electronic---Hard House", "Electronic---Tropical House",
        };

        foreach (var key in confusers)
        {
            var m = GenreTaxonomy.MapDiscogsStyle(key);
            Assert.Equal(GenreBucket.Other, m.Bucket);
            Assert.Equal(1f, m.Weight);
        }
    }

    [Fact]
    public void MapDiscogsStyle_NonElectronicParents_LandInOther()
    {
        foreach (var key in new[]
        {
            "Rock---Hardcore", "Rock---Industrial", "Jazz---Swing", "Pop---Vocal",
            "Reggae---Reggae", "Classical---Baroque", "Blues---Boogie Woogie",
            "Folk, World, & Country---Celtic", "Non-Music---Spoken Word",
        })
        {
            Assert.Equal(GenreBucket.Other, GenreTaxonomy.MapDiscogsStyle(key).Bucket);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Jamendo: all 87 labels resolve; club labels map, junk labels are a no-vote.
    // ---------------------------------------------------------------------------------------

    // MTG-Jamendo's 87-class taxonomy, in model output order (mirrors
    // EffnetClassifierHeadService.JamendoLabels).
    private static readonly string[] JamendoLabels =
    {
        "60s", "70s", "80s", "90s", "acidjazz", "alternative", "alternativerock", "ambient",
        "atmospheric", "blues", "bluesrock", "bossanova", "breakbeat", "celtic", "chanson",
        "chillout", "choir", "classical", "classicrock", "club", "contemporary", "country",
        "dance", "darkambient", "darkwave", "deephouse", "disco", "downtempo", "drumnbass",
        "dub", "dubstep", "easylistening", "edm", "electronic", "electronica", "electropop",
        "ethno", "eurodance", "experimental", "folk", "funk", "fusion", "groove", "grunge",
        "hard", "hardrock", "hiphop", "house", "idm", "improvisation", "indie", "industrial",
        "instrumentalpop", "instrumentalrock", "jazz", "jazzfusion", "latin", "lounge",
        "medieval", "metal", "minimal", "newage", "newwave", "orchestral", "pop", "popfolk",
        "poprock", "postrock", "progressive", "psychedelic", "punkrock", "rap", "reggae",
        "rnb", "rock", "rocknroll", "singersongwriter", "soul", "soundtrack", "swing",
        "symphonic", "synthpop", "techno", "trance", "triphop", "world", "worldfusion",
    };

    [Fact]
    public void MapJamendoLabel_All87Labels_ResolveWithoutThrowing()
    {
        Assert.Equal(87, JamendoLabels.Length);
        foreach (var label in JamendoLabels)
            _ = GenreTaxonomy.MapJamendoLabel(label);
    }

    [Theory]
    [InlineData("drumnbass", GenreBucket.DrumAndBass, null, 1f)]
    [InlineData("dubstep", GenreBucket.Dubstep, null, 1f)]
    [InlineData("techno", GenreBucket.Techno, null, 1f)]
    [InlineData("minimal", GenreBucket.Techno, "Minimal", 0.5f)] // ambiguous: also minimal house (plan: Minimal = 0.5)
    [InlineData("house", GenreBucket.House, null, 1f)]
    [InlineData("deephouse", GenreBucket.House, "Deep House", 1f)]
    [InlineData("breakbeat", GenreBucket.Other, "Breakbeat", 1f)]
    [InlineData("trance", GenreBucket.Other, "Trance", 1f)]
    [InlineData("ambient", GenreBucket.Other, "Ambient", 1f)]
    [InlineData("downtempo", GenreBucket.Other, "Downtempo", 1f)]
    [InlineData("idm", GenreBucket.Other, "IDM", 1f)]
    [InlineData("triphop", GenreBucket.Other, "Trip Hop", 1f)]
    [InlineData("rock", GenreBucket.Other, null, 1f)]
    [InlineData("jazz", GenreBucket.Other, null, 1f)]
    [InlineData("hiphop", GenreBucket.Other, null, 1f)]
    public void MapJamendoLabel_MapsClubAndOther(string label, GenreBucket bucket, string? subgenre, float weight)
    {
        var m = GenreTaxonomy.MapJamendoLabel(label);
        Assert.Equal(bucket, m.Bucket);
        Assert.False(m.IsJunk);
        Assert.Equal(weight, m.Weight);
        if (subgenre is not null) Assert.Equal(subgenre, m.Subgenre);
    }

    [Theory]
    [InlineData("club")]
    [InlineData("dance")]
    [InlineData("edm")]
    [InlineData("electronic")]
    [InlineData("electronica")]
    public void MapJamendoLabel_JunkLabels_AreNoVote(string label)
    {
        var m = GenreTaxonomy.MapJamendoLabel(label);
        Assert.True(m.IsJunk);
        Assert.Equal(0f, m.Weight);
        Assert.Equal(GenreBucket.Other, m.Bucket);
    }

    // ---------------------------------------------------------------------------------------
    // Free text.
    // ---------------------------------------------------------------------------------------

    [Theory]
    // Drum & Bass
    [InlineData("DnB", GenreBucket.DrumAndBass, null)]
    [InlineData("D&B", GenreBucket.DrumAndBass, null)]
    [InlineData("Drum & Bass", GenreBucket.DrumAndBass, null)]
    [InlineData("Drum and Bass", GenreBucket.DrumAndBass, null)]
    [InlineData("Drum'n'Bass", GenreBucket.DrumAndBass, null)]
    [InlineData("Jungle", GenreBucket.DrumAndBass, "Jungle")]
    [InlineData("Liquid", GenreBucket.DrumAndBass, "Liquid")]
    [InlineData("Neurofunk", GenreBucket.DrumAndBass, "Neurofunk")]
    [InlineData("Neuro", GenreBucket.DrumAndBass, "Neurofunk")]
    [InlineData("Jump Up", GenreBucket.DrumAndBass, "Jump Up")]
    [InlineData("Halftime", GenreBucket.DrumAndBass, "Halftime")]
    // Dubstep / 140
    [InlineData("Dubstep", GenreBucket.Dubstep, null)]
    [InlineData("140", GenreBucket.Dubstep, null)]
    [InlineData("Riddim", GenreBucket.Dubstep, "Riddim")]
    [InlineData("Brostep", GenreBucket.Dubstep, "Brostep")]
    [InlineData("Deep Dubstep", GenreBucket.Dubstep, "Deep Dubstep")]
    [InlineData("Grime", GenreBucket.Dubstep, "Grime")]
    [InlineData("140 / Deep Dubstep / Grime", GenreBucket.Dubstep, null)]
    // Techno
    [InlineData("Techno", GenreBucket.Techno, null)]
    [InlineData("Peak Time", GenreBucket.Techno, null)]
    [InlineData("Hard Techno", GenreBucket.Techno, "Hard Techno")]
    [InlineData("Melodic Techno", GenreBucket.Techno, "Melodic Techno")]
    [InlineData("Melodic House & Techno", GenreBucket.Techno, "Melodic Techno")]
    [InlineData("Techno (Peak Time / Driving)", GenreBucket.Techno, null)]
    [InlineData("Techno (Raw / Deep / Hypnotic)", GenreBucket.Techno, null)]
    [InlineData("Minimal / Deep Tech", GenreBucket.Techno, "Minimal")]
    [InlineData("Minimal", GenreBucket.Techno, "Minimal")]
    [InlineData("Deep Techno", GenreBucket.Techno, "Deep Techno")]
    [InlineData("Dub Techno", GenreBucket.Techno, "Dub Techno")]
    [InlineData("Acid Techno", GenreBucket.Techno, "Acid Techno")]
    [InlineData("Schranz", GenreBucket.Techno, "Schranz")]
    [InlineData("Industrial", GenreBucket.Techno, "Industrial")]
    [InlineData("EBM", GenreBucket.Techno, "EBM")]
    // House
    [InlineData("House", GenreBucket.House, null)]
    [InlineData("Tech House", GenreBucket.House, "Tech House")]
    [InlineData("Deep House", GenreBucket.House, "Deep House")]
    [InlineData("Progressive House", GenreBucket.House, "Progressive House")]
    [InlineData("Afro House", GenreBucket.House, "Afro House")]
    [InlineData("Bass House", GenreBucket.House, "Bass House")]
    [InlineData("Acid House", GenreBucket.House, "Acid House")]
    [InlineData("Electro House", GenreBucket.House, "Electro House")]
    [InlineData("Nu-Disco", GenreBucket.House, "Nu-Disco")]
    [InlineData("Tribal", GenreBucket.House, "Tribal")]
    // Other confusers
    [InlineData("Hardstyle", GenreBucket.Other, "Hardstyle")]
    [InlineData("Gabber", GenreBucket.Other, "Gabber")]
    [InlineData("UK Garage", GenreBucket.Other, "UK Garage")]
    [InlineData("Breaks", GenreBucket.Other, "Breaks")]
    [InlineData("Trance", GenreBucket.Other, "Trance")]
    [InlineData("Bass Music", GenreBucket.Other, "Bass Music")]
    [InlineData("Hardcore", GenreBucket.Other, "Hardcore")]
    [InlineData("Happy Hardcore", GenreBucket.Other, "Happy Hardcore")]
    [InlineData("Speedcore", GenreBucket.Other, "Speedcore")]
    [InlineData("Jumpstyle", GenreBucket.Other, "Jumpstyle")]
    [InlineData("Hard Dance", GenreBucket.Other, "Hard Dance")]
    [InlineData("Hard Trance", GenreBucket.Other, "Trance")]
    [InlineData("Psy-Trance", GenreBucket.Other, "Trance")]
    [InlineData("Goa Trance", GenreBucket.Other, "Trance")]
    [InlineData("Progressive Trance", GenreBucket.Other, "Trance")]
    [InlineData("Tech Trance", GenreBucket.Other, "Trance")]
    [InlineData("Breakbeat", GenreBucket.Other, "Breakbeat")]
    [InlineData("Progressive Breaks", GenreBucket.Other, "Progressive Breaks")]
    [InlineData("Big Beat", GenreBucket.Other, "Big Beat")]
    [InlineData("Electro", GenreBucket.Other, "Electro")]
    [InlineData("Speed Garage", GenreBucket.Other, "Speed Garage")]
    [InlineData("Bassline", GenreBucket.Other, "Bassline")]
    [InlineData("Juke", GenreBucket.Other, "Juke")]
    [InlineData("Footwork", GenreBucket.Other, "Footwork")]
    [InlineData("Trip Hop", GenreBucket.Other, "Trip Hop")]
    [InlineData("Downtempo", GenreBucket.Other, "Downtempo")]
    [InlineData("Ambient", GenreBucket.Other, "Ambient")]
    [InlineData("UK Garage / Bassline", GenreBucket.Other, "UK Garage")]
    [InlineData("Breaks / Breakbeat / UK Bass", GenreBucket.Other, "Breaks")]
    [InlineData("Trance (Main Floor)", GenreBucket.Other, "Trance")]
    [InlineData("Hard Dance / Hardcore", GenreBucket.Other, "Hard Dance")]
    [InlineData("Trap", GenreBucket.Other, "Trap")]
    [InlineData("Hip Hop", GenreBucket.Other, "Hip Hop")]
    [InlineData("Dub", GenreBucket.Other, "Dub")]
    [InlineData("IDM", GenreBucket.Other, "IDM")]
    [InlineData("2-Step", GenreBucket.Other, "2-Step")]
    [InlineData("UKG", GenreBucket.Other, "UK Garage")]
    [InlineData("Bass", GenreBucket.Other, "Bass")]
    [InlineData("UK Bass", GenreBucket.Other, "UK Bass")]
    [InlineData("Acid Jazz", GenreBucket.Other, "Acid Jazz")]
    // Multi-value: first non-junk value wins.
    [InlineData("Drum & Bass, Liquid", GenreBucket.DrumAndBass, null)]
    [InlineData("Electronic, Drum & Bass", GenreBucket.DrumAndBass, null)]
    public void MapFreeText_Maps(string text, GenreBucket bucket, string? subgenre)
    {
        var m = GenreTaxonomy.MapFreeText(text);
        Assert.Equal(bucket, m.Bucket);
        Assert.False(m.IsJunk);
        if (subgenre is not null) Assert.Equal(subgenre, m.Subgenre);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Electronic")]
    [InlineData("Electronica")]
    [InlineData("Dance")]
    [InlineData("Dance & DJ")]
    [InlineData("Club")]
    [InlineData("EDM")]
    [InlineData("Electronic Dance Music")]
    [InlineData("Other")]
    [InlineData("Unknown")]
    [InlineData("Misc")]
    [InlineData("Genre")]
    [InlineData("Various")]
    [InlineData("(52)")]
    [InlineData("(0)")]
    [InlineData("Electronic, Dance, Club")]
    public void MapFreeText_Junk_IsNoVote(string text)
    {
        var m = GenreTaxonomy.MapFreeText(text);
        Assert.True(m.IsJunk);
        Assert.Equal(GenreBucket.Other, m.Bucket);
        Assert.Equal(0f, m.Weight);
    }

    [Theory]
    [InlineData("Deep", true)]
    [InlineData("Taneční hudba", true)]
    [InlineData("Deep House", false)]
    [InlineData("Deep Techno", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Electronic", true)]
    [InlineData("Electronica", true)]
    [InlineData("Dance", true)]
    [InlineData("Dance & DJ", true)]
    [InlineData("Club", true)]
    [InlineData("EDM", true)]
    [InlineData("Electronic Dance Music", true)]
    [InlineData("Other", true)]
    [InlineData("Unknown", true)]
    [InlineData("Misc", true)]
    [InlineData("Genre", true)]
    [InlineData("Various", true)]
    [InlineData("(52)", true)]
    [InlineData("(0)", true)]
    [InlineData("Drum & Bass", false)]
    [InlineData("140", false)]
    [InlineData("Techno", false)]
    [InlineData("House", false)]
    [InlineData("Bass", false)]
    public void IsJunkGenre_Classifies(string text, bool expected)
        => Assert.Equal(expected, GenreTaxonomy.IsJunkGenre(text));

    // ---------------------------------------------------------------------------------------
    // Canonical display + family mapping.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(GenreBucket.DrumAndBass, null, "Drum and Bass")]
    [InlineData(GenreBucket.DrumAndBass, "Jungle", "Drum and Bass")]
    [InlineData(GenreBucket.Dubstep, null, "Dubstep")]
    [InlineData(GenreBucket.Dubstep, "Deep Dubstep", "Deep Dubstep")]
    [InlineData(GenreBucket.Dubstep, "Grime", "Dubstep")]
    [InlineData(GenreBucket.Techno, null, "Techno")]
    [InlineData(GenreBucket.Techno, "Melodic Techno", "Melodic Techno")]
    [InlineData(GenreBucket.Techno, "Hard Techno", "Hard Techno")]
    [InlineData(GenreBucket.Techno, "Minimal Techno", "Minimal Techno")]
    [InlineData(GenreBucket.Techno, "Schranz", "Techno")]
    [InlineData(GenreBucket.House, null, "House")]
    [InlineData(GenreBucket.House, "Tech House", "Tech House")]
    [InlineData(GenreBucket.House, "Deep House", "Deep House")]
    [InlineData(GenreBucket.House, "Afro House", "Afro House")]
    [InlineData(GenreBucket.House, "Nu-Disco", "House")]
    [InlineData(GenreBucket.Other, null, "Other")]
    [InlineData(GenreBucket.Other, "Hardstyle", "Hardstyle")]
    [InlineData(GenreBucket.Other, "Trance", "Trance")]
    public void CanonicalDisplay_ReturnsStableStrings(GenreBucket bucket, string? subgenre, string expected)
        => Assert.Equal(expected, GenreTaxonomy.CanonicalDisplay(bucket, subgenre));

    [Theory]
    [InlineData(GenreBucket.Other, null, GenreFamily.Unknown, FourOnTheFloorSubgenre.Unknown)]
    [InlineData(GenreBucket.Other, "Trance", GenreFamily.Unknown, FourOnTheFloorSubgenre.Unknown)]
    [InlineData(GenreBucket.DrumAndBass, null, GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown)]
    [InlineData(GenreBucket.Dubstep, null, GenreFamily.Dubstep, FourOnTheFloorSubgenre.Unknown)]
    [InlineData(GenreBucket.Techno, null, GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno)]
    [InlineData(GenreBucket.Techno, "Melodic Techno", GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno)]
    [InlineData(GenreBucket.House, null, GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House)]
    [InlineData(GenreBucket.House, "Deep House", GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House)]
    [InlineData(GenreBucket.House, "Tech House", GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno)]
    public void ToFamily_Maps(
        GenreBucket bucket, string? subgenre, GenreFamily family, FourOnTheFloorSubgenre sub)
    {
        var (f, s) = GenreTaxonomy.ToFamily(bucket, subgenre);
        Assert.Equal(family, f);
        Assert.Equal(sub, s);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    private static void AssertBucket(string key, GenreBucketMapping m, GenreBucket bucket, float weight)
    {
        Assert.True(bucket == m.Bucket, $"{key} → {m.Bucket}, expected {bucket}");
        Assert.Equal(weight, m.Weight);
        Assert.False(m.IsJunk);
    }

    private static string[] LoadDiscogsKeys()
    {
        var path = FindRepoFile(Path.Combine("Tools", "Essentia", "models", "genre_discogs400-discogs-effnet-1.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("classes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
