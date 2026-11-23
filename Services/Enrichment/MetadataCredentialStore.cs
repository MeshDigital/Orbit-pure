using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment;

/// <summary>
/// API keys for metadata providers (Discogs personal token, AcoustID client key), stored the way
/// ORBIT stores the Soulseek password: encrypted for the current user through
/// <see cref="SLSKDONET.Services.Platform.ISecretProtector"/> (DPAPI on Windows, AES-256-GCM with a keyring-held key on
/// macOS/Linux, #315) in LocalAppData/ORBIT/metadata_keys.dat, never in config.ini. When saving fails,
/// <see cref="LastError"/> says why. Never throws.
/// </summary>
public sealed class MetadataCredentialStore
{
    public const string DiscogsToken = "discogs";
    public const string AcoustIdKey = "acoustid";

    private const string Purpose = "metadata-api-keys";

    private readonly ILogger<MetadataCredentialStore> _logger;
    private readonly SLSKDONET.Services.Platform.ISecretProtector _protector;
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string>? _values;

    public MetadataCredentialStore(ILogger<MetadataCredentialStore> logger, string? directoryOverride = null,
        SLSKDONET.Services.Platform.ISecretProtector? protector = null)
    {
        _logger = logger;
        _protector = protector ?? SLSKDONET.Services.Platform.SecretProtector.Default;
        var dir = directoryOverride ?? Path.Combine(SLSKDONET.Data.OrbitPaths.LocalRoot, "ORBIT");
        _path = Path.Combine(dir, "metadata_keys.dat");
    }

    /// <summary>True on every platform: Windows uses DPAPI, macOS/Linux an AES-GCM key in the OS keyring (#315).</summary>
    public bool IsSecureStorageAvailable => true;
    public string? LastError { get; private set; }

    public string? Get(string name)
    {
        lock (_lock)
        {
            Load();
            return _values!.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }
    }

    public bool Has(string name) => Get(name) != null;

    /// <summary>Saves (or with an empty value removes) a key. False when it could not be stored.</summary>
    public bool Set(string name, string? value)
    {
        lock (_lock)
        {
            Load();
            if (string.IsNullOrWhiteSpace(value)) _values!.Remove(name);
            else _values![name] = value.Trim();
            return Save();
        }
    }

    private void Load()
    {
        if (_values != null) return;
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return;
        try
        {
            var bytes = _protector.Unprotect(File.ReadAllBytes(_path), Purpose);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(bytes));
            if (parsed != null)
                foreach (var kv in parsed) _values[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            LastError = "Stored metadata keys could not be read; enter them again.";
            _logger.LogWarning(ex, "[Enrichment] Could not read {Path}", _path);
        }
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(_values);
            var encrypted = _protector.Protect(json, Purpose);
            var temp = _path + ".tmp";
            SLSKDONET.Services.Platform.GcmSecretProtector.WriteUserOnlyBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LastError = "The key could not be saved: " + ex.Message;
            _logger.LogError(ex, "[Enrichment] Could not write {Path}", _path);
            return false;
        }
    }
}
