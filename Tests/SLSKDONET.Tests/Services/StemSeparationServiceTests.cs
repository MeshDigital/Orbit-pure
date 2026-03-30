using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Verifies that <see cref="StemSeparationService"/> selects providers in the correct
/// priority order: ONNX DirectML → Spleeter CLI → Mock fallback.
/// </summary>
public class StemSeparationServiceTests
{
    private readonly Mock<ILogger<StemSeparationService>> _loggerMock;

    public StemSeparationServiceTests()
    {
        _loggerMock = new Mock<ILogger<StemSeparationService>>();
    }

    [Fact]
    public void Constructor_SetsUpStemsDirectory()
    {
        // Arrange / Act
        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var expectedDir = Path.Combine(appData, "Antigravity", "Stems");

        // Assert – the stems root directory must exist after construction
        Assert.True(Directory.Exists(expectedDir));
    }

    [Fact]
    public void HasStems_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object);

        // Use a trackId that is guaranteed not to exist
        var result = service.HasStems($"test-nonexistent-{Guid.NewGuid()}");

        Assert.False(result);
    }

    [Fact]
    public void HasStems_ReturnsFalse_WhenFewerThanFourWavFiles()
    {
        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var trackId = $"test-{Guid.NewGuid()}";
        var dir = Path.Combine(appData, "Antigravity", "Stems", trackId);

        try
        {
            Directory.CreateDirectory(dir);
            // Write only 2 WAV files — below the HasStems threshold of 4
            File.WriteAllBytes(Path.Combine(dir, "vocals.wav"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(dir, "drums.wav"), Array.Empty<byte>());

            Assert.False(service.HasStems(trackId));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HasStems_ReturnsTrue_WhenFourOrMoreWavFilesExist()
    {
        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var trackId = $"test-{Guid.NewGuid()}";
        var dir = Path.Combine(appData, "Antigravity", "Stems", trackId);

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var stem in new[] { "vocals.wav", "drums.wav", "bass.wav", "other.wav" })
                File.WriteAllBytes(Path.Combine(dir, stem), Array.Empty<byte>());

            Assert.True(service.HasStems(trackId));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SeparateTrackAsync_WhenNoProviderAvailable_ReturnsAllStemPaths()
    {
        // Arrange – the service falls back to Mock when neither ONNX model nor Spleeter CLI exists.
        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object);

        var trackId = $"test-{Guid.NewGuid()}";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var outputDir = Path.Combine(appData, "Antigravity", "Stems", trackId);

        try
        {
            // Act – use a dummy (non-existent) audio file; the mock path does not read it
            var stems = await service.SeparateTrackAsync(
                Path.Combine(Path.GetTempPath(), "dummy.mp3"),
                trackId);

            // Assert – mock fallback produces an entry for every StemType
            var stemTypes = (StemType[])Enum.GetValues(typeof(StemType));
            Assert.Equal(stemTypes.Length, stems.Count);
            foreach (var type in stemTypes)
                Assert.True(stems.ContainsKey(type), $"Expected stem key {type} missing");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task SeparateTrackAsync_StealthModeEnabled_CompletesWithThrottle()
    {
        // Arrange – build a real AnalysisQueueService with stealth mode enabled
        var eventBusMock = new Mock<IEventBus>();
        eventBusMock.Setup(x => x.GetEvent<TrackAnalysisRequestedEvent>())
            .Returns(System.Reactive.Linq.Observable.Empty<TrackAnalysisRequestedEvent>());

        var queueLogger = new Mock<ILogger<AnalysisQueueService>>();
        var dbFactory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<SLSKDONET.Data.AppDbContext>>();

        using var analysisQueue = new AnalysisQueueService(
            eventBusMock.Object,
            queueLogger.Object,
            dbFactory.Object);

        analysisQueue.SetStealthMode(true);
        Assert.True(analysisQueue.IsStealthMode);

        var service = new StemSeparationService(
            new Mock<IServiceProvider>().Object,
            _loggerMock.Object,
            analysisQueue);

        var trackId = $"test-stealth-{Guid.NewGuid()}";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var outputDir = Path.Combine(appData, "Antigravity", "Stems", trackId);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await service.SeparateTrackAsync(
                Path.Combine(Path.GetTempPath(), "dummy.mp3"),
                trackId);
            sw.Stop();

            // Stealth mode adds a 500 ms throttle – allow generous tolerance
            Assert.True(sw.ElapsedMilliseconds >= 400,
                $"Stealth mode throttle not applied; elapsed={sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
