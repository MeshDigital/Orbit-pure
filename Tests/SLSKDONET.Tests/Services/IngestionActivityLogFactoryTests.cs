using System;
using System.Text.Json;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class IngestionActivityLogFactoryTests
{
    [Theory]
    [InlineData("ingestion_queued")]
    [InlineData("ingestion_started")]
    [InlineData("ingestion_completed")]
    [InlineData("ingestion_missing_detected")]
    public void Create_PopulatesCoreFields_ForIngestionActions(string action)
    {
        var playlistId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 05, 21, 12, 00, 00, DateTimeKind.Utc);

        var entity = IngestionActivityLogFactory.Create(
            playlistId,
            action,
            new
            {
                trackHash = "abc123",
                playlistTrackId = Guid.NewGuid(),
                source = "unit-test"
            },
            timestamp);

        Assert.NotEqual(Guid.Empty, entity.Id);
        Assert.Equal(playlistId, entity.PlaylistId);
        Assert.Equal(action, entity.Action);
        Assert.Equal(timestamp, entity.Timestamp);
        Assert.False(string.IsNullOrWhiteSpace(entity.Details));

        using var details = JsonDocument.Parse(entity.Details);
        Assert.Equal("abc123", details.RootElement.GetProperty("trackHash").GetString());
        Assert.Equal("unit-test", details.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Create_UsesUtcNow_WhenTimestampNotProvided()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var entity = IngestionActivityLogFactory.Create(
            Guid.NewGuid(),
            "ingestion_started",
            new { trackHash = "hash" });

        var after = DateTime.UtcNow.AddSeconds(1);
        Assert.InRange(entity.Timestamp, before, after);
    }
}
