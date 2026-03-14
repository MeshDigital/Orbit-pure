using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public interface ISafetyFilterService
{
    bool IsSafe(Track candidate, string query, int? targetDurationSeconds = null);
    bool IsUpscaled(PlaylistTrack track);
    SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null);
    void EvaluateSafety(Track track, string query);
}

/// <summary>
/// "The Gatekeeper"
/// Enforces integrity standards on tracks before they are added to the library or downloaded.
/// Validates bitrate vs frequency cutoff (fake FLAC detection), duration matching, and blacklists.
/// </summary>
public class SafetyFilterService : ISafetyFilterService
{
    private readonly ILogger<SafetyFilterService> _logger;
    private readonly AppConfig _config;
    private readonly string[] _bannedExtensions = new[] { ".exe", ".zip", ".rar", ".lnk", ".bat", ".cmd", ".vbs", ".dmg", ".iso" };

    public SafetyFilterService(ILogger<SafetyFilterService> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Phase 14A: The Bouncer.
    /// Evaluates track safety and flags suspicious files (Fake FLACs, Bad Users) without hiding them.
    /// Sets track.IsFlagged and track.FlagReason.
    /// </summary>
    /// <summary>
    /// Phase 14A: The Bouncer.
    /// Evaluates track safety and flags suspicious files (Fake FLACs, Bad Users) without hiding them.
    /// Sets track.IsFlagged and track.FlagReason.
    /// </summary>
    public void EvaluateSafety(Track track, string query)
    {
        var result = EvaluateCandidate(track, query, null);
        
        if (!result.IsSafe)
        {
            track.IsFlagged = true;
            track.FlagReason = result.Reason;
        }
        else
        {
            track.IsFlagged = false;
            track.FlagReason = null;
        }
    }

    /// <summary>
    /// Evaluates a candidate track and returns a detailed safety result.
    /// Used by both the UI (via EvaluateSafety) and the Automated Seeker (for Audit Logging).
    /// </summary>
    public SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null)
    {
        // 1. Check Extension Blacklist
        var ext = candidate.GetExtension().ToLower();
        if (_bannedExtensions.Contains(ext))
        {
            return new SafetyCheckResult(false, "Banned Extension", $"Extension '{ext}' is not allowed.");
        }

        // 2. The Accountant: Bitrate vs Size Math (Delegated to Forensic Core)
        // A 320kbps MP3 MUST be approx 2.4MB per minute. 
        if (candidate.Bitrate > 0 && candidate.Length > 0 && candidate.Size.HasValue)
        {
            if (MetadataForensicService.IsFake(candidate))
            {
                var trust = MetadataForensicService.CalculateTrustScore(candidate);
                _logger.LogWarning("Forensic Core detected Fake/Low-Integrity file for {Track}: Trust Score {Score}", candidate.Title, trust);
                return new SafetyCheckResult(false, "Forensic Integrity Failure", $"Metadata/Size trust score ({trust}%) is below safety threshold.");
            }
        }

        // 3. The Bouncer: Regex Blocklist
        // Guard against null filename
        if (!string.IsNullOrEmpty(candidate.Filename))
        {
            var bouncerRegex = new System.Text.RegularExpressions.Regex(@"\b(ringtone|snippet|preview|sample|teaser)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (bouncerRegex.IsMatch(candidate.Filename))
            {
                return new SafetyCheckResult(false, "Keyword Blocked", "Matches restricted content keywords (ringtone/sample/etc)");
            }
        }

        // 4. Bitrate Check (Low Quality)
        if (candidate.Bitrate > 0 && candidate.Bitrate < 128)
        {
             return new SafetyCheckResult(false, "Low Quality", $"Bitrate {candidate.Bitrate}kbps is below 128kbps threshold.");
        }
        
        // 5. Manual Blacklist (Keywords/Users)
        if (IsBlacklisted(candidate))
        {
             return new SafetyCheckResult(false, "Blacklisted", "Matches banned keyword or user.");
        }

        // 5. Duration Check (if target provided)
        if (targetDuration.HasValue && candidate.Length.HasValue && targetDuration.Value > 0)
        {
             int delta = Math.Abs(candidate.Length.Value - targetDuration.Value);
             if (delta > 10)
             {
                 return new SafetyCheckResult(false, "High-Risk Duration Mismatch", 
                    $"Length {candidate.Length}s != Target {targetDuration}s (Delta: {delta}s). High probability of wrong version (Extended vs Radio).");
             }
             else if (delta > 3)
             {
                 // Small warning for scores but don't reject yet? 
                 // Actually, Matcher handles scoring. SafetyFilter handles REJECTION.
                 // So we reject > 10s.
             }
        }

        return new SafetyCheckResult(true, "Passed", null);
    }

    // Deprecated IsSafe for compatibility if needed, but we should switch completely.
    public bool IsSafe(Track track, string query, int? targetDurationSeconds = null) 
    {
        var result = EvaluateCandidate(track, query, targetDurationSeconds);
        if (!result.IsSafe)
        {
            track.IsFlagged = true;
            track.FlagReason = result.Reason;
        }
        return result.IsSafe;
    }

    /// <summary>
    /// Checks if a search result contains banned keywords or matches blacklisted criteria.
    /// </summary>
    private bool IsBlacklisted(Track item)
    {
        // 1. Extension check
        var ext = Path.GetExtension(item.Filename)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return true; // No extension? Suspicious.

        var allowedExtensions = new[] { ".mp3", ".flac", ".wav", ".aiff", ".m4a", ".aac", ".ogg" };
        if (!allowedExtensions.Contains(ext)) return true; // Filter exes, zips, etc.

        // 2. Keyword check
        // Guard against null filename
        if (string.IsNullOrEmpty(item.Filename)) return true;
        
        var filename = item.Filename.ToLowerInvariant();
        var bannedKeywords = new[] 
        { 
            "password", "virus", "install", ".exe", ".lnk", 
            "ringtone", "snippet", "preview" 
        };

        if (bannedKeywords.Any(k => filename.Contains(k))) return true;

        if (item.Username != null && _config.BlacklistedUsers.Contains(item.Username))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a search result candidate represents a potentially upscaled or fake file.
    /// Note: Search results usually don't have frequency data yet.
    /// This method is primarily for analyzing downloaded tracks (`PlaylistTrack`).
    /// </summary>
    public bool IsUpscaled(PlaylistTrack track)
    {
        // Require Frequency Cutoff data to make a determination
        if (!track.FrequencyCutoff.HasValue) return false;

        var cutoff = track.FrequencyCutoff.Value;
        var bitrate = track.Bitrate;

        // 1. Fake 320kbps / FLAC check
        // Real 320kbps MP3s usually cutoff around 20kHz or 20.5kHz.
        // 192kbps cuts off around 18-19kHz.
        // 128kbps cuts off around 16-17kHz.
        
        // 2. Strict Gold Standard: Fake FLAC check
        // Real FLACs MUST have a frequency cutoff well above 16.1 kHz.
        // If it claims to be FLAC (lossless) but looks like a low-bitrate MP3 shelf...
        if ((track.Format?.Equals("flac", StringComparison.OrdinalIgnoreCase) ?? false) && cutoff < 16100)
        {
             _logger.LogWarning("🛑 Forensic Failure: Strict 16.1 kHz FLAC gate failed for {Track}. Cutoff: {Cutoff}Hz. Marking as fake.", track.Title, cutoff);
             return true; 
        }

        // 3. High Quality MP3 upscale check
        if (bitrate >= 256 && cutoff < 16100)
        {
            _logger.LogWarning("Potential High-Quality MP3 upscale detected: {Track} claims {Bitrate}kbps but cutoff is {Cutoff}Hz", track.Title, bitrate, cutoff);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates if the candidate track matches the target duration within tolerance.
    /// Helps reject Extended Mixes when looking for Radio Edits, or vice versa.
    /// </summary>
    public bool ValidateDuration(int candidateSeconds, int targetSeconds)
    {
        if (targetSeconds <= 0) return true; // No target to match against

        // User requested > 10s as high-risk mismatch.
        const int MaxAllowedDelta = 10;

        return Math.Abs(candidateSeconds - targetSeconds) <= MaxAllowedDelta;
    }
}

public record SafetyCheckResult(bool IsSafe, string Reason, string? TechnicalDetails);
