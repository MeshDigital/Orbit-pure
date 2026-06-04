using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Similarity;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Analysis;

public class PlaylistIntelligenceServiceTests
{
    private readonly ITestOutputHelper _output;

    public PlaylistIntelligenceServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SuggestNextAsync_PrefersClosestAnchoredMatch()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var anchor = CreateFingerprint("anchor", "8A", 0.72f);
            var best = CreateFingerprint("best", "8A", 0.74f);
            var weak = CreateFingerprint("weak", "2B", 0.18f);

            await SaveAllAsync(store, anchor, best, weak);
            SeedSections(sectionVectors, "anchor", CreateSections(0.42f, 0.86f));
            SeedSections(sectionVectors, "best", CreateSections(0.44f, 0.84f));
            SeedSections(sectionVectors, "weak", CreateSections(0.10f, 0.20f));

            var result = await sut.SuggestNextAsync("anchor", new[] { "best", "weak" }, topK: 2);

            Assert.Equal("best", result.First().TrackHash);
            Assert.True(result.First().Score > result.Last().Score);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InsertBetweenAsync_PrefersCandidateThatFitsBothAnchors()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var from = CreateFingerprint("from", "8A", 0.30f);
            var to = CreateFingerprint("to", "9A", 0.78f);
            var bridge = CreateFingerprint("bridge", "8A", 0.54f);
            var mismatch = CreateFingerprint("mismatch", "2B", 0.95f);

            await SaveAllAsync(store, from, to, bridge, mismatch);
            SeedSections(sectionVectors, "from", CreateSections(0.30f, 0.70f));
            SeedSections(sectionVectors, "to", CreateSections(0.80f, 0.88f));
            SeedSections(sectionVectors, "bridge", CreateSections(0.52f, 0.78f));
            SeedSections(sectionVectors, "mismatch", CreateSections(0.95f, 0.12f));

            var result = await sut.InsertBetweenAsync("from", "to", new[] { "bridge", "mismatch" }, topK: 2);

            Assert.Equal("bridge", result.First().TrackHash);
            Assert.Contains("Energy curve stays smooth between anchors", result.First().ReasonTags);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ReorderAsync_WithRisingEnergy_ReturnsAscendingEnergyShape()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var low = CreateFingerprint("low", "8A", 0.20f);
            var mid = CreateFingerprint("mid", "8A", 0.50f);
            var high = CreateFingerprint("high", "8A", 0.85f);

            await SaveAllAsync(store, low, mid, high);
            SeedSections(sectionVectors, "low", CreateSections(0.20f, 0.28f));
            SeedSections(sectionVectors, "mid", CreateSections(0.50f, 0.56f));
            SeedSections(sectionVectors, "high", CreateSections(0.84f, 0.90f));

            var result = await sut.ReorderAsync(new[] { "high", "mid", "low" }, energyCurve: EnergyCurvePattern.Rising);

