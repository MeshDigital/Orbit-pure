namespace SLSKDONET.Services.ProDjLink;

/// <summary>The physical device type announced over Pro DJ Link.</summary>
public enum ProDjLinkDeviceType
{
    Unknown = 0,
    Cdj = 1,
    Xdj = 2,
    Mixer = 3,
    Rekordbox = 4,
}

/// <summary>
/// Phase 3.1: a physical Pioneer deck/mixer on the Pro DJ Link network. Immutable identity snapshot —
/// the registry layers live <c>IsOnline</c> state on top of it.
/// </summary>
public sealed record HardwareDevice(
    string MacAddress,
    string IpAddress,
    int PlayerNumber,
    ProDjLinkDeviceType DeviceType);
