using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services.Network;

/// <summary>
/// "The Shield": Hardens the Soulseek protocol interactions.
/// Performs advanced search query sanitization to prevent bans and manages peer health.
/// </summary>
public class ProtocolHardeningService
{
    private readonly ILogger<ProtocolHardeningService> _logger;
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, PeerReputation> _peerReputations = new();
    private readonly ConcurrentDictionary<string, byte> _excludedPhrases = new();
    
    // Characters that are known to sometimes cause issues or bans if used excessively/wrongly in Soulseek queries
    private static readonly Regex DangerousQueryChars = new(@"[^\w\s\-\.\']", RegexOptions.Compiled);

    public ProtocolHardeningService(ILogger<ProtocolHardeningService> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Updates the local cache of phrases excluded by the Soulseek server.
    /// </summary>
    public void UpdateExcludedPhrases(IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            _excludedPhrases.TryAdd(phrase.ToLowerInvariant(), 0);
        }
    }

    /// <summary>
    /// Normalizes a search query specifically for network safety.
    /// Removes dangerous characters and ensures the query won't trigger automated Soulseek server bans.
    /// Returns null if the query contains a banned phrase that would cause a ban.
    /// </summary>
    public string? NormalizeSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        var lowerQuery = query.ToLowerInvariant();
        if (_excludedPhrases.Keys.Any(p => lowerQuery.Contains(p)))
        {
            _logger.LogWarning("🚨 [HARDENING] Aborting search: Query '{Query}' contains a server-excluded phrase.", query);
            return null; // Signals to the caller to abort
        }

        // 1. Remove dangerous special characters
        var sanitized = DangerousQueryChars.Replace(query, " ");

        // 2. Collapse whitespace
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        // 3. Length enforcement (Soulseek limit is typically around 80-100 chars, but shorter is safer)
        if (sanitized.Length > 80)
        {
            sanitized = sanitized.Substring(0, 80).Trim();
        }

        if (sanitized != query)
        {
             _logger.LogDebug("Hardened query: '{Original}' -> '{Normalized}'", query, sanitized);
        }

        return sanitized;
    }

    /// <summary>
    /// Registers a negative experience with a peer (timeout, corruption, etc).
    /// </summary>
    public void RecordNegativeExperience(string username, string reason)
    {
        if (string.IsNullOrEmpty(username)) return;

        var reputation = _peerReputations.GetOrAdd(username, _ => new PeerReputation());
        reputation.RecordFailure(reason);

        _logger.LogWarning("Negative experience with peer {Username}: {Reason}. Reputation: {Score}", 
            username, reason, reputation.Score);
    }

    /// <summary>
    /// Registers a positive experience with a peer.
    /// </summary>
    public void RecordPositiveExperience(string username)
    {
        if (string.IsNullOrEmpty(username)) return;

        var reputation = _peerReputations.GetOrAdd(username, _ => new PeerReputation());
        reputation.RecordSuccess();
    }

    /// <summary>
    /// Checks if a peer should be avoided based on past experiences.
    /// </summary>
    public bool ShouldAvoidPeer(string username)
    {
        if (string.IsNullOrEmpty(username)) return false;

        if (_peerReputations.TryGetValue(username, out var reputation))
        {
            // Avoid peers with more than 3 failures and a very low score
            return reputation.Failures > 3 && reputation.Score < 20;
        }

        return false;
    }

    private class PeerReputation
    {
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public double Score => (Successes + 1.0) / (Successes + Failures + 1.0) * 100;

        public void RecordSuccess() => Successes++;
        public void RecordFailure(string reason) => Failures++;
    }
}
