using System;
using SLSKDONET.Services.ProDjLink;
using Xunit;

namespace SLSKDONET.Tests.Services.ProDjLink;

public class HardwareDeviceManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static HardwareDevice Device(string mac, int player = 1) =>
        new(mac, "192.168.1.10", player, ProDjLinkDeviceType.Cdj);

    [Fact]
    public void Announce_MarksOnline()
    {
        var mgr = new HardwareDeviceManager();
        mgr.Announce(Device("AA:BB:CC:DD:EE:FF"), Now);

        Assert.True(mgr.IsOnline("AA:BB:CC:DD:EE:FF", Now));
    }

    [Fact]
    public void IsOnline_AfterTimeout_IsFalse()
    {
        var mgr = new HardwareDeviceManager();
        mgr.Announce(Device("AA"), Now);

        var later = Now + HardwareDeviceManager.OnlineTimeout + TimeSpan.FromSeconds(1);
        Assert.False(mgr.IsOnline("AA", later));
    }

    [Fact]
    public void MarkOffline_SetsOffline()
    {
        var mgr = new HardwareDeviceManager();
        mgr.Announce(Device("AA"), Now);
        mgr.MarkOffline("AA");

        Assert.False(mgr.IsOnline("AA", Now));
    }

    [Fact]
    public void OnlineDevices_FiltersStale()
    {
        var mgr = new HardwareDeviceManager();
        mgr.Announce(Device("AA", player: 1), Now);
        mgr.Announce(Device("BB", player: 2), Now - TimeSpan.FromSeconds(30)); // stale

        var online = mgr.OnlineDevices(Now);

        Assert.Single(online);
        Assert.Equal("AA", online[0].MacAddress);
    }

    [Fact]
    public void Announce_UpsertsSameMac()
    {
        var mgr = new HardwareDeviceManager();
        mgr.Announce(Device("AA"), Now);
        mgr.Announce(new HardwareDevice("AA", "192.168.1.99", 1, ProDjLinkDeviceType.Cdj), Now);

        Assert.Single(mgr.Devices);
        Assert.Equal("192.168.1.99", mgr.GetDevice("AA")!.IpAddress);
    }
}
