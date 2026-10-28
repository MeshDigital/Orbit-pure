using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Entertainment;
using Xunit;

namespace SLSKDONET.Tests.Entertainment;

/// <summary>
/// Flow Mode's planning maths. These pin the behaviour the Now Playing panel explains to the user:
/// harmonic neighbours are preferred over key clashes, half/double time counts as a tempo match, the
/// energy nudge bends the pick, and an empty plan always says why.
/// </summary>
public class FlowPathPlannerTests
{
    private static FlowCandidate C(
        string hash,
        double bpm = 0,
        string key = "",
        double energy = -1,
        string mood = "",
        string genre = "",
        string artist = "Artist",
        string title = "Track")
        => new()
        {
            Hash = hash,
            Artist = artist,
            Title = title,
            Bpm = bpm,
            CamelotKey = key,
            Energy = energy,
            Mood = mood,
            Genre = genre,
        };

    [Fact]
    public void NoTrack_IsReportedNotGuessed()
    {
        var result = FlowPathPlanner.Plan(null, new[] { C("a", 128, "8A", 0.6) });

        Assert.Equal(FlowPlanStatus.NoTrack, result.Status);
        Assert.Empty(result.Steps);
        Assert.Contains("Nothing is playing", result.Message);
    }

    [Fact]
    public void UnanalysedCurrentTrack_ExplainsWhatToDo()
    {
        var result = FlowPathPlanner.Plan(C("cur"), new[] { C("a", 128, "8A", 0.6) });

        Assert.Equal(FlowPlanStatus.NoAnalysis, result.Status);
        Assert.Contains("Analyse", result.Message);
    }

    [Fact]
    public void EmptyQueue_ReportsNoCandidates()
    {
        var result = FlowPathPlanner.Plan(C("cur", 128, "8A", 0.6), Array.Empty<FlowCandidate>());

        Assert.Equal(FlowPlanStatus.NoCandidates, result.Status);
        Assert.Contains("Queue", result.Message);
    }

    [Fact]
    public void UnanalysedCandidates_ReportMissingAnalysisWithCounts()
    {
        var pool = new[] { C("a"), C("b") };

        var result = FlowPathPlanner.Plan(C("cur", 128, "8A", 0.6), pool);

        Assert.Equal(FlowPlanStatus.NoCandidateData, result.Status);
        Assert.Equal(2, result.EvaluatedCandidates);
        Assert.Equal(0, result.AnalysedCandidates);
    }

    [Fact]
    public void PrefersHarmonicNeighbour_OverKeyClash()
    {
        var current = C("cur", 128, "8A", 0.60, artist: "A");
        var pool = new[]
        {
            C("same-key", 128, "8A", 0.60, artist: "B"),
            C("clash", 128, "2A", 0.60, artist: "C"),
        };

        var result = FlowPathPlanner.Plan(current, pool);

        Assert.Equal(FlowPlanStatus.Ready, result.Status);
        Assert.Equal("same-key", result.Steps[0].Hash);
        Assert.Equal("Same key", result.Steps[0].KeyLabel);
        Assert.True(result.Steps[0].Score > 0.85, $"expected a high fit, got {result.Steps[0].Score}");
        Assert.Contains("same key", result.Steps[0].ReasonSummary);
    }

    [Fact]
    public void DoubleTimeTempo_CountsAsAMatch_NotAClash()
    {
        // 174 vs 87 BPM is a 2:1 relationship a DJ mixes without touching the pitch fader.
        var current = C("cur", 174, "8A", 0.70);
        var pool = new[] { C("halftime", 87, "8A", 0.70) };

        var result = FlowPathPlanner.Plan(current, pool);

        var step = Assert.Single(result.Steps);
        Assert.True(step.TempoScore > 0.95, $"expected a tempo match, got {step.TempoScore}");
        Assert.Contains("2×", step.TempoLabel);
    }

    [Fact]
    public void SameTempo_IsLabelledWithTheTinyDelta()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[] { C("near", 129.5, "8A", 0.60) };

        var step = Assert.Single(FlowPathPlanner.Plan(current, pool).Steps);

