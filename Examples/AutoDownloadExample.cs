using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;

namespace SLSKDONET.Examples;

/// <summary>
/// Example usage of AutoDownloadStrictMode services.
/// Shows how to integrate exact-first, filtered-fallback automatic downloads.
/// 
/// PRIVACY NOTE:
/// - All operations are local-only
/// - No telemetry uploads
/// - Feature disabled by default (opt-in only)
/// - All diagnostics written to PlaylistActivityLogEntity
/// 
/// WORKFLOW:
/// 1. Enable AutoDownloadStrictMode in config
/// 2. Create search helper and register excluded phrases
/// 3. Call AutoSearchService to find match
/// 4. Verify candidate with PrefetchVerifier
/// 5. Download and promote (user consent required)
/// </summary>
public class AutoDownloadExample
{
    private readonly AutoSearchService _searchService;
    private readonly SoulseekSearchHelper _searchHelper;
    private readonly PrefetchVerifier _verifier;
    private readonly MatchScorer _scorer;
    private readonly AppConfig _config;

    public AutoDownloadExample(
        AutoSearchService searchService,
        SoulseekSearchHelper searchHelper,
        PrefetchVerifier verifier,
        AppConfig config)
    {
        _searchService = searchService;
        _searchHelper = searchHelper;
        _verifier = verifier;
        _config = config;
    }

    /// <summary>
    /// Example 1: Basic automatic search for a missing track.
    /// Demonstrates exact-first, filtered-fallback pipeline.
    /// </summary>
    public async Task<Track?> SearchAutomaticallyAsync(PlaylistTrack missingTrack, CancellationToken ct = default)
    {
        Console.WriteLine($"🔍 Searching automatically for: {missingTrack.Artist} - {missingTrack.Title}");

        // Register server-excluded phrases (normally done once at app startup)
        _searchHelper.RegisterServerExcludedPhrases(new[]
        {
            "fake", "ad", "promo", "test", "preview"
        });

        // Find best match using strict mode
        var (bestMatch, diagnostics) = await _searchService.FindBestMatchAsync(missingTrack, ct);

        if (bestMatch == null)
        {
            Console.WriteLine("❌ No suitable match found");
            return null;
        }

        Console.WriteLine($"✓ Found: {bestMatch.Filename}");
        Console.WriteLine($"  Bitrate: {bestMatch.Bitrate}kbps");
        Console.WriteLine($"  Match Type: {diagnostics.MatchType}");
        Console.WriteLine($"  Elapsed: {diagnostics.ExactFilenameElapsedMs + diagnostics.TemplateElapsedMs}ms");

        return bestMatch;
    }

    /// <summary>
    /// Example 2: Score multiple candidates and select the best one.
    /// Demonstrates deterministic scoring.
    /// </summary>
    public Track? SelectBestCandidate(PlaylistTrack track, IEnumerable<Track> candidates)
    {
        Console.WriteLine($"🎯 Scoring {candidates.Count()} candidates...");

        var options = new MatchScoringOptions
        {
            AllowedExtensions = _config.AutoDownloadAllowedExtensions ?? new(),
            MinBitrateKbps = _config.AutoDownloadMinBitrateKbps,
            MinFileSizeBytes = _config.AutoDownloadMinFileSizeBytes,
            AllowMp3Fallback = _config.EnableMp3Fallback
        };

        var scoredCandidates = candidates
            .Select(c => new { Candidate = c, Score = MatchScorer.ScoreCandidate(track, c, options) })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (!scoredCandidates.Any())
        {
            Console.WriteLine("❌ No candidates passed scoring");
            return null;
        }

        var winner = scoredCandidates[0];
        Console.WriteLine($"🏆 Winner: {winner.Candidate.Filename}");
        Console.WriteLine($"  Score: {winner.Score:F1}/100");
        Console.WriteLine($"  Bitrate: {winner.Candidate.Bitrate}kbps");
        Console.WriteLine($"  Peer: {winner.Candidate.Username} (queue: {winner.Candidate.QueueLength})");

        // Show runner-up for comparison
        if (scoredCandidates.Count > 1)
        {
            var runnerUp = scoredCandidates[1];
            Console.WriteLine($"  Runner-up: {runnerUp.Candidate.Filename} ({runnerUp.Score:F1}/100)");
        }

        return winner.Candidate;
    }

    /// <summary>
    /// Example 3: Build a filtered search query with Soulseek filter tokens.
    /// Demonstrates filter application and phrase stripping.
    /// </summary>
    public string BuildSearchQuery(PlaylistTrack track)
    {
        Console.WriteLine($"🔨 Building filtered query for: {track.Title}");

        var baseQuery = $"{track.Artist} {track.Title}";
        var filteredQuery = _searchHelper.BuildFilteredQuery(track, baseQuery, enforceFormatFilters: true);

        Console.WriteLine($"  Base: {baseQuery}");
        Console.WriteLine($"  Filtered: {filteredQuery}");

        return filteredQuery;
    }

