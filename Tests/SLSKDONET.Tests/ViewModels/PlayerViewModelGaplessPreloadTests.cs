using System.Reflection;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers PlayerViewModel.PeekNextIndex/SchedulePreloadNext — the logic that decides which
/// track (if any) to hand the audio engine ahead of time for gapless/crossfade transitions.
/// Shuffle mode is deliberately excluded from preloading because the real pick has side effects
/// (recorded into shuffle history) that must only happen once, at the moment playback actually
/// advances — these tests lock that boundary in.
/// </summary>
public class PlayerViewModelGaplessPreloadTests
{
    private static PlayerViewModel CreateSut(out Mock<IAudioPlayerService> playerService)
    {
        var sut = (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

        playerService = new Mock<IAudioPlayerService>();
        SetField(sut, "_playerService", playerService.Object);
        SetField(sut, "<Queue>k__BackingField", new System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel>());

        return sut;
    }

    private static PlaylistTrackViewModel CreateTrack(string filePath)
        => new(new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Title",
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = filePath
        });

    private static int? InvokePeekNextIndex(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("PeekNextIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int?)method.Invoke(sut, null);
    }

    private static void InvokeSchedulePreloadNext(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("SchedulePreloadNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(sut, null);
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void SetProperty(object instance, string name, object? value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new System.InvalidOperationException($"Property not found: {name}");
        property.SetValue(instance, value);
    }

    [Fact]
    public void PeekNextIndex_ReturnsNextSequentialIndex_WhenNotShufflingOrRepeating()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("a.mp3"));
        sut.Queue.Add(CreateTrack("b.mp3"));
        SetField(sut, "_currentQueueIndex", 0);

        Assert.Equal(1, InvokePeekNextIndex(sut));
    }

    [Fact]
    public void PeekNextIndex_ReturnsNull_AtEndOfQueueWithNoRepeat()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("a.mp3"));
        SetField(sut, "_currentQueueIndex", 0);

        Assert.Null(InvokePeekNextIndex(sut));
    }

    [Fact]
    public void PeekNextIndex_WrapsToStart_WhenRepeatAll()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("a.mp3"));
        sut.Queue.Add(CreateTrack("b.mp3"));
        SetField(sut, "_currentQueueIndex", 1);
        SetProperty(sut, nameof(PlayerViewModel.RepeatMode), RepeatMode.All);

        Assert.Equal(0, InvokePeekNextIndex(sut));
    }

    [Fact]
    public void PeekNextIndex_ReturnsSameIndex_WhenRepeatOne()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("a.mp3"));
        sut.Queue.Add(CreateTrack("b.mp3"));
        SetField(sut, "_currentQueueIndex", 0);
        SetProperty(sut, nameof(PlayerViewModel.RepeatMode), RepeatMode.One);

        Assert.Equal(0, InvokePeekNextIndex(sut));
    }

    [Fact]
    public void PeekNextIndex_ReturnsNull_WhenShuffling()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("a.mp3"));
        sut.Queue.Add(CreateTrack("b.mp3"));
        SetField(sut, "_currentQueueIndex", 0);
        SetProperty(sut, nameof(PlayerViewModel.IsShuffling), true);

        Assert.Null(InvokePeekNextIndex(sut));
    }

    [Fact]
    public void SchedulePreloadNext_CancelsPreload_WhenShuffling()
    {
        var sut = CreateSut(out var playerService);
        sut.Queue.Add(CreateTrack("a.mp3"));
        sut.Queue.Add(CreateTrack("b.mp3"));
        SetField(sut, "_currentQueueIndex", 0);
        SetProperty(sut, nameof(PlayerViewModel.IsShuffling), true);

        InvokeSchedulePreloadNext(sut);

        playerService.Verify(p => p.CancelPreload(), Times.AtLeastOnce);
        playerService.Verify(p => p.PreloadNext(It.IsAny<string>()), Times.Never);
    }
}
