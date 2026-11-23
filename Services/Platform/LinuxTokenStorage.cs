using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Linux Spotify refresh-token storage (#313). The token is encrypted with <see cref="ISecretProtector"/>: AES-256-GCM
/// under a random data key held in the freedesktop Secret Service (GNOME Keyring / KWallet), or in a user-only key
/// file when no keyring is reachable. The old file (<c>qmusicslsk/spotify_token.enc</c>, AES-CBC with a key anyone could
/// recompute from the machine name, user name and OS version) is read once, migrated and deleted.
/// </summary>
public class LinuxTokenStorage : ISecureTokenStorage
{
    private const string Purpose = "spotify-refresh-token";

    private readonly ILogger<LinuxTokenStorage> _logger;
    private readonly ISecretProtector _protector;
    private readonly string _tokenFilePath;
    private readonly string _legacyFilePath;

    public LinuxTokenStorage(ILogger<LinuxTokenStorage> logger, ISecretProtector? protector = null,
        string? directory = null, string? legacyFilePath = null)
    {
        _logger = logger;
        _protector = protector ?? SecretProtector.Default;
        var dir = directory ?? Path.Combine(SLSKDONET.Data.OrbitPaths.LocalRoot, "ORBIT");
        _tokenFilePath = Path.Combine(dir, "spotify_token.dat");
        _legacyFilePath = legacyFilePath ?? Path.Combine(
            string.IsNullOrEmpty(SLSKDONET.Data.OrbitPaths.RoamingRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : SLSKDONET.Data.OrbitPaths.RoamingRoot,
            "qmusicslsk", "spotify_token.enc");
    }

    public async Task SaveRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh token cannot be null or empty", nameof(refreshToken));
        try
        {
            var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(refreshToken), Purpose);
            await Task.Run(() => GcmSecretProtector.WriteUserOnlyBytes(_tokenFilePath, encrypted)).ConfigureAwait(false);
            _logger.LogInformation("Refresh token saved ({Backend}, AES-256-GCM)", _protector.Backend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save refresh token");
            throw new InvalidOperationException("Failed to save refresh token securely", ex);
        }
    }

    public async Task<string?> LoadRefreshTokenAsync()
    {
        try
        {
            if (File.Exists(_tokenFilePath))
            {
                var data = await File.ReadAllBytesAsync(_tokenFilePath).ConfigureAwait(false);
                return Encoding.UTF8.GetString(_protector.Unprotect(data, Purpose));
            }
            return await MigrateLegacyAsync().ConfigureAwait(false);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Stored refresh token couldn't be decrypted (tampered, or the keyring's data key changed); sign in to Spotify again");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load refresh token");
            return null;
        }
    }

    public async Task DeleteRefreshTokenAsync()
    {
        try
        {
            if (File.Exists(_tokenFilePath)) File.Delete(_tokenFilePath);
            if (File.Exists(_legacyFilePath)) File.Delete(_legacyFilePath);
            _logger.LogInformation("Refresh token deleted");
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete refresh token");
            throw new InvalidOperationException("Failed to delete refresh token", ex);
        }
    }

    /// <summary>One-time move from the pre-#313 file; the weak legacy file is deleted once the token is re-saved.</summary>
    private async Task<string?> MigrateLegacyAsync()
    {
        if (!File.Exists(_legacyFilePath)) return null;
        string token;
        try
        {
            token = DecryptLegacy(await File.ReadAllBytesAsync(_legacyFilePath).ConfigureAwait(false));
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Legacy refresh token couldn't be read; it is deleted, sign in to Spotify again");
            File.Delete(_legacyFilePath);
            return null;
        }

        await SaveRefreshTokenAsync(token).ConfigureAwait(false);
        File.Delete(_legacyFilePath);
        _logger.LogInformation("Migrated the Spotify refresh token from the old weakly-keyed file");
        return token;
    }

    /// <summary>The pre-#313 format (AES-CBC, IV prefix, machine-derived key). Read-only, for migration.</summary>
    internal static string DecryptLegacy(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = LegacyMachineKey();
        var iv = new byte[16];
        if (data.Length <= iv.Length) throw new CryptographicException("Legacy token file is truncated");
        Buffer.BlockCopy(data, 0, iv, 0, iv.Length);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(data, iv.Length, data.Length - iv.Length));
    }

    /// <summary>The old, guessable key. Kept only so existing tokens can be migrated; never used to write.</summary>
    internal static byte[] LegacyMachineKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion}"));
}
