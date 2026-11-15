using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Maintenance;

/// <summary>GA9 (#254): the GenreConflict proposal rule and the inspector's genre evidence block.</summary>
public sealed class GenreConflictRuleTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("genre-conflict");

    public void Dispose() => _db.Dispose();

    private const string Conflict = "Library genre says House, the audio sounds like Drum and Bass (65 %)";

    [Theory]
    // genres, source, conflict, audio label, audio confidence, fires
    [InlineData("House", "tags", Conflict, "Drum and Bass", 0.65, true)]          // a flagged conflict
    [InlineData("House", null, Conflict, "Drum and Bass", 0.65, true)]            // unknown origin, flagged
    [InlineData("House", "user", Conflict, "Drum and Bass", 0.69, false)]         // the user is always right
    [InlineData("Electronic", "tags", null, "Techno", 0.6, true)]                 // junk tag + confident audio
    [InlineData("(52)", "deezer", null, "Techno", 0.6, true)]
    [InlineData("Electronic", "tags", null, "Techno", 0.4, false)]                // junk tag, audio not confident
    [InlineData("", "tags", null, "Techno", 0.69, false)]                         // empty: GenreFill's job
    [InlineData("House", "tags", null, "Techno", 0.69, false)]                    // no conflict flagged
    [InlineData("Electronic", "tags", null, "Other", 0.69, false)]                // junk proposal
    [InlineData("Drum and Bass", "tags", Conflict, "drum and bass", 0.69, false)] // nothing to change
    public void FiresOnlyWhereItShould(string genres, string? source, string? conflict, string label, double confidence, bool fires)
    {
        var proposal = GenreConflictRule.Propose("h", genres, source, conflict, label, confidence, @"C:\music\h.flac");
        Assert.Equal(fires, proposal is not null);
        if (proposal is null) return;
        Assert.Equal("GenreConflict", proposal.Rule);
        Assert.Equal(ProposalConfidence.NeedsReview, proposal.Confidence);
        Assert.False(proposal.DefaultSelected);
        var change = Assert.Single(proposal.Changes);
        Assert.Equal(TagField.Genre, change.Field);
        Assert.Equal(label, change.Value);
        Assert.Equal(@"C:\music\h.flac", proposal.FilePath);
    }

    [Fact]
    public async Task Scan_ReadsEvidenceAndProvenance_FromTheLibrary()
    {
        _db.SeedLibrary(("conflict", "A", "1"), ("mine", "A", "2"), ("junk", "A", "3"), ("clean", "A", "4"), ("old", "A", "5"));
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.LibraryEntries.Find("conflict")!.Genres = "House";
            db.LibraryEntries.Find("mine")!.Genres = "House";
            db.LibraryEntries.Find("junk")!.Genres = "Electronic";
            db.LibraryEntries.Find("clean")!.Genres = "Drum & Bass";
            db.LibraryEntries.Find("old")!.Genres = "House";
            db.MetadataProvenance.Add(new MetadataProvenanceEntity { TrackUniqueHash = "mine", Field = "genre", Source = "user", Confidence = 1 });
            string Evidence(string? conflict, string label, float confidence) => new GenreEvidenceRecord
            {
                Conflict = conflict, AudioLabel = label, AudioConfidence = confidence,
            }.ToJson();
            db.AudioFeatures.AddRange(
                new AudioFeaturesEntity { TrackUniqueHash = "conflict", GenreEvidenceJson = Evidence(Conflict, "Drum and Bass", 0.65f) },
                new AudioFeaturesEntity { TrackUniqueHash = "mine", GenreEvidenceJson = Evidence(Conflict, "Drum and Bass", 0.65f) },
                new AudioFeaturesEntity { TrackUniqueHash = "junk", GenreEvidenceJson = Evidence(null, "Techno", 0.62f) },
                new AudioFeaturesEntity { TrackUniqueHash = "clean", GenreEvidenceJson = Evidence(null, "Drum and Bass", 0.6f) },
                new AudioFeaturesEntity { TrackUniqueHash = "old", GenreEvidenceJson = "{broken" });
            await db.SaveChangesAsync();
        }

        await using var read = _db.Factory.CreateDbContext();
        var proposals = await GenreConflictRule.ScanAsync(read);
        Assert.Equal(new[] { "conflict", "junk" }, proposals.Select(p => p.Hash).OrderBy(h => h).ToArray());
        Assert.Contains("says nothing", proposals.Single(p => p.Hash == "junk").Evidence);
    }

    // ── Inspector presentation ──────────────────────────────────────────────────────────────

    private static GenreEvidenceRecord Record(string decision, string? source = "tags", string? conflict = null) => new()
    {
        Bucket = "DrumAndBass",
        Subgenre = "Liquid",
        Display = "Drum and Bass",
        Decision = decision,
        Conflict = conflict,
        MetadataText = "Drum & Bass, Liquid",
        MetadataSource = source,
        MetadataStrength = source == "user" ? "Authoritative" : "Strong",
        AudioLabel = "Techno",
        AudioDistribution = new Dictionary<string, double> { ["Techno"] = 0.9, ["Drum and Bass"] = 0.45, ["House"] = 0.15 },
        Rhythm = new RhythmSummary(0.2, 0.85, 0.1, 0.5, 87, 174, 0.9),
    };

    [Theory]
    [InlineData("Metadata", "user", "Used for cues: Drum and Bass / Liquid · from your tag")]
    [InlineData("Agreement", "tags", "Used for cues: Drum and Bass / Liquid · the file tag and audio agree")]
    [InlineData("MetadataDespiteConflict", "beatport", "Used for cues: Drum and Bass / Liquid · from Beatport (the audio disagrees)")]
    [InlineData("AudioOverMetadata", "musicbrainz", "Used for cues: Drum and Bass / Liquid · from the audio (MusicBrainz overruled)")]
    [InlineData("Audio", null, "Used for cues: Drum and Bass / Liquid · from the audio")]
    public void UsedForLine_NamesWhatDecided(string decision, string? source, string expected)
        => Assert.Equal(expected, GenreEvidencePresentation.From(Record(decision, source)).UsedForText);

    [Fact]
    public void Bars_AreSharesAndStrengths_WithTempoInMono()
    {
        var view = GenreEvidencePresentation.From(Record("MetadataDespiteConflict", "tags", Conflict));

        Assert.Equal(Conflict, view.ConflictText);
        Assert.Equal("Drum & Bass, Liquid · the file tag", view.TagText);
        var tag = Assert.Single(view.TagBars);
        Assert.Equal(("File tag", 0.9), (tag.Label, tag.Confidence));

        Assert.Equal(new[] { "Techno", "Drum and Bass", "House" }, view.AudioBars.Select(b => b.Label).ToArray());
        Assert.Equal(1.0, view.AudioBars.Sum(b => b.Confidence), 6);
        Assert.Equal(0.6, view.AudioBars[0].Confidence, 6);

        Assert.Equal(new[] { "4x4 kick", "Backbeat", "Half-time" }, view.RhythmBars.Select(b => b.Label).ToArray());
        Assert.Equal(0.85, view.RhythmBars[1].Confidence, 6);
        Assert.Equal("87 / 174 BPM", view.TempoText);
    }

    [Fact]
    public void OldOrOddRows_GiveTheEmptyState_NotACrash()
    {
        Assert.Equal((null, null), PlaylistTrackViewModel.ParseGenreEvidence(null));
        Assert.Equal((null, null), PlaylistTrackViewModel.ParseGenreEvidence("{not json"));

        var (bare, error) = PlaylistTrackViewModel.ParseGenreEvidence("{}");
        Assert.Null(error);
        Assert.NotNull(bare);
        Assert.False(bare!.HasAudio);
        Assert.False(bare.HasRhythm);
        Assert.False(bare.HasTag);
        Assert.Equal("No genre for cues yet", bare.UsedForText);

        Assert.Empty(GenreEvidencePresentation.From(new GenreEvidenceRecord { Rhythm = new RhythmSummary(0, 0, 0, 0, 0, 0, 0) }).RhythmBars);
    }
}
