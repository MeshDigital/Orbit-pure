using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit tests for NativeDependencyHealthService — aggregate health logic, event firing, and
/// DependencyStatus record semantics. Process-launching methods are replaced by a testable
/// subclass so no external binaries are required.
/// </summary>
public class NativeDependencyHealthServiceTests
{
    // ─────────────────────────────────────────────────────────────────
    // Testable subclass — overrides virtual check methods
    // ─────────────────────────────────────────────────────────────────

    private sealed class TestableHealthService : NativeDependencyHealthService
    {
        private readonly DependencyStatus _ffmpegStatus;
        private readonly DependencyStatus _essentiaStatus;

        public TestableHealthService(DependencyStatus ffmpegStatus, DependencyStatus essentiaStatus)
            : base(NullLogger<NativeDependencyHealthService>.Instance,
                   new PathProviderService(new SLSKDONET.Configuration.AppConfig(), new FileNameFormatter(), NullLogger<PathProviderService>.Instance))
        {
            _ffmpegStatus = ffmpegStatus;
            _essentiaStatus = essentiaStatus;
        }

        protected override Task<DependencyStatus> CheckFfmpegAsync()
            => Task.FromResult(_ffmpegStatus);

        protected override Task<DependencyStatus> CheckEssentiaAsync()
            => Task.FromResult(_essentiaStatus);
    }

    private static DependencyStatus AvailableFfmpeg()
        => new DependencyStatus("FFmpeg", true, "6.0.1", "System PATH");

    private static DependencyStatus UnavailableFfmpeg(string error = "not found")
        => new DependencyStatus("FFmpeg", false, "N/A", "System PATH", error);

    private static DependencyStatus AvailableEssentia()
        => new DependencyStatus("Essentia", true, "2.1-beta5", "/tools/essentia");

    private static DependencyStatus UnavailableEssentia(string error = "not found")
        => new DependencyStatus("Essentia", false, "N/A", "/tools/essentia", error);

    // ─────────────────────────────────────────────────────────────────
    // DependencyStatus record — basic structure
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DependencyStatus_Constructor_SetsAllProperties()
    {
        var status = new DependencyStatus("FFmpeg", true, "6.0", "System PATH", null);

        Assert.Equal("FFmpeg", status.Name);
        Assert.True(status.IsAvailable);
        Assert.Equal("6.0", status.Version);
        Assert.Equal("System PATH", status.Path);
        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public void DependencyStatus_WithErrorMessage_SetsErrorMessage()
    {
        var status = new DependencyStatus("FFmpeg", false, "N/A", "System PATH", "Process failed");

        Assert.False(status.IsAvailable);
        Assert.Equal("Process failed", status.ErrorMessage);
    }

    [Fact]
    public void DependencyStatus_ErrorMessageIsOptional_DefaultsToNull()
    {
        var status = new DependencyStatus("Essentia", true, "2.1", "/path/essentia");

        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public void DependencyStatus_RecordEquality_EqualWhenPropertiesMatch()
    {
        var a = new DependencyStatus("FFmpeg", true, "6.0", "System PATH");
        var b = new DependencyStatus("FFmpeg", true, "6.0", "System PATH");

        Assert.Equal(a, b);
    }

    // ─────────────────────────────────────────────────────────────────
    // CheckHealthAsync — IsHealthy aggregate
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_BothAvailable_IsHealthyTrue()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());

        await sut.CheckHealthAsync();

        Assert.True(sut.IsHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_FfmpegMissing_IsHealthyFalse()
    {
        var sut = new TestableHealthService(UnavailableFfmpeg(), AvailableEssentia());

        await sut.CheckHealthAsync();

        Assert.False(sut.IsHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_EssentiaMissing_IsHealthyFalse()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), UnavailableEssentia());

        await sut.CheckHealthAsync();

        Assert.False(sut.IsHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_BothMissing_IsHealthyFalse()
    {
        var sut = new TestableHealthService(UnavailableFfmpeg(), UnavailableEssentia());

        await sut.CheckHealthAsync();

        Assert.False(sut.IsHealthy);
    }

    // ─────────────────────────────────────────────────────────────────
    // CheckHealthAsync — status properties populated
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_PopulatesFfmpegStatus()
    {
        var expected = AvailableFfmpeg();
        var sut = new TestableHealthService(expected, AvailableEssentia());

        await sut.CheckHealthAsync();

        Assert.Equal(expected, sut.FfmpegStatus);
    }

    [Fact]
    public async Task CheckHealthAsync_PopulatesEssentiaStatus()
    {
        var expected = AvailableEssentia();
        var sut = new TestableHealthService(AvailableFfmpeg(), expected);

        await sut.CheckHealthAsync();

        Assert.Equal(expected, sut.EssentiaStatus);
    }

    [Fact]
    public async Task CheckHealthAsync_InitialState_StatusPropertiesAreNull()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());

        // Before calling CheckHealthAsync
        Assert.Null(sut.FfmpegStatus);
        Assert.Null(sut.EssentiaStatus);
    }

    // ─────────────────────────────────────────────────────────────────
    // CheckHealthAsync — HealthChanged event
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthChanged_FiresWhenHealthTransitionsFromUnhealthyToHealthy()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());
        bool? eventValue = null;
        sut.HealthChanged += (_, healthy) => eventValue = healthy;

