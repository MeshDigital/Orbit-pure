using SLSKDONET.Services.Diagnostics;
using Xunit;

namespace SLSKDONET.Tests.Services;

[Collection(NonParallelCollection.Name)]
public sealed class StartupTelemetryTests
{
    [Fact]
    public void MarkOnce_RecordsFirstOccurrenceOnly_AndElapsedIsNonNegative()
    {
        StartupTelemetry.Start();

        Assert.True(StartupTelemetry.MarkOnce("DI built"));
        var first = StartupTelemetry.GetMarkMs("DI built");
        Assert.NotNull(first);
        Assert.True(first >= 0);

        // Second call for the same stage is a no-op and preserves the original timestamp.
        Assert.False(StartupTelemetry.MarkOnce("DI built"));
        Assert.Equal(first, StartupTelemetry.GetMarkMs("DI built"));
        Assert.Null(StartupTelemetry.GetMarkMs("Main window shown"));
    }
}
