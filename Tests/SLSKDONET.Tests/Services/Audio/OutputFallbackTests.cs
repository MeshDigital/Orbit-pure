using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// A mix opens a second output for the incoming deck. An ASIO driver allows one stream, so that
/// second open fails and the deck falls back. The fallback used to retry the ASIO driver name as a
/// WASAPI device name, which never matches, so the incoming track silently played on the Windows
/// default device (laptop speakers instead of the audio interface) with only a log line.
/// </summary>
public sealed class OutputFallbackTests
{
    private static readonly ISampleProvider Source = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)).ToSampleProvider();

    [Fact]
    public void Asio_DoesNotRetryTheDriverNameAsAWasapiDevice()
    {
        var backend = new FakeBackend();

        var attempts = AudioPlayerService.BuildOutputAttempts(AudioOutputMode.Asio, "Focusrite USB ASIO", backend);

        Assert.Equal(new[] { "Asio/Focusrite USB ASIO", "System default output" }, attempts.Select(a => a.Label));
        foreach (var (_, create) in attempts) create();
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("Shared:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AudioOutputMode.WaveOut)]
    [InlineData(AudioOutputMode.WasapiExclusive)] // legacy configs; Settings no longer offers it
    public void NonAsioModes_KeepTheSharedModeRetryOnTheChosenDevice(AudioOutputMode mode)
    {
        var attempts = AudioPlayerService.BuildOutputAttempts(mode, "Speakers (Realtek)", new FakeBackend());

        Assert.Equal(new[] { $"{mode}/Speakers (Realtek)", "WasapiShared/Speakers (Realtek)", "System default output" },
            attempts.Select(a => a.Label));
    }

    [Fact]
    public void WasapiShared_TriesTheDeviceThenTheDefault()
    {
        var attempts = AudioPlayerService.BuildOutputAttempts(AudioOutputMode.WasapiShared, "Speakers (Realtek)", new FakeBackend());

        Assert.Equal(new[] { "WasapiShared/Speakers (Realtek)", "System default output" }, attempts.Select(a => a.Label));
    }

    [Fact]
    public void ConfiguredOutputOpens_NoFallbackReported()
    {
        var first = new FakeOutput();
        var attempts = new List<(string, Func<IWavePlayer>)> { ("Asio/X", () => first), ("System default output", () => new FakeOutput()) };

        var output = AudioPlayerService.OpenFirstAvailable(attempts, Source, out var fallback);

        Assert.Same(first, output);
        Assert.Null(fallback);
    }

    [Fact]
    public void SecondAsioStreamFails_FallbackIsReportedWithTheReason()
    {
        var busy = new FakeOutput { InitError = new InvalidOperationException("ASIO driver already in use") };
        var fallbackDevice = new FakeOutput();
        var attempts = new List<(string, Func<IWavePlayer>)>
        {
            ("Asio/Focusrite USB ASIO", () => busy),
            ("System default output", () => fallbackDevice),
        };

        var output = AudioPlayerService.OpenFirstAvailable(attempts, Source, out var fallback);

        Assert.Same(fallbackDevice, output);
        Assert.True(busy.Disposed, "the output that failed Init must be released");
        Assert.NotNull(fallback);
        Assert.Equal("Asio/Focusrite USB ASIO", fallback!.Configured);
        Assert.Equal("System default output", fallback.Actual);
        Assert.True(fallback.ConfiguredIsAsio);
        Assert.Same(busy.InitError, fallback.Reason);
    }

    [Fact]
    public void NonAsioFallback_IsNotFlaggedAsAsio()
    {
        var attempts = new List<(string, Func<IWavePlayer>)>
        {
            ("WaveOut/Speakers", () => new FakeOutput { InitError = new InvalidOperationException("busy") }),
            ("System default output", () => new FakeOutput()),
        };

        AudioPlayerService.OpenFirstAvailable(attempts, Source, out var fallback);

        Assert.False(fallback!.ConfiguredIsAsio);
    }

    [Fact]
    public void NothingOpens_Throws()
    {
        var attempts = new List<(string, Func<IWavePlayer>)>
        {
            ("Asio/X", () => new FakeOutput { InitError = new InvalidOperationException("a") }),
            ("System default output", () => throw new InvalidOperationException("b")),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => AudioPlayerService.OpenFirstAvailable(attempts, Source, out _));
        Assert.Equal("b", ex.InnerException!.Message);
    }

    [Fact]
    public void AsioToast_NamesBothOutputsAndPointsToWasapi()
    {
        var (title, message) = PlayerViewModel.DescribeOutputFallback(new OutputFallbackEventArgs
        {
            Configured = "Asio/Focusrite USB ASIO",
            Actual = "System default output",
            ConfiguredIsAsio = true,
        });

        Assert.Equal("Mix played on another output", title);
        Assert.Contains("Asio/Focusrite USB ASIO", message);
        Assert.Contains("System default output", message);
        Assert.Contains("WASAPI", message);
    }

    private sealed class FakeBackend : IAudioOutputBackend
    {
        public List<string> Calls { get; } = new();
        public string BackendName => "Fake";
        public IWavePlayer CreateDevice(AudioOutputMode mode, string? deviceName) { Calls.Add($"Device:{mode}/{deviceName}"); return new FakeOutput(); }
        public IWavePlayer CreateSharedDevice(string? deviceName, int latencyMs = 100) { Calls.Add($"Shared:{deviceName}"); return new FakeOutput(); }
        public IWavePlayer CreateDefaultFallbackDevice(int latencyMs = 100) { Calls.Add("Default"); return new FakeOutput(); }
        public IEnumerable<string> GetOutputDeviceNames() => Array.Empty<string>();
        public IEnumerable<string> GetAsioDriverNames() => Array.Empty<string>();
    }

    private sealed class FakeOutput : IWavePlayer
    {
        public Exception? InitError { get; init; }
        public bool Disposed { get; private set; }
        public float Volume { get; set; } = 1f;
        public PlaybackState PlaybackState => PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) { if (InitError != null) throw InitError; }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
