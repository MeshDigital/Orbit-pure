using System;
using System.IO;
using NAudio.Wave;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Timeline;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>
/// Covers Epic #517 Phase 2 — #528 (FO2e: tracks that aren't ready still mix cleanly —
/// quick gain estimate when LUFS is missing, and EchoOut/Cut fallback when beat grid is missing
/// or tempo jump exceeds 6%).
/// </summary>
public class UnreadyTrackMixFallbackTests
{
    private static byte[] CreateSineWavBytes(double frequencyHz, float amplitude, int sampleRate, double durationSeconds)
    {
        int totalFrames = (int)(sampleRate * durationSeconds);
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
        {
            var buf = new float[2048];
            int frame = 0;
            while (frame < totalFrames)
            {
                int framesThisChunk = Math.Min(buf.Length / 2, totalFrames - frame);
                for (int i = 0; i < framesThisChunk; i++)
                {
                    float sample = amplitude * (float)Math.Sin(2.0 * Math.PI * frequencyHz * (frame + i) / sampleRate);
                    buf[i * 2] = sample;
                    buf[i * 2 + 1] = sample;
                }
                writer.WriteSamples(buf, 0, framesThisChunk * 2);
                frame += framesThisChunk;
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void QuickGainEstimate_WhenLufsMissing_MatchesFullAnalysisWithinTolerance()
    {
        const int sampleRate = 44100;
        const double targetLufs = -14.0;
        // 30-second constant-amplitude stereo sine (~ -6 LUFS) — long enough to exercise the 25% seek path too.
        var wavBytes = CreateSineWavBytes(440.0, amplitude: 0.5f, sampleRate: sampleRate, durationSeconds: 30.0);

        // Full-analysis integrated LUFS over the entire decoded PCM
        double fullLufs;
        using (var fullMs = new MemoryStream(wavBytes))
        using (var fullReader = new WaveFileReader(fullMs))
        {
            var fullSamples = new float[sampleRate * 30 * 2];
            var ch = new NAudio.Wave.SampleProviders.SampleChannel(fullReader, true);
            int read = ch.Read(fullSamples, 0, fullSamples.Length);
            fullLufs = IntegratedLoudnessMeter.MeasureIntegratedLufs(fullSamples.AsSpan(0, read), sampleRate, 2)!.Value;
        }

        // Quick estimate from stream (no stored LUFS) — must restore reader.Position to 0
        double? quickLufs;
        using (var quickMs = new MemoryStream(wavBytes))
        using (var quickReader = new WaveFileReader(quickMs))
        {
            Assert.Equal(0, quickReader.Position);
            quickLufs = IntegratedLoudnessMeter.EstimateQuickLufsFromStream(quickReader, maxScanSeconds: 6.0);
            Assert.Equal(0, quickReader.Position);
        }

        Assert.NotNull(quickLufs);
        Assert.InRange(Math.Abs(quickLufs!.Value - fullLufs), 0.0, 1.0);

        float fullGain = AudioPlayerService.ComputeLoudnessGainForTarget(true, targetLufs, fullLufs);
        float quickGain = AudioPlayerService.ComputeLoudnessGainForTarget(true, targetLufs, quickLufs);
        Assert.True(quickGain < 1.0f, "Loud track without stored LUFS should be attenuated, not left at unity gain.");
        Assert.InRange(Math.Abs(quickGain - fullGain), 0.0f, 0.05f);
    }

    [Fact]
    public void PairWithoutBeatGrid_FallsBackToEchoOutOrCut()
    {
        var gridded = new TrackStructure
        {
            DurationSeconds = 240,
            Bpm = 174,
            BeatGrid = new[] { 0.0, 0.345, 0.690, 1.034 },
        };
        var ungridded = new TrackStructure
        {
            DurationSeconds = 240,
            Bpm = 174,
            BeatGrid = null,
        };

        Assert.True(TransitionPresetLibrary.RequiresUnreadyFallback(gridded, ungridded));
        Assert.True(TransitionPresetLibrary.RequiresUnreadyFallback(ungridded, gridded));
        Assert.True(TransitionPresetLibrary.RequiresUnreadyFallback(null, gridded));

        var fallback = TransitionPresetLibrary.BuildUnreadyFallback();
        Assert.True(
            fallback.Type is TransitionType.EchoOut or TransitionType.Cut,
            $"Expected EchoOut or Cut fallback for ungridded pair, got {fallback.Type}.");
    }

    [Fact]
    public void PairWithTempoJumpAboveSixPercent_FallsBackToEchoOutOrCut()
    {
        var track128 = new TrackStructure
        {
            DurationSeconds = 240,
            Bpm = 128,
            BeatGrid = new[] { 0.0, 0.46875, 0.9375 },
        };
        var track174 = new TrackStructure
        {
            DurationSeconds = 240,
            Bpm = 174,
            BeatGrid = new[] { 0.0, 0.3448, 0.6896 },
        };
        var track130 = new TrackStructure
        {
            DurationSeconds = 240,
            Bpm = 130,
            BeatGrid = new[] { 0.0, 0.4615, 0.923 },
        };

        // 128 -> 174 is a 35.9% jump (> 6%) -> requires EchoOut/Cut fallback
        Assert.True(TransitionPresetLibrary.RequiresUnreadyFallback(track128, track174));

        // 128 -> 130 is a 1.56% difference (<= 6%) with grids on both sides -> normal mix allowed
        Assert.False(TransitionPresetLibrary.RequiresUnreadyFallback(track128, track130));
    }
}
