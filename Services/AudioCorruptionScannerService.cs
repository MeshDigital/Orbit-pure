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

        // 2. FFmpeg null-decode probe — catches truncated streams, bad checksums, broken containers
        var ffmpegError = await _ingestion.ProbeForCorruptionAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        if (ffmpegError is not null)
        {
            _logger.LogWarning("[CorruptionScanner] FFmpeg errors in {File}: {Err}",
                Path.GetFileName(filePath), ffmpegError);
            return (CorruptionStatus.Fatal, $"FFmpeg: {ffmpegError}");
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
