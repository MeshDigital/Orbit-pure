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
        if (!File.Exists(_ffmpegPath) && !ExistsOnPath(_ffmpegPath))
        {
            // No FFmpeg (#331): decode with NAudio instead, so analysis still runs for the formats it reads
            // (WAV, AIFF, MP3, and on Windows FLAC/M4A via Media Foundation).
            try
            {
                return await Task.Run(() => DecodeWithoutFfmpeg(source.FilePath), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"FFmpeg not found at '{_ffmpegPath}', and the built-in decoder couldn't read '{Path.GetFileName(source.FilePath)}': {ex.Message}. Install FFmpeg (Settings → Advanced).", ex);
            }
        }

        string tempFile = Path.Combine(Path.GetTempPath(),
            $"orbit_ingest_{Guid.NewGuid():N}.wav");

        string args = BuildFfmpegArgs(source.FilePath, tempFile);
        _logger.LogDebug("[AudioIngestion] Decoding {File} → {Tmp}", source.FilePath, tempFile);

        var (exitCode, stderr) = await RunProcessAsync(_ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            TryDelete(tempFile);
            // Log only the last meaningful line so the multi-line FFmpeg dump doesn't
            // flood the Serilog console output.  Full stderr is in the exception for debug.
            var summary = stderr.Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.Length > 0) ?? stderr.Trim();
            _logger.LogError("[AudioIngestion] FFmpeg exit={Code} for {File}: {Summary}",
                exitCode, Path.GetFileName(source.FilePath), summary);
            throw new InvalidOperationException(
                $"FFmpeg exit {exitCode}: {summary}");
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

    /// <summary>Receives one chunk of interleaved float PCM. A chunk may end mid-frame; the next
    /// chunk continues with the following sample.</summary>
    public delegate void PcmChunkHandler(ReadOnlySpan<float> interleaved);

    /// <summary>
    /// Same FFmpeg conversion as <see cref="DecodeToTempWavAsync"/> (44.1 kHz stereo 32-bit float,
    /// any input format FFmpeg reads, FLAC/M4A included) but streamed over stdout into
    /// <paramref name="onChunk"/>: no temp WAV on disk and no whole-track buffer in memory. Used by
    /// the loudness backfill, which only needs one number per track. Decoding runs on a dedicated
    /// below-normal-priority thread (FFmpeg itself runs below normal too) so a long batch never
    /// competes with playback or the UI.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg not found or decode failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the FFmpeg process is killed.</exception>
    public Task DecodePcmStreamAsync(string filePath, PcmChunkHandler onChunk, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_ffmpegPath) && !ExistsOnPath(_ffmpegPath))
            throw new InvalidOperationException(
                $"FFmpeg not found at '{_ffmpegPath}'. Install FFmpeg and ensure it is on PATH.");

        return Task.Factory.StartNew(
            () => DecodePcmStream(filePath, onChunk, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void DecodePcmStream(string filePath, PcmChunkHandler onChunk, CancellationToken cancellationToken)
    {
        // LongRunning gives this its own thread, so lowering its priority doesn't leak into the pool.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

        var psi = new ProcessStartInfo(_ffmpegPath, BuildFfmpegArgs(filePath, "pipe:1", rawStream: true))
        {
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (stderrBuilder) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "[AudioIngestion] Could not lower FFmpeg priority for {File}", Path.GetFileName(filePath));
        }
        process.BeginErrorReadLine();

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException ex) { _logger.LogDebug(ex, "[AudioIngestion] FFmpeg had already exited on cancel"); }
        });

        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[1 << 16];
        int carry = 0; // bytes of a float split across two reads
        int read;
        while ((read = stdout.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            int total = carry + read;
            int usable = total - (total % sizeof(float));
            if (usable > 0)
                onChunk(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, usable)));
            carry = total - usable;
            if (carry > 0) Buffer.BlockCopy(buffer, usable, buffer, 0, carry);
        }

        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            string stderr;
            lock (stderrBuilder) stderr = stderrBuilder.ToString();
            var summary = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no error output";
            _logger.LogWarning("[AudioIngestion] FFmpeg exit={Code} streaming {File}: {Summary}",
                process.ExitCode, Path.GetFileName(filePath), summary);
            throw new InvalidOperationException($"FFmpeg exit {process.ExitCode}: {summary}");
        }
    }

    /// <summary>
    /// Fast corruption probe: decodes <paramref name="filePath"/> to /dev/null via FFmpeg.
    /// Works for every format. Returns (0, null) if clean, or (error line count, a concise error
    /// preview) if FFmpeg logged decode errors — the caller decides Fatal vs Warning from the count,
    /// since a handful of isolated frame errors (decoder hit one bad spot and recovered) is a very
    /// different situation from errors repeating throughout most of the file. No temp file is created.
    /// </summary>
    public async Task<(int ErrorLineCount, string? Preview)> ProbeForCorruptionAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return (1, "File does not exist");

        // -threads 1: FFmpeg's default multi-threaded frame decode makes error counts
        // non-deterministic near a corrupted frame (thread-scheduling-dependent whether that
        // frame's error surfaces on a given run) — observed directly on real files, where the
        // same file logged decode errors on one run and none on the next. Single-threaded decode
        // makes the corruption probe's verdict repeatable.
        string args = $"-threads 1 -v error -i \"{filePath}\" -f null -";
        var (exitCode, stderr) = await RunProcessAsync(_ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        var errors = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            // Skip benign artwork/metadata warnings that don't affect audio decoding
            .Where(l => !l.Contains("mimetype", StringComparison.OrdinalIgnoreCase)
                     && !l.Contains("attached picture", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (errors.Length > 0)
            return (errors.Length, string.Join("; ", errors.Take(3)));

        if (exitCode != 0)
            return (1, $"FFmpeg exited {exitCode} with no stderr");

        return (0, null);
    }

    // ──────────────────────────────────── helpers ──────────────────────────

    private static string BuildFfmpegArgs(string input, string output, bool rawStream = false)
    {
        // -y          = overwrite output
        // -vn         = no video
        // -ar 44100   = resample to 44100 Hz
        // -ac 2       = stereo
        // -c:a pcm_f32le = 32-bit float little-endian PCM
        // -f f32le    = headerless samples (stdout streaming; a pipe has no extension to infer from)
        var sb = new StringBuilder();
        sb.Append("-y -v error ");
        sb.Append($"-i \"{input}\" ");
        sb.Append("-vn ");
        sb.Append($"-ar {TargetSampleRate} ");
        sb.Append($"-ac {TargetChannels} ");
        sb.Append("-c:a pcm_f32le ");
        if (rawStream)
            sb.Append($"-f f32le {output}");
        else
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
        process.EnableRaisingEvents = true;
        var stderrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Ignore platform-specific or permission errors setting priority class
        }
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Issue #47: kill the subprocess so it does not continue consuming CPU/RAM
            // after the analysis job is cancelled.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }

        return (process.ExitCode, stderrBuilder.ToString());
    }

    /// <summary>
    /// FFmpeg-free decode (#331): the same output contract as the FFmpeg path — a 44.1 kHz stereo 32-bit float WAV
    /// in the temp folder — using NAudio's readers and its managed WDL resampler (no Media Foundation resampler).
    /// </summary>
    internal string DecodeWithoutFfmpeg(string filePath)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"orbit_ingest_{Guid.NewGuid():N}.wav");
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(filePath);
            NAudio.Wave.ISampleProvider samples = reader;
            if (samples.WaveFormat.Channels == 1)
                samples = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(samples);
            else if (samples.WaveFormat.Channels != TargetChannels)
                throw new NotSupportedException($"{samples.WaveFormat.Channels}-channel audio needs FFmpeg");
            if (samples.WaveFormat.SampleRate != TargetSampleRate)
                samples = SLSKDONET.Services.Audio.OffsetSafeResampler.Create(samples, TargetSampleRate);

            NAudio.Wave.WaveFileWriter.CreateWaveFile(tempFile, new NAudio.Wave.SampleProviders.SampleToWaveProvider(samples));
            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length <= 44)
                throw new InvalidOperationException("no audio was decoded");
            _logger.LogInformation("[AudioIngestion] Decoded {File} without FFmpeg (built-in decoder)", Path.GetFileName(filePath));
            return tempFile;
        }
        catch
        {
            TryDelete(tempFile);
            throw;
        }
    }

    /// <summary>
    /// True when an FFmpeg executable was actually found (bundled, ORBIT-installed, or on PATH). When it wasn't,
    /// <see cref="ResolveFfmpegPath"/> falls back to the bare name and starting it throws (#331).
    /// </summary>
    public static bool IsFfmpegAvailable()
    {
        var path = ResolveFfmpegPath();
        return Path.IsPathRooted(path) && File.Exists(path);
    }

    public static string ResolveFfmpegPath()
    {
        // Resolve via the cross-platform IToolLocator seam (#453), falling back to the bare executable name.
        return Platform.ToolLocator.Current.Locate(Platform.ExternalTool.Ffmpeg)
            ?? (OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    }

    private static bool ExistsOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(fileName);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;

        foreach (var path in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var fullPath = Path.Combine(path.Trim(), fileName);
                if (File.Exists(fullPath)) return true;
            }
            catch
            {
                // Ignore invalid characters in path variable entries
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
