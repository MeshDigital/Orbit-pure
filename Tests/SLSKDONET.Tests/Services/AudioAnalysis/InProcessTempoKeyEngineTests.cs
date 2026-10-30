using System;
using Xunit;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Tests.Services.AudioAnalysis
{
    public class InProcessTempoKeyEngineTests
    {
        private const int SampleRate = 44100;

        [Fact]
        public void DetectTempo_KickAt120Bpm_ReturnsApproximately120()
        {
            const double bpm = 120;
            const double seconds = 20;
            var signal = BuildKickSignal(bpm, seconds);

            var result = new InProcessTempoKeyEngine().Analyze(signal, SampleRate, seconds);

            Assert.NotNull(result.Rhythm);
            Assert.InRange(result.Rhythm!.Bpm, 110f, 130f);
            Assert.True(result.Rhythm.Ticks is { Length: > 0 });
            Assert.True(result.Rhythm.OnsetRate > 1.0);
        }

        [Fact]
        public void DetectKey_CMajorChord_ReturnsCMajorOrItsRelativeMinor()
        {
            const double seconds = 12;
            var signal = BuildChordSignal(new[] { 261.63, 329.63, 392.00 }, seconds);

            var result = new InProcessTempoKeyEngine().Analyze(signal, SampleRate, seconds);

            Assert.NotNull(result.Tonal);
            Assert.NotNull(result.Tonal!.KeyEdma);
            // A bare C–E–G triad is the classic relative-key ambiguity: C major and A minor share
            // the same diatonic set. Accept either, but the detected pair must be consistent.
            Assert.True(
                (result.Tonal.KeyEdma.Key == "C" && result.Tonal.KeyEdma.Scale == "major") ||
                (result.Tonal.KeyEdma.Key == "A" && result.Tonal.KeyEdma.Scale == "minor"),
                $"unexpected key: {result.Tonal.KeyEdma.Key} {result.Tonal.KeyEdma.Scale}");
            Assert.True(result.Tonal.KeyEdma.Strength > 0.5f);
        }

        [Fact]
        public void Analyze_Silence_ReturnsEmptyOutput()
        {
            var signal = new float[SampleRate * 2]; // two seconds of zeros

            var result = new InProcessTempoKeyEngine().Analyze(signal, SampleRate, 2);

            Assert.Null(result.Rhythm);
            Assert.Null(result.Tonal);
        }

        private static float[] BuildKickSignal(double bpm, double seconds)
        {
            int n = (int)(SampleRate * seconds);
            var signal = new float[n];
            int beat = (int)(SampleRate * 60.0 / bpm);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                int p = i % beat;
                double env = Math.Exp(-p / (SampleRate * 0.08));
                double kick = Math.Sin(2 * Math.PI * 60 * t) * env;
                signal[i] = (float)(kick * 0.8);
            }
            return signal;
        }

        private static float[] BuildChordSignal(double[] freqs, double seconds)
        {
            int n = (int)(SampleRate * seconds);
            var signal = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                double v = 0;
                foreach (var f in freqs)
                    v += Math.Sin(2 * Math.PI * f * t);
                signal[i] = (float)(v / freqs.Length * 0.8);
            }
            return signal;
        }
    }
}
