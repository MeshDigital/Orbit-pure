using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Loads the heavy, per-selection data behind the library Track Inspector once selection settles
/// (LibraryViewModel debounces selection by 200 ms before calling
/// <see cref="TryAttachEnhancementsAsync"/>): technical/waveform data and the track's real cues
/// from the CuePoints table — the same rows Cue Forge edits and the Rekordbox export writes. The
/// inspector previously listed the legacy CuePointsJson blob, which the modern cue pipeline never
/// writes, so most analysed tracks showed "No cue points".
///
/// Stale loads are dropped with a generation counter: every selection change bumps it, and a load
/// only applies its result if it is still the newest one.
///
/// (Replaces a similar-tracks preview and an explainability summary that were computed on every
/// selection but bound by no view — the sidepanel's Similar tab already covers that.)
/// </summary>
public sealed class LibraryTrackInspectorViewModel
{
    private readonly ILogger _logger;
    private readonly ICuePointService? _cueService;
    private PlaylistTrackViewModel? _attached;
    private int _generation;

    public LibraryTrackInspectorViewModel(ILogger logger, ICuePointService? cueService = null)
    {
        _logger = logger;
        _cueService = cueService;
    }

    /// <summary>The track whose inspector data is currently being kept fresh.</summary>
    public PlaylistTrackViewModel? AttachedTrack => _attached;

    /// <summary>Selection moved away from a single track: stop refreshing the old one and make any
    /// in-flight load for it a no-op.</summary>
    public void ClearEnhancements()
    {
        _generation++;
        Detach();
    }

    public async Task TryAttachEnhancementsAsync(PlaylistTrackViewModel selected)
    {
        var generation = ++_generation;
        Detach();
        _attached = selected;
        selected.AnalysisDataRefreshed += OnAttachedTrackRefreshed;

        try
        {
            await selected.LoadAnalysisDataAsync();
            if (generation != _generation) return;
            await LoadCuesAsync(selected, generation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load inspector data for {TrackHash}", selected.GlobalId);
        }
    }

    private void Detach()
    {
        if (_attached != null)
            _attached.AnalysisDataRefreshed -= OnAttachedTrackRefreshed;
        _attached = null;
    }

    private void OnAttachedTrackRefreshed(object? sender, EventArgs e)
    {
        // Re-analysis regenerates auto cues — re-pull them for the track still on screen.
        if (sender is PlaylistTrackViewModel track && ReferenceEquals(track, _attached))
            _ = LoadCuesAsync(track, _generation);
    }

    internal async Task LoadCuesAsync(PlaylistTrackViewModel track, int generation)
    {
        if (_cueService is null || string.IsNullOrWhiteSpace(track.GlobalId))
            return;

        track.IsLoadingCues = true;
        track.CueLoadError = null;
        try
        {
            var stored = await _cueService.GetByTrackIdAsync(track.GlobalId);
            if (generation != _generation) return;

            // No table rows: keep whatever the legacy blob supplied rather than blanking it.
            if (stored.Count > 0)
                track.ApplyStoredCues(stored.Select(OrbitCue.FromEntity).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cue points for {TrackHash}", track.GlobalId);
            if (generation == _generation)
                track.CueLoadError = "Couldn't load cues for this track.";
        }
        finally
        {
            track.IsLoadingCues = false;
        }
    }

    /// <summary>Test hook: the current selection generation.</summary>
    internal int Generation => _generation;
}
