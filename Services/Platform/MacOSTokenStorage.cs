using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// macOS-specific secure token storage using Keychain.
/// Uses the 'security' command-line tool to interact with Keychain.
/// </summary>
public class MacOSTokenStorage : ISecureTokenStorage
{
    private readonly ILogger<MacOSTokenStorage> _logger;
    private const string ServiceName = "ORBIT";
    private const string AccountName = "spotify_refresh_token";

    public MacOSTokenStorage(ILogger<MacOSTokenStorage> logger)
    {
        _logger = logger;
    }

    public async Task SaveRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh token cannot be null or empty", nameof(refreshToken));

        try
        {
            // Delete existing token first (if any)
            await DeleteRefreshTokenAsync();

            // Add to Keychain using 'security' command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "security",
                    Arguments = $"add-generic-password -a {AccountName} -s {ServiceName} -w \"{refreshToken}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("Failed to save to Keychain: {Error}", error);
                throw new InvalidOperationException($"Failed to save to Keychain: {error}");
            }

            _logger.LogInformation("Refresh token saved securely to Keychain");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save refresh token to Keychain");
            throw new InvalidOperationException("Failed to save refresh token securely", ex);
        }
    }

    public async Task<string?> LoadRefreshTokenAsync()
    {
        try
        {
            // Read from Keychain using 'security' command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "security",
                    Arguments = $"find-generic-password -a {AccountName} -s {ServiceName} -w",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogDebug("No stored refresh token found in Keychain");
                return null;
            }

            var refreshToken = output.Trim();
            _logger.LogInformation("Refresh token loaded successfully from Keychain");
            return refreshToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load refresh token from Keychain");
            return null;
        }
    }

    public async Task DeleteRefreshTokenAsync()
    {
        try
        {
            // Delete from Keychain using 'security' command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "security",
                    Arguments = $"delete-generic-password -a {AccountName} -s {ServiceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            _logger.LogInformation("Refresh token deleted from Keychain");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete refresh token from Keychain (may not exist)");
            // Don't throw - deletion failure is not critical
        }
    }
}
