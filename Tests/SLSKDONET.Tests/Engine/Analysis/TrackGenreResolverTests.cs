using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>GA8 (#253): a trusted library genre beats the audio model's guess; weak ones only vote.</summary>
public class TrackGenreResolverTests
{
    // 174 BPM breakbeat drums: the gate favours DnB over Techno/House.
    private static readonly RhythmSignature DnbDrums = new(0.1, 0.9, 0.1, 0.6, 87, 174, 32, 0.9);
    // 128 BPM four-on-the-floor: the gate favours Techno/House over DnB.
    private static readonly RhythmSignature ClubDrums = new(0.95, 0.1, 0.05, 0.1, 64, 128, 32, 0.9);

    private static AudioGenreOpinion Audio(string label, float confidence, RhythmSignature? rhythm = null)
        => new(label, confidence, rhythm, FusionVersion: GenreFusionEngine.FusionVersion);

    private static TrackGenreContext Tag(string genres, string? source) => new(genres, source);

    [Fact]
    public void NoLibraryGenre_AudioDecides()
    {
        var r = TrackGenreResolver.Resolve(null, Audio("Hard Techno", 0.5f));
        Assert.Equal(GenreDecision.Audio, r.Decision);
        Assert.Equal(GenreBucket.Techno, r.Bucket);
        Assert.Equal("Hard Techno", r.DisplayText);
        Assert.Equal(GenreSource.ModelInferred, r.Source);
    }

    [Fact]
    public void NothingAtAll_IsNone()
    {
        var r = TrackGenreResolver.Resolve(Tag("Electronic", "tags"), null);
        Assert.Equal(GenreDecision.None, r.Decision);
        Assert.Equal(GenreSource.Unknown, r.Source);
    }

    [Theory]
    [InlineData("Electronic")]
    [InlineData("Dance, EDM")]
    [InlineData("(52)")]
    [InlineData("Other")]
    [InlineData("  ")]
    public void JunkTags_AreIgnored(string junk)
    {
        var r = TrackGenreResolver.Resolve(Tag(junk, "tags"), Audio("Drum and Bass", 0.46f));
        Assert.Equal(GenreDecision.Audio, r.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Null(r.MetadataSource);
    }

    [Fact]
    public void UserGenre_AlwaysWins_EvenAgainstStrongAudioAndDrums()
    {
        var r = TrackGenreResolver.Resolve(Tag("Drum & Bass", "user"), Audio("Techno", 0.69f, ClubDrums));
        Assert.Equal(GenreDecision.Metadata, r.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Equal(GenreSource.UserTag, r.Source);
        Assert.Equal(1f, r.Confidence);
        Assert.False(r.HasConflict);
        Assert.Equal("user", r.MetadataSource);
    }

    [Fact]
    public void UserGenre_CanBeOther()
    {
        var r = TrackGenreResolver.Resolve(Tag("Hardstyle", "user"), Audio("Drum and Bass", 0.69f, DnbDrums));
        Assert.Equal(GenreBucket.Other, r.Bucket);
        Assert.Equal("Hardstyle", r.DisplayText);
        Assert.Equal(GenreFamily.Unknown, r.Family);
    }

    [Fact]
    public void FileTag_KeptAgainstStrongAudioBackedByDrums_ButConflictFlagged()
    {
        var r = TrackGenreResolver.Resolve(Tag("Drum & Bass", "tags"), Audio("Techno", 0.65f, ClubDrums));
        Assert.Equal(GenreDecision.MetadataDespiteConflict, r.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Equal(GenreSource.LocalFileTag, r.Source);
        Assert.True(r.HasConflict);
        Assert.Contains("Techno", r.Conflict);
        Assert.True(r.Evidence.RhythmFavoursAudio);
    }

    [Fact]
    public void FileTag_NoConflict_WhenDrumsDontBackTheAudio()
    {
        var noDrums = TrackGenreResolver.Resolve(Tag("Drum & Bass", "tags"), Audio("Techno", 0.65f));
        Assert.Equal(GenreDecision.Metadata, noDrums.Decision);
        Assert.False(noDrums.HasConflict);

        var drumsBackTag = TrackGenreResolver.Resolve(Tag("Drum & Bass", "tags"), Audio("Techno", 0.65f, DnbDrums));
        Assert.Equal(GenreDecision.Metadata, drumsBackTag.Decision);
        Assert.False(drumsBackTag.HasConflict);
        Assert.False(drumsBackTag.Evidence.RhythmFavoursAudio);
    }

    [Fact]
    public void FileTag_NoConflict_WhenAudioIsNotStrong()
    {
        var r = TrackGenreResolver.Resolve(Tag("Drum & Bass", "tags"), Audio("Techno", 0.5f, ClubDrums));
        Assert.Equal(GenreDecision.Metadata, r.Decision);
        Assert.False(r.HasConflict);
    }

    [Theory]
    [InlineData("Drum & Bass, Liquid")]
    [InlineData("Liquid; Drum and Bass")]
    [InlineData("Liquid Funk")]
    public void MetadataSubgenre_WinsInTheSameBucket(string genres)
    {
        var r = TrackGenreResolver.Resolve(Tag(genres, "tags"), Audio("Drum and Bass", 0.6f));
        Assert.Equal(GenreDecision.Agreement, r.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
        Assert.Equal("Liquid", r.Subgenre);
        Assert.Equal("Drum and Bass", r.DisplayText); // the matchers downstream keep working
        Assert.True(r.Confidence > 0.9f);
    }

    [Fact]
    public void Agreement_KeepsTheAudioSubgenre_WhenTheTagHasNone()
    {
        var r = TrackGenreResolver.Resolve(Tag("Techno", "tags"), Audio("Hard Techno", 0.6f));
        Assert.Equal("Hard Techno", r.Subgenre);
        Assert.Equal("Hard Techno", r.DisplayText);
    }

    [Theory]
    [InlineData(0.69f)]
    [InlineData(0.5f)]
    [InlineData(0.46f)]
    public void SpotifyArtistGenre_NeverOverridesAConfidentAudio(float audioConfidence)
    {
        var r = TrackGenreResolver.Resolve(Tag("techno", "spotify"), Audio("Drum and Bass", audioConfidence));
        Assert.Equal(GenreDecision.Audio, r.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, r.Bucket);
    }

    [Fact]
    public void SpotifyArtistGenre_DecidesOnlyWhenTheAudioIsWeakOrMissing()
    {
        var weak = TrackGenreResolver.Resolve(Tag("deep house", "spotify"), Audio("Techno", 0.3f));
        Assert.Equal(GenreDecision.Metadata, weak.Decision);
        Assert.Equal(GenreBucket.House, weak.Bucket);
        Assert.Equal(GenreSource.ModelInferred, weak.Source); // a guess like the model's, not a trusted tag

        var missing = TrackGenreResolver.Resolve(Tag("deep house", "deezer"), null);
        Assert.Equal(GenreDecision.Metadata, missing.Decision);
        Assert.Equal(GenreSource.ModelInferred, missing.Source);
    }

    [Fact]
    public void MusicBrainz_LosesToStrongAudioThatTheDrumsDontContradict()
    {
        var overridden = TrackGenreResolver.Resolve(Tag("House", "musicbrainz"), Audio("Drum and Bass", 0.65f, DnbDrums));
        Assert.Equal(GenreDecision.AudioOverMetadata, overridden.Decision);
        Assert.Equal(GenreBucket.DrumAndBass, overridden.Bucket);
        Assert.Equal(GenreSource.ModelInferred, overridden.Source);
        Assert.True(overridden.HasConflict);

        var kept = TrackGenreResolver.Resolve(Tag("House", "musicbrainz"), Audio("Drum and Bass", 0.65f, ClubDrums));
        Assert.Equal(GenreDecision.Metadata, kept.Decision);
        Assert.Equal(GenreBucket.House, kept.Bucket);
    }

    [Fact]
    public void UnknownOrigin_IsMedium()
    {
        Assert.Equal(GenreMetadataStrength.Medium, TrackGenreResolver.StrengthOf(null));
        var r = TrackGenreResolver.Resolve(Tag("Dubstep", null), Audio("Techno", 0.4f));
        Assert.Equal(GenreDecision.Metadata, r.Decision);
        Assert.Equal(GenreSource.LocalFileTag, r.Source);
        Assert.Equal(0.75f, r.Confidence);
    }

    [Fact]
    public void MaintenanceGenre_IsTheAudioAgain()
    {
        var withAudio = TrackGenreResolver.Resolve(Tag("House", "maintenance"), Audio("Techno", 0.4f));
        Assert.Equal(GenreDecision.Audio, withAudio.Decision);
        Assert.Equal(GenreBucket.Techno, withAudio.Bucket);

        var alone = TrackGenreResolver.Resolve(Tag("House", "maintenance"), null);
        Assert.Equal(GenreBucket.House, alone.Bucket);
        Assert.Equal(GenreSource.ModelInferred, alone.Source);
    }

    [Fact]
    public void DiscogsStyleSoup_IsDowngraded()
    {
        var soup = TrackGenreResolver.Resolve(Tag("House, Techno, Trance, Breaks, Ambient", "discogs"), Audio("Drum and Bass", 0.65f, DnbDrums));
        Assert.Equal(GenreMetadataStrength.Weak.ToString(), soup.Evidence.MetadataStrength); // VA soup + two club buckets
        Assert.Equal(GenreBucket.DrumAndBass, soup.Bucket);

        var clean = TrackGenreResolver.Resolve(Tag("Deep House", "discogs"), Audio("Drum and Bass", 0.65f, DnbDrums));
        Assert.Equal(GenreBucket.House, clean.Bucket);
        Assert.True(clean.HasConflict);
    }

    [Fact]
    public void TwoClubBuckets_InOneTag_AreAmbiguous()
    {
        var r = TrackGenreResolver.Resolve(Tag("Techno, House", "tags"), Audio("Drum and Bass", 0.65f, DnbDrums));
        Assert.Equal(GenreMetadataStrength.Medium.ToString(), r.Evidence.MetadataStrength);
        Assert.Equal(GenreDecision.AudioOverMetadata, r.Decision);
    }

    [Fact]
    public void AudioOtherLabel_IsTheOtherBucket()
    {
        var r = TrackGenreResolver.Resolve(Tag("Techno", "tags"), Audio("Other", 0.6f));
        Assert.Equal(GenreBucket.Techno, r.Bucket);
        Assert.Equal(GenreBucket.Other.ToString(), r.Evidence.AudioBucket);
    }

    [Fact]
    public void Evidence_RoundTripsThroughJson_AndCarriesGateAndFusionVersionIntoTheNextRun()
    {
        var gate = new[] { new GenreGateEvidence("dnb-break", GenreBucket.DrumAndBass, 1.5, 0.8, "in") };
        var audio = new AudioGenreOpinion("Drum and Bass", 0.6f, DnbDrums,
            new Dictionary<string, double> { ["Drum and Bass"] = 0.8, ["Techno"] = 0.2 }, gate, 3);
        var first = TrackGenreResolver.Resolve(Tag("Drum & Bass, Neurofunk", "tags"), audio);

        var json = first.Evidence.ToJson();
        var parsed = GenreEvidenceRecord.TryParse(json)!;
        Assert.Equal(3, parsed.FusionVersion);
        Assert.Equal("DrumAndBass", parsed.Bucket);
        Assert.Equal("Neurofunk", parsed.Subgenre);
        Assert.Equal("tags", parsed.MetadataSource);
        Assert.Equal(0.8, parsed.AudioDistribution!["Drum and Bass"]);
        Assert.Equal(174, parsed.Rhythm!.TempoHigh);
        Assert.Equal("dnb-break", Assert.Single(parsed.Gate!).RuleId);

        var features = new AudioFeaturesEntity
        {
            DetectedSubGenre = "Drum and Bass",
            SubGenreConfidence = 0.6f,
            GenreDistributionJson = "{\"Drum and Bass\":0.8}",
            RhythmSignatureJson = System.Text.Json.JsonSerializer.Serialize(DnbDrums),
            GenreEvidenceJson = json,
        };
        var fromRow = AudioGenreOpinion.FromFeatures(features);
        Assert.Equal(3, fromRow.FusionVersion);
        Assert.Single(fromRow.Gate!);
        Assert.Equal(DnbDrums, fromRow.Rhythm);

        Assert.Null(GenreEvidenceRecord.TryParse("{broken"));
        Assert.Null(GenreEvidenceRecord.TryParse(null));
    }
}