    /// <summary>
    /// Example 4: Filter candidates by strict criteria.
    /// Demonstrates local candidate filtering before download.
    /// </summary>
    public List<Track> ApplyStrictFilters(IEnumerable<Track> candidates)
    {
        Console.WriteLine("🛡️ Applying strict filters...");

        var allowedExtensions = _config.AutoDownloadAllowedExtensions ?? new();
        var minBitrate = _config.AutoDownloadMinBitrateKbps;
        var minFileSize = _config.AutoDownloadMinFileSizeBytes;

        var filtered = _searchHelper.FilterCandidates(
            candidates,
            allowedExtensions,
            minBitrate,
            minFileSize)
            .ToList();

        Console.WriteLine($"  Accepted: {filtered.Count}/{candidates.Count()}");

        return filtered;
    }

    /// <summary>
    /// Example 5: Verify a downloaded file after completion.
    /// Demonstrates post-download verification and fingerprinting.
    /// </summary>
    public async Task<bool> VerifyDownloadAsync(
        PlaylistTrack track,
        Track candidate,
        string localFilePath,
        CancellationToken ct = default)
    {
        Console.WriteLine($"✓ Verifying: {localFilePath}");

        var result = await _verifier.VerifyDownloadAsync(track, candidate, localFilePath, ct);

        switch (result)
        {
            case VerificationResult.Success:
                Console.WriteLine("✓ Verification passed");
                return true;
            case VerificationResult.SizeMismatch:
                Console.WriteLine("❌ File size too small");
                _verifier.CleanupStagingFile(localFilePath);
                return false;
            case VerificationResult.FormatInvalid:
                Console.WriteLine("❌ Format not allowed");
                _verifier.CleanupStagingFile(localFilePath);
                return false;
            case VerificationResult.FingerprintFailed:
                Console.WriteLine("❌ Fingerprint verification failed");
                _verifier.CleanupStagingFile(localFilePath);
                return false;
            default:
                Console.WriteLine($"❌ Verification failed: {result}");
                return false;
        }
    }

    /// <summary>
    /// Example 6: Full end-to-end automatic download workflow.
    /// Shows how to chain search, score, verify, and finalize.
    /// </summary>
    public async Task<bool> AutoDownloadTrackAsync(PlaylistTrack missingTrack, CancellationToken ct = default)
    {
        Console.WriteLine($"\n🚀 Auto-Download Workflow: {missingTrack.Artist} - {missingTrack.Title}");
        Console.WriteLine($"Enabled: {_config.EnableAutoDownloadStrictMode}");

        if (!_config.EnableAutoDownloadStrictMode)
        {
            Console.WriteLine("❌ AutoDownloadStrictMode disabled");
            return false;
        }

        // Step 1: Search
        var bestMatch = await SearchAutomaticallyAsync(missingTrack, ct);
        if (bestMatch == null) return false;

        // Step 2: Verify candidate metadata
        Console.WriteLine($"\n📋 Candidate: {bestMatch.Filename}");
        Console.WriteLine($"  Size: {bestMatch.Size} bytes");
        Console.WriteLine($"  Bitrate: {bestMatch.Bitrate} kbps");
        Console.WriteLine($"  Peer: {bestMatch.Username}");

        // Step 3: (In real implementation) Download to staging
        // For this example, assume file is already downloaded to stagingPath
        var stagingPath = $"/tmp/staging/{bestMatch.Filename}";
        Console.WriteLine($"\n📥 Would download to: {stagingPath}");

        // Step 4: Verify (skip if file doesn't exist in this example)
        if (System.IO.File.Exists(stagingPath))
        {
            var verified = await VerifyDownloadAsync(missingTrack, bestMatch, stagingPath, ct);
            if (!verified) return false;
        }
        else
        {
            Console.WriteLine("   (Staging file not found in example; skipping verification)");
        }

        // Step 5: Ready for promotion
        Console.WriteLine("\n✓ Ready for promotion to library");
        Console.WriteLine("  (Requires explicit user consent in real app)");

        return true;
    }

    /// <summary>
    /// Helper: Show current config
    /// </summary>
    public void ShowConfig()
    {
        Console.WriteLine("\n⚙️ AutoDownloadStrictMode Config:");
        Console.WriteLine($"  Enabled: {_config.EnableAutoDownloadStrictMode}");
        Console.WriteLine($"  Initial Wait: {_config.AutoDownloadInitialWaitMs}ms");
        Console.WriteLine($"  Extended Wait: {_config.AutoDownloadExtendedWaitMs}ms");
        Console.WriteLine($"  Min Bitrate: {_config.AutoDownloadMinBitrateKbps}kbps");
        Console.WriteLine($"  Min File Size: {_config.AutoDownloadMinFileSizeBytes} bytes");
        Console.WriteLine($"  Allowed Formats: {string.Join(", ", _config.AutoDownloadAllowedExtensions ?? new())}");
        Console.WriteLine($"  Excluded Phrases: {_config.AutoDownloadExcludedPhrases}");
        Console.WriteLine($"  Diagnostics: {_config.AutoDownloadDiagnosticsEnabled}");
    }
}
