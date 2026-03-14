using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

public interface ISoulseekCredentialService
{
    Task SaveCredentialsAsync(string username, string password);
    Task<(string? Username, string? Password)> LoadCredentialsAsync();
    Task DeleteCredentialsAsync();
}

public class SoulseekCredentialService : ISoulseekCredentialService
{
    private readonly ILogger<SoulseekCredentialService> _logger;
    private readonly string _credentialFilePath;

    public SoulseekCredentialService(ILogger<SoulseekCredentialService> logger)
    {
        _logger = logger;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "ORBIT");
        Directory.CreateDirectory(appFolder);
        
        _credentialFilePath = Path.Combine(appFolder, "slsk_creds.dat");
    }

    public async Task SaveCredentialsAsync(string username, string password)
    {
        try
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return;

            string payload = $"{username}|{password}";
            
            if (OperatingSystem.IsWindows())
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(_credentialFilePath, encrypted);
                _logger.LogInformation("Soulseek credentials saved securely.");
            }
            else
            {
                // Fallback for non-Windows (or implement specific secure storage per platform)
                _logger.LogWarning("Secure credential storage not implemented for this platform.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credentials");
        }
    }

    public async Task<(string? Username, string? Password)> LoadCredentialsAsync()
    {
        try
        {
            if (!File.Exists(_credentialFilePath))
                return (null, null);

            if (OperatingSystem.IsWindows())
            {
                var encrypted = await File.ReadAllBytesAsync(_credentialFilePath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var payload = Encoding.UTF8.GetString(bytes);
                
                var parts = payload.Split('|', 2);
                if (parts.Length == 2)
                {
                    return (parts[0], parts[1]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load credentials");
        }

        return (null, null);
    }

    public async Task DeleteCredentialsAsync()
    {
        try
        {
            if (File.Exists(_credentialFilePath))
            {
                File.Delete(_credentialFilePath);
                _logger.LogInformation("Soulseek credentials deleted.");
            }
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete credentials");
        }
    }
}
