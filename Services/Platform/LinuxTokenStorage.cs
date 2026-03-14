using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Linux-specific secure token storage using AES encryption.
/// Encrypts tokens with a machine-specific key derived from hardware ID.
/// </summary>
public class LinuxTokenStorage : ISecureTokenStorage
{
    private readonly ILogger<LinuxTokenStorage> _logger;
    private readonly string _tokenFilePath;
    private readonly byte[] _machineKey;

    public LinuxTokenStorage(ILogger<LinuxTokenStorage> logger)
    {
        _logger = logger;
        
        // Store in user's .config folder
        var configPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(configPath))
        {
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        
        var appFolder = Path.Combine(configPath, "qmusicslsk");
        Directory.CreateDirectory(appFolder);
        
        _tokenFilePath = Path.Combine(appFolder, "spotify_token.enc");
        
        // Generate machine-specific encryption key
        _machineKey = GenerateMachineKey();
    }

    public async Task SaveRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh token cannot be null or empty", nameof(refreshToken));

        try
        {
            using var aes = Aes.Create();
            aes.Key = _machineKey;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var tokenBytes = Encoding.UTF8.GetBytes(refreshToken);
            var encryptedBytes = encryptor.TransformFinalBlock(tokenBytes, 0, tokenBytes.Length);

            // Prepend IV to encrypted data
            var dataToWrite = new byte[aes.IV.Length + encryptedBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, dataToWrite, 0, aes.IV.Length);
            Buffer.BlockCopy(encryptedBytes, 0, dataToWrite, aes.IV.Length, encryptedBytes.Length);

            await File.WriteAllBytesAsync(_tokenFilePath, dataToWrite);
            
            // Set file permissions to user-only (chmod 600)
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(_tokenFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            
            _logger.LogInformation("Refresh token saved securely using AES encryption");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save refresh token");
            throw new InvalidOperationException("Failed to save refresh token securely", ex);
        }
    }

    public async Task<string?> LoadRefreshTokenAsync()
    {
        if (!File.Exists(_tokenFilePath))
        {
            _logger.LogDebug("No stored refresh token found");
            return null;
        }

        try
        {
            var dataToRead = await File.ReadAllBytesAsync(_tokenFilePath);

            using var aes = Aes.Create();
            aes.Key = _machineKey;

            // Extract IV from the beginning
            var iv = new byte[aes.IV.Length];
            Buffer.BlockCopy(dataToRead, 0, iv, 0, iv.Length);
            aes.IV = iv;

            // Extract encrypted data
            var encryptedBytes = new byte[dataToRead.Length - iv.Length];
            Buffer.BlockCopy(dataToRead, iv.Length, encryptedBytes, 0, encryptedBytes.Length);

            // Decrypt
            using var decryptor = aes.CreateDecryptor();
            var tokenBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            var refreshToken = Encoding.UTF8.GetString(tokenBytes);
            
            _logger.LogInformation("Refresh token loaded successfully");
            return refreshToken;
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt refresh token (may be corrupted or from different machine)");
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
            if (File.Exists(_tokenFilePath))
            {
                File.Delete(_tokenFilePath);
                _logger.LogInformation("Refresh token deleted");
            }
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete refresh token");
            throw new InvalidOperationException("Failed to delete refresh token", ex);
        }
    }

    /// <summary>
    /// Generates a machine-specific encryption key.
    /// Uses machine name and user name to create a deterministic key.
    /// </summary>
    private static byte[] GenerateMachineKey()
    {
        // Combine machine-specific identifiers
        var machineId = $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(machineId));
        
        // Use first 32 bytes for AES-256
        return hashBytes;
    }
}
