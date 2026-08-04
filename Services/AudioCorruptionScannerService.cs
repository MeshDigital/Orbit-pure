using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.IO;

namespace SLSKDONET.Services;

/// <summary>
/// Lightweight corruption scanner for audio files.
/// Pipeline: TagLib structural check → FFmpeg null-decode probe → MP3 frame scan (MP3 only).
/// Does NOT run the full analysis pipeline — safe to call on every file at startup or on demand.
/// </summary>
public sealed class AudioCorruptionScannerService
{
    /// <summary>
    /// Above this many FFmpeg decode-error lines, treat the file as pervasively corrupt (Fatal)
    /// rather than a recoverable isolated glitch (Warning). Calibrated against real P2P-sourced
    /// FLAC files: a single bad frame the decoder recovers from logs a handful of lines (typically
    /// 3-5, one incident); files that are actually broken throughout log hundreds to thousands of
    /// repeated errors as the decoder fails on nearly every subsequent frame. Tune if real-world
    /// files land ambiguously between these two shapes.
    /// </summary>
    private const int MaxRecoverableDecodeErrorLines = 25;

    private readonly AudioIngestionPipeline _ingestion;
    private readonly ILogger<AudioCorruptionScannerService> _logger;

    public AudioCorruptionScannerService(
        AudioIngestionPipeline ingestion,
        ILogger<AudioCorruptionScannerService> logger)
    {
        _ingestion = ingestion;
        _logger = logger;
    }

    /// <summary>
    /// Scans a single file for corruption. Returns the detected status and a short description.
    /// </summary>
    public async Task<(CorruptionStatus Status, string? Details)> ScanAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return (CorruptionStatus.Fatal, "File not found");

        // 1. TagLib: header / container structural check
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            if (tagFile.Properties == null || tagFile.Properties.Duration <= TimeSpan.Zero)
                return (CorruptionStatus.Fatal, "TagLib: invalid or zero-duration container");
        }
        catch (TagLib.CorruptFileException ex)
        {
            return (CorruptionStatus.Fatal, $"TagLib: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal — TagLib doesn't support every variant; let FFmpeg decide
            _logger.LogDebug("[CorruptionScanner] TagLib warning for {File}: {Msg}",
                Path.GetFileName(filePath), ex.Message);
        }

        // 2. FFmpeg null-decode probe — catches truncated streams, bad checksums, broken containers.
        // A handful of isolated decode-error lines (the decoder hit one bad frame and recovered) is
        // common in P2P-sourced files and barely dents a multi-minute track's overall BPM/key/energy
        // signal — very different from errors repeating throughout the file, where the underlying
        // data genuinely can't be trusted. Only the latter blocks analysis; the former is a Warning
        // (previously ANY decode error at all was treated as fully Fatal, which blocked real,
        // largely-intact tracks from ever being analysed over a single recovered glitch).
        var (errorLineCount, ffmpegError) = await _ingestion.ProbeForCorruptionAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        if (ffmpegError is not null)
        {
            if (errorLineCount > MaxRecoverableDecodeErrorLines)
            {
                _logger.LogWarning("[CorruptionScanner] FFmpeg pervasive decode errors ({Count} lines) in {File}: {Err}",
                    errorLineCount, Path.GetFileName(filePath), ffmpegError);
                return (CorruptionStatus.Fatal, $"FFmpeg: {ffmpegError} ({errorLineCount} decode errors)");
            }

            _logger.LogWarning("[CorruptionScanner] FFmpeg isolated decode error(s) ({Count} line(s), recovered) in {File}: {Err}",
                errorLineCount, Path.GetFileName(filePath), ffmpegError);
            return (CorruptionStatus.Warning, $"FFmpeg: {ffmpegError}");
        }

        // 3. MP3-specific: NAudio frame-level scan (first 200 frames)
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext is "mp3")
        {
            var (frameCount, mp3Error) = await FileVerificationHelper
                .VerifyMp3FramesAsync(filePath).ConfigureAwait(false);

            if (mp3Error is not null)
            {
                _logger.LogWarning("[CorruptionScanner] MP3 frame error in {File}: {Err}",
                    Path.GetFileName(filePath), mp3Error);
                return (CorruptionStatus.Warning, $"MP3 frames: {mp3Error}");
            }

            if (frameCount == 0)
                return (CorruptionStatus.Fatal, "MP3: no valid frames found");
        }

        return (CorruptionStatus.Clean, null);
    }
}
