using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

public class GuidGeneratorStableIntTests
{
    [Fact]
    public void CreateStableIntFromSeed_SameSeed_ReturnsSameValue()
    {
        var a = GuidGenerator.CreateStableIntFromSeed("artist|title|hash123");
        var b = GuidGenerator.CreateStableIntFromSeed("artist|title|hash123");

        Assert.Equal(a, b);
    }

    [Fact]
    public void CreateStableIntFromSeed_DifferentSeeds_ReturnDifferentValues()
    {
        var a = GuidGenerator.CreateStableIntFromSeed("track-one");
        var b = GuidGenerator.CreateStableIntFromSeed("track-two");

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("some/file/path/track.mp3")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void CreateStableIntFromSeed_AlwaysReturnsNonNegative(string seed)
    {
        var value = GuidGenerator.CreateStableIntFromSeed(seed);

        Assert.True(value >= 0);
    }
}
