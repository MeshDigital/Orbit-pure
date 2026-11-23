using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Encrypts small secrets (Soulseek login, Spotify token, metadata API keys) for the current user before they are
/// written to disk. One implementation per platform family:
/// <list type="bullet">
/// <item>Windows: DPAPI (CurrentUser) — byte-for-byte what ORBIT always wrote, so existing files keep working.</item>
/// <item>macOS / Linux: AES-256-GCM (authenticated) with a <b>random</b> 256-bit data key kept in the OS keyring
///   (Keychain / Secret Service), or — only when no keyring is reachable — in a key file readable by the user alone.
///   The key is never derived from machine name, user name or OS version (#313).</item>
/// </list>
/// </summary>
public interface ISecretProtector
{
    /// <summary>"Windows DPAPI", "macOS Keychain", "Secret Service", or "key file (no keyring found)".</summary>
    string Backend { get; }
    byte[] Protect(byte[] plaintext, string purpose);
    /// <exception cref="CryptographicException">Tampered, truncated, or encrypted under another key.</exception>
    byte[] Unprotect(byte[] ciphertext, string purpose);
}

public static class SecretProtector
{
    private static readonly Lazy<ISecretProtector> DefaultInstance = new(() =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new GcmSecretProtector(new OsKeyring(OsKeyring.ForCurrentOs(), new CliProcessRunner()),
                Path.Combine(SLSKDONET.Data.OrbitPaths.LocalRoot, "ORBIT"), NullLogger.Instance));

    /// <summary>The protector for this OS (created lazily; never throws on creation).</summary>
    public static ISecretProtector Default => DefaultInstance.Value;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Backend => "Windows DPAPI";
    public byte[] Protect(byte[] plaintext, string purpose) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext, string purpose) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}

/// <summary>AES-256-GCM with a random data key from the OS keyring (or a user-only key file). Format: "OGC1" | nonce(12) | tag(16) | ciphertext.</summary>
public sealed class GcmSecretProtector : ISecretProtector
{
    public const string DataKeyAccount = "data-key";
    private static readonly byte[] Magic = "OGC1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly OsKeyring _keyring;
    private readonly string _keyFilePath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private byte[]? _key;
    private string _backend = "not initialised";

    public GcmSecretProtector(OsKeyring keyring, string directory, ILogger logger)
    {
        _keyring = keyring;
        _keyFilePath = Path.Combine(directory, "secrets.key");
        _logger = logger;
    }

    public string Backend
    {
        get { EnsureKey(); return _backend; }
    }

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        var key = EnsureKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plaintext.Length];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        var output = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        tag.CopyTo(output, Magic.Length + NonceSize);
        cipher.CopyTo(output, Magic.Length + NonceSize + TagSize);
        return output;
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        if (data.Length < Magic.Length + NonceSize + TagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not an ORBIT-encrypted secret");
        var key = EnsureKey();
        var nonce = data.AsSpan(Magic.Length, NonceSize);
        var tag = data.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = data.AsSpan(Magic.Length + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }

    /// <summary>Keyring first, then an existing key file, else a new random key (stored in the keyring when possible).</summary>
    private byte[] EnsureKey()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;

            var fromKeyring = _keyring.TryGet(DataKeyAccount, out _);
            if (TryParseKey(fromKeyring) is { } stored)
            {
                _backend = BackendName(_keyring.Kind);
                return _key = stored;
            }

            if (File.Exists(_keyFilePath) && TryParseKey(File.ReadAllText(_keyFilePath).Trim()) is { } fileKey)
            {
                _backend = "key file (no keyring found)";
                return _key = fileKey;
            }

            var fresh = RandomNumberGenerator.GetBytes(32);
            var hex = Convert.ToHexString(fresh);
            if (_keyring.Kind != KeyringKind.None)
            {
                if (_keyring.TrySet(DataKeyAccount, hex, out var keyringError))
                {
                    _backend = BackendName(_keyring.Kind);
                    _logger.LogInformation("[Secrets] New data key stored in the {Keyring}", _backend);
                    return _key = fresh;
                }
                _logger.LogWarning("[Secrets] OS keyring unavailable ({Error}); keeping the data key in a user-only file", keyringError);
            }

            WriteUserOnlyFile(_keyFilePath, hex);
            _backend = "key file (no keyring found)";
            return _key = fresh;
        }
    }

    private static byte[]? TryParseKey(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 64) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private static string BackendName(KeyringKind kind) => kind switch
    {
        KeyringKind.MacKeychain => "macOS Keychain",
        KeyringKind.SecretService => "Secret Service",
        _ => "key file (no keyring found)",
    };

    /// <summary>Creates the file with owner-only permissions from the start (no window where it is world-readable).</summary>
    internal static void WriteUserOnlyFile(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    /// <summary>Writes encrypted bytes with owner-only permissions (non-Windows).</summary>
    public static void WriteUserOnlyBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }
}
