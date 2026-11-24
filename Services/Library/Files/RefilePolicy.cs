using System;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF3 (#345): whether a track should be re-filed (relocated via the single move path) after its
/// analysis finishes. Only applies when the naming template depends on analysis-derived tokens and
/// the track has not been locked/exported (LF4) — re-filing an exported track would break the
/// Rekordbox/USB export's recorded location.
/// </summary>
public static class RefilePolicy
{
    /// <summary>True when the template resolves to a different path once {key}, {bpm} or {bpm_bracket} are known.</summary>
    public static bool TemplateUsesAnalysisTokens(string? nameFormat)
    {
        if (string.IsNullOrWhiteSpace(nameFormat)) return false;
        return nameFormat.Contains("{key}", StringComparison.OrdinalIgnoreCase)
            || nameFormat.Contains("{bpm}", StringComparison.OrdinalIgnoreCase)
            || nameFormat.Contains("{bpm_bracket}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the track should be re-filed after analysis: analysis-dependent template + not locked/exported.</summary>
    public static bool ShouldRefileAfterAnalysis(string? nameFormat, DateTime? pathLockedAt) =>
        TemplateUsesAnalysisTokens(nameFormat) && pathLockedAt is null;
}
