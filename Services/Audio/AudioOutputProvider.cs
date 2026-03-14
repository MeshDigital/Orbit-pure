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
    
    public AudioOutputMode CurrentMode { get; private set; } = AudioOutputMode.WasapiShared;
    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _outputDevice?.PlaybackState == PlaybackState.Stopped;
    
    /// <summary>
    /// Event raised when playback stops.
    /// </summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    
    /// <summary>
    /// Gets available ASIO drivers on the system.
    /// </summary>
    public static IEnumerable<string> GetAsioDriverNames()
    {
        try
        {
            return AsioOut.GetDriverNames();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }
    
    /// <summary>
    /// Gets available WASAPI output devices.
    /// </summary>
    public static IEnumerable<string> GetWasapiDeviceNames()
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.Select(d => d.FriendlyName);
    }
    
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
            Console.WriteLine($"[AudioOutputProvider] Failed to initialize {mode}: {ex.Message}");
            
            // Fallback to WaveOut if preferred mode fails
            if (mode != AudioOutputMode.WaveOut)
            {
                Console.WriteLine("[AudioOutputProvider] Falling back to WaveOut...");
                CurrentMode = AudioOutputMode.WaveOut;
                _outputDevice = new WaveOutEvent { DesiredLatency = 100 };
                _outputDevice.Init(source);
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
            else
            {
                throw;
            }
        }
    }
    
    private IWavePlayer CreateOutputDevice(AudioOutputMode mode, string? deviceName)
    {
        return mode switch
        {
            AudioOutputMode.WaveOut => new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 },
            
            AudioOutputMode.WasapiShared => new WasapiOut(
                deviceName != null ? GetWasapiDevice(deviceName) : GetDefaultWasapiDevice(),
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: 50), // 50ms shared mode
            
            AudioOutputMode.WasapiExclusive => new WasapiOut(
                deviceName != null ? GetWasapiDevice(deviceName) : GetDefaultWasapiDevice(),
                AudioClientShareMode.Exclusive,
                useEventSync: true,
                latency: 10), // 10ms exclusive mode
            
            AudioOutputMode.Asio => CreateAsioDevice(deviceName),
            
            _ => throw new NotSupportedException($"Unsupported audio mode: {mode}")
        };
    }
    
    private static MMDevice GetDefaultWasapiDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
    
    private static MMDevice GetWasapiDevice(string friendlyName)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.FirstOrDefault(d => d.FriendlyName == friendlyName) 
               ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
    
    private static IWavePlayer CreateAsioDevice(string? driverName)
    {
        var drivers = AsioOut.GetDriverNames();
        
        if (!drivers.Any())
        {
            throw new InvalidOperationException("No ASIO drivers found on this system.");
        }
        
        string selectedDriver = driverName ?? drivers.First();
        
        if (!drivers.Contains(selectedDriver))
        {
            Console.WriteLine($"[AudioOutputProvider] ASIO driver '{selectedDriver}' not found, using '{drivers.First()}'");
            selectedDriver = drivers.First();
        }
        
        return new AsioOut(selectedDriver);
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
