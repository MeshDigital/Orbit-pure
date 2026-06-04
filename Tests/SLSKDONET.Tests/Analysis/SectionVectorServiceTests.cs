using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class SectionVectorServiceTests
{
    [Fact]
    public async Task SectionApis_TolerateNullOrWhitespaceHashes_WithSafeDefaults()
    {
        var sut = new SectionVectorService(NullLogger<SectionVectorService>.Instance);

        var fromNull = await sut.GetSectionsAsync(null!);
        var fromWhitespace = await sut.GetSectionsAsync("   ");

        Assert.Empty(fromNull);
        Assert.Empty(fromWhitespace);

        Assert.Equal(0.0, sut.TransitionCostCached(null!, "target"));
        Assert.Equal(0.0, sut.TransitionCostCached("source", "   "));
        Assert.Equal(0.0, sut.DropSimilarityCached(null!, "target"));
        Assert.Equal(0.5, sut.TransitionScoreCached(null!, "target"));
        Assert.Equal(0.5, sut.TransitionScoreCached("source", "   "));

        // No-op guards should never throw.
        sut.Invalidate(null);
        await sut.PreloadAsync(null!);

        var bridgeCandidates = await sut.FindBridgeCandidatesAsync(null!, "to", new[] { "x" });
        Assert.Empty(bridgeCandidates);
    }

    [Fact]
    public async Task SectionApis_UseNeutralDefaults_WhenTrackHasNoSections()
    {
        var sut = new SectionVectorService(NullLogger<SectionVectorService>.Instance);

        var missingHash = $"missing-{Guid.NewGuid():N}";
        var otherMissingHash = $"missing-{Guid.NewGuid():N}";

        Assert.Equal(0.5, sut.TransitionScoreCached(missingHash, otherMissingHash));
        Assert.Equal(0.0, sut.TransitionCostCached(missingHash, otherMissingHash));
        Assert.Equal(0.0, sut.DropSimilarityCached(missingHash, otherMissingHash));

        var asyncCost = await sut.TransitionCostAsync(missingHash, otherMissingHash);
        Assert.Equal(0.0, asyncCost);
    }

    [Fact]
    public async Task GetSectionsAsync_PrefersPhraseSectionEmbedding_WhenPresent()
    {
        var hash = $"secvec-{Guid.NewGuid():N}";
        var expectedEmbedding = new[] { 0.11f, 0.22f, 0.33f };

        try
        {
            await SeedTrackAsync(
                hash,
                sectionEmbeddingJson: "[0.11,0.22,0.33]",
                deepTextureEmbedding: new[] { 0.9f, 0.8f, 0.7f },
                vectorEmbedding: new[] { 0.6f, 0.5f, 0.4f });

            var sut = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sections = await sut.GetSectionsAsync(hash);

            var intro = Assert.Single(sections.Where(s => s.SectionType == PhraseType.Intro));
            Assert.NotNull(intro.Embedding);
            Assert.Equal(expectedEmbedding.Length, intro.Embedding!.Length);
            for (var i = 0; i < expectedEmbedding.Length; i++)
            {
                Assert.Equal(expectedEmbedding[i], intro.Embedding[i], 3);
            }
        }
        finally
        {
            await CleanupTrackAsync(hash);
        }
    }

    [Fact]
    public async Task GetSectionsAsync_FallsBackToDeepTextureEmbedding_WhenPhraseEmbeddingMissing()
    {
        var hash = $"secvec-{Guid.NewGuid():N}";
        var expectedEmbedding = new[] { 0.41f, 0.52f, 0.63f };

        try
        {
            await SeedTrackAsync(
                hash,
                sectionEmbeddingJson: null,
                deepTextureEmbedding: expectedEmbedding,
                vectorEmbedding: new[] { 0.9f, 0.9f, 0.9f });

            var sut = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sections = await sut.GetSectionsAsync(hash);

            var intro = Assert.Single(sections.Where(s => s.SectionType == PhraseType.Intro));
            Assert.NotNull(intro.Embedding);
            Assert.Equal(expectedEmbedding.Length, intro.Embedding!.Length);
            for (var i = 0; i < expectedEmbedding.Length; i++)
            {
                Assert.Equal(expectedEmbedding[i], intro.Embedding[i], 3);
            }
        }
        finally
        {
            await CleanupTrackAsync(hash);
        }
    }

    [Fact]
    public async Task GetSectionsAsync_FallsBackToVectorEmbedding_WhenDeepTextureMissing()
    {
        var hash = $"secvec-{Guid.NewGuid():N}";
        var expectedEmbedding = new[] { 0.71f, 0.82f, 0.93f };

        try
        {
            await SeedTrackAsync(
                hash,
                sectionEmbeddingJson: null,
                deepTextureEmbedding: null,
                vectorEmbedding: expectedEmbedding);

            var sut = new SectionVectorService(NullLogger<SectionVectorService>.Instance);
            var sections = await sut.GetSectionsAsync(hash);

            var intro = Assert.Single(sections.Where(s => s.SectionType == PhraseType.Intro));
            Assert.NotNull(intro.Embedding);
            Assert.Equal(expectedEmbedding.Length, intro.Embedding!.Length);
            for (var i = 0; i < expectedEmbedding.Length; i++)
            {
                Assert.Equal(expectedEmbedding[i], intro.Embedding[i], 3);
            }
        }
        finally
        {
            await CleanupTrackAsync(hash);
        }
    }

    private static async Task SeedTrackAsync(
        string hash,
        string? sectionEmbeddingJson,
        float[]? deepTextureEmbedding,
        float[]? vectorEmbedding)
    {
        await using var context = new AppDbContext();
        await context.Database.EnsureCreatedAsync();

        context.AudioFeatures.Add(new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            TrackDuration = 120,
            Arousal = 4.5f,
            Danceability = 0.7f,
            SpectralCentroid = 3000f,
            DeepTextureEmbedding = deepTextureEmbedding,
            VectorEmbedding = vectorEmbedding
        });

        context.TrackPhrases.Add(new TrackPhraseEntity
        {
            TrackUniqueHash = hash,
            Type = PhraseType.Intro,
            StartTimeSeconds = 0,
            EndTimeSeconds = 24,
            EnergyLevel = 0.35f,
            Confidence = 0.9f,
            OrderIndex = 0,
            SectionEmbeddingJson = sectionEmbeddingJson,
            EmbeddingMagnitude = 0f
        });

        await context.SaveChangesAsync();
    }

    private static async Task CleanupTrackAsync(string hash)
    {
        await using var context = new AppDbContext();

        var phrases = await context.TrackPhrases
            .Where(p => p.TrackUniqueHash == hash)
            .ToListAsync();
        if (phrases.Count > 0)
            context.TrackPhrases.RemoveRange(phrases);

        var features = await context.AudioFeatures
            .Where(f => f.TrackUniqueHash == hash)
            .ToListAsync();
        if (features.Count > 0)
            context.AudioFeatures.RemoveRange(features);

        await context.SaveChangesAsync();
    }
}