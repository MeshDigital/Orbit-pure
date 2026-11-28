using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Cross-platform audio output seam (#455):
/// <list type="bullet">
/// <item>Windows: NAudio WASAPI Shared / WaveOut / ASIO (existing behaviour preserved).</item>
/// <item>macOS: CoreAudio via <c>ffplay</c> PCM stream (ARM64 + x64).</item>
/// <item>Linux: PipeWire (<c>pw-play</c>), PulseAudio (<c>paplay</c>), ALSA (<c>aplay</c>), or <c>ffplay</c> PCM stream.</item>
/// </list>
/// All audio players and preview engines route through this seam instead of instantiating
/// <see cref="WasapiOut"/> / <see cref="WaveOutEvent"/> directly.
/// </summary>
public interface IAudioOutputBackend
{
    /// <summary>Human-readable backend name (e.g. "Windows (NAudio)", "macOS (CoreAudio/PCM)", "Linux (PipeWire/PulseAudio)").</summary>
    string BackendName { get; }

    /// <summary>Creates an output device honoring the user's mode and device selection.</summary>
    IWavePlayer CreateDevice(AudioOutputMode mode, string? deviceName);

    /// <summary>Creates a shared-mode output device on the named device (or OS default when null).</summary>
    IWavePlayer CreateSharedDevice(string? deviceName, int latencyMs = 100);

    /// <summary>Creates the guaranteed default fallback device for this OS.</summary>
    IWavePlayer CreateDefaultFallbackDevice(int latencyMs = 100);

    /// <summary>Lists available output device names on the host OS.</summary>
    IEnumerable<string> GetOutputDeviceNames();

    /// <summary>Lists available ASIO driver names (Windows only; empty on macOS/Linux).</summary>
    IEnumerable<string> GetAsioDriverNames();

    /// <summary>
    /// Returns the output device's current native sample rate (e.g., WASAPI MixFormat rate or ASIO
    /// driver rate) so the unified mix bus runs fixed at the device rate for the whole session
    /// without restarting the device between tracks. Defaults to 44100 Hz if unavailable.
    /// </summary>
    int GetPreferredSampleRate(AudioOutputMode mode, string? deviceName) => 44100;
}

/// <summary>Windows implementation wrapping NAudio WASAPI / WaveOut / ASIO (#455).</summary>
public sealed class WindowsAudioOutputBackend : IAudioOutputBackend
{
    private static readonly Lazy<MMDeviceEnumerator> SharedEnumerator = new(() => new MMDeviceEnumerator());

    public string BackendName => "Windows (NAudio)";

    public IWavePlayer CreateDevice(AudioOutputMode mode, string? deviceName) => mode switch
    {
        AudioOutputMode.WaveOut => new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 },
        AudioOutputMode.WasapiShared or AudioOutputMode.WasapiExclusive => CreateSharedDevice(deviceName, latencyMs: 50),
        AudioOutputMode.Asio => CreateAsioDevice(deviceName),
        _ => throw new NotSupportedException($"Unsupported audio mode: {mode}")
    };

    public IWavePlayer CreateSharedDevice(string? deviceName, int latencyMs = 100) =>
        new WasapiOut(
            deviceName != null ? GetWasapiDevice(deviceName) : GetDefaultWasapiDevice(),
            AudioClientShareMode.Shared,
            useEventSync: true,
            latency: latencyMs);

    public IWavePlayer CreateDefaultFallbackDevice(int latencyMs = 100) =>
        new WasapiOut(AudioClientShareMode.Shared, latencyMs);

    public IEnumerable<string> GetOutputDeviceNames()
    {
        var names = new List<string>();
        foreach (var device in SharedEnumerator.Value.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (TryGetName(device) is { } name) names.Add(name);
        }
        return names;
    }

    public IEnumerable<string> GetAsioDriverNames()
    {
        try { return AsioOut.GetDriverNames(); }
        catch { return Enumerable.Empty<string>(); }
    }

    public int GetPreferredSampleRate(AudioOutputMode mode, string? deviceName)
    {
        try
        {
            if (mode == AudioOutputMode.Asio)
            {
                using var asio = (AsioOut)CreateAsioDevice(deviceName);
                // Prefer 48 kHz if 44.1 kHz isn't supported (e.g. an ASIO interface locked at 48 kHz).
                if (asio.IsSampleRateSupported(48000) && !asio.IsSampleRateSupported(44100))
                    return 48000;
                if (asio.IsSampleRateSupported(44100))
                    return 44100;
                if (asio.IsSampleRateSupported(48000))
                    return 48000;
                if (asio.IsSampleRateSupported(96000))
                    return 96000;
                return 44100;
            }

            var endpoint = deviceName != null ? GetWasapiDevice(deviceName) : GetDefaultWasapiDevice();
            int rate = endpoint.AudioClient.MixFormat.SampleRate;
            return rate > 0 ? rate : 44100;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[AudioOutputBackend] Could not query preferred sample rate for {Mode}/{Device}; using 44100 Hz", mode, deviceName ?? "default");
            return 44100;
        }
    }

    private static string? TryGetName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return null; }
    }

    private static MMDevice GetDefaultWasapiDevice() =>
        SharedEnumerator.Value.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    private static MMDevice GetWasapiDevice(string friendlyName)
    {
        var devices = SharedEnumerator.Value.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var match = devices.FirstOrDefault(d => TryGetName(d) == friendlyName)
                    ?? devices.FirstOrDefault(d => string.Equals(TryGetName(d), friendlyName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            Serilog.Log.Warning("[AudioOutputBackend] Output device '{Device}' not found or not active — using the Windows default", friendlyName);
        return match ?? SharedEnumerator.Value.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static IWavePlayer CreateAsioDevice(string? driverName)
    {
        var drivers = AsioOut.GetDriverNames();
        if (!drivers.Any())
            throw new InvalidOperationException("No ASIO drivers found on this system.");

        string selectedDriver = driverName ?? drivers.First();
        if (!drivers.Contains(selectedDriver))
        {
            Serilog.Log.Warning("[AudioOutputBackend] ASIO driver '{Requested}' not found, using '{Fallback}'", selectedDriver, drivers.First());
            selectedDriver = drivers.First();
        }
        return new AsioOut(selectedDriver);
    }
}

/// <summary>
/// macOS &amp; Linux implementation of <see cref="IAudioOutputBackend"/> (#455).
/// Streams real-time PCM from NAudio's <see cref="IWaveProvider"/>/<see cref="ISampleProvider"/>
/// into the host OS audio daemon (<c>ffplay</c>/CoreAudio on macOS; <c>pw-play</c>/<c>paplay</c>/<c>aplay</c>/<c>ffplay</c> on Linux).
/// </summary>
public sealed class PosixAudioOutputBackend : IAudioOutputBackend
{
    private readonly bool _isMacOS;
    private readonly Func<string, bool> _fileExists;
    private readonly string _pathEnv;

    public PosixAudioOutputBackend(
        bool? isMacOS = null,
        Func<string, bool>? fileExists = null,
        string? pathEnv = null)
    {
        _isMacOS = isMacOS ?? OperatingSystem.IsMacOS();
        _fileExists = fileExists ?? File.Exists;
        _pathEnv = pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    public string BackendName => _isMacOS ? "macOS (CoreAudio/ffplay)" : "Linux (PipeWire/PulseAudio)";

    public IWavePlayer CreateDevice(AudioOutputMode mode, string? deviceName)
        => new PosixPcmWavePlayer(fmt => BuildPlayerStartInfo(fmt, deviceName));

    public IWavePlayer CreateSharedDevice(string? deviceName, int latencyMs = 100)
        => new PosixPcmWavePlayer(fmt => BuildPlayerStartInfo(fmt, deviceName));

    public IWavePlayer CreateDefaultFallbackDevice(int latencyMs = 100)
        => new PosixPcmWavePlayer(fmt => BuildPlayerStartInfo(fmt, deviceName: null));

    public IEnumerable<string> GetOutputDeviceNames() => new[] { "System Default" };

    public IEnumerable<string> GetAsioDriverNames() => Enumerable.Empty<string>();

    internal ProcessStartInfo BuildPlayerStartInfo(WaveFormat format, string? deviceName)
    {
        var rate = format.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var channels = format.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!_isMacOS)
        {
            if (FindOnPath("pw-play") is { } pwPlay)
            {
                var psi = CreatePipePsi(pwPlay);
                psi.ArgumentList.Add("--raw");
                psi.ArgumentList.Add("--format=f32");
                psi.ArgumentList.Add($"--rate={rate}");
                psi.ArgumentList.Add($"--channels={channels}");
                psi.ArgumentList.Add("-");
                return psi;
            }

            if (FindOnPath("paplay") is { } paPlay)
            {
                var psi = CreatePipePsi(paPlay);
                psi.ArgumentList.Add("--raw");
                psi.ArgumentList.Add("--format=float32le");
                psi.ArgumentList.Add($"--rate={rate}");
                psi.ArgumentList.Add($"--channels={channels}");
                if (!string.IsNullOrWhiteSpace(deviceName) && !string.Equals(deviceName, "System Default", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add($"--device={deviceName}");
                return psi;
            }
        }

        // macOS (CoreAudio via ffplay) and Linux fallback
        var ffplay = FindOnPath("ffplay") ?? "ffplay";
        var ffPsi = CreatePipePsi(ffplay);
        foreach (var arg in new[]
        {
            "-nodisp", "-autoexit", "-loglevel", "quiet",
            "-f", "f32le", "-ar", rate, "-ac", channels, "-i", "pipe:0"
        })
        {
            ffPsi.ArgumentList.Add(arg);
        }
        return ffPsi;
    }

    private static ProcessStartInfo CreatePipePsi(string fileName) => new(fileName)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private string? FindOnPath(string binary)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_pathEnv))
            dirs.AddRange(_pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries));
        if (_isMacOS)
        {
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
            dirs.Add("/usr/bin");
        }
        else
        {
            dirs.Add("/usr/bin");
            dirs.Add("/usr/local/bin");
        }

        foreach (var dir in dirs)
        {
            var full = Path.Combine(dir.Trim(), binary);
            if (_fileExists(full)) return full;
        }
        return null;
    }
}

