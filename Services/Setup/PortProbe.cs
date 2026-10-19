using System.Net;
using System.Net.Sockets;

namespace SLSKDONET.Services.Setup;

/// <summary>Finds a free TCP port for a second ORBIT instance (profile preview) next to the real one.</summary>
public static class PortProbe
{
    public static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            Serilog.Log.Debug("Port {Port} is in use ({Error})", port, ex.SocketErrorCode);
            return false;
        }
    }

    /// <summary><paramref name="start"/> when it is free, otherwise the next free port above it (up to 50 tries).</summary>
    public static int FindFreePortFrom(int start)
    {
        var first = start is >= 1024 and <= 65000 ? start : 49998;
        for (var port = first; port < first + 50; port++)
        {
            if (IsFree(port))
            {
                if (port != start)
                    Serilog.Log.Information("Soulseek listen port {Start} is taken (another ORBIT is probably running); this profile uses {Port}", start, port);
                return port;
            }
        }
        Serilog.Log.Warning("No free Soulseek listen port found near {Start}; keeping it", start);
        return start;
    }
}
