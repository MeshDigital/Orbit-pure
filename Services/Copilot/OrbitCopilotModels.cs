using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Copilot;

/// <summary>Constraints that gate the Bridge Finder's recursive graph search.</summary>
public sealed record CopilotConstraints(
    int MaxDepth = 4,
    int MaxResults = 5,
    double MinBpmScore = 70.0,
    double MinHarmonicScore = 60.0);

/// <summary>A verified path of <see cref="TransitionKnowledgeEntity"/> edges from a source to a target track.</summary>
public sealed record BridgePath(
    IReadOnlyList<string> TrackHashes,
    int Depth,
    double TotalScore);

/// <summary>Request to auto-build a set spine (primary path).</summary>
public sealed record SetBuildRequest(
    double TargetMinutes,
    EventProfileEntity? EventProfile,
    double MaxBpmVariance,
    string? SeedTrackHash = null,
    double? TargetBpm = null);

/// <summary>Result of an auto-built set spine.</summary>
public sealed record SetBuildResult(
    IReadOnlyList<string> SpineTrackHashes,
    double DurationMinutes);

/// <summary>A peak-time branch candidate for a set slot.</summary>
public sealed record AlternativeRoute(
    string SourceTrackHash,
    string TargetTrackHash,
    double Score);

/// <summary>A detected vocal or harmonic collision in a set timeline.</summary>
public sealed record ClashReport(
    string TrackHashA,
    string TrackHashB,
    string Reason);
