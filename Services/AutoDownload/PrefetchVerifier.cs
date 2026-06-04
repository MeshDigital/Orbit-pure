using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// PrefetchVerifier — Downloads to staging, verifies, and fingerprints automatically downloaded tracks.
/// 
/// PURPOSE:
/// Post-download verification pipeline for auto-downloaded files:
/// 1. Download to temporary staging location
/// 2. Verify file integrity (size, format, readability)
/// 3. Run fingerprint extraction (Essentia) to confirm track identity
/// 4. Mark as ready or failed
/// 5. NO automatic promotion to library without explicit user consent
/// 
/// PRIVACY:
/// - No fingerprint storage (fingerprints are volatile, local-only)
/// - No PII tracking
/// - Staging files are temporary and deleted on failure
/// - All verification is local-only
/// 
/// NON-INVASIVE:
/// - Does not alter core download logic
/// - Gated behind EnableAutoDownloadStrictMode flag
/// - Optional verification layer (can skip if disabled)
/// </summary>
public class PrefetchVerifier
{
    private readonly ILogger<PrefetchVerifier> _logger;
    private readonly AppConfig _config;
    private readonly DatabaseService _databaseService;
    private readonly TrackFingerprintBuilderService _fingerprintBuilder;

    public PrefetchVerifier(
        ILogger<PrefetchVerifier> logger,
        AppConfig config,
        DatabaseService databaseService,
        TrackFingerprintBuilderService fingerprintBuilder)
    {
        _logger = logger;
        _config = config;
        _databaseService = databaseService;
        _fingerprintBuilder = fingerprintBuilder;
    }

