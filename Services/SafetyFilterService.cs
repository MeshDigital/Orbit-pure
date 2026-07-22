using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public interface ISafetyFilterService
{
    bool IsSafe(Track candidate, string query, int? targetDurationSeconds = null);
    bool IsUpscaled(PlaylistTrack track);
    SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null, bool allowLossy = false, SearchPolicy? policy = null);
    void EvaluateSafety(Track track, string query, bool allowLossy = false, SearchPolicy? policy = null);
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
    private static readonly HashSet<string> _losslessWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".aif", ".aiff", ".wav"
    };
    private static readonly HashSet<string> _lossyBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".mp4", ".ogg", ".wma"
    };

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
    public void EvaluateSafety(Track track, string query, bool allowLossy = false, SearchPolicy? policy = null)
    {
        var result = EvaluateCandidate(track, query, null, allowLossy, policy);
        
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
    public SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null, bool allowLossy = false, SearchPolicy? policy = null)
    {
        // Settings toggles (SearchPolicy.EnforceFileIntegrity / EnforceDurationMatch / EnforceStrictTitleMatch)
        // gate the checks below. When no policy is supplied (legacy callers/tests), preserve the
        // exact prior always-on behavior for FileIntegrity/DurationMatch. StrictTitleMatch is new
        // logic that never existed before, so it only runs when a caller explicitly opts in via a
        // policy — it must not silently start rejecting results for callers that don't pass one.
        bool enforceFileIntegrity = policy?.EnforceFileIntegrity ?? true;
        bool enforceDurationMatch = policy?.EnforceDurationMatch ?? true;

        // 1. Check Extension Blacklist
        var ext = Path.GetExtension(candidate.Filename ?? string.Empty).ToLowerInvariant();
        if (_bannedExtensions.Contains(ext))
        {
            return new SafetyCheckResult(false, "Banned Extension", $"Extension '{ext}' is not allowed.");
        }

        // 1B. Purist First Barrier: hard extension allow/deny
        if (!allowLossy && _lossyBlacklist.Contains(ext))
        {
            return new SafetyCheckResult(false, "Lossy Extension Rejected", $"Extension '{ext}' is blacklisted for lossless hunting.");
        }

        if (!_losslessWhitelist.Contains(ext) && (!allowLossy || !_lossyBlacklist.Contains(ext)))
        {
            return new SafetyCheckResult(false, "Unsupported Extension", $"Extension '{ext}' is outside the allowed audio whitelist.");
        }

        if (enforceFileIntegrity)
        {
            // 2. The Accountant: Bitrate vs Size Math (Delegated to Forensic Core)
            // A 320kbps MP3 MUST be approx 2.4MB per minute.
            if (candidate.Bitrate > 0 && candidate.Length > 0 && candidate.Size.HasValue)
            {
                // Simple size check instead of forensic service
                double expectedBytes = (candidate.Bitrate * 1000.0 / 8.0) * (candidate.Length.Value);
                if (candidate.Size < (expectedBytes * 0.5) || candidate.Size > (expectedBytes * 2.0))
                {
                    _logger.LogWarning("Size check detected suspicious file for {Track}: Size {Size} vs Expected {Expected}", candidate.Title, candidate.Size, expectedBytes);
                    return new SafetyCheckResult(false, "Forensic Integrity Failure", "File size deviates significantly from expected bitrate duration.");
                }
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

        if (enforceFileIntegrity)
        {
            // 4. Bitrate & sample-rate evidence gate for lossless intake
            if (!allowLossy)
            {
                // If bitrate is reported (> 0), require > 400kbps for lossless.
                // Real 16-bit/44.1kHz FLACs of acoustic/folk/quiet music commonly fall between 400-700kbps;
                // the old 700kbps floor was rejecting these legitimate files. 400kbps still catches
                // MP3-to-FLAC transcodes (which report ~320kbps) and other low-bitrate fakes.
                if (candidate.Bitrate > 0 && candidate.Bitrate <= 400)
                {
                    return new SafetyCheckResult(false, "Bitrate Too Low", $"Bitrate {candidate.Bitrate}kbps is below 400kbps lossless floor (likely MP3 transcode).");
                }

                // Require 44.1kHz / 48kHz or higher for strict pass (if reported)
                if (candidate.SampleRate.HasValue && candidate.SampleRate.Value < 44100)
                {
                    return new SafetyCheckResult(false, "Sample Rate Too Low", $"Sample rate {candidate.SampleRate.Value}Hz is below 44.1kHz threshold.");
                }
            }
            else
            {
                // MP3 Fallback Standard Enforcer: Enforce bitrate >= 256kbps for StemSeparation viability (if reported)
                if (candidate.Bitrate > 0 && candidate.Bitrate < 256)
                {
                    return new SafetyCheckResult(false, "Bitrate Too Low (Lossy Fallback)", $"Bitrate {candidate.Bitrate}kbps is below required standard 256kbps fallback threshold.");
                }
            }
        }

        // 5. Manual Blacklist (Keywords/Users)
        if (IsBlacklisted(candidate))
        {
             return new SafetyCheckResult(false, "Blacklisted", "Matches banned keyword or user.");
        }

        if (enforceDurationMatch)
        {
            // 6. Duration Check (if target provided) — uses the configured tolerance when a policy
            // is supplied (SearchPolicy.DurationToleranceSeconds, e.g. DJ Ready allows 15s for
            // extended mixes), falling back to the original hardcoded 10s otherwise.
            if (targetDuration.HasValue && candidate.Length.HasValue && targetDuration.Value > 0)
            {
                 int maxDelta = policy?.DurationToleranceSeconds ?? 10;
                 int delta = Math.Abs(candidate.Length.Value - targetDuration.Value);
                 if (delta > maxDelta)
                 {
                     return new SafetyCheckResult(false, "High-Risk Duration Mismatch",
                        $"Length {candidate.Length}s != Target {targetDuration}s (Delta: {delta}s). High probability of wrong version (Extended vs Radio).");
                 }
            }
        }

        // 7. Strict Title Match (opt-in only — new check, see comment above): reject if the
        // filename doesn't contain every token from the search query.
        if (policy?.EnforceStrictTitleMatch == true && !string.IsNullOrWhiteSpace(query) && !string.IsNullOrEmpty(candidate.Filename))
        {
            var filenameLower = candidate.Filename.ToLowerInvariant();
            var queryTokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var missingToken = queryTokens.FirstOrDefault(t => t.Length > 1 && !filenameLower.Contains(t.ToLowerInvariant()));
            if (missingToken != null)
            {
                return new SafetyCheckResult(false, "Strict Title Mismatch", $"Filename is missing query token '{missingToken}'.");
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
        // 1. Keyword check
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
           // Real FLACs should preserve energy well beyond 20kHz. If they shelf early,
           // treat as likely upscaled lossy source.
           if ((track.Format?.Equals("flac", StringComparison.OrdinalIgnoreCase) ?? false) && cutoff < 20000)
        {
               _logger.LogWarning("🛑 Forensic Failure: Strict 20kHz FLAC gate failed for {Track}. Cutoff: {Cutoff}Hz. Marking as fake.", track.Title, cutoff);
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
