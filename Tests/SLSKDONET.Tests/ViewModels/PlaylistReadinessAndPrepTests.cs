using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers Epic #517 Phase 2 — #526 (FO2c: playlist readiness badge "Ready 42/45" + unready list)
/// and #527 (FO2d: "Prep for Mix" coordinator, progress events, cancellation, and error routing).
/// </summary>
[Collection(NonParallelCollection.Name)]
public class PlaylistReadinessAndPrepTests
{
    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext());
    }

    private sealed class FailingDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("DB unavailable");
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromException<AppDbContext>(new InvalidOperationException("DB unavailable"));
    }

    private sealed class StubNotificationService : INotificationService
    {
        public List<(string Title, string Message, NotificationType Type)> Shown { get; } = new();
        public void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null, string? actionRoute = null)
            => Shown.Add((title, message, type));
        public void ShowActionable(string title, string message, string actionLabel, Action onAction, NotificationType type = NotificationType.Information, TimeSpan? duration = null)
            => Shown.Add((title, message, type));
    }

    private sealed class StubPrepService : IPlaylistPrepService
    {
        private readonly Func<Guid, IProgress<PlaylistPrepProgress>?, CancellationToken, Task<PlaylistPrepResult>> _handler;
        public StubPrepService(Func<Guid, IProgress<PlaylistPrepProgress>?, CancellationToken, Task<PlaylistPrepResult>> handler)
            => _handler = handler;

        public Task<PlaylistPrepResult> PrepareAsync(Guid playlistId, IProgress<PlaylistPrepProgress>? progress = null, CancellationToken cancellationToken = default)
            => _handler(playlistId, progress, cancellationToken);

        public Task<PlaylistPrepResult> PrepareTracksAsync(IReadOnlyList<PlaylistTrack> tracks, IProgress<PlaylistPrepProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new PlaylistPrepResult(tracks.Count, 0, tracks.Count, 0, 0, false, Array.Empty<string>()));
    }

    private static async Task<TestDbContextFactory> CreateFreshFactoryAsync()
    {
        var factory = new TestDbContextFactory();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    [Fact]
    public async Task ReadinessBadge_CountsReadyAndUnready_AndUpdatesAfterCueSave()
    {
        using var scope = new FreshDatabaseScope();
        var factory = await CreateFreshFactoryAsync();
        var playlistId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PlaylistTracks.Add(new PlaylistTrackEntity
            {
                Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "t-ready",
                Artist = "Calibre", Title = "Even If", Status = TrackStatus.Downloaded, SortOrder = 0,
            });
            db.PlaylistTracks.Add(new PlaylistTrackEntity
            {
                Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "t-missing",
                Artist = "Alix Perez", Title = "Forsaken", Status = TrackStatus.Downloaded, SortOrder = 1,
            });

            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = "t-ready", Bpm = 174f, BeatGridJson = "[0.0,0.345]", LoudnessLUFS = -8.2f, WaveformBlob = new byte[] { 1 },
            });
            db.CuePoints.AddRange(
                new CuePointEntity { TrackUniqueHash = "t-ready", TimestampInSeconds = 8, Type = CuePointType.MixIn },
                new CuePointEntity { TrackUniqueHash = "t-ready", TimestampInSeconds = 200, Type = CuePointType.MixOut });

            // t-missing has BPM + grid + LUFS, missing both cues
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = "t-missing", Bpm = 172f, BeatGridJson = "[0.0,0.349]", LoudnessLUFS = -9.1f, WaveformBlob = new byte[] { 1 },
            });

            await db.SaveChangesAsync();
        }

        UnreadyTrackItem? openedItem = null;
        var vm = new PlaylistReadinessViewModel(factory, openInCueForge: item => openedItem = item);

        await vm.RefreshAsync(playlistId, "Liquid DnB");

        Assert.True(vm.IsVisible);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.AllReady);
        Assert.Equal("Ready 1/2", vm.BadgeText);
        var unready = Assert.Single(vm.UnreadyTracks);
        Assert.Equal("t-missing", unready.TrackUniqueHash);
        Assert.Contains("Mix-in / Intro cue", unready.MissingSummary);
        Assert.Contains("Mix-out / Outro cue", unready.MissingSummary);

        // Clicking "Open in Cue Forge" passes the unready track item
        vm.OpenInCueForgeCommand.Execute(unready);
        Assert.Equal("t-missing", openedItem?.TrackUniqueHash);

        // Simulate Cue Forge saving Intro + Outro cues -> badge updates live on refresh
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CuePoints.AddRange(
                new CuePointEntity { TrackUniqueHash = "t-missing", TimestampInSeconds = 12, Type = CuePointType.Intro, IsAutoGenerated = false },
                new CuePointEntity { TrackUniqueHash = "t-missing", TimestampInSeconds = 215, Type = CuePointType.Outro, IsAutoGenerated = false });
            await db.SaveChangesAsync();
        }

        await vm.RefreshAsync(playlistId, "Liquid DnB");

        Assert.True(vm.AllReady);
        Assert.Empty(vm.UnreadyTracks);
        Assert.Equal("Ready 2/2", vm.BadgeText);
    }

    [Fact]
    public async Task ReadinessBadge_EmptyAndErrorStates_AreExplicit()
    {
        using var scope = new FreshDatabaseScope();
        var factory = await CreateFreshFactoryAsync();

        var emptyVm = new PlaylistReadinessViewModel(factory);
        await emptyVm.RefreshAsync(Guid.NewGuid(), "Empty Playlist");
        Assert.True(emptyVm.IsEmpty);
        Assert.Equal("Ready 0/0", emptyVm.BadgeText);

        var errorVm = new PlaylistReadinessViewModel(new FailingDbContextFactory());
        await errorVm.RefreshAsync(Guid.NewGuid(), "Broken DB");
        Assert.True(errorVm.HasError);
        Assert.Equal("Readiness error", errorVm.BadgeText);
        Assert.Contains("DB unavailable", errorVm.ErrorMessage);
    }

    [Fact]
    public async Task PrepCoordinator_ReportsProgress_SupportsCancel_AndRoutesFailuresToNotifications()
    {
        var eventBus = new EventBusService();
        var notifications = new StubNotificationService();
        var events = new List<PlaylistPrepStatusChangedEvent>();
        using var sub = eventBus.GetEvent<PlaylistPrepStatusChangedEvent>().Subscribe(e => events.Add(e));

        // Cancellation test
        var cancelService = new StubPrepService(async (_, progress, ct) =>
        {
            progress?.Report(new PlaylistPrepProgress(1, 5, 1, 0, 0, "Track 1", "Prepping Track 1"));
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { }
            return new PlaylistPrepResult(5, 1, 0, 0, 0, WasCancelled: ct.IsCancellationRequested, Array.Empty<string>());
        });

        using var coordinator = new PlaylistPrepCoordinator(cancelService, eventBus, notifications);
        var runTask = coordinator.RunAsync(Guid.NewGuid(), "Club Set");
        await Task.Delay(50);
        Assert.True(coordinator.IsRunning);
        coordinator.Cancel();

        var cancelResult = await runTask;
        Assert.True(cancelResult.WasCancelled);
        Assert.False(coordinator.IsRunning);
        Assert.Contains(notifications.Shown, n => n.Type == NotificationType.Warning && n.Title.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, e => !e.IsRunning && e.WasCancelled);

        // Failure routing test (never silent catch)
        var failingService = new StubPrepService((_, _, _) => throw new InvalidOperationException("Disk full"));
        using var failingCoordinator = new PlaylistPrepCoordinator(failingService, eventBus, notifications);
        var failResult = await failingCoordinator.RunAsync(Guid.NewGuid(), "Broken Set");

        Assert.Equal(1, failResult.Failed);
        Assert.Contains(notifications.Shown, n => n.Type == NotificationType.Error && n.Message.Contains("Disk full"));
    }
}
