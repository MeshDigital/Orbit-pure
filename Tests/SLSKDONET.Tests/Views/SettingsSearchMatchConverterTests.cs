using SLSKDONET.Views.Avalonia.Converters;
using Xunit;

namespace SLSKDONET.Tests.Views;

// ─────────────────────────────────────────────────────────────────────────
// SettingsSearchMatchConverter drives the Settings page's search box —
// binds a section's IsVisible to the typed search text, with each section's
// own keyword string as the ConverterParameter.
// ─────────────────────────────────────────────────────────────────────────

public class SettingsSearchMatchConverterTests
{
    private readonly SettingsSearchMatchConverter _sut = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceSearch_AlwaysVisible(string? searchText)
    {
        var result = _sut.Convert(searchText, typeof(bool), "Soulseek Connection username", null);

        Assert.Equal(true, result);
    }

    [Fact]
    public void MatchingSubstring_CaseInsensitive_ReturnsTrue()
    {
        var result = _sut.Convert("USERNAME", typeof(bool), "Soulseek Connection username auto-connect", null);

        Assert.Equal(true, result);
    }

    [Fact]
    public void NonMatchingSubstring_ReturnsFalse()
    {
        var result = _sut.Convert("bitrate", typeof(bool), "Soulseek Connection username auto-connect", null);

        Assert.Equal(false, result);
    }

    [Fact]
    public void NullKeywords_NonEmptySearch_ReturnsFalse()
    {
        var result = _sut.Convert("anything", typeof(bool), null, null);

        Assert.Equal(false, result);
    }
}