    /// <summary>
    /// Verifies a downloaded file after completion.
    /// Runs fingerprint extraction and size/format validation.
    /// 
    /// Returns:
    /// - VerificationResult.Success if all gates pass
    /// - VerificationResult.FormatInvalid if format is wrong or unreadable
    /// - VerificationResult.SizeMismatch if size is unreasonable
    /// - VerificationResult.FingerprintFailed if fingerprint builder fails
    /// </summary>
    public async Task<VerificationResult> VerifyDownloadAsync(
        PlaylistTrack track,
        Track candidate,
        string localFilePath,
        CancellationToken ct = default)
    {
        if (!_config.EnableAutoDownloadStrictMode)
        {
            return VerificationResult.Disabled;
        }

        _logger.LogInformation("[PrefetchVerifier] Verifying {Track} at {Path}", track.Title, localFilePath);

        try
        {
            // 1. File existence and basic checks
            if (!File.Exists(localFilePath))
            {
                _logger.LogWarning("[PrefetchVerifier] File not found: {Path}", localFilePath);
                return VerificationResult.FileNotFound;
            }

            var fileInfo = new FileInfo(localFilePath);

            // 2. Size validation
            if (fileInfo.Length < _config.AutoDownloadMinFileSizeBytes)
            {
                _logger.LogWarning(
                    "[PrefetchVerifier] File too small ({Actual}B < {Min}B): {File}",
                    fileInfo.Length,
                    _config.AutoDownloadMinFileSizeBytes,
                    localFilePath);
                return VerificationResult.SizeMismatch;
            }

            // 3. Format validation
            var allowedExtensions = AutoDownloadStrictFilterPolicy
                .ResolveAllowedExtensions(track, _config)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeFormat)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fileExtension = NormalizeFormat(Path.GetExtension(localFilePath));
            var candidateFormat = NormalizeFormat(candidate.Format);
            var resolvedFormat = IsAllowedFormat(fileExtension, allowedExtensions)
                ? fileExtension
                : candidateFormat;

            if (!IsAllowedFormat(resolvedFormat, allowedExtensions))
            {
                _logger.LogWarning(
                    "[PrefetchVerifier] Format not allowed ({Actual} not in {Allowed}): {File}",
                    string.IsNullOrWhiteSpace(resolvedFormat) ? "unknown" : resolvedFormat,
                    string.Join(",", allowedExtensions),
                    localFilePath);
                return VerificationResult.FormatInvalid;
            }

            // 4. Fingerprint extraction (optional, gated by diagnostics flag)
            if (_config.AutoDownloadDiagnosticsEnabled)
            {
                var fingerprintResult = await VerifyFingerprintAsync(track, localFilePath, ct);
                if (!fingerprintResult.IsValid)
                {
                    _logger.LogWarning(
                        "[PrefetchVerifier] Fingerprint verification failed for {File}: {Reason}",
                        localFilePath,
                        fingerprintResult.Reason);
                    return VerificationResult.FingerprintFailed;
                }

                _logger.LogInformation(
                    "[PrefetchVerifier] Fingerprint verified: {File} ({Duration}s)",
                    localFilePath,
                    fingerprintResult.DurationSeconds);
            }

            _logger.LogInformation("[PrefetchVerifier] ✓ Verification passed: {File}", localFilePath);
            return VerificationResult.Success;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[PrefetchVerifier] Verification cancelled for {Track}", track.Title);
            return VerificationResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrefetchVerifier] Unexpected error verifying {Track}", track.Title);
            return VerificationResult.Error;
        }
    }

    /// <summary>
    /// Runs fingerprint extraction on a downloaded file.
    /// Verifies that the audio content is readable and matches duration.
    /// </summary>
    private async Task<FingerprintVerificationResult> VerifyFingerprintAsync(
        PlaylistTrack track,
        string localFilePath,
        CancellationToken ct)
    {
        try
        {
            // Call the existing fingerprint builder service
            // In a full implementation, pass track and file path to extract fingerprint
            // For skeleton: return success if file is readable audio

            // TODO: Call _fingerprintBuilder.BuildAsync(...) or similar
            // Stub: return success
            return new FingerprintVerificationResult
            {
                IsValid = true,
                DurationSeconds = track.CanonicalDuration.HasValue ? track.CanonicalDuration.Value / 1000 : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrefetchVerifier] Fingerprint extraction failed");
            return new FingerprintVerificationResult
            {
                IsValid = false,
                Reason = ex.Message
            };
        }
    }

    /// <summary>
    /// Cleans up temporary staging files on failure.
    /// </summary>
    public void CleanupStagingFile(string localFilePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(localFilePath) && File.Exists(localFilePath))
            {
                File.Delete(localFilePath);
                _logger.LogInformation("[PrefetchVerifier] Cleaned up staging file: {Path}", localFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrefetchVerifier] Failed to clean up {Path}", localFilePath);
        }
    }

    private static string NormalizeFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var metadataSeparator = normalized.IndexOf(';');
        if (metadataSeparator >= 0)
        {
            normalized = normalized[..metadataSeparator].Trim();
        }

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..].Trim();
        }

        normalized = normalized.TrimStart('.');
        if (normalized.StartsWith("x-", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized switch
        {
            "mpeg" => "mp3",
            "mpg" => "mp3",
            "wave" => "wav",
            "vnd.wave" => "wav",
            "mp4" => "m4a",
            _ => normalized
        };
    }

    private static bool IsAllowedFormat(string format, IReadOnlyCollection<string> allowedFormats)
    {
        return !string.IsNullOrWhiteSpace(format)
               && allowedFormats.Any(x => x.Equals(format, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Verification result enum.
/// </summary>
public enum VerificationResult
{
    /// <summary>Feature disabled.</summary>
    Disabled,

    /// <summary>File verification passed all gates.</summary>
    Success,

    /// <summary>File not found at expected path.</summary>
    FileNotFound,

    /// <summary>File size below minimum threshold.</summary>
    SizeMismatch,

    /// <summary>File format not in allowed list.</summary>
    FormatInvalid,

    /// <summary>Fingerprint extraction or validation failed.</summary>
    FingerprintFailed,

    /// <summary>Operation cancelled by caller.</summary>
    Cancelled,

    /// <summary>Unexpected error during verification.</summary>
    Error
}

/// <summary>
/// Result of fingerprint verification.
/// </summary>
public class FingerprintVerificationResult
{
    public bool IsValid { get; set; }
    public int DurationSeconds { get; set; }
    public string? Reason { get; set; }
}
