using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using SLSKDONET.Tests.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

/// <summary>Ratings from the transition editor become append-only knowledge evidence (P3.3).</summary>
public sealed class TransitionRatingServiceTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("transition-ratings");
    private TransitionRatingService Service => new(_db.Factory);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Unrated_PairSaysSo()
    {
        var summary = await Service.GetAsync("a", "b");

        Assert.Equal(TransitionRatingSummary.None, summary);
        Assert.StartsWith("Not rated yet", summary.Text);
    }

    [Fact]
    public async Task Rating_IsAppendOnlyEvidence_LatestWins_LivePlaysCounted()
    {
        await Service.RateAsync("a", "b", 60, workedLive: false);
        var summary = await Service.RateAsync("a", "b", 84.6, workedLive: true);

        Assert.Equal((85.0, 2, 1), (summary.LastRating, summary.RatingCount, summary.LivePlays));
        Assert.Contains("Rated 85", summary.Text);
        Assert.Contains("played live once", summary.Text);

        using var db = _db.Factory.CreateDbContext();
        var edge = await db.TransitionKnowledge.SingleAsync();
        Assert.Equal(("a", "b"), (edge.SourceTrackId, edge.TargetTrackId));
        Assert.Equal(3, await db.TransitionEvidence.CountAsync()); // 2 ratings + 1 live play, none overwritten
        Assert.True(edge.EvidenceCount > 0); // recomputed
    }

    [Fact]
    public async Task Ratings_AreDirectional()
    {
        await Service.RateAsync("a", "b", 90, workedLive: false);

        Assert.Equal(0, (await Service.GetAsync("b", "a")).RatingCount);
    }

    [Fact]
    public async Task MissingIdentity_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service.RateAsync("", "b", 50, false));
    }
}