/// <summary>
/// Cross-platform <see cref="IWavePlayer"/> that pumps 32-bit float PCM from an <see cref="IWaveProvider"/>
/// to a POSIX audio sink process (<c>ffplay</c> / <c>pw-play</c> / <c>paplay</c>) or an injected test stream (#455).
/// </summary>
public sealed class PosixPcmWavePlayer : IWavePlayer
{
    private readonly Func<WaveFormat, ProcessStartInfo>? _startInfoFactory;
    private readonly Func<WaveFormat, Stream>? _streamFactory;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private ISampleProvider? _sampleProvider;
    private WaveFormat? _outputFormat;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Process? _process;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile float _volume = 1.0f;
    private bool _disposed;

    public PosixPcmWavePlayer(Func<WaveFormat, ProcessStartInfo> startInfoFactory)
    {
        _startInfoFactory = startInfoFactory ?? throw new ArgumentNullException(nameof(startInfoFactory));
    }

    /// <summary>Test constructor accepting a custom PCM sink stream without spawning an OS process.</summary>
    internal PosixPcmWavePlayer(Func<WaveFormat, Stream> streamFactory)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public PlaybackState PlaybackState => _state;

    public WaveFormat OutputWaveFormat => _outputFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider)
    {
        if (waveProvider is null) throw new ArgumentNullException(nameof(waveProvider));
        _sampleProvider = waveProvider.ToSampleProvider();
        _outputFormat = _sampleProvider.WaveFormat;
    }

    public void Play()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PosixPcmWavePlayer));
        if (_sampleProvider is null || _outputFormat is null)
            throw new InvalidOperationException("Init must be called before Play.");

        if (_state == PlaybackState.Paused)
        {
            _state = PlaybackState.Playing;
            _pauseGate.Set();
            return;
        }

        if (_state == PlaybackState.Playing)
            return;

        _cts = new CancellationTokenSource();
        _pauseGate.Set();
        _state = PlaybackState.Playing;
        var token = _cts.Token;
        _pumpTask = Task.Run(() => PumpLoop(token), token);
    }

    public void Pause()
    {
        if (_state != PlaybackState.Playing) return;
        _state = PlaybackState.Paused;
        _pauseGate.Reset();
    }

    public void Stop()
    {
        if (_state == PlaybackState.Stopped) return;
        _state = PlaybackState.Stopped;
        _pauseGate.Set();
        _cts?.Cancel();
        KillProcess();
    }

    private void PumpLoop(CancellationToken ct)
    {
        Exception? failure = null;
        Stream? sink = null;
        try
        {
            var format = _outputFormat!;
            if (_streamFactory is not null)
            {
                sink = _streamFactory(format);
            }
            else
            {
                var psi = _startInfoFactory!(format);
                _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start audio output '{psi.FileName}'.");
                sink = _process.StandardInput.BaseStream;
            }

            // ~40ms chunks at 44.1kHz stereo
            var floatBuf = new float[4096];
            var byteBuf = new byte[floatBuf.Length * sizeof(float)];

            while (!ct.IsCancellationRequested)
            {
                _pauseGate.Wait(ct);
                int read = _sampleProvider!.Read(floatBuf, 0, floatBuf.Length);
                if (read <= 0) break;

                float vol = _volume;
                if (Math.Abs(vol - 1.0f) > 0.0001f)
                {
                    for (int i = 0; i < read; i++) floatBuf[i] *= vol;
                }

                int bytes = read * sizeof(float);
                Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, bytes);
                sink.Write(byteBuf, 0, bytes);
            }
            sink.Flush();
        }
        catch (OperationCanceledException)
        {
            // Normal stop
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try { sink?.Dispose(); } catch { /* intentional: best-effort teardown */ }
            KillProcess();
            _state = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private void KillProcess()
    {
        var proc = Interlocked.Exchange(ref _process, null);
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { /* intentional: process may already have exited */ }
        finally
        {
            try { proc.Dispose(); } catch { /* intentional */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
        _pauseGate.Dispose();
    }
}

/// <summary>Process-wide accessor for audio output creation (#455).</summary>
public static class AudioOutputBackend
{
    private static IAudioOutputBackend? _current;

    public static IAudioOutputBackend Current => _current ??= CreateDefault();

    public static void SetCurrent(IAudioOutputBackend backend)
        => _current = backend ?? throw new ArgumentNullException(nameof(backend));

    private static IAudioOutputBackend CreateDefault() =>
        OperatingSystem.IsWindows()
            ? new WindowsAudioOutputBackend()
            : new PosixAudioOutputBackend();
}
