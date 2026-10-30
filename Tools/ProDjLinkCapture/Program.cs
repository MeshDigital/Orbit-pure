using System.Net;
using System.Net.Sockets;

var port = 50000;
var seconds = -1;
var outPath = (string?)null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--seconds" when i + 1 < args.Length: seconds = int.Parse(args[++i]); break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
    }
}

using var client = new UdpClient();
client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
client.ExclusiveAddressUse = false;
client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

using var cts = seconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds)) : new CancellationTokenSource();
using var writer = outPath is null ? null : new StreamWriter(outPath, append: true);

Console.WriteLine($"ProDjLinkCapture: listening on UDP {port}" + (seconds > 0 ? $" for {seconds}s" : "") + ". Ctrl+C to stop.");

try
{
    while (!cts.IsCancellationRequested)
    {
        var result = await client.ReceiveAsync(cts.Token);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{result.Buffer.Length} bytes] {Convert.ToHexString(result.Buffer)}";
        Console.WriteLine(line);
        writer?.WriteLine(line);
        writer?.Flush();
    }
}
catch (OperationCanceledException)
{
    // Timed out.
}

Console.WriteLine("Done.");
