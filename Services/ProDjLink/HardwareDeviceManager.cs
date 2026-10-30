using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.ProDjLink;

/// <summary>
/// Phase 3.1: thread-safe registry of Pro DJ Link hardware. Tracks devices as they announce and
/// derives <c>IsOnline</c> from a keep-alive timeout (a deck that stops announcing drops off).
/// Keyed by MAC address (case-insensitive).
/// </summary>
public sealed class HardwareDeviceManager
{
    /// <summary>Keep-alive window before a device is considered offline.</summary>
    public static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, HardwareDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upserts a device and marks it online as of <paramref name="now"/> (default: now).</summary>
    public void Announce(HardwareDevice device, DateTimeOffset? now = null)
    {
        _devices[device.MacAddress] = device;
        _lastSeen[device.MacAddress] = now ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Explicitly marks a device offline.</summary>
    public void MarkOffline(string macAddress)
        => _lastSeen[macAddress] = DateTimeOffset.MinValue;

    /// <summary>True when the device was seen within <see cref="OnlineTimeout"/> of <paramref name="now"/>.</summary>
    public bool IsOnline(string macAddress, DateTimeOffset? now = null)
        => _lastSeen.TryGetValue(macAddress, out var seen)
           && ((now ?? DateTimeOffset.UtcNow) - seen) < OnlineTimeout;

    public HardwareDevice? GetDevice(string macAddress)
        => _devices.TryGetValue(macAddress, out var device) ? device : null;

    /// <summary>All known devices, ordered by player number.</summary>
    public IReadOnlyList<HardwareDevice> Devices
        => _devices.Values.OrderBy(d => d.PlayerNumber).ToList();

    /// <summary>Currently-online devices, ordered by player number.</summary>
    public IReadOnlyList<HardwareDevice> OnlineDevices(DateTimeOffset? now = null)
        => _devices.Values.Where(d => IsOnline(d.MacAddress, now)).OrderBy(d => d.PlayerNumber).ToList();
}
