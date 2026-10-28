using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

/// <summary>
/// The coverage analyzer is what lets the Analysis page say "this track is missing its key"
/// instead of claiming every library row is analysed. These tests pin the predicates to the
/// defaults an unanalysed row actually carries, because that is where the old page went wrong:
/// it treated "a row exists" as "analysis is done".
/// </summary>
public sealed class AnalysisCoverageAnalyzerTests
{
    /// <summary>A freshly-created entity as the pipeline would start from — nothing measured.</summary>
    private static AudioFeaturesEntity EmptyRow() => new();

    private static AudioFeaturesEntity FullRow() => new()
    {
        TrackUniqueHash = "artist-title",
        WaveformBlob = new byte[] { 1, 2, 3 },
        WaveformBlobSampleCount = 900,
        LoudnessLUFS = -8.4f,
        Bpm = 128.0f,
        BeatGridJson = "[0.0,0.468,0.937]",
        Key = "A minor",
        CamelotKey = "8A",
        KeyConfidence = 0.82f,
        EnergyScore = 7,
        GenreDistributionJson = "{\"house\":0.9}",
        PhraseSegmentsJson = "[{\"type\":\"Intro\"}]",
        EnergyCurveJson = "[0.1,0.4]",
        CuePointsJson = "[{\"position\":12.5}]",
        Fingerprint = "abc123",
    };

    [Fact]
    public void Detect_UnanalysedRow_ReportsEverythingMissing()
    {
        var coverage = AnalysisCoverageAnalyzer.Analyze(EmptyRow(), AnalysisEngine.Pipeline);

        Assert.Equal(AnalysisEngine.None, coverage.Present);
        Assert.Equal(AnalysisEngine.Pipeline, coverage.Missing);
        Assert.False(coverage.IsComplete);
        Assert.Equal(8, coverage.MissingEngines.Count);
    }

    [Fact]
    public void Detect_MissingFeaturesRow_ReportsEverythingMissing()
    {
        var coverage = AnalysisCoverageAnalyzer.Analyze(null, AnalysisEngine.Pipeline);

        Assert.False(coverage.HasAnyOutput);
        Assert.Equal(AnalysisEngine.Pipeline, coverage.Missing);
    }

    [Fact]
    public void Detect_CompleteRow_ReportsNothingMissing()
    {
        var coverage = AnalysisCoverageAnalyzer.Analyze(FullRow(), AnalysisEngine.Pipeline);

        Assert.True(coverage.IsComplete, coverage.MissingSummary);
        Assert.Equal(AnalysisEngine.None, coverage.Missing);
        Assert.Equal(1.0, coverage.CompletionFraction);
    }

    [Fact]
    public void Detect_TempoRequiresBeatGridNotJustBpm()
    {
        var row = EmptyRow();
        row.Bpm = 174.0f;

        var coverage = AnalysisCoverageAnalyzer.Analyze(row, AnalysisEngine.Tempo);

        Assert.Equal(AnalysisEngine.Tempo, coverage.Missing);
    }

    [Fact]
    public void Detect_BareEnergyCurve_IsOnlyStructureInput_NotCompletedStructure()
    {
        var row = EmptyRow();
        row.EnergyCurveJson = "[0.5]";
        Assert.False(AnalysisCoverageAnalyzer.HasStructure(row));

        row.StructuralVersion = 1;
        Assert.True(AnalysisCoverageAnalyzer.HasStructure(row));
    }

    [Fact]
    public void Detect_StructuralEnergyScoreAlone_DoesNotCompleteMood()
    {
        var row = EmptyRow();
        row.EnergyScore = 5;

        Assert.False(AnalysisCoverageAnalyzer.HasMood(row));

        row.MoodTag = "Happy";
        row.MoodConfidence = 0.8f;
        Assert.True(AnalysisCoverageAnalyzer.HasMood(row));
    }

