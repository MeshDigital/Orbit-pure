using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.Repositories;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers the Mix-transition visibility PlayerViewModel gained this session: previously
/// AudioPlayerService computed crossfade/preset state entirely internally with nothing bindable
/// exposing it, and the player's own Queue (a separate set of PlaylistTrackViewModel instances
/// from the Library track list) never had ShowMixTransitionBadge/TransitionPresetLabel populated
/// at all — "what/how is playing" had no visibility during a Mix playlist session.
/// </summary>
public class PlayerViewModelMixVisibilityTests
{
    private static PlayerViewModel CreateSut(out Mock<IAudioPlayerService> playerService, out Mock<ITransitionRepository> transitionRepository)
    {
        var sut = (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

        playerService = new Mock<IAudioPlayerService>();
        transitionRepository = new Mock<ITransitionRepository>();
        SetField(sut, "_playerService", playerService.Object);
        SetField(sut, "_transitionRepository", transitionRepository.Object);
        SetField(sut, "<Queue>k__BackingField", new System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel>());

        return sut;
    }

    private static PlaylistTrackViewModel CreateTrack(Guid playlistId, string? filePath = null)
        => new(new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            Artist = "Artist",
            Title = "Title",
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = filePath,
        });

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void SetProperty(object instance, string name, object? value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property not found: {name}");
        property.SetValue(instance, value);
    }

    private static Task InvokeUpdateQueueTransitionBadgesAsync(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("UpdateQueueTransitionBadgesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UpdateQueueTransitionBadgesAsync not found");
        return (Task)method.Invoke(sut, null)!;
    }

    private static void InvokeSchedulePreloadNext(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("SchedulePreloadNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(sut, null);
    }

    [Fact]
    public async Task UpdateQueueTransitionBadgesAsync_ShowsBadgeOnEveryNonLastTrack_AndClearsOnLast()
    {
        var playlistId = Guid.NewGuid();
        var sut = CreateSut(out _, out var transitionRepository);
        var t1 = CreateTrack(playlistId);
        var t2 = CreateTrack(playlistId);
        var t3 = CreateTrack(playlistId);
        sut.Queue.Add(t1);
        sut.Queue.Add(t2);
        sut.Queue.Add(t3);

        transitionRepository.Setup(r => r.GetTransitionsForPlaylistAsync(playlistId))
            .ReturnsAsync(new List<PlaylistTrackTransition>());

        await InvokeUpdateQueueTransitionBadgesAsync(sut);

        Assert.True(t1.ShowMixTransitionBadge);
        Assert.Equal(t2.Id, t1.NextPlaylistTrackId);
        Assert.Equal("Auto", t1.TransitionPresetLabel);

        Assert.True(t2.ShowMixTransitionBadge);
        Assert.Equal(t3.Id, t2.NextPlaylistTrackId);

        Assert.False(t3.ShowMixTransitionBadge, "Last track in the queue has nothing to transition into.");
        Assert.Null(t3.NextPlaylistTrackId);
    }

    [Fact]
    public async Task UpdateQueueTransitionBadgesAsync_UsesSavedPresetName_WhenTransitionExists()
    {
        var playlistId = Guid.NewGuid();
        var sut = CreateSut(out _, out var transitionRepository);
        var t1 = CreateTrack(playlistId);
        var t2 = CreateTrack(playlistId);
        sut.Queue.Add(t1);
        sut.Queue.Add(t2);

        transitionRepository.Setup(r => r.GetTransitionsForPlaylistAsync(playlistId))
            .ReturnsAsync(new List<PlaylistTrackTransition>
            {
                new() { OutgoingPlaylistTrackId = t1.Id, IncomingPlaylistTrackId = t2.Id, PresetName = "Wave" }
            });

        await InvokeUpdateQueueTransitionBadgesAsync(sut);

        Assert.Equal("Wave", t1.TransitionPresetLabel);
    }

    [Fact]
    public async Task SchedulePreloadNext_SetsUpcomingTransitionPresetName_WhenSavedTransitionExists()
    {
        var playlistId = Guid.NewGuid();
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            var sut = CreateSut(out _, out var transitionRepository);
            var current = CreateTrack(playlistId, filePath: null);
            var next = CreateTrack(playlistId, filePath: tempFile);
            sut.Queue.Add(current);
            sut.Queue.Add(next);
            SetField(sut, "_currentQueueIndex", 0);
            SetField(sut, "_currentTrack", current);

            var saved = new PlaylistTrackTransition
            {
                OutgoingPlaylistTrackId = current.Id,
                IncomingPlaylistTrackId = next.Id,
                PresetName = "Melt",
            };
            transitionRepository.Setup(r => r.GetTransitionAsync(current.Id, next.Id)).ReturnsAsync(saved);

            InvokeSchedulePreloadNext(sut);
            // SchedulePreloadNext's saved-transition lookup (AttachSavedTransitionAsync) is a
            // fire-and-forget async call — give it a beat to complete against the fully in-memory
            // mock, matching the established pattern used elsewhere for fire-and-forget setters.
            // (SetPendingTransitionForNext itself is invoked via Dispatcher.UIThread.Post inside
            // that method and so isn't directly observable from a headless test host — this
            // asserts the half of the flow that is: UpcomingTransitionPresetName, which is set
            // synchronously in the same continuation specifically so it CAN be tested this way.)
            await Task.Delay(50);

            Assert.Equal("Melt", sut.UpcomingTransitionPresetName);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public void SchedulePreloadNext_ClearsUpcomingTransitionPresetName_Immediately()
    {
        // Regression guard: stale "via <preset>" visibility from whatever pair was previously
        // up next must not linger on screen while the new pair's saved-transition lookup (an
        // async DB round-trip) is still in flight.
        var sut = CreateSut(out _, out _);
        SetProperty(sut, nameof(PlayerViewModel.UpcomingTransitionPresetName), "Wave");

        InvokeSchedulePreloadNext(sut);

        Assert.Null(sut.UpcomingTransitionPresetName);
    }
}
