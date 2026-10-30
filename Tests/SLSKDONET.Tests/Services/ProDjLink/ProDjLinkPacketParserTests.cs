using SLSKDONET.Services.ProDjLink;
using Xunit;

namespace SLSKDONET.Tests.Services.ProDjLink;

public class ProDjLinkPacketParserTests
{
    private readonly ProDjLinkPacketParser _parser = new();

    [Fact]
    public void TryParse_ValidMagic_DecodesDevice()
    {
        // "Pio" | type=CDJ | player=2 | MAC 00:1B:2C:3D:4E:5F | IP 192.168.1.2
        byte[] packet = { 0x50, 0x69, 0x6F, 0x01, 0x02, 0x00, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F, 192, 168, 1, 2 };

        var ok = _parser.TryParse(packet, out var device);

        Assert.True(ok);
        Assert.Equal("00:1B:2C:3D:4E:5F", device!.MacAddress);
        Assert.Equal("192.168.1.2", device.IpAddress);
        Assert.Equal(2, device.PlayerNumber);
        Assert.Equal(ProDjLinkDeviceType.Cdj, device.DeviceType);
    }

    [Fact]
    public void TryParse_BadMagic_ReturnsFalse()
    {
        byte[] packet = { 0x00, 0x01, 0x02, 0x01, 0x02, 0x00, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F, 192, 168, 1, 2 };

        Assert.False(_parser.TryParse(packet, out _));
    }

    [Fact]
    public void TryParse_ShortPacket_ReturnsFalse()
    {
        byte[] shortPacket = { 0x50, 0x69, 0x6F };

        Assert.False(_parser.TryParse(shortPacket, out _));
    }
}
