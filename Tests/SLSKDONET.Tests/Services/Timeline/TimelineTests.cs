using System;
using System.Linq;
using Xunit;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Tests.Services.Timeline
{
    // ─────────────────────────────────────────────────────────────────────────
    // BeatGridService tests (Issue 4.3)
    // ─────────────────────────────────────────────────────────────────────────

    public class BeatGridServiceTests
    {
        [Theory]
        [InlineData(120.0, 1.0)]   // 1 beat at 120 bpm = 0.5 s
        [InlineData(128.0, 4.0)]   // 4 beats at 128 bpm
        [InlineData(60.0, 0.5)]    // 0.5 beats at 60 bpm = 0.5 s
        public void BeatToSeconds_ReturnsCorrectValue(double bpm, double beats)
        {
            double expected = beats * (60.0 / bpm);
            Assert.Equal(expected, BeatGridService.BeatToSeconds(beats, bpm), precision: 10);
        }

        [Theory]
        [InlineData(120.0, 0.5)]   // 0.5 s at 120 bpm = 1 beat
        [InlineData(128.0, 1.875)] // 1.875 s at 128 bpm = 4 beats
        public void SecondsToBeat_ReturnsCorrectValue(double bpm, double seconds)
        {
            double expected = seconds * (bpm / 60.0);
            Assert.Equal(expected, BeatGridService.SecondsToBeat(seconds, bpm), precision: 10);
        }

        [Fact]
        public void SnapToGrid_Quarter_RoundsToNearestBeat()
        {
            Assert.Equal(4.0, BeatGridService.SnapToGrid(3.6, GridResolution.Quarter));
            Assert.Equal(3.0, BeatGridService.SnapToGrid(3.4, GridResolution.Quarter));
        }

        [Fact]
        public void SnapToGrid_Eighth_RoundsToHalfBeat()
        {
            Assert.Equal(3.5, BeatGridService.SnapToGrid(3.6, GridResolution.Eighth));
            Assert.Equal(3.5, BeatGridService.SnapToGrid(3.26, GridResolution.Eighth));
        }

        [Fact]
        public void SnapToGrid_Sixteenth_RoundsToQuarterBeat()
        {
            Assert.Equal(3.25, BeatGridService.SnapToGrid(3.3, GridResolution.Sixteenth));
            Assert.Equal(3.75, BeatGridService.SnapToGrid(3.74, GridResolution.Sixteenth));
        }

        [Fact]
        public void ComputeBeatGrid_CorrectCount_NoOffset()
        {
            // 120 bpm, 4 seconds → 8 beats (0, 0.5, 1, ..., 3.5)
            var grid = BeatGridService.ComputeBeatGrid(bpm: 120, durationSeconds: 4.0);
            Assert.Equal(9, grid.Length); // includes beat at t=0
            Assert.Equal(0.0, grid[0]);
            Assert.Equal(0.5, grid[1], precision: 10);
        }

        [Fact]
        public void ComputeBeatGrid_WithOffset_StartsAtOffset()
        {
            var grid = BeatGridService.ComputeBeatGrid(bpm: 120, durationSeconds: 4.0, downbeatOffsetSeconds: 0.25);
            Assert.True(grid.Length > 0);
            Assert.Equal(0.25, grid[0], precision: 10);
        }

        [Fact]
        public void ComputeBarGrid_Correct()
        {
            // 120 bpm, 4/4, 8 seconds → bars at 0, 2, 4, 6
            var bars = BeatGridService.ComputeBarGrid(bpm: 120, beatsPerBar: 4, durationSeconds: 8.0);
            Assert.Equal(5, bars.Length);
            Assert.Equal(0.0, bars[0]);
            Assert.Equal(2.0, bars[1], precision: 5);
            Assert.Equal(4.0, bars[2], precision: 5);
        }

        [Fact]
        public void BeatToBarIndex_Correct()
        {
            Assert.Equal(0, BeatGridService.BeatToBarIndex(3.9, 4));
            Assert.Equal(1, BeatGridService.BeatToBarIndex(4.0, 4));
            Assert.Equal(2, BeatGridService.BeatToBarIndex(8.1, 4));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TimelineSession / TimelineTrack / TimelineClip tests (Issue 4.1)
    // ─────────────────────────────────────────────────────────────────────────

    public class TimelineSessionTests
    {
        private static TimelineSession MakeSession() => new TimelineSession
        {
            ProjectBpm = 120.0,
            BeatsPerBar = 4,
            TotalBars = 8
        };

        [Fact]
        public void BeatsToSeconds_UsesProjectBpm()
        {
            var s = MakeSession();
            Assert.Equal(0.5, s.BeatsToSeconds(1.0), precision: 10);
        }

        [Fact]
        public void SecondsToBeats_Inverse()
        {
            var s = MakeSession();
            Assert.Equal(1.0, s.SecondsToBeats(0.5), precision: 10);
        }

        [Fact]
        public void TotalDurationSeconds_Correct()
        {
            var s = MakeSession();
            // 8 bars × 4 beats × 0.5 s/beat = 16 s
            Assert.Equal(16.0, s.TotalDurationSeconds, precision: 10);
        }

        [Fact]
        public void AddTrack_AssignsIndex()
        {
            var s = MakeSession();
            var t1 = s.AddTrack("A");
            var t2 = s.AddTrack("B");
            Assert.Equal(0, t1.Index);
            Assert.Equal(1, t2.Index);
        }

        [Fact]
        public void RemoveTrack_ReIndexes()
        {
            var s = MakeSession();
            var t1 = s.AddTrack("A");
            var t2 = s.AddTrack("B");
            var t3 = s.AddTrack("C");
            s.RemoveTrack(t2.Id);
            Assert.Equal(2, s.Tracks.Count);
            Assert.Equal(0, s.Tracks[0].Index);
            Assert.Equal(1, s.Tracks[1].Index);
        }

        [Fact]
        public void JsonRoundTrip_PreservesData()
        {
            var s = MakeSession();
            s.Name = "Test Session";
            var t = s.AddTrack("Vox");
            t.AddClip(new TimelineClip { StartBeat = 0, LengthBeats = 8, TrackUniqueHash = "abc" });

            var json = s.ToJson();
            var restored = TimelineSession.FromJson(json);

            Assert.NotNull(restored);
            Assert.Equal("Test Session", restored!.Name);
            Assert.Equal(120.0, restored.ProjectBpm);
            Assert.Single(restored.Tracks);
            Assert.Single(restored.Tracks[0].Clips);
            Assert.Equal("abc", restored.Tracks[0].Clips[0].TrackUniqueHash);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TimelineTrack clip operations tests (Issue 4.1)
    // ─────────────────────────────────────────────────────────────────────────

    public class TimelineTrackTests
    {
        private static TimelineClip MakeClip(double start, double length, string hash = "h") =>
            new TimelineClip { StartBeat = start, LengthBeats = length, TrackUniqueHash = hash };

        [Fact]
        public void AddClip_KeepsClipsSortedByStartBeat()
        {
            var track = new TimelineTrack();
            track.AddClip(MakeClip(8, 4));
            track.AddClip(MakeClip(0, 4));
            track.AddClip(MakeClip(4, 4));

            Assert.Equal(0.0, track.Clips[0].StartBeat);
            Assert.Equal(4.0, track.Clips[1].StartBeat);
            Assert.Equal(8.0, track.Clips[2].StartBeat);
        }

        [Fact]
        public void RemoveClip_RemovesById()
        {
            var track = new TimelineTrack();
            var c = MakeClip(0, 4);
            track.AddClip(c);
            bool removed = track.RemoveClip(c.Id);
            Assert.True(removed);
            Assert.Empty(track.Clips);
        }

        [Fact]
        public void MoveClip_UpdatesStartBeat()
        {
            var track = new TimelineTrack();
            var c = MakeClip(0, 4);
            track.AddClip(c);
            bool moved = track.MoveClip(c.Id, 8.0);
            Assert.True(moved);
            Assert.Equal(8.0, c.StartBeat);
        }

        [Fact]
        public void SplitClip_SplitsCorrectly()
        {
            var track = new TimelineTrack();
            var clip = MakeClip(0, 16);
            track.AddClip(clip);

            var right = track.SplitClip(clip.Id, 8.0);

            Assert.NotNull(right);
            Assert.Equal(8.0, clip.LengthBeats);
            Assert.Equal(8.0, right!.StartBeat);
            Assert.Equal(8.0, right.LengthBeats);
            Assert.Equal(2, track.Clips.Count);
        }

        [Fact]
        public void SplitClip_OutsideClip_ReturnsNull()
        {
            var track = new TimelineTrack();
            var clip = MakeClip(0, 4);
            track.AddClip(clip);

            var result = track.SplitClip(clip.Id, 10.0);
            Assert.Null(result);
        }

        [Fact]
        public void GetClipAt_ReturnsCorrectClip()
        {
            var track = new TimelineTrack();
            var c = MakeClip(4, 8);
            track.AddClip(c);

            Assert.Equal(c, track.GetClipAt(5.0));
            Assert.Null(track.GetClipAt(12.5));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TimelineClip gain envelope tests (Issue 4.1)
    // ─────────────────────────────────────────────────────────────────────────

    public class TimelineClipTests
    {
        [Fact]
        public void EvaluateGainDb_EmptyEnvelope_ReturnsStaticGain()
        {
            var clip = new TimelineClip { GainDb = -3f };
            Assert.Equal(-3f, clip.EvaluateGainDb(5.0));
        }

        [Fact]
        public void EvaluateGainDb_LinearInterpolation()
        {
            var clip = new TimelineClip { GainDb = 0f };
            clip.GainEnvelope.Add(new GainPoint { BeatPosition = 0, GainDb = 0f });
            clip.GainEnvelope.Add(new GainPoint { BeatPosition = 4, GainDb = -6f });

            // At beat 2 (midpoint) → interpolated -3 dB
            Assert.Equal(-3f, clip.EvaluateGainDb(2.0), precision: 5);
        }

        [Fact]
        public void ContainsBeat_CorrectBoundary()
        {
            var clip = new TimelineClip { StartBeat = 4, LengthBeats = 8 };
            Assert.True(clip.ContainsBeat(4.0));
            Assert.True(clip.ContainsBeat(11.99));
            Assert.False(clip.ContainsBeat(12.0)); // EndBeat is exclusive
            Assert.False(clip.ContainsBeat(3.99));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TransitionDsp parameter mapping tests (Issue 4.4)
    // ─────────────────────────────────────────────────────────────────────────

    public class TransitionDspTests
    {
        [Fact]
        public void BeatsToSamples_Correct()
        {
            // 4 beats at 128 bpm, 44100 Hz stereo
            // = 4 * (60/128) * 44100 * 2 = 82687.5 → 82687 (truncated)
            long samples = TransitionDsp.BeatsToSamples(beats: 4.0, bpm: 128.0, sampleRate: 44100, channels: 2);
            long expected = (long)(4.0 * (60.0 / 128.0) * 44100) * 2;
            Assert.Equal(expected, samples);
        }

        [Fact]
        public void Build_Cut_ReturnsSameProvider()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var outProvider = new ConstantSampleProvider(fmt, 0f);
            var inProvider = new ConstantSampleProvider(fmt, 0f);
            var model = new TransitionModel { Type = TransitionType.Cut, DurationBeats = 4 };

            var result = TransitionDsp.Build(outProvider, inProvider, model, 128.0);
            Assert.Same(outProvider, result);
        }

        [Fact]
        public void Build_Crossfade_ReturnsCrossfadeProvider()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var outProvider = new ConstantSampleProvider(fmt, 0f);
            var inProvider = new ConstantSampleProvider(fmt, 0f);
            var model = new TransitionModel { Type = TransitionType.Crossfade, DurationBeats = 4 };

            var result = TransitionDsp.Build(outProvider, inProvider, model, 128.0);
            Assert.IsType<CrossfadeProvider>(result);
        }

        [Fact]
        public void CrossfadeProvider_MixesAtHalfPoint()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            // Outgoing = constant 1.0, Incoming = constant 0.5
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, 0.5f);

            long totalSamples = 44100; // 1 second window
            var xfade = new CrossfadeProvider(outProvider, inProvider, totalSamples);

            // Skip half-way through the crossfade
            int skip = 44100 / 2;
            var skipBuf = new float[skip];
            xfade.Read(skipBuf, 0, skip);

            // At t=0.5: outGain = cos(π/4) ≈ 0.707, inGain = sin(π/4) ≈ 0.707
            var buf = new float[1];
            xfade.Read(buf, 0, 1);

            float expected = 1.0f * 0.7071068f + 0.5f * 0.7071068f;
            Assert.Equal(expected, buf[0], precision: 2);
        }
    }

    // ── Helper: generates a constant sample value ─────────────────────────────
    internal sealed class ConstantSampleProvider : NAudio.Wave.ISampleProvider
    {
        private readonly float _value;
        public NAudio.Wave.WaveFormat WaveFormat { get; }
        public ConstantSampleProvider(NAudio.Wave.WaveFormat fmt, float value)
        {
            WaveFormat = fmt;
            _value = value;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = _value;
            return count;
        }
    }
}
