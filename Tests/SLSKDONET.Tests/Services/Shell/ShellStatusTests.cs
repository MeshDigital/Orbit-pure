using System;
using System.Linq;
using SLSKDONET.Services.Shell;
using Xunit;

namespace SLSKDONET.Tests.Services.Shell;

/// <summary>
/// Pure logic behind the tray icon and the Windows taskbar button: tooltip text, menu labels and
/// enabled state, thumbnail-toolbar model, taskbar progress mapping, throttling and glyph pixels.
/// The Win32/COM layer itself (WindowsTaskbarIntegration) only copies these values.
/// </summary>
public class ShellStatusTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ShellPlaybackState Playing(string artist = "Noisia", string title = "Tentacles", bool isPlaying = true) =>
        new(HasTrack: true, IsPlaying: isPlaying, Artist: artist, Title: title, CanPlayPause: true, CanNext: true, CanPrevious: false);

    private static ShellDownloadState Downloads(int downloading = 0, int searching = 0, int queued = 0, int failed = 0,
        bool running = true, bool paused = false, double progress = 0) =>
        new(downloading, searching, queued, failed, running, paused, progress, CanToggleEngine: true);

    private static ShellState State(ShellPlaybackState? p = null, ShellDownloadState? d = null, bool shown = true) =>
        new(p ?? ShellPlaybackState.Empty, d ?? ShellDownloadState.Idle, shown);

    // ── Tooltip ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tooltip_Idle_IsJustTheAppName()
    {
        Assert.Equal("ORBIT", ShellStatusFormatter.BuildTooltip(State()));
    }

    [Fact]
    public void Tooltip_PlayingAndDownloading_ShowsBothLines()
    {
        var tip = ShellStatusFormatter.BuildTooltip(State(Playing(), Downloads(downloading: 3, queued: 12)));

        Assert.Equal("ORBIT\nPlaying: Noisia – Tentacles\n3 downloading · 12 queued", tip);
    }

    [Fact]
    public void Tooltip_PausedTrack_SaysPaused()
    {
        var tip = ShellStatusFormatter.BuildTooltip(State(Playing(isPlaying: false)));
        Assert.Equal("ORBIT\nPaused: Noisia – Tentacles", tip);
    }

    [Fact]
    public void Tooltip_MissingArtist_ShowsTitleOnly()
    {
        var tip = ShellStatusFormatter.BuildTooltip(State(Playing(artist: "  ")));
        Assert.Equal("ORBIT\nPlaying: Tentacles", tip);
    }

    [Fact]
    public void Tooltip_EnginePausedWithWork_SaysDownloadsPaused_AndOmitsZeroCounts()
    {
        var tip = ShellStatusFormatter.BuildTooltip(State(d: Downloads(queued: 4, failed: 1, paused: true)));
        Assert.Equal("ORBIT\nDownloads paused · 4 queued · 1 failed", tip);
    }

    [Fact]
    public void Tooltip_VeryLongTrack_IsTruncatedWithEllipsis_UnderWindowsLimit_KeepingDownloadLine()
    {
        var longTitle = new string('x', 300);
        var tip = ShellStatusFormatter.BuildTooltip(State(Playing(title: longTitle), Downloads(downloading: 2, queued: 7)));

        Assert.True(tip.Length <= ShellStatusFormatter.MaxTooltipLength, $"length {tip.Length}");
        Assert.StartsWith("ORBIT\nPlaying: Noisia – xxx", tip);
        Assert.EndsWith("…\n2 downloading · 7 queued", tip);
    }

    [Fact]
    public void Tooltip_NeverExceedsLimit_EvenWithHugeCounts()
    {
        var tip = ShellStatusFormatter.BuildTooltip(State(Playing(artist: new string('a', 200), title: new string('b', 200)),
            Downloads(downloading: int.MaxValue, searching: int.MaxValue, queued: int.MaxValue, failed: int.MaxValue, paused: true)));
        Assert.True(tip.Length <= ShellStatusFormatter.MaxTooltipLength);
    }

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 5, "abcd…")]
    [InlineData("abc  def", 6, "abc…")]
    public void Truncate_AddsEllipsisOnlyWhenNeeded(string input, int max, string expected)
    {
        Assert.Equal(expected, ShellStatusFormatter.Truncate(input, max));
    }

    // ── Menu ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Menu_NothingLoaded_DisablesTransport()
    {
        var menu = ShellStatusFormatter.BuildMenu(State());

        Assert.Equal("Nothing playing", menu.NowPlayingHeader);
        Assert.Equal("Play", menu.PlayPauseLabel);
        Assert.False(menu.CanPlayPause);
        Assert.False(menu.CanNext);
        Assert.False(menu.CanPrevious);
    }

    [Fact]
    public void Menu_Playing_OffersPause_AndReflectsCommandAvailability()
    {
        var menu = ShellStatusFormatter.BuildMenu(State(Playing()));

        Assert.Equal("Now playing: Noisia – Tentacles", menu.NowPlayingHeader);
        Assert.Equal("Pause", menu.PlayPauseLabel);
        Assert.True(menu.CanPlayPause);
        Assert.True(menu.CanNext);
        Assert.False(menu.CanPrevious);
    }

    [Fact]
    public void Menu_PausedTrack_OffersPlay()
    {
        var menu = ShellStatusFormatter.BuildMenu(State(Playing(isPlaying: false)));
        Assert.Equal("Play", menu.PlayPauseLabel);
        Assert.Equal("Paused: Noisia – Tentacles", menu.NowPlayingHeader);
    }

    [Fact]
    public void Menu_LongHeader_IsCapped()
    {
        var menu = ShellStatusFormatter.BuildMenu(State(Playing(title: new string('t', 200))));
        Assert.True(menu.NowPlayingHeader.Length <= ShellStatusFormatter.MaxMenuHeaderLength);
        Assert.EndsWith("…", menu.NowPlayingHeader);
    }

    [Theory]
    [InlineData(false, false, "Start downloads")]
    [InlineData(true, false, "Pause downloads")]
    [InlineData(true, true, "Resume downloads")]
    public void Menu_DownloadsItem_FollowsEngineState(bool running, bool paused, string expected)
    {
        var menu = ShellStatusFormatter.BuildMenu(State(d: Downloads(running: running, paused: paused)));
        Assert.Equal(expected, menu.DownloadsLabel);
    }

    [Fact]
    public void Menu_DownloadsItem_DisabledWhenEngineCommandCannotRun()
    {
        var d = Downloads() with { CanToggleEngine = false };
        Assert.False(ShellStatusFormatter.BuildMenu(State(d: d)).CanToggleDownloads);
    }

    [Theory]
    [InlineData(true, "Hide ORBIT")]
    [InlineData(false, "Show ORBIT")]
    public void Menu_WindowItem_FollowsVisibility(bool shown, string expected)
    {
        Assert.Equal(expected, ShellStatusFormatter.BuildMenu(State(shown: shown)).WindowLabel);
    }

    // ── Thumbnail toolbar ───────────────────────────────────────────────────────────────

    [Fact]
    public void ThumbBar_TogglesGlyphAndTip_WithPlayback()
    {
        var playing = ShellStatusFormatter.BuildThumbBar(Playing());
        Assert.True(playing.ShowPauseGlyph);
        Assert.Equal("Pause", playing.PlayPauseTip);
        Assert.False(playing.CanPrevious);
        Assert.True(playing.CanNext);

        var paused = ShellStatusFormatter.BuildThumbBar(Playing(isPlaying: false));
        Assert.False(paused.ShowPauseGlyph);
        Assert.Equal("Play", paused.PlayPauseTip);
    }

    [Fact]
    public void ThumbBar_RecordEquality_LetsTheInteropSkipRedundantUpdates()
    {
        Assert.Equal(ShellStatusFormatter.BuildThumbBar(Playing()), ShellStatusFormatter.BuildThumbBar(Playing()));
    }

    // ── Taskbar progress ────────────────────────────────────────────────────────────────

    [Fact]
    public void Progress_Idle_IsNone()
    {
        Assert.Equal(ShellProgressKind.None, TaskbarProgressMapper.MapSteady(Downloads()).Kind);
        Assert.Equal(ShellProgressKind.None, TaskbarProgressMapper.MapSteady(Downloads(downloading: 2, running: false)).Kind);
    }

    [Fact]
    public void Progress_Downloading_IsNormalWithAverageProgress()
    {
        var p = TaskbarProgressMapper.MapSteady(Downloads(downloading: 2, queued: 5, progress: 0.4));
        Assert.Equal(ShellProgressKind.Normal, p.Kind);
        Assert.Equal(0.4, p.Value, 3);
    }

    [Fact]
    public void Progress_JustStarted_StillShowsASliver()
    {
        var p = TaskbarProgressMapper.MapSteady(Downloads(downloading: 1, progress: 0));
        Assert.Equal(ShellProgressKind.Normal, p.Kind);
        Assert.True(p.Value > 0);
    }

    [Fact]
    public void Progress_EnginePaused_IsPaused()
    {
        Assert.Equal(ShellProgressKind.Paused, TaskbarProgressMapper.MapSteady(Downloads(downloading: 1, paused: true, progress: 0.5)).Kind);
        var waiting = TaskbarProgressMapper.MapSteady(Downloads(queued: 3, paused: true));
        Assert.Equal(ShellProgressKind.Paused, waiting.Kind);
        Assert.Equal(1.0, waiting.Value);
    }

    [Fact]
    public void Progress_SearchingOnly_IsIndeterminate_QueuedOnly_IsNone()
    {
        Assert.Equal(ShellProgressKind.Indeterminate, TaskbarProgressMapper.MapSteady(Downloads(searching: 2)).Kind);
        Assert.Equal(ShellProgressKind.None, TaskbarProgressMapper.MapSteady(Downloads(queued: 9)).Kind);
    }

    [Fact]
    public void Progress_InvalidValues_AreClamped()
    {
        Assert.Equal(1.0, TaskbarProgressMapper.MapSteady(Downloads(downloading: 1, progress: 7)).Value);
        Assert.True(TaskbarProgressMapper.MapSteady(Downloads(downloading: 1, progress: double.NaN)).Value is > 0 and <= 1);
    }

    [Fact]
    public void Progress_NewFailure_FlashesErrorBriefly_ThenReturnsToSteadyState()
    {
        var mapper = new TaskbarProgressMapper();
        Assert.Equal(ShellProgressKind.Normal, mapper.Map(Downloads(downloading: 1, progress: 0.3), T0).Kind);

        var failed = Downloads(downloading: 1, failed: 1, progress: 0.3);
        Assert.Equal(ShellProgressKind.Error, mapper.Map(failed, T0.AddSeconds(1)).Kind);
        Assert.NotNull(mapper.ErrorFlashEndsUtc(T0.AddSeconds(2)));

        var after = T0.AddSeconds(1) + TaskbarProgressMapper.ErrorFlashDuration + TimeSpan.FromMilliseconds(1);
        Assert.Null(mapper.ErrorFlashEndsUtc(after));
        Assert.Equal(ShellProgressKind.Normal, mapper.Map(failed, after).Kind);
    }

    [Fact]
    public void Progress_FailuresAlreadyListedAtStartup_DoNotFlash()
    {
        var mapper = new TaskbarProgressMapper();
        Assert.Equal(ShellProgressKind.None, mapper.Map(Downloads(failed: 5), T0).Kind);
        Assert.Null(mapper.ErrorFlashEndsUtc(T0));
    }

    [Fact]
    public void Progress_FailureCountGoingDown_DoesNotFlash()
    {
        var mapper = new TaskbarProgressMapper();
        mapper.Map(Downloads(failed: 3), T0);
        Assert.Equal(ShellProgressKind.None, mapper.Map(Downloads(failed: 0), T0.AddSeconds(1)).Kind);
    }

    // ── Throttle ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Throttle_AllowsFirst_BlocksWithinInterval_AllowsAfter()
    {
        var throttle = new ShellUpdateThrottle(TimeSpan.FromMilliseconds(500));

        Assert.True(throttle.TryEnter(T0));
        Assert.False(throttle.TryEnter(T0.AddMilliseconds(100)));
        Assert.False(throttle.TryEnter(T0.AddMilliseconds(499)));
        Assert.True(throttle.TryEnter(T0.AddMilliseconds(500)));
        Assert.False(throttle.TryEnter(T0.AddMilliseconds(700)));
    }

    [Fact]
    public void Throttle_CapsBurstToTwoPerSecond()
    {
        var throttle = new ShellUpdateThrottle(ShellStateMonitor.MinUpdateInterval);
        var passes = Enumerable.Range(0, 1000).Count(i => throttle.TryEnter(T0.AddMilliseconds(i)));
        Assert.Equal(2, passes); // t=0 and t=500
    }

    [Fact]
    public void Throttle_ClockGoingBackwards_DoesNotBlockForever()
    {
        var throttle = new ShellUpdateThrottle(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryEnter(T0));
        Assert.True(throttle.TryEnter(T0.AddHours(-1)));
    }

    // ── Glyphs ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ShellGlyph.Previous)]
    [InlineData(ShellGlyph.Play)]
    [InlineData(ShellGlyph.Pause)]
    [InlineData(ShellGlyph.Next)]
    public void Glyphs_RenderOpaqueWhiteCentreAndTransparentCorners(ShellGlyph glyph)
    {
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            var px = ShellGlyphRasterizer.Render(glyph, size);
            Assert.Equal(size * size, px.Length);
            Assert.Equal(0u, px[0]);                         // top-left corner transparent
            Assert.Equal(0u, px[size * size - 1]);           // bottom-right corner transparent
            Assert.Contains(px, p => p == 0xFFFFFFFFu);       // some fully covered pixels
            Assert.Contains(px, p => p >> 24 is > 0 and < 255); // anti-aliased edges
        }
    }

    [Fact]
    public void Glyphs_PauseAndPlayDiffer_PreviousMirrorsNext()
    {
        const int n = 24;
        var play = ShellGlyphRasterizer.Render(ShellGlyph.Play, n);
        var pause = ShellGlyphRasterizer.Render(ShellGlyph.Pause, n);
        Assert.NotEqual(play, pause);

        var next = ShellGlyphRasterizer.Render(ShellGlyph.Next, n);
        var prev = ShellGlyphRasterizer.Render(ShellGlyph.Previous, n);
        var mirrored = 0;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (next[y * n + x] == prev[y * n + (n - 1 - x)]) mirrored++;
        Assert.True(mirrored > n * n * 0.95, $"only {mirrored} of {n * n} pixels mirror");
    }
}
