using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Provides methods to encrypt and decrypt data using the Windows Data Protection API (DPAPI).
/// The data is encrypted for the current user account, so only this user can decrypt it.
/// Phase 13: Added cross-platform guards to prevent runtime exceptions on Linux/macOS.
/// </summary>
public class ProtectedDataService
{
    private readonly ILogger<ProtectedDataService>? _logger;
    
    public ProtectedDataService(ILogger<ProtectedDataService>? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Returns true if secure storage is available on this platform (Windows only).
    /// Use this to show UI warnings on unsupported platforms.
    /// </summary>
    public bool IsSecurityAvailable => OperatingSystem.IsWindows();
    
    // Secure implementation using Windows DPAPI
    public string? Protect(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        
        // Phase 13: Cross-platform guard
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogWarning("Secure storage (DPAPI) is only available on Windows. Data will not be encrypted.");
            return null; // Do NOT store unencrypted data silently
        }
            
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            // Protect bytes for Current User
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to encrypt data using Windows DPAPI");
            return null;
        }
    }

    public string? Unprotect(string? encryptedData)
    {
        if (string.IsNullOrEmpty(encryptedData))
            return null;
        
        // Phase 13: Cross-platform guard
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogWarning("Secure storage (DPAPI) is only available on Windows. Cannot decrypt data.");
            return null;
        }
            
        try
        {
            var bytes = Convert.FromBase64String(encryptedData);
            // Unprotect bytes
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to decrypt data (wrong user context or corrupted data)");
            return null;
        }
    }
}