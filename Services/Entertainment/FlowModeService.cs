using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Implements ORBIT's Flow Mode engine — scores and selects upcoming tracks
/// for a seamless, DJ-like continuous playback experience.
///
/// The maths lives in <see cref="FlowPathPlanner"/>; this class owns the runtime state, the last
/// assembled path and the change notification. It holds no database dependency: Flow plans over the
/// cues the player already has (the queue), which is what lets it re-plan synchronously the instant a
/// track starts — the previous async, DB-backed version was never actually called, so Flow showed a
/// toggle and an empty panel.
/// </summary>
public sealed class FlowModeService : IFlowModeService
{
    private readonly ILogger<FlowModeService> _logger;
    private FlowModeState _state = new();

    public FlowModeState State => _state;

    public event EventHandler<FlowModeState>? StateChanged;

    public FlowModeService(ILogger<FlowModeService> logger)
    {
        _logger = logger;
    }

    public void Activate(VibePreset preset = VibePreset.SilkySmooth, double energyNudge = 0.0)
    {
        _state = _state with
        {
            IsActive = true,
            ActivePreset = preset,
            EnergyNudge = Math.Clamp(energyNudge, -1.0, 1.0),
            Status = FlowPlanStatus.Off,
            StatusMessage = string.Empty,
        };

        _logger.LogInformation("Flow Mode activated (preset: {Preset}, nudge: {Nudge:+0.0;-0.0;0})",
            preset, _state.EnergyNudge);
        RaiseStateChanged();
    }

    public void Deactivate()
    {
        if (!_state.IsActive) return;

        _state = _state with
        {
            IsActive = false,
            Status = FlowPlanStatus.Off,
            StatusMessage = string.Empty,
            FlowPath = [],
            NextTrackReason = string.Empty,
            CrossfadeDurationSeconds = 4.0,
        };

        _logger.LogInformation("Flow Mode deactivated.");
        RaiseStateChanged();
    }

    public void Toggle()
    {
        if (_state.IsActive) Deactivate();
        else Activate(_state.ActivePreset, _state.EnergyNudge);
    }

    public void SetEnergyNudge(double nudge)
    {
        nudge = Math.Clamp(nudge, -1.0, 1.0);
        if (Math.Abs(nudge - _state.EnergyNudge) < 0.0005) return;

        _state = _state with { EnergyNudge = nudge };
        RaiseStateChanged();
    }

    public void SetPreset(VibePreset preset)
    {
        if (preset == _state.ActivePreset) return;

        _state = _state with { ActivePreset = preset };
        _logger.LogInformation("Flow Mode preset changed to {Preset}", preset);
        RaiseStateChanged();
    }

    public void RebuildFlowPath(
        FlowCandidate? current,
        IReadOnlyList<FlowCandidate> candidates,
        IReadOnlyCollection<string>? recentlyPlayed = null)
    {
        // While Flow is off there is nothing to plan; the UI shows the "what Flow does" copy instead.
        if (!_state.IsActive) return;

        try
        {
            var weights = _state.ActivePreset.ToSettings();

            var result = FlowPathPlanner.Plan(
                current,
                candidates,
                weights,
                _state.EnergyNudge,
                FlowPathPlanner.DefaultMaxSteps,
                recentlyPlayed);

            var next = result.Steps.Count > 0 ? result.Steps[0] : null;

            _state = _state with
            {
                Status = result.Status,
                StatusMessage = result.Message,
                FlowPath = result.Steps,
                PlannedFromName = current?.DisplayName ?? string.Empty,
                EvaluatedCandidates = result.EvaluatedCandidates,
                AnalysedCandidates = result.AnalysedCandidates,
                CrossfadeDurationSeconds = next?.CrossfadeSeconds ?? 4.0,
                NextTrackReason = BuildNextReason(next, result),
                PlannedAt = DateTime.UtcNow,
            };

            if (result.Status == FlowPlanStatus.Ready)
            {
                _logger.LogDebug(
                    "Flow Mode: planned {Count} hop(s) from {Current} within {Analysed}/{Total} analysed queued track(s). Next: {Next} ({Score})",
                    result.Steps.Count, current?.DisplayName, result.AnalysedCandidates, result.EvaluatedCandidates,
                    next?.DisplayName, next?.ScoreText);
            }
            else
            {
                _logger.LogDebug("Flow Mode: no path — {Status}: {Message}", result.Status, result.Message);
            }

            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            // A throwing planner must not take the player down with it, but it must not fail silently
            // either: the panel shows the reason and it lands in the log.
            _logger.LogError(ex, "Flow Mode: planning failed.");

            _state = _state with
            {
                Status = FlowPlanStatus.Faulted,
                StatusMessage = "Flow could not plan the next tracks. See the log for details.",
                FlowPath = [],
                NextTrackReason = "Flow could not plan the next tracks.",
            };
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// The one line under the visualizer. When a path exists it names the pick and why; otherwise it
    /// carries the reason the path is empty (so the UI is never just blank).
    /// </summary>
    private static string BuildNextReason(FlowStep? next, FlowPlanResult result)
    {
        if (next is null) return result.Message;

        var reason = string.IsNullOrWhiteSpace(next.ReasonSummary)
            ? string.Empty
            : NumFormat.Ui($" — {next.ReasonSummary}");

        return NumFormat.Ui($"Flow next: {next.DisplayName}") + reason;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, _state);
}
