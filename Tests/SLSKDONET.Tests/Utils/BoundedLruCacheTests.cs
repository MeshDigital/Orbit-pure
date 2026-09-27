using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

/// <summary>
/// Pure logic coverage for the LRU eviction ArtworkCacheService's hot cache relies on to keep
/// recently-scrolled-past artwork from being garbage collected the instant it leaves screen —
/// tested here against plain strings rather than real Bitmaps, since Bitmap decoding needs a
/// live Avalonia/Skia platform this test host doesn't have.
/// </summary>
public class BoundedLruCacheTests
{
    [Fact]
    public void Set_ThenTryGet_ReturnsTheValue()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 3);

        cache.Set("a", "apple");

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("apple", value);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 3);

        Assert.False(cache.TryGet("missing", out _));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 2);

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3"); // capacity 2 — "a" was touched longest ago, should be evicted

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryGet_RefreshesRecency_SoItSurvivesOverAnUntouchedEntry()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 2);

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.TryGet("a", out _); // touching "a" makes "b" the least-recently-used one
        cache.Set("c", "3");

        Assert.True(cache.TryGet("a", out _), "\"a\" was re-touched via TryGet and should have survived.");
        Assert.False(cache.TryGet("b", out _), "\"b\" was never re-touched and should have been evicted.");
    }

    [Fact]
    public void Set_SameKeyTwice_UpdatesValueWithoutGrowingCount()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 3);

        cache.Set("a", "1");
        cache.Set("a", "2");

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("2", value);
    }

    [Fact]
    public void Set_SameKeyTwice_RefreshesRecency()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 2);

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("a", "1-again"); // re-Set makes "b" the least-recently-used one
        cache.Set("c", "3");

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 3);
        cache.Set("a", "1");
        cache.Set("b", "2");

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));
    }
}
