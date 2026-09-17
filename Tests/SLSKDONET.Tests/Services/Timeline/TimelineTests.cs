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

        // ── GetNearestBeatSeconds (Issue #50) ──────────────────────────────

        [Theory]
        [InlineData(120.0, 0.49, 0.05, 0.5)]   // 120 BPM, beat at 0.5s, query 0.49s → snaps
        [InlineData(120.0, 0.51, 0.05, 0.5)]   // query 0.51s → snaps
        [InlineData(120.0, 0.0,  0.05, 0.0)]   // downbeat itself → snaps to 0.0
        [InlineData(128.0, 0.46875, 0.05, 0.46875)] // exact beat at 128 BPM
        public void GetNearestBeatSeconds_WithinRadius_ReturnsSnappedBeat(
            double bpm, double position, double radius, double expected)
        {
            double? result = BeatGridService.GetNearestBeatSeconds(position, bpm, radius);
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Value, 6);
        }

        [Theory]
        [InlineData(120.0, 0.75, 0.05)]   // 120 BPM beats at 0.5 and 1.0 — 0.75 is 0.25s away, outside 0.05 radius
        [InlineData(120.0, 0.56, 0.05)]   // 0.56 is 0.06s from 0.5 → outside radius
        public void GetNearestBeatSeconds_OutsideRadius_ReturnsNull(
            double bpm, double position, double radius)
        {
            double? result = BeatGridService.GetNearestBeatSeconds(position, bpm, radius);
            Assert.Null(result);
        }

        [Fact]
        public void GetNearestBeatSeconds_InvalidBpm_ReturnsNull()
        {
            Assert.Null(BeatGridService.GetNearestBeatSeconds(1.0, 0));
            Assert.Null(BeatGridService.GetNearestBeatSeconds(1.0, -120));
        }

        [Fact]
        public void GetNearestBeatSeconds_RespectsDownbeatOffset()
        {
            // BPM=120, beatDuration=0.5s, offset=0.1s
            // Beats are at 0.1, 0.6, 1.1 ...
            // Query 0.62 → nearest is 0.6, distance 0.02 < 0.05
            double? result = BeatGridService.GetNearestBeatSeconds(0.62, 120.0, 0.05, 0.1);
            Assert.NotNull(result);
            Assert.Equal(0.6, result!.Value, 6);
        }

        // ── SnapToBeatMultiple / GetNearestBeatMultipleSeconds ──────────────
        // Consolidated from TransientAwareSnappingEngine.SnapRawTimeToPhraseLedger (32-beat phrase
        // snapping) and CueForgeWaveformControl's hand-rolled quantize-grid snap, so this is the
        // single place both behaviors are proven correct.

        [Fact]
        public void SnapToBeatMultiple_SnapsToNearestBar_At4Beats()
        {
            // 120 BPM: beat=0.5s, bar (4 beats)=2.0s. Bars at 0, 2, 4...
            double result = BeatGridService.SnapToBeatMultiple(2.9, 120.0, beatMultiple: 4);
            Assert.Equal(2.0, result, 6);
        }

        [Fact]
        public void SnapToBeatMultiple_SnapsToNearestPhrase_At32Beats()
        {
            // 120 BPM: beat=0.5s, 32-beat phrase=16s. Phrases at 0, 16, 32...
            double result = BeatGridService.SnapToBeatMultiple(17.9, 120.0, beatMultiple: 32);
            Assert.Equal(16.0, result, 6);
        }

        [Fact]
        public void SnapToBeatMultiple_RespectsDownbeatOffset()
        {
            double result = BeatGridService.SnapToBeatMultiple(16.6, 120.0, beatMultiple: 32, downbeatOffsetSeconds: 1.0);
            // Phrases anchored at 1.0: 1.0, 17.0, 33.0 ... 16.6 is nearest to 17.0
            Assert.Equal(17.0, result, 6);
        }

        [Fact]
        public void SnapToBeatMultiple_NeverReturnsNegative()
        {
            double result = BeatGridService.SnapToBeatMultiple(0.1, 120.0, beatMultiple: 32, downbeatOffsetSeconds: 5.0);
            Assert.True(result >= 0.0);
        }

        [Fact]
        public void SnapToBeatMultiple_InvalidBpmOrMultiple_ReturnsInputUnchanged()
        {
            Assert.Equal(3.3, BeatGridService.SnapToBeatMultiple(3.3, bpm: 0, beatMultiple: 4));
            Assert.Equal(3.3, BeatGridService.SnapToBeatMultiple(3.3, bpm: 120, beatMultiple: 0));
        }

        [Fact]
        public void GetNearestBeatMultipleSeconds_WithinRadius_ReturnsSnappedBar()
        {
            // 120 BPM bar = 2.0s; query 1.98s is 0.02s from the 2.0s bar line.
            double? result = BeatGridService.GetNearestBeatMultipleSeconds(1.98, 120.0, beatMultiple: 4, snapRadiusSeconds: 0.05);
            Assert.NotNull(result);
            Assert.Equal(2.0, result!.Value, 6);
        }

        [Fact]
        public void GetNearestBeatMultipleSeconds_OutsideRadius_ReturnsNull()
        {
            // 1.7s is 0.3s from the nearest bar line (2.0s) — well outside a 0.05s radius.
            double? result = BeatGridService.GetNearestBeatMultipleSeconds(1.7, 120.0, beatMultiple: 4, snapRadiusSeconds: 0.05);
            Assert.Null(result);
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

        // ── Mix (Spotify-Mix-parity) preset DSP additions ──────────────────────

        [Theory]
        [InlineData(TransitionType.EqSwap, typeof(EqSwapProvider))]
        [InlineData(TransitionType.WaveDuck, typeof(WaveDuckProvider))]
        public void Build_NewMixTypes_DispatchToExpectedProvider(TransitionType type, System.Type expectedProviderType)
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, -1.0f);
            var model = new TransitionModel { Type = type, DurationBeats = 16 };

            var result = TransitionDsp.Build(outProvider, inProvider, model, 128.0);
            Assert.IsType(expectedProviderType, result);
        }

        private static (float first, float last) SampleFirstAndLast(NAudio.Wave.ISampleProvider provider, long totalSamples)
        {
            var buffer = new float[256];
            float first = 0, last = 0;
            long produced = 0;
            bool gotFirst = false;

            while (produced < totalSamples)
            {
                int toRead = (int)System.Math.Min(buffer.Length, totalSamples - produced);
                int read = provider.Read(buffer, 0, toRead);
                if (read == 0) break;
                if (!gotFirst) { first = buffer[0]; gotFirst = true; }
                last = buffer[read - 1];
                produced += read;
            }

            return (first, last);
        }

        [Fact]
        public void EqSwapProvider_TrendsFromOutgoingTowardIncoming()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, -1.0f);
            long durationSamples = 44100;

            var provider = new EqSwapProvider(outProvider, inProvider, durationSamples);
            var (first, last) = SampleFirstAndLast(provider, durationSamples);

            Assert.True(first > last, $"Expected output to trend from outgoing (1.0) toward incoming (-1.0), got first={first}, last={last}");
        }

        /// <summary>
        /// Regression coverage for what EqSwapProvider used to get wrong: only the Low band ever
        /// had a real swap/crossfade distinction — Mid/High always crossfaded together with the
        /// same equal-power curve regardless of any "swap" intent, and there was no way to swap
        /// Mid or High independently at all. Both source streams are DC (same sign), so the whole
        /// signal falls in the "low" band by construction — with swapLow explicitly false, the low
        /// band must follow the equal-power crossfade (bounded combined gain), not the old
        /// always-on linear swap.
        /// </summary>
        [Fact]
        public void EqSwapProvider_UnswappedLowBand_FollowsEqualPowerCrossfade_NotLinearSwap()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, 1.0f);
            long durationSamples = 44100;

            var provider = new EqSwapProvider(outProvider, inProvider, durationSamples,
                swapLow: false, swapMid: false, swapHigh: false);

            var buffer = new float[1];
            long produced = 0;
            float midpointValue = 0;
            while (produced < durationSamples)
            {
                provider.Read(buffer, 0, 1);
                produced++;
                if (produced == durationSamples / 2) midpointValue = buffer[0];
            }

            // Equal-power crossfade of two identical DC=1.0 streams at the midpoint:
            // cos(pi/4) + sin(pi/4) ≈ 1.414 — bounded, not the linear-swap sum (which stays
            // pinned at 1.0 the whole way through since (1-t)+t=1), and nowhere near the ~2.0
            // a naive "both full volume" bug would produce.
            Assert.InRange(midpointValue, 1.2, 1.6);
        }

        /// <summary>Sibling to the unswapped test above: with swapMid/swapHigh explicitly true
        /// instead, the same DC signal (all "low band" by construction) is unaffected by them —
        /// it's still governed entirely by swapLow, so explicitly swapping the OTHER two bands
        /// must not change the low-band DC behavior at all.</summary>
        [Fact]
        public void EqSwapProvider_SwappingOtherBands_DoesNotAffectDcLowBandBehavior()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            long durationSamples = 44100;

            float SampleMidpoint(bool swapMid, bool swapHigh)
            {
                var outProvider = new ConstantSampleProvider(fmt, 1.0f);
                var inProvider = new ConstantSampleProvider(fmt, 1.0f);
                var provider = new EqSwapProvider(outProvider, inProvider, durationSamples,
                    swapLow: false, swapMid: swapMid, swapHigh: swapHigh);

                var buffer = new float[1];
                long produced = 0;
                float midpointValue = 0;
                while (produced < durationSamples)
                {
                    provider.Read(buffer, 0, 1);
                    produced++;
                    if (produced == durationSamples / 2) midpointValue = buffer[0];
                }
                return midpointValue;
            }

            var withoutMidHighSwap = SampleMidpoint(swapMid: false, swapHigh: false);
            var withMidHighSwap = SampleMidpoint(swapMid: true, swapHigh: true);

            Assert.Equal(withoutMidHighSwap, withMidHighSwap, precision: 3);
        }

        [Fact]
        public void WaveDuckProvider_TrendsFromOutgoingTowardIncoming()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, -1.0f);
            long durationSamples = 44100;

            var provider = new WaveDuckProvider(outProvider, inProvider, durationSamples, beatPeriodSeconds: 0.5, duckDepth: 0.3f);
            var (first, last) = SampleFirstAndLast(provider, durationSamples);

            Assert.True(first > last, $"Expected output to trend from outgoing toward incoming, got first={first}, last={last}");
        }

        [Fact]
        public void WaveDuckProvider_PastDuration_ReturnsIncomingUnmodified()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, 0.5f);
            long durationSamples = 100;

            var provider = new WaveDuckProvider(outProvider, inProvider, durationSamples, beatPeriodSeconds: 0.5);
            SampleFirstAndLast(provider, durationSamples); // consume the transition window

            var buffer = new float[16];
            provider.Read(buffer, 0, buffer.Length);
            Assert.All(buffer, v => Assert.Equal(0.5f, v));
        }

        [Fact]
        public void FilterSweepProvider_Rising_TrendsFromOutgoingTowardIncoming()
        {
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var outProvider = new ConstantSampleProvider(fmt, 1.0f);
            var inProvider = new ConstantSampleProvider(fmt, -1.0f);
            long durationSamples = 44100;

            var provider = new FilterSweepProvider(outProvider, inProvider, durationSamples, freqStart: 20000f, freqEnd: 300f, rising: true);
            var (first, last) = SampleFirstAndLast(provider, durationSamples);

            Assert.True(first > last, $"Expected rising-mode output to trend from outgoing toward incoming, got first={first}, last={last}");
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
