using System;
using System.Text;

namespace SLSKDONET.Services.Platform;

/// <summary>Which OS keyring a secret lives in.</summary>
public enum KeyringKind
{
    None,
    /// <summary>macOS Keychain via the <c>security</c> tool.</summary>
    MacKeychain,
    /// <summary>freedesktop Secret Service (GNOME Keyring, KWallet…) via <c>secret-tool</c> (libsecret-tools).</summary>
    SecretService,
}

/// <summary>
/// Small, safe wrapper over the OS keyring tools (#313/#314). Every secret goes to the tool on stdin:
/// <list type="bullet">
/// <item>macOS: <c>security -i</c> reads the command from stdin, and the secret is hex-encoded (<c>-X</c>) so no
///   quoting or escaping is ever involved.</item>
/// <item>Linux: <c>secret-tool store</c> reads the secret from stdin.</item>
/// </list>
/// Every write is verified by reading it back. All service/account names are fixed ASCII constants.
/// </summary>
public sealed class OsKeyring
{
    public const string ServiceName = "ORBIT";

    private readonly ICliProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public OsKeyring(KeyringKind kind, ICliProcessRunner runner, TimeSpan? timeout = null)
    {
        Kind = kind;
        _runner = runner;
        _timeout = timeout ?? CliProcessRunner.DefaultTimeout;
    }

    public KeyringKind Kind { get; }

    /// <summary>The keyring this OS uses (None on Windows, which has DPAPI instead).</summary>
    public static KeyringKind ForCurrentOs() =>
        OperatingSystem.IsMacOS() ? KeyringKind.MacKeychain
        : OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? KeyringKind.SecretService
        : KeyringKind.None;

    /// <summary>The stored secret, or null when absent or the keyring isn't reachable (<paramref name="error"/> says why).</summary>
    public string? TryGet(string account, out string? error)
    {
        ValidateAccount(account);
        error = null;
        var result = Kind switch
        {
            KeyringKind.MacKeychain => _runner.Run("security", new[] { "find-generic-password", "-a", account, "-s", ServiceName, "-w" }, null, _timeout),
            KeyringKind.SecretService => _runner.Run("secret-tool", new[] { "lookup", "service", ServiceName, "account", account }, null, _timeout),
            _ => CliResult.NotStarted("no OS keyring on this platform"),
        };
        if (!result.Succeeded)
        {
            error = result.Started ? result.StdErr.Trim() : result.StdErr;
            return null;
        }
        var value = result.StdOut.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    /// <summary>Stores <paramref name="secret"/> (printable ASCII) and verifies it by reading it back.</summary>
    public bool TrySet(string account, string secret, out string? error)
    {
        ValidateAccount(account);
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("Secret is empty", nameof(secret));
        CliResult result;
        switch (Kind)
        {
            case KeyringKind.MacKeychain:
                // `security -i` takes its commands from stdin, so the secret never reaches argv; -X takes the
                // password as hex, so no character in it can break the command line. -U updates an existing item.
                var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(secret));
                result = _runner.Run("security", new[] { "-i" },
                    $"add-generic-password -U -a {account} -s {ServiceName} -X {hex}\n", _timeout);
                break;
            case KeyringKind.SecretService:
                result = _runner.Run("secret-tool", new[] { "store", "--label", $"{ServiceName} {account}", "service", ServiceName, "account", account },
                    secret, _timeout);
                break;
            default:
                error = "no OS keyring on this platform";
                return false;
        }

        if (!result.Started || result.TimedOut)
        {
            error = result.StdErr;
            return false;
        }
        // `security -i` can exit 0 after a failed command: trust only the read-back.
        var stored = TryGet(account, out var readError);
        if (string.Equals(stored, secret, StringComparison.Ordinal))
        {
            error = null;
            return true;
        }
        error = string.IsNullOrWhiteSpace(result.StdErr) ? readError ?? "the keyring didn't keep the secret" : result.StdErr.Trim();
        return false;
    }

    public void Delete(string account)
    {
        ValidateAccount(account);
        _ = Kind switch
        {
            KeyringKind.MacKeychain => _runner.Run("security", new[] { "delete-generic-password", "-a", account, "-s", ServiceName }, null, _timeout),
            KeyringKind.SecretService => _runner.Run("secret-tool", new[] { "clear", "service", ServiceName, "account", account }, null, _timeout),
            _ => CliResult.NotStarted("no OS keyring"),
        };
    }

    private static void ValidateAccount(string account)
    {
        if (string.IsNullOrEmpty(account) || !System.Linq.Enumerable.All(account, c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("Keyring account names are fixed ASCII identifiers", nameof(account));
    }
}