    [Fact]
    public void Detect_WaveformSampleCountWithoutBlob_DoesNotCompleteWaveform()
    {
        var row = EmptyRow();
        row.WaveformBlobSampleCount = 900;

        Assert.False(AnalysisCoverageAnalyzer.HasWaveform(row));

        row.WaveformBlob = new byte[] { 1 };
        Assert.True(AnalysisCoverageAnalyzer.HasWaveform(row));
    }

    [Fact]
    public void Detect_InspectorCueCache_DoesNotCompletePipelineCues()
    {
        var row = EmptyRow();
        row.CueDetrJson = "[{\"position\":12.5}]";

        Assert.False(AnalysisCoverageAnalyzer.HasCues(row));

        row.CuePointsJson = "[{\"position\":12.5}]";
        Assert.True(AnalysisCoverageAnalyzer.HasCues(row));
    }

    [Fact]
    public void GetRequests_ReturnsOnlySelectedTracksAndTheirOwnMissingEngines()
    {
        var missingKey = new TrackAnalysisCoverage(
            AnalysisEngine.Pipeline,
            AnalysisEngine.Pipeline & ~AnalysisEngine.Key);
        var complete = new TrackAnalysisCoverage(AnalysisEngine.Pipeline, AnalysisEngine.Pipeline);
        var coverage = new PlaylistAnalysisCoverage(null, "Selected set", new[]
        {
            new TrackCoverageEntry("selected-hash", "Artist", "Title", missingKey),
            new TrackCoverageEntry("SELECTED-HASH", "Artist", "Duplicate", missingKey),
            new TrackCoverageEntry("complete-hash", "Artist", "Complete", complete),
        });

        var request = Assert.Single(coverage.GetRequests(AnalysisEngine.Key | AnalysisEngine.Tempo));

        Assert.Equal("selected-hash", request.TrackHash);
        Assert.Equal(AnalysisEngine.Key, request.Engines);
        Assert.Empty(coverage.GetRequests(AnalysisEngine.None));
    }

    [Fact]
    public void Detect_TypeDefaultsAreNotEvidence_TempoMoodAndStructure()
    {
        var row = EmptyRow();

        // Defaults the entity ships with: "[]", "{}", energy 0.5, valence/arousal 5.0.
        Assert.False(AnalysisCoverageAnalyzer.HasTempo(row));
        Assert.False(AnalysisCoverageAnalyzer.HasMood(row));
        Assert.False(AnalysisCoverageAnalyzer.HasKey(row));
        Assert.False(AnalysisCoverageAnalyzer.HasStructure(row));
        Assert.False(AnalysisCoverageAnalyzer.HasCues(row));
        Assert.False(AnalysisCoverageAnalyzer.HasLoudness(row));
        Assert.False(AnalysisCoverageAnalyzer.HasWaveform(row));
    }

    [Fact]
    public void Detect_CueRowsCountEvenWithoutLegacyJson()
    {
        var row = FullRow();
        row.CuePointsJson = null;

        var withRows = AnalysisCoverageAnalyzer.Analyze(row, AnalysisEngine.Cues, cuePointCount: 8);
        var withoutRows = AnalysisCoverageAnalyzer.Analyze(row, AnalysisEngine.Cues, cuePointCount: 0);

        Assert.True(withRows.IsComplete);
        Assert.False(withoutRows.IsComplete);
    }

    [Fact]
    public void Detect_FingerprintFileCountsBecausePipelineStoresItOutsideTheRow()
    {
        var row = FullRow();
        row.Fingerprint = string.Empty;

        Assert.True(AnalysisCoverageAnalyzer.Analyze(row, AnalysisEngine.Fingerprint).IsComplete is false);
        Assert.True(AnalysisCoverageAnalyzer.Analyze(row, AnalysisEngine.Fingerprint, hasFingerprint: true).IsComplete);
    }

    [Fact]
    public void Detect_LoudnessSilentMarkerCountsAsMeasured()
    {
        var row = EmptyRow();
        row.LoudnessLUFS = -100f; // IntegratedLoudnessMeter.SilentMarker

        Assert.True(AnalysisCoverageAnalyzer.HasLoudness(row));
    }

