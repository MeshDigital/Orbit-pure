using System;

namespace SLSKDONET.Services.ProDjLink;

/// <summary>Decodes a raw Pro DJ Link packet into device metadata, or reports it as unrecognized.</summary>
public interface IPacketParser
{
    bool TryParse(ReadOnlySpan<byte> packet, out HardwareDevice? device);
}

/// <summary>
/// Phase 3.1: parser for the Pioneer Pro DJ Link keep-alive. Recognizes the magic header
/// (<c>0x50 0x69 0x6F</c>, "Pio") and decodes device type, player number, MAC and IP into a
/// <see cref="HardwareDevice"/>. Packet layout is the scaffold's contract and can be refined once
/// the live byte stream is captured.
/// </summary>
public sealed class ProDjLinkPacketParser : IPacketParser
{
    private static readonly byte[] Magic = { 0x50, 0x69, 0x6F }; // "Pio"

    public bool TryParse(ReadOnlySpan<byte> packet, out HardwareDevice? device)
    {
        device = null;
        if (packet.Length < 15 || !packet.StartsWith(Magic))
            return false;

        var deviceType = (ProDjLinkDeviceType)packet[3];
        var playerNumber = packet[4];
        var mac = $"{packet[5]:X2}:{packet[6]:X2}:{packet[7]:X2}:{packet[8]:X2}:{packet[9]:X2}:{packet[10]:X2}";
        var ip = $"{packet[11]}.{packet[12]}.{packet[13]}.{packet[14]}";

        device = new HardwareDevice(mac, ip, playerNumber, deviceType);
        return true;
    }
}
