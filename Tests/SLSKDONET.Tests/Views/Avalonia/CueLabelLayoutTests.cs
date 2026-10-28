using System.Collections.Generic;
using Avalonia;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.Views.Avalonia;

public class CueLabelLayoutTests
{
    [Fact]
    public void RightEdgeLabel_IsShiftedBackInsideViewport()
    {
        Assert.True(CueLabelLayout.TryPlace(390, 80, 14, 400, 16, 160, new List<Rect>(), out var label));
        Assert.Equal(316, label.X);
        Assert.Equal(400, label.Right);
    }

    [Fact]
    public void NearbyLabels_UseSeparateLanesWithoutOverlapping()
    {
        var occupied = new List<Rect>();
        Assert.True(CueLabelLayout.TryPlace(100, 90, 14, 800, 16, 160, occupied, out var first));
        occupied.Add(first);
        Assert.True(CueLabelLayout.TryPlace(110, 90, 14, 800, 16, 160, occupied, out var second));

        Assert.False(first.Intersects(second));
        Assert.True(second.Y > first.Y);
    }

    [Fact]
    public void LabelThatCannotFit_IsHidden()
    {
        var occupied = new List<Rect>
        {
            new(0, 16, 500, 14),
            new(0, 34, 500, 14),
            new(0, 52, 500, 14),
        };

        Assert.False(CueLabelLayout.TryPlace(100, 90, 14, 800, 16, 75, occupied, out _));
    }

    [Fact]
    public void TooWideLabel_IsHiddenRatherThanClipped()
    {
        Assert.False(CueLabelLayout.TryPlace(0, 401, 14, 400, 16, 160, new List<Rect>(), out _));
    }
}