    [Fact]
    public void RunnableMissing_ExcludesStemsBecauseThePipelineCannotProduceThem()
    {
        var coverage = new TrackAnalysisCoverage(
            AnalysisEngine.Pipeline | AnalysisEngine.Stems,
            AnalysisEngine.Waveform);

        Assert.Equal(AnalysisEngine.Stems, coverage.Missing & AnalysisEngine.Stems);
        Assert.Equal(AnalysisEngine.None, coverage.RunnableMissing & AnalysisEngine.Stems);
        Assert.False(coverage.RunnableMissing.Has(AnalysisEngine.Stems));
    }

    [Fact]
    public void Summarize_CountsPerEngineAcrossTracks()
    {
        var complete = AnalysisCoverageAnalyzer.Analyze(FullRow(), AnalysisEngine.Pipeline);
        var nothing = AnalysisCoverageAnalyzer.Analyze(EmptyRow(), AnalysisEngine.Pipeline);
        var noKey = FullRow();
        noKey.CamelotKey = string.Empty;
        noKey.Key = string.Empty;
        var partial = AnalysisCoverageAnalyzer.Analyze(noKey, AnalysisEngine.Pipeline);

        var rows = AnalysisCoverageAnalyzer.Summarize(new[] { complete, nothing, partial }, AnalysisEngine.Pipeline);

        var key = rows.Single(r => r.Engine == AnalysisEngine.Key);
        Assert.Equal(3, key.Total);
        Assert.Equal(1, key.Complete);
        Assert.Equal(2, key.Missing);

        var waveform = rows.Single(r => r.Engine == AnalysisEngine.Waveform);
        Assert.Equal(2, waveform.Complete);
        Assert.Equal(1, waveform.Missing);
        Assert.Equal("2 / 3", waveform.CompleteOfTotal);
    }

    [Fact]
    public void MissingUnion_ReturnsOnlyWhatSomeTrackStillNeeds()
    {
        var missingKey = FullRow();
        missingKey.CamelotKey = string.Empty;
        missingKey.Key = string.Empty;

        var union = AnalysisCoverageAnalyzer.MissingUnion(new[]
        {
            AnalysisCoverageAnalyzer.Analyze(FullRow(), AnalysisEngine.Pipeline),
            AnalysisCoverageAnalyzer.Analyze(missingKey, AnalysisEngine.Pipeline),
        });

        Assert.Equal(AnalysisEngine.Key, union);
    }

    [Theory]
    [InlineData(TrackStatus.Downloaded, TrackAvailabilityState.Ghost, true)]
    [InlineData(TrackStatus.Missing, TrackAvailabilityState.Ready, true)]
    [InlineData(TrackStatus.Missing, TrackAvailabilityState.Ghost, false)]
    [InlineData(TrackStatus.Failed, TrackAvailabilityState.QueuedForDownload, false)]
    public void IsAvailableOffline_MatchesTheStatesThatHaveAudioOnDisk(
        TrackStatus status, TrackAvailabilityState availability, bool expected)
    {
        Assert.Equal(expected, AnalysisCoverageService.IsAvailableOffline(status, availability));
    }

    [Fact]
    public void Describe_ReadsAsASentence()
    {
        var mask = AnalysisEngine.Key | AnalysisEngine.Cues | AnalysisEngine.Fingerprint;

        Assert.Equal("Key, Cues and Fingerprint", AnalysisEngines.Describe(mask));
        Assert.Equal("3 engines", AnalysisEngines.DescribeCount(mask));
        Assert.Equal(string.Empty, AnalysisEngines.Describe(AnalysisEngine.None));
    }

    [Fact]
    public void Catalog_CoversEveryPipelineEngineInDisplayOrder()
    {
        Assert.Equal(AnalysisEngine.Waveform, AnalysisEngines.Catalog[0].Engine);
        Assert.All(AnalysisEngines.Catalog, info => Assert.False(string.IsNullOrWhiteSpace(info.Purpose)));

        var union = AnalysisEngines.Catalog.Aggregate(AnalysisEngine.None, (mask, info) => mask | info.Engine);
        Assert.Equal(AnalysisEngine.All, union);
    }
}
