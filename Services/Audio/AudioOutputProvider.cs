using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Audio output mode for the DAW engine.
/// </summary>
public enum AudioOutputMode
{
    /// <summary>Standard Windows audio (highest latency, most compatible).</summary>
    WaveOut,
    
    /// <summary>WASAPI Shared mode (~50-100ms latency).</summary>
    WasapiShared,
    
    /// <summary>WASAPI Exclusive mode (~10-20ms latency).</summary>
    WasapiExclusive,
    
    /// <summary>ASIO driver (lowest latency, requires driver).</summary>
    Asio
}

/// <summary>
/// Factory and manager for low-latency audio output devices.
/// Provides abstraction over WaveOut, WASAPI, and ASIO.
/// </summary>
public class AudioOutputProvider : IDisposable
{
    private IWavePlayer? _outputDevice;
    private ISampleProvider? _source;

    // Shared across all device resolution calls (every CreateDeck() on every track load/preload
    // used to construct a brand-new MMDeviceEnumerator just to look up a device). The enumerator
    // itself is a stateless COM factory — safe to reuse. Each call still does a fresh
    // EnumerateAudioEndPoints/GetDefaultAudioEndpoint lookup, so every deck still gets its own
    // independent MMDevice (and therefore its own AudioClient) — required for concurrent
    // dual-deck crossfade playback; sharing the resolved MMDevice itself would not be safe, since
    // WASAPI's IAudioClient can only be Initialize()'d once per device instance.
    private static readonly Lazy<MMDeviceEnumerator> _sharedEnumerator = new(() => new MMDeviceEnumerator());

    public AudioOutputMode CurrentMode { get; private set; } = AudioOutputMode.WasapiShared;
    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _outputDevice?.PlaybackState == PlaybackState.Stopped;
    
    /// <summary>
    /// Event raised when playback stops.
    /// </summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    
    /// <summary>
    /// Gets available ASIO drivers on the system (Windows only; empty on macOS/Linux, #455).
    /// </summary>
    public static IEnumerable<string> GetAsioDriverNames()
        => AudioOutputBackend.Current.GetAsioDriverNames();

    /// <summary>
    /// Gets available audio output devices via <see cref="AudioOutputBackend.Current"/> (#455).
    /// </summary>
    public static IEnumerable<string> GetWasapiDeviceNames()
        => AudioOutputBackend.Current.GetOutputDeviceNames();
    
    /// <summary>
    /// Initializes the audio output with the specified mode and source.
    /// </summary>
    public void Initialize(ISampleProvider source, AudioOutputMode mode, string? deviceName = null)
    {
        Dispose();
        
        _source = source;
        CurrentMode = mode;
        
        try
        {
            _outputDevice = CreateOutputDevice(mode, deviceName);
            _outputDevice.Init(source);
            _outputDevice.PlaybackStopped += OnPlaybackStopped;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AudioOutputProvider] Failed to initialize {Mode}", mode);

            // Fallback to default device if preferred mode fails (#455)
            if (mode != AudioOutputMode.WaveOut)
            {
                Serilog.Log.Information("[AudioOutputProvider] Falling back to default output backend...");
                CurrentMode = AudioOutputMode.WaveOut;
                _outputDevice = CreateDefaultFallbackDevice(100);
                _outputDevice.Init(source);
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
            else
            {
                throw;
            }
        }
    }
    
    private IWavePlayer CreateOutputDevice(AudioOutputMode mode, string? deviceName) => CreateDevice(mode, deviceName);

    /// <summary>
    /// Builds an output device for the given mode/device selection — a pure factory with no
    /// dependency on this instance's own lifecycle, so callers that manage their own
    /// <see cref="IWavePlayer"/> instances (e.g. <see cref="AudioPlayerService"/>'s per-deck
    /// devices) can honor the user's audio-output settings without duplicating the mode/device
    /// resolution logic here.
    /// </summary>
    public static IWavePlayer CreateDevice(AudioOutputMode mode, string? deviceName)
        => AudioOutputBackend.Current.CreateDevice(mode, deviceName);

    /// <summary>A shared-mode output on the named device (the OS default when null or not found, #455).</summary>
    public static IWavePlayer CreateSharedDevice(string? deviceName, int latencyMs = 100)
        => AudioOutputBackend.Current.CreateSharedDevice(deviceName, latencyMs);

    /// <summary>Default fallback output device for the host OS (#455).</summary>
    public static IWavePlayer CreateDefaultFallbackDevice(int latencyMs = 100)
        => AudioOutputBackend.Current.CreateDefaultFallbackDevice(latencyMs);

    /// <summary>
    /// Output for the preview players (library/waveform/cue auditions, Discover clips, transition
    /// previews): the device chosen in Settings, always in shared mode so it can play alongside
    /// the main player.
    /// </summary>
    public static IWavePlayer CreatePreviewDevice(string? outputMode, string? deviceName)
    {
        var wasapi = outputMode is null or "WasapiShared" or "WasapiExclusive";
        return CreateSharedDevice(wasapi ? deviceName : null, latencyMs: 100);
    }
    
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackStopped?.Invoke(this, e);
    }
    
    /// <summary>
    /// Starts playback.
    /// </summary>
    public void Play()
    {
        _outputDevice?.Play();
    }
    
    /// <summary>
    /// Pauses playback.
    /// </summary>
    public void Pause()
    {
        _outputDevice?.Pause();
    }
    
    /// <summary>
    /// Stops playback.
    /// </summary>
    public void Stop()
    {
        _outputDevice?.Stop();
    }
    
    /// <summary>
    /// Gets the actual latency of the current output device.
    /// </summary>
    public int GetLatencyMs()
    {
        return _outputDevice switch
        {
            WaveOutEvent waveOut => waveOut.DesiredLatency,
            WasapiOut wasapi => (int)wasapi.OutputWaveFormat.AverageBytesPerSecond, // Approximation
            AsioOut asio => (int)(asio.PlaybackLatency * 1000),
            _ => 100
        };
    }

    public void Dispose()
    {
        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            _outputDevice.Stop();
            _outputDevice.Dispose();
            _outputDevice = null;
        }
    }
}

/// <summary>
/// Settings for audio output configuration.
/// </summary>
public class AudioOutputSettings
{
    public AudioOutputMode Mode { get; set; } = AudioOutputMode.WasapiShared;
    public string? DeviceName { get; set; }
    public int BufferSizeMs { get; set; } = 50;
}
