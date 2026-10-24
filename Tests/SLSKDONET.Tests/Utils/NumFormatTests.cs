using System;
using System.Globalization;
using SLSKDONET.Converters;
using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

/// <summary>
/// The owner runs a Belgian Windows: "174,0 BPM", "1,0x", "0,0 %" and "2400,50 GB free" showed up
/// next to English copy. Every assertion here runs under nl-BE to prove the output no longer
/// follows the OS culture.
/// </summary>
public class NumFormatTests : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;

    public NumFormatTests()
    {
        CultureInfo.CurrentCulture = new CultureInfo("nl-BE");
        CultureInfo.CurrentUICulture = new CultureInfo("nl-BE");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previous;
        CultureInfo.CurrentUICulture = _previousUi;
    }

    [Fact]
    public void Sanity_TheTestReallyRunsUnderADecimalCommaCulture()
    {
        Assert.Equal("174,0", 174.0.ToString("F1"));
    }

    [Theory]
    [InlineData(174.0, "174.0")]
    [InlineData(128.25, "128.3")]
    [InlineData(87.04, "87.0")]
    public void Bpm_OneDecimal_Invariant(double bpm, string expected) => Assert.Equal(expected, NumFormat.Bpm(bpm));

    [Fact]
    public void Bpm_MissingValue_ShowsDash()
    {
        Assert.Equal("—", NumFormat.Bpm((double?)null));
        Assert.Equal("—", NumFormat.Bpm((double?)0));
        Assert.Equal("174.0", NumFormat.Bpm((double?)174));
    }

    [Theory]
    [InlineData(1.0, "+1.0 BPM")]
    [InlineData(-2.46, "-2.5 BPM")]
    [InlineData(0.0, "0.0 BPM")]
    [InlineData(-0.01, "0.0 BPM")]
    public void BpmDelta_IsSigned(double delta, string expected) => Assert.Equal(expected, NumFormat.BpmDelta(delta));

    [Fact]
    public void Percentages_HaveNoSpaceAndADot()
    {
        Assert.Equal("91%", NumFormat.Percent(0.91));
        Assert.Equal("91.0%", NumFormat.PercentValue(91, 1));
        Assert.Equal("0%", NumFormat.Percent(-0.001));
        Assert.Equal("+1.6%", NumFormat.SignedPercent(1.6));
        Assert.Equal("0.0%", NumFormat.SignedPercent(0));
        Assert.Equal("-3.2%", NumFormat.SignedPercent(-3.2));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(8_808_038L, "8.4 MB")]
    [InlineData(536_870_912L, "512 MB")]
    [InlineData(2_577_517_248_512L, "2,400.5 GB")]
    public void Size_UsesInvariantGroupingAndDecimals(long bytes, string expected) => Assert.Equal(expected, NumFormat.Size(bytes));

    [Fact]
    public void Durations_SecondsAndRatios()
    {
        Assert.Equal("22.1 s", NumFormat.Seconds(22.08));
        Assert.Equal("3:07", NumFormat.Duration(187));
        Assert.Equal("1:02:45", NumFormat.Duration(TimeSpan.FromSeconds(3765)));
        Assert.Equal("1.0x", NumFormat.Ratio(1));
        Assert.Equal("3.2 ms", NumFormat.Ms(3.2));
        Assert.Equal("-14.0 LUFS", NumFormat.Lufs(-14));
        Assert.Equal("+1.5 dB", NumFormat.Db(1.5, signed: true));
        Assert.Equal("44.1 kHz", NumFormat.Khz(44100));
    }

    [Fact]
    public void Speed_SwitchesUnits()
    {
        Assert.Equal("512 KB/s", NumFormat.Speed(512 * 1024));
        Assert.Equal("1.5 MB/s", NumFormat.Speed(1.5 * 1024 * 1024));
        Assert.Equal("0 KB/s", NumFormat.Speed(-5));
    }

    [Fact]
    public void Ui_InterpolationIsInvariant_AndPercentHasNoSpace()
    {
        double gain = 1.25, score = 0.913;
        Assert.Equal("+1.3 dB · 91%", NumFormat.Ui($"{gain:+0.0;-0.0;0.0} dB · {score:P0}"));
        Assert.Equal("2,400.5", NumFormat.Ui($"{2400.5:N1}"));
    }

    [Theory]
    [InlineData("174,5", 174.5)]
    [InlineData("174.5", 174.5)]
    [InlineData(" 128 ", 128)]
    [InlineData("-3,5", -3.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1,234.5", 1234.5)]
    public void TryParse_AcceptsBothDecimalStyles(string text, double expected)
    {
        Assert.True(NumFormat.TryParse(text, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void TryParse_RejectsGarbage(string? text) => Assert.False(NumFormat.TryParse(text, out _));

    [Fact]
    public void NumberConverter_FormatsAndParsesBack()
    {
        var c = new NumberConverter();
        var nl = CultureInfo.CurrentCulture;
        Assert.Equal("174.0", c.Convert(174.0, typeof(string), "bpm", nl));
        Assert.Equal("91%", c.Convert(0.91, typeof(string), "pct", nl));
        Assert.Equal("1.0x", c.Convert(1.0, typeof(string), "{}{0:0.0}x", nl));
        Assert.Equal("174.0 BPM", c.Convert(174.0, typeof(string), "{0:F1} BPM", nl));
        Assert.Equal("+1.0%", c.Convert(1.0, typeof(string), "{}{0:+0.0;-0.0;+0.0}%", nl));
        Assert.Equal("2.4 MB", c.Convert(2_500_000L, typeof(string), "size", nl));
        Assert.Equal("12.35", c.Convert(12.345f, typeof(string), "F2", nl));

        Assert.Equal(174.5, c.ConvertBack("174,5", typeof(double), "{0:F1}", nl));
        Assert.Equal(174.5, c.ConvertBack("174.5 BPM", typeof(double), "{0:F1} BPM", nl));
        Assert.Equal(174.5f, c.ConvertBack("174,5", typeof(float), null, nl));
    }
}
