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
    /// Ensures any legacy <c>RankingProfile</c> value normalises to the standard
    /// <see cref="SearchPriority.QualityFirst"/> policy while preserving the user's saved
    /// Safety Gates (Epic #543 / #546 SQ2).
    /// </summary>
    public bool Migrate(AppConfig config)
    {
        if (config.SearchPolicy == null)
        {
            config.SearchPolicy = SearchPolicy.QualityFirst();
            return true;
        }

        if (config.SearchPolicy.Priority != SearchPriority.QualityFirst)
        {
            _logger.LogInformation("Normalizing legacy search priority {Priority} to QualityFirst while preserving safety gates.", config.SearchPolicy.Priority);
            config.SearchPolicy.Priority = SearchPriority.QualityFirst;
            return true;
        }

        return false;
    }
}
