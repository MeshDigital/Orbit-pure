using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Transitions;

namespace MixBenchmark;

/// <summary>
/// MX0 Part B (#599): offline audio-level scoring of Auto-Mix transitions.
/// Renders each beat-matched pair through the same decode / exact-seek / varispeed / resample /
/// loudness-normalization / equal-power crossfade path that playback uses, and measures:
///   1. Beat alignment: kick-onset (&lt;150 Hz) cross-correlation offset between the two decks (ms);
///   2. Loudness: 3-second short-term K-weighted LUFS dip and bump across the overlap vs outgoing body (dB);
///   3. Clipping: samples exceeding 0 dBFS (|x| &gt; 1.0) and peak dBFS.
/// </summary>
internal static class AudioMixEvaluator
{
    private const int BusRate = 44100;
    private const int BusChannels = 2;

    public sealed record AudioPairScore(
        string PairLabel,
        double KickOffsetMs,
        double MaxDipDb,
        double MaxBumpDb,
        int ClippedSamples,
        double PeakDbfs);

    public static AudioPairScore? EvaluatePair(
        PlaylistTrackEntity outgoing,
        PlaylistTrackEntity incoming,
        PlaybackTransitionDecision decision,
        AudioFeaturesEntity? outFeatures,
        AudioFeaturesEntity? inFeatures)
    {
        if (decision.Path == PlaybackTransitionPath.EchoOutFallback) return null;
        if (string.IsNullOrWhiteSpace(outgoing.ResolvedFilePath) || !File.Exists(outgoing.ResolvedFilePath)) return null;
        if (string.IsNullOrWhiteSpace(incoming.ResolvedFilePath) || !File.Exists(incoming.ResolvedFilePath)) return null;
        if (decision.SourceTriggerSeconds is not double srcStart || decision.TargetTriggerSeconds is not double tgtStart) return null;

        double srcBpm = decision.OutgoingBpm is > 0 ? decision.OutgoingBpm.Value : (outgoing.BPM ?? outFeatures?.Bpm ?? 0);
        double tgtBpm = decision.IncomingBpm > 0 ? decision.IncomingBpm : (incoming.BPM ?? inFeatures?.Bpm ?? 0);
        if (srcBpm <= 0 || tgtBpm <= 0) return null;

        double overlapSec = Math.Clamp(decision.Model.DurationBeats * 60.0 / Math.Max(1.0, srcBpm), 2.0, 32.0);

        double tempoRatio = AudioPlayerService.TempoSyncRatio(tgtBpm, srcBpm);
        if (tempoRatio <= 0) tempoRatio = 1.0;

        float outGain = ComputeLoudnessGain(outFeatures?.LoudnessLUFS);
        float inGain = ComputeLoudnessGain(inFeatures?.LoudnessLUFS);

        try
        {
            using var readerA = new AudioFileReader(outgoing.ResolvedFilePath);
            using var readerB = new AudioFileReader(incoming.ResolvedFilePath);

            // 1. Measure outgoing body short-term LUFS (up to 12 s before sourceTrigger).
            double bodyStart = Math.Max(0, srcStart - 12.0);
            double bodyDur = Math.Max(3.0, srcStart - bodyStart);
            if (bodyStart + bodyDur > readerA.TotalTime.TotalSeconds)
                bodyDur = Math.Max(0, readerA.TotalTime.TotalSeconds - bodyStart);
            if (bodyDur < 3.0) return null;

            ExactSeek.Seek(readerA, outgoing.ResolvedFilePath, bodyStart);
            var bodyProvider = BuildChain(readerA, speed: 1.0, gain: outGain);
            float[] bodyStereo = RenderSamples(bodyProvider, (int)(bodyDur * BusRate) * BusChannels);
            var bodyLufsWindows = ShortTermLufsSeries(bodyStereo, BusRate);
            if (bodyLufsWindows.Count == 0) return null;
            double bodyLufs = Median(bodyLufsWindows);
            if (double.IsNaN(bodyLufs) || bodyLufs < -60.0) return null;

            // 2. Render both decks over the overlap window.
            double maxOverlapA = Math.Max(0, readerA.TotalTime.TotalSeconds - srcStart);
            double maxOverlapB = Math.Max(0, (readerB.TotalTime.TotalSeconds - tgtStart) / tempoRatio);
            double actualOverlap = Math.Min(overlapSec, Math.Min(maxOverlapA, maxOverlapB));
            if (actualOverlap < 3.0) return null;

            ExactSeek.Seek(readerA, outgoing.ResolvedFilePath, srcStart);
            ExactSeek.Seek(readerB, incoming.ResolvedFilePath, tgtStart);

            var chainA = BuildChain(readerA, speed: 1.0, gain: outGain);
            var chainB = BuildChain(readerB, speed: tempoRatio, gain: inGain);

            int totalFrames = (int)(actualOverlap * BusRate);
            int totalSamples = totalFrames * BusChannels;
            float[] rawA = RenderSamples(chainA, totalSamples);
            float[] rawB = RenderSamples(chainB, totalSamples);
            int samples = Math.Min(rawA.Length, rawB.Length);
            int frames = samples / BusChannels;
            if (frames < BusRate * 3) return null;

            // Apply equal-power crossfade gains and sum (matching AutoMixBusSampleProvider).
            float[] mix = new float[frames * BusChannels];
            int clipped = 0;
            float peak = 0f;
            for (int f = 0; f < frames; f++)
            {
                double p = (double)f / frames;
                float gOut = (float)Math.Cos(p * Math.PI * 0.5);
                float gIn = (float)Math.Sin(p * Math.PI * 0.5);
                int idx = f * 2;
                float l = rawA[idx] * gOut + rawB[idx] * gIn;
                float r = rawA[idx + 1] * gOut + rawB[idx + 1] * gIn;
                mix[idx] = l;
                mix[idx + 1] = r;
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > peak) peak = al;
                if (ar > peak) peak = ar;
                if (al > 1.0f) clipped++;
                if (ar > 1.0f) clipped++;
            }

            // 3. Short-term LUFS dip / bump across the overlap vs outgoing body.
            var mixLufs = ShortTermLufsSeries(mix, BusRate);
            if (mixLufs.Count == 0) return null;
            double maxDip = mixLufs.Min() - bodyLufs;
            double maxBump = mixLufs.Max() - bodyLufs;

            // 4. Kick-onset (<150 Hz) cross-correlation over the middle 60% of the overlap.
            double beatSec = 60.0 / srcBpm;
            double kickOffsetMs = EstimateKickOffsetMs(rawA, rawB, frames, BusRate, beatSec);
            double peakDbfs = peak > 1e-9f ? 20.0 * Math.Log10(peak) : -96.0;

            return new AudioPairScore(
                $"{outgoing.Artist} - {outgoing.Title} -> {incoming.Artist} - {incoming.Title}",
                kickOffsetMs,
                maxDip,
                maxBump,
                clipped,
                peakDbfs);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or NAudio.MmException)
        {
            Console.Error.WriteLine($"  [audio skip] {outgoing.Title} -> {incoming.Title}: {ex.Message}");
            return null;
        }
    }

    private static ISampleProvider BuildChain(AudioFileReader reader, double speed, float gain)
    {
        ISampleProvider chain = new SampleChannel(reader, true);
        if (Math.Abs(speed - 1.0) > 1e-4)
            chain = new AudioPlayerService.VariSpeedSampleProvider(chain) { Speed = (float)speed };
        if (chain.WaveFormat.Channels == 1)
            chain = new MonoToStereoSampleProvider(chain);
        if (chain.WaveFormat.SampleRate != BusRate)
            chain = new WdlResamplingSampleProvider(chain, BusRate);
        if (Math.Abs(gain - 1.0f) > 1e-4f)
            chain = new VolumeSampleProvider(chain) { Volume = gain };
        return chain;
    }

    private static float ComputeLoudnessGain(double? lufs)
    {
        if (lufs is not double v || v < -40.0 || v > 0.0) return 1.0f;
        const double targetLufs = -14.0;
        double db = Math.Clamp(targetLufs - v, -12.0, 12.0);
        return (float)Math.Pow(10.0, db / 20.0);
    }

    private static float[] RenderSamples(ISampleProvider provider, int totalSamples)
    {
        var output = new float[totalSamples];
        int filled = 0;
        while (filled < totalSamples)
        {
            int n = provider.Read(output, filled, Math.Min(4096, totalSamples - filled));
            if (n <= 0) break;
            filled += n;
        }
        if (filled < totalSamples) Array.Resize(ref output, filled);
        return output;
    }

    /// <summary>3-second sliding window (0.5 s hop) K-weighted short-term LUFS series (ITU-R BS.1770).</summary>
    private static List<double> ShortTermLufsSeries(float[] stereo, int sampleRate)
    {
        int frames = stereo.Length / 2;
        int winFrames = sampleRate * 3;
        int hopFrames = sampleRate / 2;
        if (frames < winFrames) return new List<double>();

        // ITU-R BS.1770 K-weighting: high-shelf (+4 dB @ 1681 Hz) + high-pass (38 Hz).
        var hpL = BiQuadFilter.HighPassFilter(sampleRate, 38f, 0.5f);
        var hpR = BiQuadFilter.HighPassFilter(sampleRate, 38f, 0.5f);
        var shelfL = BiQuadFilter.HighShelf(sampleRate, 1681f, 0.707f, 4.0f);
        var shelfR = BiQuadFilter.HighShelf(sampleRate, 1681f, 0.707f, 4.0f);

        var sq = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            float l = shelfL.Transform(hpL.Transform(stereo[f * 2]));
            float r = shelfR.Transform(hpR.Transform(stereo[f * 2 + 1]));
            sq[f] = l * l + r * r;
        }

        // Prefix sums for O(1) window mean square.
        var prefix = new double[frames + 1];
        for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + sq[f];

        var series = new List<double>();
        for (int start = 0; start + winFrames <= frames; start += hopFrames)
        {
            double meanSq = (prefix[start + winFrames] - prefix[start]) / winFrames;
            if (meanSq > 1e-12)
                series.Add(-0.691 + 10.0 * Math.Log10(meanSq));
        }
        return series;
    }

    /// <summary>Cross-correlates &lt;150 Hz kick-onset envelopes (10 ms hops) of Deck A vs Deck B over
    /// the middle 60% of the overlap, searching lags within ±half-beat.</summary>
    private static double EstimateKickOffsetMs(float[] stereoA, float[] stereoB, int frames, int sampleRate, double beatSec)
    {
        int startFrame = (int)(frames * 0.20);
        int endFrame = (int)(frames * 0.80);
        int hop = Math.Max(1, sampleRate / 100); // 10 ms
        int bins = (endFrame - startFrame) / hop;
        if (bins < 32) return 0;

        double[] envA = KickOnsetEnvelope(stereoA, startFrame, bins, hop, sampleRate);
        double[] envB = KickOnsetEnvelope(stereoB, startFrame, bins, hop, sampleRate);

        int maxLag = Math.Clamp((int)Math.Round((beatSec * 0.5) / 0.010), 1, Math.Min(50, bins / 4));
        int bestLag = 0;
        double bestCorr = double.NegativeInfinity;

        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < bins; i++)
            {
                int j = i + lag;
                if (j < 0 || j >= bins) continue;
                double a = envA[i], b = envB[j];
                dot += a * b;
                normA += a * a;
                normB += b * b;
            }
            double denom = Math.Sqrt(normA * normB);
            double corr = denom > 1e-12 ? dot / denom : 0;
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        return Math.Abs(bestLag) * 10.0;
    }

    private static double[] KickOnsetEnvelope(float[] stereo, int startFrame, int bins, int hop, int sampleRate)
    {
        var lp = BiQuadFilter.LowPassFilter(sampleRate, 150f, 0.707f);
        var rms = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            int f0 = startFrame + b * hop;
            double sum = 0;
            for (int k = 0; k < hop; k++)
            {
                int idx = (f0 + k) * 2;
                float mono = 0.5f * (stereo[idx] + stereo[idx + 1]);
                float low = lp.Transform(mono);
                sum += low * low;
            }
            rms[b] = Math.Sqrt(sum / hop);
        }

        // Half-wave rectified first difference = kick onset strength.
        var onset = new double[bins];
        for (int b = 1; b < bins; b++)
            onset[b] = Math.Max(0, rms[b] - rms[b - 1]);
        return onset;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }
}
