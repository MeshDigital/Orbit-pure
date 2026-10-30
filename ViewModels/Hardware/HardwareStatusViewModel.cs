using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Services.ProDjLink;

namespace SLSKDONET.ViewModels.Hardware;

/// <summary>A status-bar slot (a player deck or the mixer) with its online state.</summary>
public sealed record HardwareStatusItem(string Label, bool IsOnline, ProDjLinkDeviceType DeviceType)
{
    public string DisplayText => $"{Label}: {(IsOnline ? "Online" : "Offline")}";
}

/// <summary>
/// Phase 3.2: exposes the live Pro DJ Link hardware state to the cockpit. Polls
/// <see cref="HardwareDeviceManager.OnlineDevices()"/> on a UI timer and maintains a fixed set of
/// player/mixer slots so the status bar reflects decks the moment they announce or drop off.
/// </summary>
public sealed class HardwareStatusViewModel : ReactiveObject, IDisposable
{
    private readonly HardwareDeviceManager _registry;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<HardwareStatusItem> Slots { get; } = new();

    public HardwareStatusViewModel(HardwareDeviceManager registry)
    {
        _registry = registry;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        var online = _registry.OnlineDevices();

        Slots.Clear();
        for (var player = 1; player <= 4; player++)
        {
            var deck = online.FirstOrDefault(d => d.PlayerNumber == player && d.DeviceType != ProDjLinkDeviceType.Mixer);
            Slots.Add(new HardwareStatusItem($"Player {player}", deck is not null, deck?.DeviceType ?? ProDjLinkDeviceType.Unknown));
        }

        var mixer = online.FirstOrDefault(d => d.DeviceType == ProDjLinkDeviceType.Mixer);
        Slots.Add(new HardwareStatusItem("Mixer", mixer is not null, mixer?.DeviceType ?? ProDjLinkDeviceType.Unknown));
    }

    public void Dispose() => _timer.Stop();
}