        Assert.Contains("BPM", step.TempoLabel);
    }

    [Fact]
    public void EnergyNudge_BendsThePickTowardTheTarget()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[]
        {
            C("flat", 128, "8A", 0.60),
            C("hotter", 128, "8A", 0.68),
        };

        var neutral = FlowPathPlanner.Plan(current, pool, energyNudge: 0.0, maxSteps: 1);
        var pushed = FlowPathPlanner.Plan(current, pool, energyNudge: 1.0, maxSteps: 1);

        Assert.Equal("flat", neutral.Steps[0].Hash);
        Assert.Equal("hotter", pushed.Steps[0].Hash);
        Assert.Contains("Lifts", pushed.Steps[0].EnergyLabel);
    }

    [Fact]
    public void PathEasesTowardTheNudgeTarget_AcrossSeveralHops()
    {
        var current = C("cur", 128, "8A", 0.55);
        var pool = new[]
        {
            C("a", 128.2, "8A", 0.58),
            C("b", 128.4, "8B", 0.70),
            C("c", 128.6, "9A", 0.85),
        };

        var result = FlowPathPlanner.Plan(current, pool, energyNudge: 1.0, maxSteps: 3);

        Assert.Equal(3, result.Steps.Count);
        // The later a hop sits in the ramp, the closer it should be to the (higher) goal.
        Assert.True(result.Steps[^1].Energy > result.Steps[0].Energy,
            $"expected a rising arc, got {string.Join(" → ", result.Steps.Select(s => s.Energy))}");
    }

    [Fact]
    public void RecentlyPlayedTrack_IsDeprioritised()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[]
        {
            C("played", 128, "8A", 0.60),
            C("fresh", 128, "8A", 0.60),
        };

        var result = FlowPathPlanner.Plan(current, pool, recentlyPlayed: new[] { "played" });

        Assert.Equal("fresh", result.Steps[0].Hash);
    }

    [Fact]
    public void RecentlyPlayedTrack_IsStillAllowed_WhenItIsTheOnlyOption()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[] { C("played", 128, "8A", 0.60) };

        var result = FlowPathPlanner.Plan(current, pool, recentlyPlayed: new[] { "played" });

        var step = Assert.Single(result.Steps);
        Assert.Equal("played", step.Hash);
        Assert.Contains("recently played", step.ReasonSummary);
    }

    [Fact]
    public void MoodMatch_IsPreferred()
    {
        var current = C("cur", 128, "8A", 0.60, mood: "Sad", genre: "House");
        var pool = new[]
        {
            C("sad", 128, "8A", 0.60, mood: "Sad", genre: "House"),
            C("happy", 128, "8A", 0.60, mood: "Happy", genre: "House"),
        };

        var step = FlowPathPlanner.Plan(current, pool).Steps[0];

        Assert.Equal("sad", step.Hash);
        Assert.Contains("Mood match", step.VibeLabel);
    }

    [Fact]
    public void Path_NeverRepeatsATrack_OrTheCurrentTrack()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[]
        {
            C("cur", 128, "8A", 0.60), // duplicate of the playing track
            C("only", 128, "8A", 0.60),
            C("only", 128, "8A", 0.60), // duplicate row in the queue
        };

        var result = FlowPathPlanner.Plan(current, pool, maxSteps: 5);

        var step = Assert.Single(result.Steps);
        Assert.Equal("only", step.Hash);
    }

    [Fact]
    public void Plan_IsDeterministic_ForIdenticalInputs()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[]
        {
            C("a", 128, "8A", 0.60),
            C("b", 128, "8A", 0.60),
            C("c", 128, "8A", 0.60),
        };

        var first = FlowPathPlanner.Plan(current, pool);
        var second = FlowPathPlanner.Plan(current, pool);

        Assert.Equal(first.Steps.Select(s => s.Hash), second.Steps.Select(s => s.Hash));
        Assert.Equal(first.Steps.Select(s => s.Rank), second.Steps.Select(s => s.Rank));
    }

    [Fact]
    public void Steps_AreRankedAndCarryEverySubScore()
    {        var current = C("cur", 128, "8A", 0.60, mood: "Sad", genre: "House");
        var pool = new[] { C("next", 126, "8B", 0.66, mood: "Sad", genre: "House", artist: "B", title: "Two") };

        var step = Assert.Single(FlowPathPlanner.Plan(current, pool).Steps);

        Assert.Equal(1, step.Rank);
        Assert.Equal("B — Two", step.DisplayName);
        Assert.InRange(step.KeyScore, 0, 1);
        Assert.InRange(step.TempoScore, 0, 1);
        Assert.InRange(step.EnergyScore, 0, 1);
        Assert.InRange(step.VibeScore, 0, 1);
        Assert.InRange(step.CrossfadeSeconds, 2.0, 12.0);
        Assert.False(string.IsNullOrWhiteSpace(step.KeyLabel));
        Assert.False(string.IsNullOrWhiteSpace(step.TempoLabel));
        Assert.False(string.IsNullOrWhiteSpace(step.EnergyLabel));
        Assert.False(string.IsNullOrWhiteSpace(step.VibeLabel));
        Assert.False(string.IsNullOrWhiteSpace(step.ScoreText));
    }

    [Fact]
    public void Rows_SpellOutTheNumbersBehindTheVerdict()
    {
        var current = C("cur", 128, "8A", 0.60, mood: "Sad", genre: "House", artist: "A");
        var pool = new[] { C("next-t", 174, "8B", 0.68, mood: "Sad", genre: "House", artist: "B") };

        var step = Assert.Single(FlowPathPlanner.Plan(current, pool).Steps);

        Assert.Equal("Key · 8A → 8B · Compatible (+1)", step.KeyRowText);
        Assert.StartsWith("Tempo · 128.0 → 174.0 BPM · ", step.TempoRowText, StringComparison.Ordinal);
        Assert.StartsWith("Energy · ", step.EnergyRowText, StringComparison.Ordinal);
        Assert.Equal("Mood · Mood match (Sad)", step.VibeRowText);
    }

    [Fact]
    public void Presets_ChangeWhichCandidateWins()
    {
        var current = C("cur", 128, "8A", 0.60);
        var pool = new[]
        {
            C("keyed", 128, "9A", 0.95),  // near key, but a big energy jump
            C("smooth", 200, "4A", 0.60), // holds the energy, clashes in key and tempo
        };

        // Smooth Blend weights harmony 1.0 / energy 0.6; Club Banger flips that to 0.5 / 1.0.
        var smooth = FlowPathPlanner.Plan(current, pool, FlowWeightSettings.SmoothBlend);
        var club = FlowPathPlanner.Plan(current, pool, FlowWeightSettings.HighEnergy);

        Assert.Equal("keyed", smooth.Steps[0].Hash);
        Assert.Equal("smooth", club.Steps[0].Hash);
    }
}
