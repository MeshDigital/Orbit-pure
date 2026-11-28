using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Vault;

/// <summary>This machine as a Vault knows it: a stable device id and name.</summary>
public sealed record VaultDeviceIdentity(string Id, string Name, string Platform);

/// <summary>
/// Per-machine Vault state in <c>vault-device.json</c> next to the library database: this device's
/// id (created once) and the last Vault folder used. Kept out of config.ini on purpose: it is not a
/// setting, and it must never travel to another machine with a copied config.
/// </summary>
public sealed class VaultLocalState
{
    private sealed class State
    {
        public string DeviceId { get; set; } = Guid.NewGuid().ToString();
        public string? LastVaultPath { get; set; }
    }

    private readonly string _path;
    private readonly ILogger<VaultLocalState> _logger;
    private readonly object _gate = new();
    private State? _state;

    public VaultLocalState(ILogger<VaultLocalState> logger)
        : this(Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "vault-device.json"), logger) { }

    internal VaultLocalState(string path, ILogger<VaultLocalState> logger)
    {
        _path = path;
        _logger = logger;
    }

    public VaultDeviceIdentity Device
    {
        get
        {
            var s = Load();
            return new VaultDeviceIdentity(s.DeviceId, Environment.MachineName, RuntimeInformation.OSDescription);
        }
    }

    public string? LastVaultPath => Load().LastVaultPath;

    public void RememberVault(string vaultRoot)
    {
        lock (_gate)
        {
            var s = Load();
            s.LastVaultPath = vaultRoot;
            Save(s);
        }
    }

    private State Load()
    {
        lock (_gate)
        {
            if (_state != null) return _state;
            try
            {
                if (File.Exists(_path))
                    _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Vault: couldn't read {Path}; starting a new device identity", _path);
            }
            if (_state == null)
            {
                _state = new State();
                Save(_state);
            }
            return _state;
        }
    }

    private void Save(State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Vault: couldn't save {Path}", _path);
        }
    }
}
