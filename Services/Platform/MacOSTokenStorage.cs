using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// macOS Spotify refresh-token storage in the Keychain (#314). The token reaches the <c>security</c> tool only on
/// stdin, hex-encoded (<see cref="OsKeyring"/>), so it never appears in <c>ps</c> output and no character in it can
/// break the command; every call has a timeout. The service/account names are unchanged, so tokens saved by older
/// versions are still found.
/// </summary>
public class MacOSTokenStorage : ISecureTokenStorage
{
    public const string AccountName = "spotify_refresh_token";

    private readonly ILogger<MacOSTokenStorage> _logger;
    private readonly OsKeyring _keyring;

    public MacOSTokenStorage(ILogger<MacOSTokenStorage> logger, OsKeyring? keyring = null)
    {
        _logger = logger;
        _keyring = keyring ?? new OsKeyring(KeyringKind.MacKeychain, new CliProcessRunner());
    }

    public async Task SaveRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh token cannot be null or empty", nameof(refreshToken));

        var (ok, error) = await Task.Run(() => (_keyring.TrySet(AccountName, refreshToken, out var e), e)).ConfigureAwait(false);
        if (!ok)
        {
            _logger.LogError("Failed to save the refresh token to the Keychain: {Error}", error);
            throw new InvalidOperationException($"Failed to save refresh token securely: {error}");
        }
        _logger.LogInformation("Refresh token saved securely to the Keychain");
    }

    public async Task<string?> LoadRefreshTokenAsync()
    {
        var (token, error) = await Task.Run(() => (_keyring.TryGet(AccountName, out var e), e)).ConfigureAwait(false);
        if (token is null) _logger.LogDebug("No refresh token in the Keychain ({Reason})", error);
        return token;
    }

    public async Task DeleteRefreshTokenAsync()
    {
        await Task.Run(() => _keyring.Delete(AccountName)).ConfigureAwait(false);
        _logger.LogInformation("Refresh token deleted from the Keychain");
    }
}
