using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Handles valid updates and migrations for Application Configuration.
/// Specifically responsible for migrating legacy "ScoringWeights" to the new "SearchPolicy".
/// </summary>
public class ConfigMigrationService
{
    private readonly ILogger<ConfigMigrationService> _logger;

    public ConfigMigrationService(ILogger<ConfigMigrationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks the configuration for legacy settings and migrates them to the new format if needed.
    /// </summary>
    /// <param name="config">The AppConfig instance to check and update.</param>
    /// <returns>True if migration occurred and config should be saved; otherwise false.</returns>
    public bool Migrate(AppConfig config)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        bool changed = false;

        // Migration: ScoringWeights -> SearchPolicy
        // We detect "legacy mode" if the SearchPolicy is default but user had Custom weights
        // This is a heuristic - we'll run this once if we detect a specific flag or just always run "smart fix" on startup
        
        // Strategy: Inspect the 'RankingProfile' string or the weights themselves
        
        if (config.RankingProfile == "Custom" && config.SearchPolicy.Priority == SearchPriority.QualityFirst)
        {
            // User had custom weights. Let's try to interpret them.
            _logger.LogInformation("Detected Custom legacy ranking profile. Attempting migration to SearchPolicy.");
            
            var weights = config.CustomWeights;
            var newPolicy = new SearchPolicy();

            // 1. Determine Priority
            if (weights.MusicalWeight > 20)
            {
                // High emphasis on musical keys/BPM implies DJ intent
                 _logger.LogInformation("Legacy config prioritized Musical Key/BPM. Migrating to 'DjReady' priority.");
                newPolicy = SearchPolicy.DjReady();
            }
            else
            {
                // Default to Quality
                _logger.LogInformation("Legacy config standard. Migrating to 'QualityFirst' priority.");
                newPolicy = SearchPolicy.QualityFirst();
            }

            // 2. Determine Speed vs Quality
            if (weights.AvailabilityWeight > weights.QualityWeight + 20)
            {
                // User cared WAY more about finding *something* (Availability) than Quality
                 _logger.LogInformation("Legacy config prioritized Availability. Enabling 'PreferSpeedOverQuality'.");
                newPolicy.PreferSpeedOverQuality = true;
            }

            config.SearchPolicy = newPolicy;
            
            // Set profile to something meaningful so we don't migrate again unnecessarily
            config.RankingProfile = "Migrated_Custom"; 
            changed = true;
        }
        else if (config.RankingProfile == "DJ Mode")
        {
             _logger.LogInformation("Legacy profile 'DJ Mode'. Migrating to 'DjReady' policy.");
             config.SearchPolicy = SearchPolicy.DjReady();
             config.RankingProfile = "Migrated_DJ";
             changed = true;
        }
        else if (config.RankingProfile == "Data Saver")
        {
             _logger.LogInformation("Legacy profile 'Data Saver'. Migrating to 'DataSaver' policy.");
             config.SearchPolicy = SearchPolicy.DataSaver();
             config.RankingProfile = "Migrated_DataSaver";
             changed = true;
        }

        return changed;
#pragma warning restore CS0618 // Type or member is obsolete
    }
}
