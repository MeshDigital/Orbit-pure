using System.Reflection;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression coverage for a real, live-reported bug: the bottom player bar kept showing the
/// previous track's name/artist forever after the engine autonomously advanced to the next track
/// (a gapless swap or crossfade completion) — TrackTitle/TrackArtist were only ever set inside
/// LoadTrackCore, which OnTrackAdvanced deliberately never calls (the audio is already playing, so
/// it must not re-trigger playback). PlayTrackAtIndex (an explicit user Play/Next click) happened
/// to still work because it calls PlayTrack()/LoadTrackCore itself right after SetNowPlayingState.
/// </summary>
public class PlayerViewModelTrackAdvancedDisplayTests
{
    private static PlayerViewModel CreateSut(out Mock<IAudioPlayerService> playerService)
    {
        var sut = (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

        playerService = new Mock<IAudioPlayerService>();
        SetField(sut, "_playerService", playerService.Object);
        SetField(sut, "<Queue>k__BackingField", new System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel>());

        return sut;
    }

    private static PlaylistTrackViewModel CreateTrack(string artist, string title, string filePath)
        => new(new PlaylistTrack
        {
            Artist = artist,
            Title = title,
            Status = TrackStatus.Downloaded,
            ResolvedFilePath = filePath
        });

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    private static void InvokeOnTrackAdvanced(PlayerViewModel sut)
    {
        var method = typeof(PlayerViewModel).GetMethod("OnTrackAdvanced", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(sut, null);
    }

    [Fact]
    public void OnTrackAdvanced_UpdatesTrackTitleAndArtist_ToThePromotedTrack()
    {
        var sut = CreateSut(out _);
        sut.Queue.Add(CreateTrack("Kanine", "Bloodstream", "a.wav"));
        sut.Queue.Add(CreateTrack("Major Lazer & DJ Snake", "Lean On", "b.wav"));
        SetField(sut, "_currentQueueIndex", 0);
        SetField(sut, "_trackTitle", "Bloodstream");
        SetField(sut, "_trackArtist", "Kanine");
        SetField(sut, "_preloadedQueueIndex", 1); // engine has already promoted index 1

        InvokeOnTrackAdvanced(sut);

        Assert.Equal("Lean On", sut.TrackTitle);
        Assert.Equal("Major Lazer & DJ Snake", sut.TrackArtist);
    }
}
