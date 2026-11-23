using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Platform;

namespace SLSKDONET.Services;

public interface ISoulseekCredentialService
{
    Task SaveCredentialsAsync(string username, string password);
    Task<(string? Username, string? Password)> LoadCredentialsAsync();
    Task DeleteCredentialsAsync();
}

/// <summary>
/// The Soulseek login, encrypted for the current user on every platform (#315) through <see cref="ISecretProtector"/>:
/// DPAPI on Windows (the same file format as before, so saved logins keep working), AES-256-GCM with a keyring-held
/// key on macOS/Linux. Stored as <c>LocalAppData/ORBIT/slsk_creds.dat</c> on all three. Before #315, macOS/Linux
/// logged "not implemented" and silently saved nothing.
/// </summary>
public class SoulseekCredentialService : ISoulseekCredentialService
{
    private const string Purpose = "soulseek-credentials";

    private readonly ILogger<SoulseekCredentialService> _logger;
    private readonly ISecretProtector _protector;
    private readonly string _credentialFilePath;

    public SoulseekCredentialService(ILogger<SoulseekCredentialService> logger)
        : this(logger, null, null)
    {
    }

    public SoulseekCredentialService(ILogger<SoulseekCredentialService> logger, ISecretProtector? protector, string? directory)
    {
        _logger = logger;
        _protector = protector ?? SecretProtector.Default;
        var appFolder = directory ?? Path.Combine(SLSKDONET.Data.OrbitPaths.LocalRoot, "ORBIT");
        _credentialFilePath = Path.Combine(appFolder, "slsk_creds.dat");
    }

    /// <summary>False after a save that didn't persist (the reason is logged).</summary>
    public bool LastSaveSucceeded { get; private set; } = true;

    public async Task SaveCredentialsAsync(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return;
        try
        {
            var encrypted = _protector.Protect(Encoding.UTF8.GetBytes($"{username}|{password}"), Purpose);
            await Task.Run(() => GcmSecretProtector.WriteUserOnlyBytes(_credentialFilePath, encrypted)).ConfigureAwait(false);
            LastSaveSucceeded = true;
            _logger.LogInformation("Soulseek credentials saved securely ({Backend}).", _protector.Backend);
        }
        catch (Exception ex)
        {
            LastSaveSucceeded = false;
            _logger.LogError(ex, "Failed to save Soulseek credentials; they will have to be entered again next session");
        }
    }

    public async Task<(string? Username, string? Password)> LoadCredentialsAsync()
    {
        try
        {
            if (!File.Exists(_credentialFilePath))
                return (null, null);

            var encrypted = await File.ReadAllBytesAsync(_credentialFilePath).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetString(_protector.Unprotect(encrypted, Purpose));
            var parts = payload.Split('|', 2);
            if (parts.Length == 2)
                return (parts[0], parts[1]);
            _logger.LogWarning("Stored Soulseek credentials are malformed; sign in again");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Soulseek credentials");
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
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete credentials");
        }
    }
}
