using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace SLSKDONET.Tests;

public class AppExceptionNoiseFilterTests
{
    private static bool InvokeIsTransientSoulseekError(Exception ex)
    {
        var method = typeof(SLSKDONET.App).GetMethod(
            "IsTransientSoulseekError",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { ex })!;
    }

    [Fact]
    public void IsTransientSoulseekError_ReportedPeerReadTimeout_ReturnsTrue()
    {
        var socketEx = new SocketException(10060);
        var ioEx = new IOException(
            "Unable to read data from the transport connection: A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond.",
            socketEx);
        var connectionEx = new Exception(
            "Failed to read 4 bytes from 80.170.189.31:59688: Unable to read data from the transport connection.",
            ioEx);
        var aggregate = new AggregateException(connectionEx);

        var result = InvokeIsTransientSoulseekError(aggregate);

        Assert.True(result);
    }

    [Fact]
    public void IsTransientSoulseekError_UnrelatedLogicError_ReturnsFalse()
    {
        var ex = new InvalidOperationException("The collection was modified unexpectedly.");

        var result = InvokeIsTransientSoulseekError(ex);

        Assert.False(result);
    }
}
