using System.Collections.ObjectModel;
using System.Reflection;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// PlayerViewModel.UpdateQueueStates marks queue rows as played / playing / upcoming, which is
/// what every queue list (sidepanel, Now Playing page, fullscreen player) uses to show where
/// playback is.
/// </summary>
public class PlayerViewModelQueueStateTests
{
    private static PlayerViewModel CreateSut(int trackCount, out ObservableCollection<PlaylistTrackViewModel> queue)
    {
        var sut = (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));
        Set(sut, "_playerService", new Mock<IAudioPlayerService>().Object);
        queue = new ObservableCollection<PlaylistTrackViewModel>();
        for (int i = 0; i < trackCount; i++)
            queue.Add(new PlaylistTrackViewModel(new PlaylistTrack { Artist = "A", Title = $"T{i}", CanonicalDuration = 240_000 }));
        Set(sut, "<Queue>k__BackingField", queue);
        return sut;
    }

    private static void Set(object o, string field, object? value) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static void PlayIndex(PlayerViewModel sut, int index)
    {
        Set(sut, "_currentQueueIndex", index);
        sut.UpdateQueueStates();
    }

    [Fact]
    public void MarksPlayedCurrentAndUpcoming()
    {
        var sut = CreateSut(5, out var queue);
        PlayIndex(sut, 2);

        Assert.True(queue[0].IsQueuePlayed);
        Assert.True(queue[1].IsQueuePlayed);
        Assert.True(queue[2].IsQueueCurrent);
        Assert.False(queue[2].IsQueuePlayed);
        Assert.False(queue[3].IsQueuePlayed);
        Assert.False(queue[3].IsQueueCurrent);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, queue.Select(t => t.QueueNumber));
    }

    [Fact]
    public void MovingOn_ClearsThePreviousCurrentRow()
    {
        var sut = CreateSut(3, out var queue);
        PlayIndex(sut, 0);
        PlayIndex(sut, 1);

        Assert.False(queue[0].IsQueueCurrent);
        Assert.True(queue[0].IsQueuePlayed);
        Assert.True(queue[1].IsQueueCurrent);
    }

    [Fact]
    public void NothingPlaying_NoRowIsCurrentOrPlayed()
    {
        var sut = CreateSut(3, out var queue);
        PlayIndex(sut, -1);
        Assert.DoesNotContain(queue, t => t.IsQueueCurrent || t.IsQueuePlayed);
    }

    [Fact]
    public void UpNextSummaryAndPosition()
    {
        var sut = CreateSut(4, out _);
        PlayIndex(sut, 1);

        Assert.Equal(2, sut.UpNextQueue.Count);
        Assert.Equal("2 up next · 8 min", sut.UpNextSummary);
        Assert.Equal("Track 2 of 4", sut.QueuePositionText);

        PlayIndex(sut, 3);
        Assert.Empty(sut.UpNextQueue);
        Assert.Equal(string.Empty, sut.UpNextSummary);
    }
}
