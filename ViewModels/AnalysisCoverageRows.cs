using System;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One entry in the Analysis page's playlist picker. A null <see cref="PlaylistId"/> means
/// "the whole library", which is the default so the card is never empty.
/// </summary>
public sealed class PlaylistChoice
{
    public PlaylistChoice(Guid? playlistId, string name, int trackCount = 0)
    {
        PlaylistId = playlistId;
        Name = name;
        TrackCount = trackCount;
    }

    public Guid? PlaylistId { get; }

    public string Name { get; }

    public int TrackCount { get; }

    public bool IsLibraryScope => PlaylistId is null;

    /// <summary>Label for the picker: the name, with a count when there is one.</summary>
    public string DisplayName => TrackCount > 0 ? $"{Name} ({TrackCount})" : Name;

    /// <summary>The "whole library" entry.</summary>
    public static PlaylistChoice Library => new(null, "Whole library");

    public override string ToString() => DisplayName;
}

/// <summary>
/// One row of the coverage table: an engine, how many tracks in the selection have its output,
/// and the action that would fill the rest.
/// </summary>
public sealed class EngineCoverageRow
{
    public EngineCoverageRow(EngineCoverageSummary summary)
    {
        Summary = summary;
        Info = AnalysisEngines.Info(summary.Engine);
    }

    public EngineCoverageSummary Summary { get; }

    public AnalysisEngineInfo Info { get; }

    public AnalysisEngine Engine => Summary.Engine;

    public string Name => Info.Name;

    public string Purpose => Info.Purpose;

    public string Produces => Info.Produces;

    public int Complete => Summary.Complete;

    public int Missing => Summary.Missing;

    public int Total => Summary.Total;

    /// <summary>"118 / 140" for the mono column.</summary>
    public string CompleteOfTotal => Summary.CompleteOfTotal;

    /// <summary>0–1, for the row's progress meter.</summary>
    public double Fraction => Summary.Fraction;

    public bool IsComplete => Summary.IsComplete;

    /// <summary>"22 missing" or "Complete".</summary>
    public string MissingLabel => IsComplete ? "Complete" : $"{Missing} missing";

    /// <summary>Tooltip: what the engine produces and, if applicable, why it cannot run here.</summary>
    public string ToolTip => Info.RunsInPipeline
        ? $"{Info.Purpose} Produces {Info.Produces}."
        : $"{Info.Purpose} Produces {Info.Produces}. Run this per track: it is a separate job, not part of the analysis pass.";
}
