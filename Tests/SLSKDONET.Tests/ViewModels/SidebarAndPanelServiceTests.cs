using System.Reactive;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression tests for the right-panel service that underpins sidebar tab
/// sync. These verify the core contract that SidebarViewModel depends on:
/// <see cref="RightPanelService.OpenPanel"/> correctly routes content and
/// exposes state, and <see cref="RightPanelService.ClosePanel"/> falls back
/// gracefully to the registered fallback VM.
/// </summary>
public class SidebarAndPanelServiceTests
{
    // ── OpenPanel ─────────────────────────────────────────────────────────

    [Fact]
    public void OpenPanel_SetsCurrentPanelVm()
    {
        var svc = new RightPanelService();
        var vm = new object();

        svc.OpenPanel(vm, "LABEL", "🎵");

        Assert.Same(vm, svc.CurrentPanelVm);
    }

    [Fact]
    public void OpenPanel_SetsPanelOpen()
    {
        var svc = new RightPanelService();

        svc.OpenPanel(new object(), "LABEL", "🎵");

        Assert.True(svc.IsPanelOpen);
    }

    [Fact]
    public void OpenPanel_SetsModeLabel()
    {
        var svc = new RightPanelService();

        svc.OpenPanel(new object(), "NOW PLAYING", "🎵");

        Assert.Equal("NOW PLAYING", svc.ModeLabel);
    }

    [Fact]
    public void OpenPanel_SetsModeIcon()
    {
        var svc = new RightPanelService();

        svc.OpenPanel(new object(), "NOW PLAYING", "🎵");

        Assert.Equal("🎵", svc.ModeIcon);
    }

    [Fact]
    public void OpenPanel_ReplacesExistingVm()
    {
        var svc = new RightPanelService();
        var first  = new object();
        var second = new object();

        svc.OpenPanel(first,  "A", "a");
        svc.OpenPanel(second, "B", "b");

        Assert.Same(second, svc.CurrentPanelVm);
        Assert.Equal("B", svc.ModeLabel);
    }

    // ── ClosePanel ────────────────────────────────────────────────────────

    [Fact]
    public void ClosePanel_WithoutFallback_ClosesPanel()
    {
        var svc = new RightPanelService();
        svc.OpenPanel(new object(), "X", "x");

        svc.ClosePanel();

        Assert.False(svc.IsPanelOpen);
    }

    [Fact]
    public void ClosePanel_WithFallback_ClosesPanelAndResetsToFallbackVm()
    {
        var svc      = new RightPanelService();
        var fallback = new object();
        var panel    = new object();

        svc.SetFallback(fallback, "PLAYER", "🎵");
        svc.OpenPanel(panel, "INSPECTOR", "🔬");

        svc.ClosePanel();

        // ClosePanel must always close in a single call. Content still
        // resets to the fallback so the next OpenPanel starts clean, but
        // the panel itself is closed immediately rather than requiring a
        // second ClosePanel call.
        Assert.Same(fallback, svc.CurrentPanelVm);
        Assert.False(svc.IsPanelOpen);
    }

    [Fact]
    public void ClosePanel_WhenCurrentIsAlreadyFallback_ClosesPanel()
    {
        var svc      = new RightPanelService();
        var fallback = new object();

        svc.SetFallback(fallback, "PLAYER", "🎵");
        // Open the fallback explicitly so it becomes the current panel
        svc.OpenPanel(fallback, "PLAYER", "🎵");

        svc.ClosePanel();

        Assert.False(svc.IsPanelOpen);
    }

    // ── SetFallback ───────────────────────────────────────────────────────

    [Fact]
    public void SetFallback_WhenNothingOpen_PopulatesCurrentPanelVm()
    {
        var svc      = new RightPanelService();
        var fallback = new object();

        svc.SetFallback(fallback, "PLAYER", "🎵");

        // Fallback should be populated as the current content even when
        // the panel is not yet visible (IsPanelOpen may stay false).
        Assert.Same(fallback, svc.CurrentPanelVm);
    }

    [Fact]
    public void SetFallback_WhenPanelAlreadyOpen_DoesNotReplaceCurrentVm()
    {
        var svc      = new RightPanelService();
        var active   = new object();
        var fallback = new object();

        svc.OpenPanel(active, "INSPECTOR", "🔬");
        svc.SetFallback(fallback, "PLAYER", "🎵");

        // Active panel should not be replaced by the fallback.
        Assert.Same(active, svc.CurrentPanelVm);
    }

    // ── IsPanelOpen toggling ──────────────────────────────────────────────

    [Fact]
    public void IsPanelOpen_CanBeToggledDirectly()
    {
        var svc = new RightPanelService();

        svc.IsPanelOpen = true;
        Assert.True(svc.IsPanelOpen);

        svc.IsPanelOpen = false;
        Assert.False(svc.IsPanelOpen);
    }
}
