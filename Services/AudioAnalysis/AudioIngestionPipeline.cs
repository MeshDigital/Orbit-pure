using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Decodes any audio format to a normalised 44100 Hz stereo PCM WAV temp file
/// using FFmpeg as a subprocess.  Used by all downstream analysis services so
/// that format handling is centralised in one place.
/// </summary>
public sealed class AudioIngestionPipeline
{
    private readonly string _ffmpegPath;
    private readonly ILogger<AudioIngestionPipeline> _logger;

    /// <summary>
    /// Target sample rate for all decoded audio (Hz).
    /// Essentia and ONNX stem models both expect 44100 Hz.
    /// </summary>
    public const int TargetSampleRate = 44100;

    /// <summary>Target channel count (stereo).</summary>
    public const int TargetChannels = 2;

    public AudioIngestionPipeline(ILogger<AudioIngestionPipeline> logger, string? ffmpegPath = null)
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath ?? ResolveFfmpegPath();
    }

    /// <summary>
    /// Decodes <paramref name="source"/> to a normalised 44.1 kHz stereo WAV
    /// written to a file in <see cref="Path.GetTempPath()"/>.
    /// The caller is responsible for deleting the temp file.
    /// </summary>
    /// <returns>Absolute path of the decoded WAV temp file.</returns>
    /// <exception cref="InvalidOperationException">FFmpeg not found or decode failed.</exception>
    public async Task<string> DecodeToTempWavAsync(
        TrackAudioSource source,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_ffmpegPath))
            throw new InvalidOperationException(
                $"FFmpeg not found at '{_ffmpegPath}'. Install FFmpeg and ensure it is on PATH.");

        string tempFile = Path.Combine(Path.GetTempPath(),
            $"orbit_ingest_{Guid.NewGuid():N}.wav");

        string args = BuildFfmpegArgs(source.FilePath, tempFile);
        _logger.LogDebug("[AudioIngestion] Decoding {File} → {Tmp}", source.FilePath, tempFile);

        var (exitCode, stderr) = await RunProcessAsync(_ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            // Clean up partial output before throwing
            TryDelete(tempFile);
            _logger.LogError("[AudioIngestion] FFmpeg exited {Code} for {File}: {Err}",
                exitCode, source.FilePath, stderr);
            throw new InvalidOperationException(
                $"FFmpeg decode failed (exit {exitCode}) for '{source.FilePath}': {stderr.Trim()}");
        }

        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
        {
            TryDelete(tempFile);
            throw new InvalidOperationException(
                $"FFmpeg produced an empty output file for '{source.FilePath}'.");
        }

        _logger.LogDebug("[AudioIngestion] Decode complete → {Tmp}", tempFile);
        return tempFile;
    }

    /// <summary>
    /// Reads a decoded WAV temp file (produced by <see cref="DecodeToTempWavAsync"/>) into a
    /// flat interleaved float[] buffer and returns sample rate + channel count.
    /// </summary>
    public static (float[] Samples, int SampleRate, int Channels) ReadPcmFloat(string wavPath)
    {
        using var reader = new NAudio.Wave.AudioFileReader(wavPath);
        int samplesTotal = (int)(reader.Length / sizeof(float));
        var buffer = new float[samplesTotal];
        int read = reader.Read(buffer, 0, samplesTotal);
        return (buffer[..read], reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    }

    // ──────────────────────────────────── helpers ──────────────────────────

    private static string BuildFfmpegArgs(string input, string output)
    {
        // -y          = overwrite output
        // -vn         = no video
        // -ar 44100   = resample to 44100 Hz
        // -ac 2       = stereo
        // -c:a pcm_f32le = 32-bit float little-endian PCM
        var sb = new StringBuilder();
        sb.Append("-y -v error ");
        sb.Append($"-i \"{input}\" ");
        sb.Append("-vn ");
        sb.Append($"-ar {TargetSampleRate} ");
        sb.Append($"-ac {TargetChannels} ");
        sb.Append("-c:a pcm_f32le ");
        sb.Append($"\"{output}\"");
        return sb.ToString();
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError  = true,
            RedirectStandardOutput = false,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, stderrBuilder.ToString());
    }

    private static string ResolveFfmpegPath()
    {
        // 1. Bundled alongside the executable
        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;

        // 2. Relative tools folder
        string toolsPath = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(toolsPath)) return toolsPath;

        // 3. Fall back to system PATH (let the OS find it)
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
