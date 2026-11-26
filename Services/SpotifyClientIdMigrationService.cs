using System;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public enum SpotifyClientIdKind
{
    None,
    BuiltInTester,
    CustomOwnApp
}

public enum SpotifyClientIdMigrationOutcome
{
    UnchangedNone,
    UnchangedCustom,
    KeptBuiltInWithActiveSession,
    ClearedLegacyBuiltIn
}

/// <summary>
/// Migrates existing installs away from silently defaulting to ORBIT's built-in development-mode
/// Client ID (Epic #508 / #512 SP1). Allowlisted testers whose stored session still verifies keep
/// working; everyone else has the legacy default cleared so they see the honest public-link /
/// own-app setup path.
/// </summary>
public static class SpotifyClientIdMigrationService
{
    public static SpotifyClientIdKind Classify(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return SpotifyClientIdKind.None;
        if (string.Equals(clientId.Trim(), SpotifyAuthService.DefaultClientId, StringComparison.OrdinalIgnoreCase))
            return SpotifyClientIdKind.BuiltInTester;
        return SpotifyClientIdKind.CustomOwnApp;
    }

    public static string DescribeKind(string? clientId) => Classify(clientId) switch
    {
        SpotifyClientIdKind.None => "none configured",
        SpotifyClientIdKind.BuiltInTester => "ORBIT tester app (development mode: allowlisted accounts only)",
        SpotifyClientIdKind.CustomOwnApp => "own Spotify app",
        _ => "none configured"
    };

    public static SpotifyClientIdMigrationOutcome MigrateIfNeeded(
        AppConfig config,
        bool hasVerifiedBuiltInSession,
        Action<AppConfig>? saveConfig = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var kind = Classify(config.SpotifyClientId);
        if (kind == SpotifyClientIdKind.None)
            return SpotifyClientIdMigrationOutcome.UnchangedNone;

        if (kind == SpotifyClientIdKind.CustomOwnApp)
            return SpotifyClientIdMigrationOutcome.UnchangedCustom;

        if (hasVerifiedBuiltInSession)
        {
            logger?.LogInformation("Spotify Client ID migration: kept ORBIT built-in tester ID because the stored session verified.");
            return SpotifyClientIdMigrationOutcome.KeptBuiltInWithActiveSession;
        }

        config.SpotifyClientId = string.Empty;
        saveConfig?.Invoke(config);
        logger?.LogInformation("Spotify Client ID migration: cleared legacy built-in Client ID (no verified session); public-link import and own-app setup are now the default.");
        return SpotifyClientIdMigrationOutcome.ClearedLegacyBuiltIn;
    }
}
