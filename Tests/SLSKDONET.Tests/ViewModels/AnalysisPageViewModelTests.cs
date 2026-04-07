using System;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Tests for issue #43 additions to <see cref="AnalysisPageViewModel"/>
/// and <see cref="AnalysisTrackItem"/>:
///   - per-track status indicators (StemsReady, IsInPlaylist)
///   - error surfaces (AnalysisError, StemError, HasAnalysisError, HasStemError)
///   - tooltip properties (LastAnalyzedAt, ModelVersion, LastAnalyzedDisplay)
///   - automix flow (TogglePlaylist, CreateAutomixPlaylist, AutomixConstraints)
/// </summary>
public class AnalysisPageViewModelTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly AnalysisPageViewModel _vm;

    public AnalysisPageViewModelTests()
    {
        _vm = new AnalysisPageViewModel(_bus);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _vm.Dispose();
    }

    // ── AnalysisTrackItem status indicators ───────────────────────────────

    [Fact]
    public void StemsReady_DefaultsFalse()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.StemsReady);
    }

    [Fact]
    public void IsInPlaylist_DefaultsFalse()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.IsInPlaylist);
    }

    [Fact]
    public void SetStemsReady_RaisesPropertyChanged()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        bool changed = false;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AnalysisTrackItem.StemsReady)) changed = true; };
        item.StemsReady = true;
        Assert.True(changed);
    }

    // ── Error surfaces ────────────────────────────────────────────────────

    [Fact]
    public void HasAnalysisError_FalseWhenNoError()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.HasAnalysisError);
    }

    [Fact]
    public void HasAnalysisError_TrueWhenErrorSet()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.AnalysisError = "Essentia failed: codec not found";
        Assert.True(item.HasAnalysisError);
    }

    [Fact]
    public void HasStemError_TrueWhenStemErrorSet()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.StemError = "ONNX runtime OOM";
        Assert.True(item.HasStemError);
    }

    [Fact]
    public void HasStemError_FalseForNullOrEmpty()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.StemError = string.Empty;
        Assert.False(item.HasStemError);
    }

    // ── Tooltip / timestamp properties ────────────────────────────────────

    [Fact]
    public void LastAnalyzedDisplay_ReturnsNotYetAnalyzedWhenNull()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.Equal("Not yet analyzed", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsJustNowForRecentTimestamp()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddSeconds(-10);
        Assert.Equal("Just now", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsMinutesAgo()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddMinutes(-5);
        Assert.Contains("min ago", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsDaysAgo()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddDays(-3);
        Assert.Contains("day", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void ModelVersion_RoundTrips()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.ModelVersion = "essentia-2.1-b6";
        Assert.Equal("essentia-2.1-b6", item.ModelVersion);
    }

    // ── TogglePlaylist command ────────────────────────────────────────────

    [Fact]
    public void TogglePlaylist_AddsAnalysedTrackToPlaylistTracks()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);
        Assert.True(track.IsInPlaylist);
        Assert.Contains(track, _vm.PlaylistTracks);
    }

    [Fact]
    public void TogglePlaylist_RemovesTrackWhenAlreadyInPlaylist()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);   // add
        _vm.TogglePlaylist(track);   // remove
        Assert.False(track.IsInPlaylist);
        Assert.DoesNotContain(track, _vm.PlaylistTracks);
    }

    [Fact]
    public void TogglePlaylist_IgnoresTrackWithoutAnalysis()
    {
        var track = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.TogglePlaylist(track);
        Assert.False(track.IsInPlaylist);
        Assert.Empty(_vm.PlaylistTracks);
    }

    // ── CreateAutomixPlaylist command ─────────────────────────────────────

    [Fact]
    public void CreateAutomixPlaylist_RequiresAtLeastTwoTracks()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);  // only 1

        _vm.CreateAutomixPlaylist();

        Assert.NotNull(_vm.AutomixStatusMessage);
        Assert.Contains("at least 2", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateAutomixPlaylist_SortsByBpm()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        _vm.AutomixConstraints.MinBpm = 0;
        _vm.AutomixConstraints.MaxBpm = 300;

        _vm.CreateAutomixPlaylist();

        var bpms = _vm.PlaylistTracks
            .Select(t => t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0)
            .ToList();

        for (int i = 1; i < bpms.Count; i++)
            Assert.True(bpms[i] >= bpms[i - 1], $"Track {i} BPM {bpms[i]} is less than previous {bpms[i-1]}");
    }

    [Fact]
    public void CreateAutomixPlaylist_FiltersOutOfRangeBpm()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        // An extremely narrow BPM range that should match no tracks
        _vm.AutomixConstraints.MinBpm = 999;
        _vm.AutomixConstraints.MaxBpm = 1000;

        _vm.CreateAutomixPlaylist();

        Assert.Contains("Not enough tracks", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateAutomixPlaylist_SetsSuccessStatusMessage()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        _vm.AutomixConstraints.MinBpm = 0;
        _vm.AutomixConstraints.MaxBpm = 300;

        _vm.CreateAutomixPlaylist();

        Assert.NotNull(_vm.AutomixStatusMessage);
        Assert.Contains("BPM", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── AutomixConstraints ────────────────────────────────────────────────

    [Fact]
    public void AutomixConstraints_DefaultsAreReasonable()
    {
        var c = new AutomixConstraints();
        Assert.True(c.MinBpm >= 80 && c.MinBpm <= 120);
        Assert.True(c.MaxBpm > c.MinBpm);
        Assert.True(c.MaxTracks >= 2);
    }

    [Fact]
    public void AutomixConstraints_RaisesPropertyChangedOnMatchKeyChange()
    {
        var c = new AutomixConstraints();
        bool changed = false;
        c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AutomixConstraints.MatchKey)) changed = true; };
        c.MatchKey = !c.MatchKey;
        Assert.True(changed);
    }
}
