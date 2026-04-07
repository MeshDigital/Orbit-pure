using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace SLSKDONET.Services.Video;

/// <summary>
/// Configuration for a video export job.
/// </summary>
public sealed class VideoRenderOptions
{
    public int    Width          { get; init; } = 1920;
    public int    Height         { get; init; } = 1080;
    public int    FrameRate      { get; init; } = 30;
    /// <summary>FFmpeg video codec (e.g., "libx264", "libx265").</summary>
    public string VideoCodec     { get; init; } = "libx264";
    /// <summary>FFmpeg audio codec (e.g., "aac", "libmp3lame").</summary>
    public string AudioCodec     { get; init; } = "aac";
    public VisualPreset Preset   { get; init; } = VisualPreset.Bars;

    public string OutputPath     { get; init; } = "output.mp4";
    /// <summary>Path to the mixed audio WAV file to embed.</summary>
    public string AudioPath      { get; init; } = string.Empty;
    /// <summary>Override FFmpeg binary path. Null = auto-detect via PATH.</summary>
    public string? FfmpegPath    { get; init; }

    // ── Validation ───────────────────────────────────────────────────────

    public void Validate()
    {
        if (Width  <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0) throw new ArgumentOutOfRangeException(nameof(Height));
        if (FrameRate <= 0) throw new ArgumentOutOfRangeException(nameof(FrameRate));
        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new ArgumentException("OutputPath must not be empty.", nameof(OutputPath));
        if (string.IsNullOrWhiteSpace(VideoCodec))
            throw new ArgumentException("VideoCodec must not be empty.",  nameof(VideoCodec));
        if (string.IsNullOrWhiteSpace(AudioCodec))
            throw new ArgumentException("AudioCodec must not be empty.",  nameof(AudioCodec));
    }
}

/// <summary>
/// Progress event raised once per rendered frame.
/// </summary>
public sealed class RenderProgressEventArgs : EventArgs
{
    public int    FrameIndex  { get; init; }
    public int    TotalFrames { get; init; }
    public double Percentage  => TotalFrames > 0 ? (double)FrameIndex / TotalFrames * 100.0 : 0;
}

/// <summary>
/// Renders an audio-reactive video by driving <see cref="VisualEngine"/> frame-by-frame
/// and piping raw RGBA pixel data to FFmpeg's stdin.  Implements Issue 5.2 / #36.
/// </summary>
public sealed class VideoRenderer
{
    private readonly ILogger<VideoRenderer> _logger;

    public VideoRenderer(ILogger<VideoRenderer> logger)
    {
        _logger = logger;
    }

    public event EventHandler<RenderProgressEventArgs>? ProgressChanged;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Exports a video file from a sequence of <see cref="VisualFrame"/> samples.
    /// Frames are rendered by <see cref="VisualEngine"/> and piped to FFmpeg.
    /// </summary>
    /// <param name="frames">
    ///   One <see cref="VisualFrame"/> per rendered output frame
    ///   (i.e., <c>durationSeconds × frameRate</c> items).
    /// </param>
    /// <param name="options">Export configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RenderAsync(
        IReadOnlyList<VisualFrame>  frames,
        VideoRenderOptions          options,
        CancellationToken           ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string? outDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        string ffmpeg = ResolveFfmpeg(options.FfmpegPath);
        string args   = BuildFfmpegArgs(options);

        _logger.LogInformation("Starting FFmpeg: {Ffmpeg} {Args}", ffmpeg, args);

        using var ffmpegProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                Arguments              = args,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
            EnableRaisingEvents = true,
        };

        ffmpegProc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[ffmpeg] {Line}", e.Data);
        };

        ffmpegProc.Start();
        ffmpegProc.BeginErrorReadLine();

        var engine = new VisualEngine
        {
            Preset = options.Preset,
            Width  = options.Width,
            Height = options.Height,
        };

        int totalFrames  = frames.Count;
        int pixelBytes   = options.Width * options.Height * 4; // RGBA
        var pixelBuffer  = new byte[pixelBytes];

        await using var stdin = ffmpegProc.StandardInput.BaseStream;

        for (int i = 0; i < totalFrames && !ct.IsCancellationRequested; i++)
        {
            using var bmp = engine.RenderFrame(frames[i]);
            CopyBitmapToBuffer(bmp, pixelBuffer);
            await stdin.WriteAsync(pixelBuffer, ct).ConfigureAwait(false);

            ProgressChanged?.Invoke(this, new RenderProgressEventArgs
            {
                FrameIndex  = i + 1,
                TotalFrames = totalFrames,
            });
        }

        await stdin.FlushAsync(ct).ConfigureAwait(false);
        stdin.Close();

        await ffmpegProc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (ffmpegProc.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg exited with code {ffmpegProc.ExitCode}. Check logs for details.");

        _logger.LogInformation("Video exported: {Path}", options.OutputPath);
    }

    // ── Command-line helpers (testable) ──────────────────────────────────

    /// <summary>
    /// Builds the FFmpeg argument string for the given options.
    /// Public so it can be verified in unit tests without launching FFmpeg.
    /// </summary>
    public static string BuildFfmpegArgs(VideoRenderOptions options)
    {
        // Raw RGBA frames piped to stdin
        string videoInput = $"-f rawvideo -pixel_format rgba -video_size {options.Width}x{options.Height}" +
                            $" -framerate {options.FrameRate} -i pipe:0";

        // Optional audio input
        string audioInput = !string.IsNullOrWhiteSpace(options.AudioPath)
            ? $"-i \"{options.AudioPath}\""
            : string.Empty;

        string codecArgs  = $"-c:v {options.VideoCodec} -preset fast -pix_fmt yuv420p";
        string audioArgs  = !string.IsNullOrWhiteSpace(options.AudioPath)
            ? $"-c:a {options.AudioCodec}"
            : "-an";

        string output = $"-y \"{options.OutputPath}\"";

        return string.Join(" ", videoInput, audioInput, codecArgs, audioArgs, output).Trim();
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private static string ResolveFfmpeg(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
            return customPath;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
    }

    private static void CopyBitmapToBuffer(SKBitmap bmp, byte[] buffer)
    {
        IntPtr ptr    = bmp.GetPixels();
        int    length = Math.Min(buffer.Length, bmp.RowBytes * bmp.Height);
        Marshal.Copy(ptr, buffer, 0, length);
    }
}
