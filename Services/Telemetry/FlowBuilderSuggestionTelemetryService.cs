using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SLSKDONET.Data;
using SLSKDONET.Models.Flow;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Telemetry;

public sealed class FlowBuilderSuggestionTelemetryService
{
    private readonly DatabaseService _databaseService;

    public FlowBuilderSuggestionTelemetryService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public Task LogSuggestedFlowAsync(
        string action,
        Guid? playlistId,
        int trackCount,
        SuggestedFlowStyleImpact impact,
        double averageTransitionScore)
    {
        if (playlistId is null || playlistId == Guid.Empty)
            return Task.CompletedTask;

        var payload = new SuggestedFlowTelemetryPayload(
            playlistId.Value,
            trackCount,
            averageTransitionScore,
            impact.SummaryText,
            ToSerializableCounts(impact.CurrentCounts),
            ToSerializableCounts(impact.ProposedCounts),
            ToSerializableCounts(impact.DeltaCounts),
            DateTime.UtcNow,
            impact.RefreshElapsed.HasValue ? (long?)Math.Round(impact.RefreshElapsed.Value.TotalMilliseconds) : null);

        var entity = new PlaylistActivityLogEntity
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId.Value,
            Action = action,
            Details = JsonSerializer.Serialize(payload),
            Timestamp = DateTime.UtcNow,
        };

        return _databaseService.LogActivityAsync(entity);
    }

    private static Dictionary<string, int> ToSerializableCounts(IReadOnlyDictionary<TransitionStyle, int> counts)
        => counts.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);

    private sealed record SuggestedFlowTelemetryPayload(
        Guid PlaylistId,
        int TrackCount,
        double AverageTransitionScore,
        string SummaryText,
        IReadOnlyDictionary<string, int> CurrentStyleCounts,
        IReadOnlyDictionary<string, int> ProposedStyleCounts,
        IReadOnlyDictionary<string, int> DeltaStyleCounts,
        DateTime GeneratedAtUtc,
        long? RefreshBridgesElapsedMs);
}