            Assert.Equal(new[] { "low", "mid", "high" }, result.OrderedTrackHashes);
            Assert.Equal(2, result.TransitionRecommendations.Count);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LargeSetFixture_ScorePathTransitionsAsync_CompletesWithinBaselineBudget()
    {
        const int trackCount = 64;
        const double maxDurationMs = 2500;

        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var dataset = BuildSyntheticDataset(trackCount);
            await SaveAllAsync(store, dataset.Fingerprints.ToArray());
            foreach (var (hash, sections) in dataset.Sections)
                SeedSections(sectionVectors, hash, sections);

            var orderedHashes = dataset.Fingerprints.Select(fp => fp.TrackUniqueHash).ToList();

            var stopwatch = Stopwatch.StartNew();
            var result = await sut.ScorePathTransitionsAsync(orderedHashes);
            stopwatch.Stop();

            _output.WriteLine($"A10.6 fixture path-score: tracks={trackCount}, edges={result.Count}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");

            Assert.Equal(trackCount - 1, result.Count);
            Assert.True(stopwatch.Elapsed.TotalMilliseconds < maxDurationMs,
                $"Path transition scoring exceeded baseline budget ({stopwatch.Elapsed.TotalMilliseconds:F1}ms >= {maxDurationMs}ms)");
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LargeSetFixture_ReorderAsync_CompletesWithinBaselineBudget()
    {
        const int trackCount = 64;
        const double maxDurationMs = 3500;

        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var dataset = BuildSyntheticDataset(trackCount);
            await SaveAllAsync(store, dataset.Fingerprints.ToArray());
            foreach (var (hash, sections) in dataset.Sections)
                SeedSections(sectionVectors, hash, sections);

            var orderedHashes = dataset.Fingerprints.Select(fp => fp.TrackUniqueHash).ToList();

            var stopwatch = Stopwatch.StartNew();
            var result = await sut.ReorderAsync(orderedHashes);
            stopwatch.Stop();

            _output.WriteLine($"A10.6 fixture reorder: tracks={trackCount}, transitions={result.TransitionRecommendations.Count}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}, avgFlow={result.AverageTransitionScore:F3}");

            Assert.Equal(trackCount, result.OrderedTrackHashes.Count);
            Assert.Equal(trackCount - 1, result.TransitionRecommendations.Count);
            Assert.True(stopwatch.Elapsed.TotalMilliseconds < maxDurationMs,
                $"Reorder scoring exceeded baseline budget ({stopwatch.Elapsed.TotalMilliseconds:F1}ms >= {maxDurationMs}ms)");
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static PlaylistIntelligenceService CreateSut(TrackFingerprintStore store, SectionVectorService sectionVectors)
    {
        var harmonicAnalysis = new HarmonicAnalysisService();
        var harmonicCompatibility = new HarmonicCompatibilityService(harmonicAnalysis);
        var similarity = new TrackSimilarityService(harmonicCompatibility, store, sectionVectors);
        return new PlaylistIntelligenceService(store, similarity, harmonicCompatibility, sectionVectors);
    }

    private static async Task SaveAllAsync(TrackFingerprintStore store, params TrackFingerprint[] fingerprints)
    {
        foreach (var fingerprint in fingerprints)
            await store.SaveAsync(fingerprint);
    }

    private static void SeedSections(SectionVectorService service, string trackHash, IReadOnlyList<SectionFeatureVector> sections)
    {
        var cacheField = typeof(SectionVectorService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        var cache = (ConcurrentDictionary<string, IReadOnlyList<SectionFeatureVector>>)cacheField!.GetValue(service)!;
        cache[trackHash] = sections;
    }

    private static TrackFingerprint CreateFingerprint(string hash, string key, float globalEnergy)
    {
        return new TrackFingerprint
        {
            TrackUniqueHash = hash,
            BuilderVersion = "A10.4-test",
            Harmonic = new HarmonicVector
            {
                PrimaryKey = key,
                PrimaryConfidence = 0.9f,
                SecondaryKeys = new[] { "9A", "8B" },
                SecondaryConfidences = new[] { 0.6f, 0.55f },
                PrimaryKeyPositionNormalized = 0.61f,
                ModulationScore = 0.10f,
                StabilityScore = 0.88f,
            },
            Energy = new EnergyVector
            {
                GlobalEnergy = globalEnergy,
                IntroEnergy = Math.Max(0f, globalEnergy - 0.20f),
                BuildEnergy = Math.Max(0f, globalEnergy - 0.05f),
                DropEnergy = Math.Min(1f, globalEnergy + 0.15f),
                BreakdownEnergy = Math.Max(0f, globalEnergy - 0.28f),
                OutroEnergy = Math.Max(0f, globalEnergy - 0.18f),
                DropIntensity = Math.Min(1f, globalEnergy + 0.11f),
                BreakdownDepth = Math.Max(0f, 0.7f - globalEnergy),
                Confidence = 0.93f,
            },
            Rhythm = new RhythmVector
            {
                TempoNormalized = 0.62f,
                SwingGrooveScore = 0.58f,
                BeatHistogramSignature = 0.70f,
                PercussiveDensity = 0.74f,
                Confidence = 0.92f,
            },
            Timbre = new TimbreVector
            {
                MfccTextureProxy = 0.66f,
                SpectralCentroidProfile = 0.54f,
                BrightnessWarmthBalance = 0.60f,
                SpectralComplexity = 0.55f,
                Confidence = 0.90f,
            },
            Structure = new StructureVector
            {
                PhraseMapDensity = 0.68f,
                IntroLengthRatio = 0.12f,
                BuildLengthRatio = 0.18f,
                DropLengthRatio = 0.22f,
                BreakdownLengthRatio = 0.14f,
                OutroLengthRatio = 0.11f,
                BuildUpSlope = 0.72f,
                Confidence = 0.88f,
            },
            Mood = new MoodVector
            {
                Danceability = 0.76f,
                Aggressiveness = 0.52f,
                Acousticness = 0.08f,
                TonalVsPercussiveBalance = 0.64f,
                Confidence = 0.91f,
            },
        };
    }

    private static IReadOnlyList<SectionFeatureVector> CreateSections(float introEnergy, float dropEnergy)
    {
        return new List<SectionFeatureVector>
        {
            new() { SectionType = PhraseType.Intro, EnergyLevel = introEnergy, StartRatio = 0f, DurationRatio = 0.12f, Arousal = 0.42f, Danceability = 0.66f, SpectralBrightness = 0.38f, Confidence = 0.95f },
            new() { SectionType = PhraseType.Build, EnergyLevel = Math.Min(1f, introEnergy + 0.18f), StartRatio = 0.2f, DurationRatio = 0.15f, Arousal = 0.61f, Danceability = 0.71f, SpectralBrightness = 0.52f, Confidence = 0.91f },
            new() { SectionType = PhraseType.Drop, EnergyLevel = dropEnergy, StartRatio = 0.38f, DurationRatio = 0.18f, Arousal = 0.86f, Danceability = 0.84f, SpectralBrightness = 0.69f, Confidence = 0.93f },
            new() { SectionType = PhraseType.Breakdown, EnergyLevel = Math.Max(0f, introEnergy - 0.10f), StartRatio = 0.58f, DurationRatio = 0.14f, Arousal = 0.34f, Danceability = 0.56f, SpectralBrightness = 0.28f, Confidence = 0.88f },
            new() { SectionType = PhraseType.Outro, EnergyLevel = introEnergy, StartRatio = 0.82f, DurationRatio = 0.12f, Arousal = 0.41f, Danceability = 0.61f, SpectralBrightness = 0.36f, Confidence = 0.9f },
        };
    }

    private static (List<TrackFingerprint> Fingerprints, Dictionary<string, IReadOnlyList<SectionFeatureVector>> Sections) BuildSyntheticDataset(int count)
    {
        var random = new Random(84102);
        var fingerprints = new List<TrackFingerprint>(count);
        var sections = new Dictionary<string, IReadOnlyList<SectionFeatureVector>>(StringComparer.Ordinal);

        for (var index = 0; index < count; index++)
        {
            var hash = $"fixture_{index:D3}";
            var key = BuildCamelot(index);
            var globalEnergy = 0.22f + (index / (float)Math.Max(1, count - 1)) * 0.62f;
            var jitter = (float)(random.NextDouble() * 0.06 - 0.03);
            var introEnergy = Math.Clamp(globalEnergy + jitter - 0.08f, 0.05f, 0.92f);
            var dropEnergy = Math.Clamp(globalEnergy + 0.12f + jitter, 0.12f, 0.98f);

            fingerprints.Add(CreateFingerprint(hash, key, Math.Clamp(globalEnergy + jitter, 0.08f, 0.95f)));
            sections[hash] = CreateSections(introEnergy, dropEnergy);
        }

        return (fingerprints, sections);
    }

    private static string BuildCamelot(int index)
    {
        var number = (index % 12) + 1;
        var mode = index % 2 == 0 ? "A" : "B";
        return $"{number}{mode}";
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-playlist-intelligence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    // ── A10.6 hardening tests ───────────────────────────────────────────────

    [Fact]
    public async Task SuggestNextAsync_WithEmptyStore_ReturnsEmptyAndDoesNotThrow()
    {
        // Cold-start: no fingerprints on disk, no sections in memory.
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var result = await sut.SuggestNextAsync("nonexistent_a", new[] { "nonexistent_b", "nonexistent_c" });

            Assert.Empty(result);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task FingerprintStore_MemoryCache_ServesRepeatGetWithoutDiskRead()
    {
        // After the first GetAsync (disk read + cache population), a second GetAsync for
        // the same hash must return the identical object from cache — not a new object
        // deserialized from disk.
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var fp = CreateFingerprint("cache_test", "8A", 0.72f);
            await store.SaveAsync(fp);

            var first = await store.GetAsync("cache_test");
            var second = await store.GetAsync("cache_test");

            Assert.NotNull(first);
            Assert.Same(first, second); // must be the identical in-memory instance
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task FingerprintStore_Invalidate_ForcesNextGetToRehit_Disk()
    {
        // After Invalidate(hash), the cache entry is gone. The next GetAsync re-reads
        // from disk and returns a new object (not the previously-cached reference).
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var fp = CreateFingerprint("inv_test", "9A", 0.55f);
            await store.SaveAsync(fp);

            var first = await store.GetAsync("inv_test");
            store.Invalidate("inv_test");
            var second = await store.GetAsync("inv_test");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second); // cache miss → new deserialized object
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task ReorderAsync_OverLimit_ThrowsArgumentException()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            // Provide MaxReorderTracks + 1 hash strings (no fingerprints needed — guard fires first).
            var tooMany = Enumerable.Range(0, PlaylistIntelligenceService.MaxReorderTracks + 1)
                                    .Select(i => $"hash_{i}")
                                    .ToList();

            await Assert.ThrowsAsync<ArgumentException>(() => sut.ReorderAsync(tooMany));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task LargeSetFixture_SuggestNextAsync_128Tracks_CompletesWithinBudget()
    {
        const int trackCount = 128;
        const double maxDurationMs = 3000;

        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sut = CreateSut(store, sectionVectors);

            var dataset = BuildSyntheticDataset(trackCount);
            await SaveAllAsync(store, dataset.Fingerprints.ToArray());
            foreach (var (hash, sections) in dataset.Sections)
                SeedSections(sectionVectors, hash, sections);

            var anchor = dataset.Fingerprints.First().TrackUniqueHash;
            var candidates = dataset.Fingerprints.Skip(1).Select(fp => fp.TrackUniqueHash).ToList();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await sut.SuggestNextAsync(anchor, candidates, topK: 10);
            stopwatch.Stop();

            _output.WriteLine($"A10.6 SuggestNextAsync: tracks={trackCount}, elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms, topK={result.Count}");

            Assert.Equal(10, result.Count);
            Assert.True(stopwatch.Elapsed.TotalMilliseconds < maxDurationMs,
                $"SuggestNextAsync 128-track exceeded budget ({stopwatch.Elapsed.TotalMilliseconds:F1}ms >= {maxDurationMs}ms)");
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task SimilarityResultCache_ReturnsIdenticalInstance_OnRepeatScore()
    {
        // ScoreAsync called twice for the same (left, right, profile) must return the
        // same TrackSimilarityResult instance from the result cache.
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);

            var harmonicAnalysis = new HarmonicAnalysisService();
            var harmonicCompatibility = new HarmonicCompatibilityService(harmonicAnalysis);
            var similarityService = new TrackSimilarityService(harmonicCompatibility, store, sectionVectors);

            var fpA = CreateFingerprint("sim_a", "8A", 0.70f);
            var fpB = CreateFingerprint("sim_b", "9A", 0.75f);
            await store.SaveAsync(fpA);
            await store.SaveAsync(fpB);

            var first = await similarityService.ScoreAsync("sim_a", "sim_b");
            var second = await similarityService.ScoreAsync("sim_a", "sim_b");

            Assert.NotNull(first);
            Assert.Same(first, second); // identical cached instance
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task SimilarityResultCache_InvalidateResultCache_ClearsStaleEntries()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            using var store = new TrackFingerprintStore(NullLogger<TrackFingerprintStore>.Instance, tempRoot);
            var sectionVectors = new SectionVectorService(NullLogger<SectionVectorService>.Instance);

            var harmonicAnalysis = new HarmonicAnalysisService();
            var harmonicCompatibility = new HarmonicCompatibilityService(harmonicAnalysis);
            var similarityService = new TrackSimilarityService(harmonicCompatibility, store, sectionVectors);

            var fpA = CreateFingerprint("inv_a", "8A", 0.60f);
            var fpB = CreateFingerprint("inv_b", "8B", 0.65f);
            await store.SaveAsync(fpA);
            await store.SaveAsync(fpB);

            var first = await similarityService.ScoreAsync("inv_a", "inv_b");
            similarityService.InvalidateResultCache("inv_a");
            var second = await similarityService.ScoreAsync("inv_a", "inv_b");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second); // cache invalidated → recomputed
            Assert.Equal(first!.FinalSimilarity, second!.FinalSimilarity, 6); // same value, new instance
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }
}