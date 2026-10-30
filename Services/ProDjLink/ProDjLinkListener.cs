using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.ProDjLink;

/// <summary>
/// Phase 3.1: passive Pro DJ Link network listener. Binds UDP port 50000 (the Pioneer keep-alive
/// broadcast port) with <c>ReuseAddress</c> + non-exclusive bind so it observes the hardware's
/// traffic without disrupting its timing, then feeds each captured packet through the
/// <see cref="IPacketParser"/> into the <see cref="HardwareDeviceManager"/>.
/// </summary>
public sealed class ProDjLinkListener : BackgroundService
{
    public const int PioneerKeepAlivePort = 50000;

    private readonly IPacketParser _parser;
    private readonly HardwareDeviceManager _registry;
    private readonly ILogger<ProDjLinkListener> _logger;

    public ProDjLinkListener(
        IPacketParser parser,
        HardwareDeviceManager registry,
        ILogger<ProDjLinkListener> logger)
    {
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, PioneerKeepAlivePort));

        _logger.LogInformation("[ProDjLink] listening on UDP {Port}", PioneerKeepAlivePort);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(stoppingToken).ConfigureAwait(false);

                if (_parser.TryParse(result.Buffer, out var device) && device is not null)
                {
                    _registry.Announce(device);
                    _logger.LogDebug("[ProDjLink] announced {Mac} (player {Player})", device.MacAddress, device.PlayerNumber);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProDjLink] listener stopped unexpectedly");
        }
    }
}
