using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Tests.Services.Similarity;

// ─────────────────────────────────────────────────────────────────────────────
// DiscogsEffnetEmbeddingExtractor tests — Task 2.1 (#70)
// ─────────────────────────────────────────────────────────────────────────────

public class DiscogsEffnetEmbeddingExtractorTests : IDisposable
{
    // Use a non-existent path so IsAvailable = false (model not bundled in CI)
    private readonly DiscogsEffnetEmbeddingExtractor _sut = new(
        NullLogger<DiscogsEffnetEmbeddingExtractor>.Instance,
        modelPath: "nonexistent-model.onnx");

    public void Dispose() => _sut.Dispose();

    // ── IsAvailable ───────────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenModelFileAbsent()
        => Assert.False(_sut.IsAvailable);

    [Fact]
    public void ModelTag_ReturnsNull_WhenModelFileAbsent()
        => Assert.Null(_sut.ModelTag);

    // ── ExtractAsync graceful no-op ───────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ReturnsNull_WhenModelUnavailable()
    {
        var audio  = new float[44100]; // 1 s of silence
        var result = await _sut.ExtractAsync(audio);
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractAsync_ThrowsArgumentNull_ForNullInput()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => _sut.ExtractAsync(null!));

    // ── PopulateEntityAsync no-op ─────────────────────────────────────────────

    [Fact]
    public async Task PopulateEntityAsync_LeavesEntityUnchanged_WhenModelUnavailable()
    {
        var entity = new AudioFeaturesEntity { TrackUniqueHash = "test-hash" };
        await _sut.PopulateEntityAsync(entity, new float[44100]);

        Assert.Null(entity.EmbeddingBlob);
        Assert.Null(entity.EmbeddingModelTag);
    }

    [Fact]
    public async Task PopulateEntityAsync_ThrowsArgumentNull_ForNullEntity()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => _sut.PopulateEntityAsync(null!, new float[44100]));

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ReturnsNull_WhenTokenCancelledAndModelUnavailable()
    {
        // When the model is absent, the service returns null before reaching the CT check.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.ExtractAsync(new float[100], cts.Token);
        Assert.Null(result);
    }

    // ── AudioFeaturesEntity.Embedding round-trip ──────────────────────────────

    [Fact]
    public void Entity_Embedding_RoundTrip_ViaMemoryMarshal()
    {
        var entity = new AudioFeaturesEntity();
        var original = new float[DiscogsEffnetEmbeddingExtractor.EmbeddingDimension];
        for (int i = 0; i < original.Length; i++) original[i] = i * 0.001f;

        entity.Embedding = original;

        Assert.NotNull(entity.EmbeddingBlob);
        Assert.Equal(DiscogsEffnetEmbeddingExtractor.EmbeddingDimension * sizeof(float),
            entity.EmbeddingBlob!.Length);

        var roundTripped = entity.Embedding!;
        Assert.Equal(original.Length, roundTripped.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], roundTripped[i], precision: 6);
    }

    [Fact]
    public void Entity_Embedding_Null_ClearsBlob()
    {
        var entity = new AudioFeaturesEntity
        {
            Embedding = new float[DiscogsEffnetEmbeddingExtractor.EmbeddingDimension],
        };

        entity.Embedding = null;
        Assert.Null(entity.EmbeddingBlob);
    }

    [Fact]
    public void EmbeddingDimension_Is2048()
        => Assert.Equal(2048, DiscogsEffnetEmbeddingExtractor.EmbeddingDimension);
}
