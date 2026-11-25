using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Cross-platform audio decoding seam (#456):
/// <list type="bullet">
/// <item>Windows: uses NAudio / Media Foundation where supported, falling back to FFmpeg when a codec is missing or rejects a file.</item>
/// <item>macOS / Linux: uses pure managed WAV reading for plain <c>.wav</c> files and FFmpeg for FLAC, M4A/AAC, AIFF, MP3, OGG, Opus, etc.
/// Because the FFmpeg output is plain PCM WAV, NAudio's managed reader opens it with sample-accurate seeking and zero Windows Media Foundation/ACM calls.</item>
/// </list>
/// </summary>
public interface IAudioDecoder
{
    /// <summary>Human-readable backend identifier (e.g. "Windows (MediaFoundation + FFmpeg)" or "FFmpeg").</summary>
    string BackendName { get; }

    /// <summary>
    /// True when <paramref name="filePath"/> must be decoded via FFmpeg before NAudio's managed
    /// reader can open it on the current OS.
    /// </summary>
    bool RequiresFfmpegForPlayback(string filePath);

    /// <summary>
    /// Opens <paramref name="filePath"/> for playback with frame-accurate seeking.
    /// <paramref name="playablePath"/> is the actual file opened (original or cached FFmpeg WAV).
    /// </summary>
    AudioFileReader OpenForPlayback(string filePath, out string playablePath);

    /// <summary>Frame-accurate seek compatible with both Media Foundation and FFmpeg-backed WAV readers.</summary>
    void SeekExact(AudioFileReader reader, string playablePath, double seconds);

    /// <summary>
    /// Decodes up to <paramref name="maxSeconds"/> of mono float PCM samples for integrity/spectral checks.
    /// </summary>
    (float[] Samples, int SampleRate) DecodeMonoSamples(
        string filePath,
        int maxSeconds,
        int targetSampleRate = 44100,
        CancellationToken ct = default);
}

/// <summary>Default cross-platform <see cref="IAudioDecoder"/> implementation (#456).</summary>
public sealed class CrossPlatformAudioDecoder : IAudioDecoder
{
    private readonly bool _isWindows;
    private readonly Func<string, string> _ensureDecodedWav;

    public CrossPlatformAudioDecoder(
        bool? isWindows = null,
        Func<string, string>? ensureDecodedWav = null)
    {
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _ensureDecodedWav = ensureDecodedWav ?? PlayableAudio.EnsureDecodedCopy;
    }

    public string BackendName => _isWindows ? "Windows (MediaFoundation + FFmpeg)" : "FFmpeg";

    public bool RequiresFfmpegForPlayback(string filePath)
    {
        if (_isWindows)
            return false; // Windows tries native AudioFileReader first and falls back to FFmpeg on failure.

        // On macOS/Linux, NAudio's WaveFileReader (plain .wav) is 100% managed C#.
        // MP3 (ACM), FLAC, M4A/AAC, AIFF-C, OGG, and Opus all require FFmpeg decode.
        var ext = Path.GetExtension(filePath);
        return !string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase);
    }

    public AudioFileReader OpenForPlayback(string filePath, out string playablePath)
    {
        if (RequiresFfmpegForPlayback(filePath))
        {
            playablePath = _ensureDecodedWav(filePath);
            return new AudioFileReader(playablePath);
        }

        try
        {
            var reader = new AudioFileReader(filePath);
            playablePath = filePath;
            return reader;
        }
        catch (Exception ex) when (File.Exists(filePath))
        {
            Serilog.Log.Information("[AudioDecoder] Native reader couldn't decode {File} ({Error}) — using FFmpeg copy",
                Path.GetFileName(filePath), ex.Message.Split('\n')[0].Trim());
            playablePath = _ensureDecodedWav(filePath);
            return new AudioFileReader(playablePath);
        }
    }

    public void SeekExact(AudioFileReader reader, string playablePath, double seconds)
        => ExactSeek.Seek(reader, playablePath, seconds);

    public (float[] Samples, int SampleRate) DecodeMonoSamples(
        string filePath,
        int maxSeconds,
        int targetSampleRate = 44100,
        CancellationToken ct = default)
    {
        if (!RequiresFfmpegForPlayback(filePath))
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                return DrainMono(reader, maxSeconds, ct);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                            or InvalidOperationException
                                            or System.ComponentModel.Win32Exception
                                            or DllNotFoundException
                                            or PlatformNotSupportedException)
            {
                // Fall through to FFmpeg pipe.
            }
        }

        return DecodeMonoViaFfmpegPipe(filePath, maxSeconds, targetSampleRate, ct);
    }

    private static (float[] Samples, int SampleRate) DrainMono(AudioFileReader reader, int maxSeconds, CancellationToken ct)
    {
        int sr = reader.WaveFormat.SampleRate;
        int ch = reader.WaveFormat.Channels;
        int maxFrames = sr * Math.Max(1, maxSeconds);

        var mono = new float[maxFrames];
        var readBuf = new float[8192 * ch];
        int framesWritten = 0;

        while (framesWritten < maxFrames)
        {
            ct.ThrowIfCancellationRequested();
            int framesWanted = Math.Min(8192, maxFrames - framesWritten);
            int samplesRead = reader.Read(readBuf, 0, framesWanted * ch);
            if (samplesRead == 0) break;

            int framesRead = samplesRead / ch;
            for (int f = 0; f < framesRead; f++)
            {
                float sum = 0f;
                for (int c = 0; c < ch; c++) sum += readBuf[f * ch + c];
                mono[framesWritten++] = sum / ch;
            }
        }

        if (framesWritten < maxFrames)
            Array.Resize(ref mono, framesWritten);

        return (mono, sr);
    }

    private static (float[] Samples, int SampleRate) DecodeMonoViaFfmpegPipe(
        string filePath,
        int maxSeconds,
        int targetSampleRate,
        CancellationToken ct)
    {
        var ffmpeg = AudioAnalysis.AudioIngestionPipeline.ResolveFfmpegPath();
        if (!File.Exists(ffmpeg) && !ExistsOnSystemPath(ffmpeg))
            throw new InvalidOperationException(
                $"Cannot decode '{Path.GetExtension(filePath)}' and FFmpeg is not available.");

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-v", "quiet",
            "-i", filePath,
            "-t", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "1",
            "-ar", targetSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "s16le",
            "-",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg process.");
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(); }
            catch { /* intentional: process may already have exited */ }
        });

        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        ct.ThrowIfCancellationRequested();

        byte[] raw = ms.ToArray();
        int sampleCount = raw.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = BitConverter.ToInt16(raw, i * 2);
            samples[i] = s / 32768f;
        }

        return (samples, targetSampleRate);
    }

    private static bool ExistsOnSystemPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv == null) return false;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), name))) return true;
            }
            catch
            {
                // intentional: malformed PATH entry
            }
        }
        return false;
    }
}

/// <summary>Process-wide accessor for static audio decode call sites (#456).</summary>
public static class AudioDecoder
{
    private static IAudioDecoder? _current;

    public static IAudioDecoder Current => _current ??= new CrossPlatformAudioDecoder();

    public static void SetCurrent(IAudioDecoder decoder)
        => _current = decoder ?? throw new ArgumentNullException(nameof(decoder));
}