        // IsHealthy starts as false (default), transitions to true — event should fire
        await sut.CheckHealthAsync();

        Assert.True(eventValue);
    }

    [Fact]
    public async Task HealthChanged_DoesNotFireWhenHealthRemainsUnchanged()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());

        // First call — transitions false → true
        await sut.CheckHealthAsync();

        var eventCount = 0;
        sut.HealthChanged += (_, _) => eventCount++;

        // Second call — health remains true, no event expected
        await sut.CheckHealthAsync();

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task HealthChanged_FiresWithFalseWhenHealthTransitionsToUnhealthy()
    {
        // Start healthy
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());
        await sut.CheckHealthAsync();
        Assert.True(sut.IsHealthy);

        // Now simulate unhealthy on second run — we need a new instance for that
        var sut2 = new TwoPhaseHealthService(
            phase1Ffmpeg: AvailableFfmpeg(), phase1Essentia: AvailableEssentia(),
            phase2Ffmpeg: UnavailableFfmpeg(), phase2Essentia: AvailableEssentia());

        await sut2.CheckHealthAsync(); // transitions false → true
        bool? lastEvent = null;
        sut2.HealthChanged += (_, v) => lastEvent = v;

        await sut2.CheckHealthAsync(); // transitions true → false

        Assert.False(lastEvent);
    }

    // ─────────────────────────────────────────────────────────────────
    // Initial state
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void InitialState_IsHealthyFalse()
    {
        var sut = new TestableHealthService(AvailableFfmpeg(), AvailableEssentia());

        Assert.False(sut.IsHealthy);
    }

    // ─────────────────────────────────────────────────────────────────
    // Two-phase helper for transition-to-unhealthy tests
    // ─────────────────────────────────────────────────────────────────

    private sealed class TwoPhaseHealthService : NativeDependencyHealthService
    {
        private readonly DependencyStatus _p1Ffmpeg;
        private readonly DependencyStatus _p1Essentia;
        private readonly DependencyStatus _p2Ffmpeg;
        private readonly DependencyStatus _p2Essentia;
        private int _callCount;

        public TwoPhaseHealthService(
            DependencyStatus phase1Ffmpeg, DependencyStatus phase1Essentia,
            DependencyStatus phase2Ffmpeg, DependencyStatus phase2Essentia)
            : base(NullLogger<NativeDependencyHealthService>.Instance,
                   new PathProviderService(new SLSKDONET.Configuration.AppConfig(), new FileNameFormatter(), NullLogger<PathProviderService>.Instance))
        {
            _p1Ffmpeg = phase1Ffmpeg;
            _p1Essentia = phase1Essentia;
            _p2Ffmpeg = phase2Ffmpeg;
            _p2Essentia = phase2Essentia;
        }

        protected override Task<DependencyStatus> CheckFfmpegAsync()
            => Task.FromResult(_callCount < 1 ? _p1Ffmpeg : _p2Ffmpeg);

        protected override Task<DependencyStatus> CheckEssentiaAsync()
        {
            _callCount++;
            return Task.FromResult(_callCount <= 1 ? _p1Essentia : _p2Essentia);
        }
    }
}
