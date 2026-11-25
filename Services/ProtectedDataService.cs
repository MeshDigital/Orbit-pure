using System;
using System.Text;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Platform;

namespace SLSKDONET.Services;

/// <summary>
/// Encrypts and decrypts user-scoped secrets through the platform shell's
/// <see cref="ISecretProtector"/> (#452): DPAPI CurrentUser on Windows (byte-for-byte
/// identical to previous builds), Keychain + AES-256-GCM on macOS, and Secret Service /
/// user-only key file + AES-256-GCM on Linux.
/// </summary>
public class ProtectedDataService
{
    private const string Purpose = "orbit-protected-data";
    private readonly ILogger<ProtectedDataService>? _logger;
    private readonly ISecretProtector _protector;

    public ProtectedDataService(ILogger<ProtectedDataService>? logger = null, ISecretProtector? protector = null)
    {
        _logger = logger;
        _protector = protector ?? PlatformShell.Current.Secrets;
    }

    /// <summary>
    /// True whenever the platform has a working secret protector (Windows DPAPI, macOS Keychain,
    /// Linux Secret Service or user-only key file).
    /// </summary>
    public bool IsSecurityAvailable => _protector is not null;

    public string? Protect(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            var encrypted = _protector.Protect(bytes, Purpose);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to encrypt data using {Backend}", _protector.Backend);
            return null;
        }
    }

    public string? Unprotect(string? encryptedData)
    {
        if (string.IsNullOrEmpty(encryptedData))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(encryptedData);
            var decrypted = _protector.Unprotect(bytes, Purpose);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to decrypt data using {Backend} (wrong user context or corrupted data)", _protector.Backend);
            return null;
        }
    }
}