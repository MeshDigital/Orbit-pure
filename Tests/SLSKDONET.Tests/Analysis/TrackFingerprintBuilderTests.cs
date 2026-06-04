using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class TrackFingerprintBuilderTests
{
    [Fact]
    public void Build_Deterministic_ForFixedEssentiaFixture()
    {
        var sut = new TrackFingerprintBuilderService(new HarmonicAnalysisService());
        var fixedNow = new DateTime(2026, 05, 11, 12, 00, 00, DateTimeKind.Utc);

        var features = CreateFeaturesFixture();
        var essentia = CreateEssentiaFixture();
        var phrases = CreatePhraseFixture();

        var a = sut.Build("hash_1", features, essentia, phrases, fixedNow);
        var b = sut.Build("hash_1", features, essentia, phrases, fixedNow);

        var jsonA = JsonSerializer.Serialize(a);
        var jsonB = JsonSerializer.Serialize(b);

        Assert.Equal(jsonA, jsonB);
    }

    [Fact]
    public void Build_NormalizedOutputs_StayWithinZeroToOne()
    {
        var sut = new TrackFingerprintBuilderService(new HarmonicAnalysisService());

        var fp = sut.Build("hash_bounds", CreateFeaturesFixture(), CreateEssentiaFixture(), CreatePhraseFixture(), DateTime.UnixEpoch);

        foreach (var value in ExtractAllNormalizedValues(fp))
        {
            Assert.InRange(value, 0f, 1f);
        }
    }

    [Fact]
    public void Build_PopulatesAllVectorsAndSchemaVersion()
    {
        var sut = new TrackFingerprintBuilderService(new HarmonicAnalysisService());

        var fp = sut.Build("hash_presence", CreateFeaturesFixture(), CreateEssentiaFixture(), CreatePhraseFixture(), DateTime.UnixEpoch);

        Assert.Equal(TrackFingerprint.CurrentSchemaVersion, fp.SchemaVersion);
        Assert.NotNull(fp.Harmonic);
        Assert.NotNull(fp.Energy);
        Assert.NotNull(fp.Rhythm);
        Assert.NotNull(fp.Timbre);
        Assert.NotNull(fp.Structure);
        Assert.NotNull(fp.Mood);
    }

    [Fact]
    public async Task Store_VersionedLoader_RejectsUnsupportedSchema()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "orbit-fingerprint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sut = new TrackFingerprintBuilderService(new HarmonicAnalysisService());
            var fp = sut.Build("hash_store", CreateFeaturesFixture(), CreateEssentiaFixture(), CreatePhraseFixture(), DateTime.UnixEpoch);

            await store.SaveAsync(fp);

            var path = store.GetStoragePath("hash_store");
            var json = await File.ReadAllTextAsync(path);
            json = json.Replace("\"schema_version\":2", "\"schema_version\":999", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, json);

            // A10.6: SaveAsync populates the memory cache; evict so the next GetAsync
            // re-reads the patched file from disk rather than returning the cached object.
            store.Invalidate("hash_store");

            var loaded = await store.GetAsync("hash_store");
            Assert.Null(loaded);

            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

        [Fact]
        public async Task Store_LoadsV1Fingerprint_WithoutThrowing_WhenHarmonicIsMissing()
        {
                var tempRoot = Path.Combine(Path.GetTempPath(), "orbit-fingerprint-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);

                try
                {
                        var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
                        var path = store.GetStoragePath("legacy_hash");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                        var v1Json = """
                        {
                            "schema_version": 1,
                            "generated_at_utc": "2026-05-11T00:00:00Z",
                            "fingerprint": {
                                "trackUniqueHash": "legacy_hash",
                                "schemaVersion": 1,
                                "generatedAtUtc": "2026-05-11T00:00:00Z",
                                "builderVersion": "A10.1",
                                "energy": { "globalEnergy": 0.5, "confidence": 0.5 },
                                "rhythm": { "tempoNormalized": 0.5, "confidence": 0.5 },
                                "timbre": { "mfccTextureProxy": 0.5, "confidence": 0.5 },
                                "structure": { "phraseMapDensity": 0.5, "confidence": 0.5 },
                                "mood": { "danceability": 0.5, "confidence": 0.5 }
                            }
                        }
                        """;

                        await File.WriteAllTextAsync(path, v1Json);

                        var loaded = await store.GetAsync("legacy_hash");

                        Assert.NotNull(loaded);
                        Assert.Equal(1, loaded!.SchemaVersion);
                        Assert.Null(loaded.Harmonic);
                }
                finally
                {
                        if (Directory.Exists(tempRoot))
                                Directory.Delete(tempRoot, recursive: true);
                }
        }

    private static List<float> ExtractAllNormalizedValues(TrackFingerprint fp)
    {
        return
        [
            fp.Harmonic!.PrimaryKeyPositionNormalized,
            fp.Harmonic.PrimaryConfidence,
            fp.Harmonic.ModulationScore,
            fp.Harmonic.StabilityScore,

            fp.Energy.GlobalEnergy,
            fp.Energy.IntroEnergy,
            fp.Energy.BuildEnergy,
            fp.Energy.DropEnergy,
            fp.Energy.BreakdownEnergy,
            fp.Energy.OutroEnergy,
            fp.Energy.DropIntensity,
            fp.Energy.BreakdownDepth,
            fp.Energy.Confidence,

            fp.Rhythm.TempoNormalized,
            fp.Rhythm.SwingGrooveScore,
            fp.Rhythm.BeatHistogramSignature,
            fp.Rhythm.PercussiveDensity,
            fp.Rhythm.Confidence,

            fp.Timbre.MfccTextureProxy,
            fp.Timbre.SpectralCentroidProfile,
            fp.Timbre.BrightnessWarmthBalance,
            fp.Timbre.SpectralComplexity,
            fp.Timbre.Confidence,

            fp.Structure.PhraseMapDensity,
            fp.Structure.IntroLengthRatio,
            fp.Structure.BuildLengthRatio,
            fp.Structure.DropLengthRatio,
            fp.Structure.BreakdownLengthRatio,
            fp.Structure.OutroLengthRatio,
            fp.Structure.BuildUpSlope,
            fp.Structure.Confidence,

            fp.Mood.Danceability,
            fp.Mood.Aggressiveness,
            fp.Mood.Acousticness,
            fp.Mood.TonalVsPercussiveBalance,
            fp.Mood.Confidence,
        ];
    }

    private static AudioFeaturesEntity CreateFeaturesFixture()
    {
        return new AudioFeaturesEntity
        {
            TrackUniqueHash = "fixture_hash",
            TrackDuration = 240,
            Bpm = 126,
            BpmConfidence = 0.88f,
            Key = "A",
            Scale = "minor",
            KeyConfidence = 0.83f,
            CamelotKey = "8A",
            BpmStability = 0.91f,
            Energy = 0.74f,
            DropConfidence = 0.8f,
            Danceability = 0.67f,
            DynamicComplexity = 4.1f,
            OnsetRate = 6.8f,
            SpectralCentroid = 3150,
            SpectralComplexity = 0.58f,
            LoudnessLUFS = -9.2f,
            Intensity = 0.71f,
            MoodConfidence = 0.76f,
            TonalProbability = 0.62f,
        };
    }

    private static EssentiaOutput CreateEssentiaFixture()
    {
        var histogram = new float[16];
        histogram[4] = 10f;
        histogram[5] = 3f;
        histogram[6] = 1f;

        return new EssentiaOutput
        {
            Rhythm = new RhythmData
            {
                Bpm = 126,
                BpmConfidence = 0.88f,
                BpmHistogram = histogram,
                OnsetRate = 6.8f,
            },
            Tonal = new TonalData
            {
                KeyEdma = new KeyData
                {
                    Key = "A",
                    Scale = "minor",
                    Strength = 0.83f,
                },
            },
            LowLevel = new LowLevelData
            {
                SpectralCentroid = new StatsData { Mean = 3150 },
                SpectralComplexity = new StatsData { Mean = 0.58f },
                DynamicComplexity = 4.1f,
            },
        };
    }

    private static IReadOnlyList<TrackPhraseEntity> CreatePhraseFixture()
    {
        return
        [
            new TrackPhraseEntity { TrackUniqueHash = "fixture_hash", Type = PhraseType.Intro, StartTimeSeconds = 0, EndTimeSeconds = 32, EnergyLevel = 0.41f, Confidence = 0.8f, OrderIndex = 0 },
            new TrackPhraseEntity { TrackUniqueHash = "fixture_hash", Type = PhraseType.Build, StartTimeSeconds = 32, EndTimeSeconds = 64, EnergyLevel = 0.63f, Confidence = 0.75f, OrderIndex = 1 },
            new TrackPhraseEntity { TrackUniqueHash = "fixture_hash", Type = PhraseType.Drop, StartTimeSeconds = 64, EndTimeSeconds = 96, EnergyLevel = 0.91f, Confidence = 0.9f, OrderIndex = 2 },
            new TrackPhraseEntity { TrackUniqueHash = "fixture_hash", Type = PhraseType.Breakdown, StartTimeSeconds = 96, EndTimeSeconds = 128, EnergyLevel = 0.36f, Confidence = 0.7f, OrderIndex = 3 },
            new TrackPhraseEntity { TrackUniqueHash = "fixture_hash", Type = PhraseType.Outro, StartTimeSeconds = 200, EndTimeSeconds = 240, EnergyLevel = 0.49f, Confidence = 0.82f, OrderIndex = 4 },
        ];
    }
}
