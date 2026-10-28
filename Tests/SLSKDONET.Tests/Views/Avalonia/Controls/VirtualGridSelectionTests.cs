using System.Collections;
using System.Linq;
using SLSKDONET.ViewModels;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.Views.Avalonia.Controls;

public class VirtualGridSelectionTests
{
    [Fact]
    public void MaterializedSelection_UsesEnumerationAndSkipsPlaceholders()
    {
        var first = new object();
        var second = new object();
        var source = new EnumerationOnlyCollection(first, new PlaceholderRow(), null, second);

        var selected = VirtualGrid.GetMaterializedSelectableItems(source).ToArray();

        Assert.Equal(new[] { first, second }, selected);
        Assert.Equal(1, source.EnumerationCount);
    }

    [Fact]
    public void MaterializedRange_ReturnsOnlySelectableRowsInsideBounds()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        var source = new EnumerationOnlyCollection(first, new PlaceholderRow(), second, third);

        var selected = VirtualGrid.GetMaterializedSelectableItems(source, 1, 2).ToArray();

        Assert.Equal(new[] { second }, selected);
        Assert.Equal(1, source.EnumerationCount);
    }

    [Fact]
    public void MaterializedRange_InvalidBounds_DoNotEnumerateSource()
    {
        var source = new EnumerationOnlyCollection(new object());

        Assert.Empty(VirtualGrid.GetMaterializedSelectableItems(source, 3, 2));
        Assert.Empty(VirtualGrid.GetMaterializedSelectableItems(source, -1, 2));
        Assert.Equal(0, source.EnumerationCount);
    }

    [Fact]
    public void MaterializedSelection_NullSource_ReturnsEmpty()
    {
        Assert.Empty(VirtualGrid.GetMaterializedSelectableItems(null));
    }

    private sealed class PlaceholderRow : IPlaceholderRow
    {
        public bool IsPlaceholder => true;
    }

    // Deliberately exposes no Count or indexer: select-all must not force a virtualized source
    // to materialize its entire database through indexed page loads.
    private sealed class EnumerationOnlyCollection : IEnumerable
    {
        private readonly object?[] _items;

        public EnumerationOnlyCollection(params object?[] items) => _items = items;

        public int EnumerationCount { get; private set; }

        public IEnumerator GetEnumerator()
        {
            EnumerationCount++;
            foreach (var item in _items)
                yield return item!;
        }
    }
}
