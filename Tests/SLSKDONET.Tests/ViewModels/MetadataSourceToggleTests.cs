using SLSKDONET.Services.Enrichment;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Discogs/AcoustID showed their switch ON next to "Needs a token", which read as if they worked.
/// A source's switch is ON only when the source will actually be asked.
/// </summary>
public class MetadataSourceToggleTests
{
    [Theory]
    [InlineData("discogs", ProviderAvailability.NeedsKey, "Waiting for a token")]
    [InlineData("acoustid", ProviderAvailability.NeedsKey, "Waiting for an API key")]
    [InlineData("acoustid", ProviderAvailability.NeedsTool, "Waiting for fpcalc (Chromaprint)")]
    [InlineData("spotify", ProviderAvailability.NotConnected, "Waiting for Spotify · connect it first")]
    public void MissingRequirement_SwitchIsOffAndDisabled_EvenWhenWanted(string name, ProviderAvailability availability, string text)
    {
        var s = MetadataSourceToggle.Compute(name, userWantsIt: true, isAlwaysOn: false, availability);
        Assert.False(s.SwitchOn);
        Assert.False(s.CanToggle);
        Assert.True(s.IsWaiting);
        Assert.Equal(text, s.WaitingText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ready_SwitchFollowsTheUsersChoice(bool wanted)
    {
        var s = MetadataSourceToggle.Compute("discogs", wanted, isAlwaysOn: false, ProviderAvailability.Ready);
        Assert.Equal(wanted, s.SwitchOn);
        Assert.True(s.CanToggle);
        Assert.False(s.IsWaiting);
    }

    [Fact]
    public void AlwaysOn_IsOnAndLocked()
    {
        var s = MetadataSourceToggle.Compute("tags", userWantsIt: false, isAlwaysOn: true, ProviderAvailability.Ready);
        Assert.True(s.SwitchOn);
        Assert.False(s.CanToggle);
    }

    [Fact]
    public void Blocked_IsNotWaiting_TheChoiceStaysVisible()
    {
        var s = MetadataSourceToggle.Compute("discogs", userWantsIt: true, isAlwaysOn: false, ProviderAvailability.Blocked);
        Assert.True(s.SwitchOn);
        Assert.False(s.IsWaiting);
    }
}
