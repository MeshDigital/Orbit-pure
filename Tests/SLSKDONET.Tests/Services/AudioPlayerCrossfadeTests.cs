using System;
using System.IO;
using System.Reflection;
using Moq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Mix playback end-of-track behaviour (2026-09-29): auto Outro cues sit seconds before the end
/// of a track, so the outgoing track regularly ran out mid-crossfade — and the player stalled
/// there: the incoming track kept playing at a partial fade level, the UI stayed on the old
/// track past the end of its waveform, and the hand-over came late or never.
/// </summary>
public class AudioPlayerCrossfadeTests : IDisposable
{
    private readonly string _wav = Path.Combine(Path.GetTempPath(), $"xfade-{Guid.NewGuid():N}.wav");

    public AudioPlayerCrossfadeTests()
    {
        using var writer = new WaveFileWriter(_wav, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
        for (int i = 0; i < 8000; i++) writer.WriteSample(0f);
    }

    public void Dispose()
    {
        try { File.Delete(_wav); } catch { /* reader may still be closing on the disposal task */ }
    }

    [Theory]
    [InlineData(177.3, 195.8, 21.9, 173.9)] // Outro cue 18 s from the end, 16-bar mix → start 22 s before the end
    [InlineData(161.5, 161.8, 21.9, 139.9)] // Outro cue at the very end
    [InlineData(120.0, 240.0, 21.9, 120.0)] // plenty of room → the mix-out point itself
    [InlineData(10.0, 15.0, 30.0, 0.0)]     // transition longer than the track → start immediately
    public void MixStart_LeavesRoomForTheWholeTransition(double mixOut, double track, double transition, double expected)
    {
        Assert.Equal(expected, AudioPlayerService.LatestMixStart(mixOut, track, transition), 1);
    }

    [Fact]
    public void OutgoingTrackEndingMidCrossfade_CompletesTheHandOver()
    {
        var sut = new AudioPlayerService(new AppConfig());
        Get<System.Timers.Timer>(sut, "_timer")!.Stop(); // drive the tick by hand

        var outgoing = NewDeck(PlaybackState.Stopped, gain: 0.3f);   // ran out mid-fade
        var incoming = NewDeck(PlaybackState.Playing, gain: 0.4f);   // audible but not at full level
        Set(sut, "_current", outgoing.Deck);
        Set(sut, "_next", incoming.Deck);
        Set(sut, "_isCrossfading", true);
        bool ended = false;
        sut.CrossfadeEnded += (_, _) => ended = true;

        typeof(AudioPlayerService).GetMethod("OnTimerElapsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, new object?[] { null, null });

        Assert.True(ended);
        Assert.False(Get<bool>(sut, "_isCrossfading"));
        Assert.Same(incoming.Deck, Get<object>(sut, "_current"));
        Assert.Null(Get<object>(sut, "_next"));
        Assert.Equal(1f, incoming.Gain.Volume);
    }

    [Fact]
    public void NoOutroCue_FallbackMixOutIsInSeconds_NotMilliseconds()
    {
        var engine = new TransitionEngine();
        var source = new TrackEntity { GlobalId = "a", BPM = 174, MusicalKey = "8A", CanonicalDuration = 195000 };
        var target = new TrackEntity { GlobalId = "b", BPM = 174, MusicalKey = "8A", CanonicalDuration = 200000 };

        var suggestion = engine.OptimizeTransition(source, target, new(), new());

        Assert.Equal(165.0, suggestion.SourceTriggerTime, 1); // 195 s - 30 s, not 194,970 s
    }

    private (object Deck, VolumeSampleProvider Gain) NewDeck(PlaybackState state, float gain)
    {
        var deckType = typeof(AudioPlayerService).GetNestedType("Deck", BindingFlags.NonPublic)!;
        var deck = Activator.CreateInstance(deckType, nonPublic: true)!;
        var reader = new AudioFileReader(_wav);
        var output = new Mock<IWavePlayer>();
        output.SetupGet(o => o.PlaybackState).Returns(state);
        var volume = new VolumeSampleProvider(reader) { Volume = gain };
        deckType.GetField("AudioFile")!.SetValue(deck, reader);
        deckType.GetField("Output")!.SetValue(deck, output.Object);
        deckType.GetField("Gain")!.SetValue(deck, volume);
        deckType.GetField("FilePath")!.SetValue(deck, _wav);
        return (deck, volume);
    }

    private static void Set(object o, string field, object? value) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static T? Get<T>(object o, string field) =>
        (T?)o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
}
