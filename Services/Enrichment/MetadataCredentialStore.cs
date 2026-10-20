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
/// ORBIT stores the Soulseek password and Spotify token: DPAPI-encrypted for the current Windows
/// user in %LocalAppData%\ORBIT (metadata_keys.dat), never in config.ini. On platforms without
/// DPAPI nothing is stored and <see cref="LastError"/> says why. Never throws.
/// </summary>
public sealed class MetadataCredentialStore
{
    public const string DiscogsToken = "discogs";
    public const string AcoustIdKey = "acoustid";

    private readonly ILogger<MetadataCredentialStore> _logger;
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string>? _values;

    public MetadataCredentialStore(ILogger<MetadataCredentialStore> logger, string? directoryOverride = null)
    {
        _logger = logger;
        var dir = directoryOverride ?? Path.Combine(SLSKDONET.Data.OrbitPaths.LocalRoot, "ORBIT");
        _path = Path.Combine(dir, "metadata_keys.dat");
    }

    public bool IsSecureStorageAvailable => OperatingSystem.IsWindows();
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
        if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
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
        if (!OperatingSystem.IsWindows())
        {
            LastError = "Secure key storage is only available on Windows; the key was not saved.";
            _logger.LogWarning("[Enrichment] {Error}", LastError);
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(_values);
            var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            LastError = "The key could not be saved: " + ex.Message;
            _logger.LogError(ex, "[Enrichment] Could not write {Path}", _path);
            return false;
        }
    }
}
