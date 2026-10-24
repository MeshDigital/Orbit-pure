using SLSKDONET.Views.Avalonia;
using Xunit;

namespace SLSKDONET.Tests.UI;

/// <summary>
/// Regression: Install() overrode NumericUpDown's own property defaults, which Avalonia rejects
/// ("Metadata is already set for NumberFormat"), so App.Initialize threw and every launch crashed
/// (found by the 0.10.0-alpha release smoke test). Install runs at startup, so it must never throw.
/// </summary>
public class NumericInputLocaleTests
{
    [Fact]
    public void Install_DoesNotThrow_AndIsIdempotent()
    {
        var first = Record.Exception(NumericInputLocale.Install);
        var second = Record.Exception(NumericInputLocale.Install);

        Assert.Null(first);
        Assert.Null(second);
    }
}
