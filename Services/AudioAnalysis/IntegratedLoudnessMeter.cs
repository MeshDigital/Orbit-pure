using System;
using System.Globalization;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Integrated loudness (LUFS) per ITU-R BS.1770-4 / EBU R128: K-weighting (high shelf + high pass),
/// 400 ms blocks with 75% overlap, an absolute gate at −70 LUFS and a relative gate 10 LU below the
/// ungated mean.
///
/// Before this, analysis stored Essentia's <c>lowlevel.average_loudness</c> as "LUFS". That value is
/// a 0–1 dynamic-range descriptor (1 = heavily compressed), not a loudness, so the inspector showed
/// "0.8 LUFS" and loudness-normalised playback attenuated every analysed track by ~15 dB.
/// </summary>
public static class IntegratedLoudnessMeter
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;

    /// <summary>Measures interleaved float PCM (−1..1). Returns null for silence, empty or sub-400 ms
    /// input, or an unusable sample rate. Every channel is weighted 1.0 (L/R/C); surround weights
    /// don't matter for the stereo files analysis decodes to.</summary>
    public static double? MeasureIntegratedLufs(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        if (sampleRate < 8000 || channels < 1) return null;
        var accumulator = new Accumulator(sampleRate, channels);
        accumulator.Add(interleaved);
        return accumulator.GetIntegratedLufs();
    }

    /// <summary>
    /// Streaming form of <see cref="MeasureIntegratedLufs"/>: feed interleaved PCM in chunks of any
    /// size (a chunk may end mid-frame) and read the result at the end. Memory stays at one double
    /// per 100 ms of audio, so a whole track never has to be held as a float[] (the loudness
    /// backfill streams FFmpeg's output straight in). The one-shot form is built on this, so both
    /// give identical results.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly int _channels;
        private readonly int _step;          // 100 ms hop, in frames
        private readonly Biquad _shelf, _highPass;
        private readonly BiquadState[] _s1, _s2;
        private readonly double[] _channelSums;
        private readonly System.Collections.Generic.List<double> _segmentEnergy = new();
        private int _channel;                // channel of the next sample
        private int _framesInSegment;

        /// <exception cref="ArgumentOutOfRangeException">Sample rate below 8 kHz or no channels.</exception>
        public Accumulator(int sampleRate, int channels)
        {
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            _channels = channels;
            _step = sampleRate / 10;
            (_shelf, _highPass) = KWeighting(sampleRate);
            _s1 = new BiquadState[channels];
            _s2 = new BiquadState[channels];
            _channelSums = new double[channels];
        }

        /// <summary>Complete 100 ms segments seen so far. A trailing partial segment is ignored, as
        /// in BS.1770's block framing.</summary>
        public int SegmentCount => _segmentEnergy.Count;

        public void Add(ReadOnlySpan<float> interleaved)
        {
            for (int i = 0; i < interleaved.Length; i++)
            {
                int ch = _channel;
                double y = _highPass.Process(_shelf.Process(interleaved[i], ref _s1[ch]), ref _s2[ch]);
                _channelSums[ch] += y * y;

                if (++_channel < _channels) continue;
                _channel = 0;
                if (++_framesInSegment < _step) continue;

                // Mean square of the K-weighted signal per 100 ms segment, summed over channels.
                double energy = 0;
                for (int c = 0; c < _channels; c++) { energy += _channelSums[c]; _channelSums[c] = 0; }
                _segmentEnergy.Add(energy);
                _framesInSegment = 0;
            }
        }

        /// <summary>Gated integrated loudness of everything added so far, or null for silence or
        /// less than one 400 ms block.</summary>
        public double? GetIntegratedLufs()
        {
            int blockFrames = _step * 4;         // 400 ms block, 75% overlap
            int blocks = _segmentEnergy.Count - 3;
            if (blocks <= 0 || _step == 0) return null;
            var blockPower = new double[blocks];
            for (int b = 0; b < blocks; b++)
                blockPower[b] = (_segmentEnergy[b] + _segmentEnergy[b + 1] + _segmentEnergy[b + 2] + _segmentEnergy[b + 3]) / blockFrames;

            double absoluteGatePower = LufsToPower(AbsoluteGateLufs);
            double sumAbs = 0; int countAbs = 0;
            foreach (var p in blockPower)
                if (p > absoluteGatePower) { sumAbs += p; countAbs++; }
            if (countAbs == 0) return null;

            double relativeGatePower = LufsToPower(PowerToLufs(sumAbs / countAbs) + RelativeGateLu);
            double sumRel = 0; int countRel = 0;
            foreach (var p in blockPower)
                if (p > absoluteGatePower && p > relativeGatePower) { sumRel += p; countRel++; }
            if (countRel == 0) return null;

            return PowerToLufs(sumRel / countRel);
        }
    }

    /// <summary>
    /// The stored loudness as real LUFS, or null when it can't be one. Integrated loudness of music is
    /// always below 0 LUFS; a value ≥ 0 is a legacy Essentia average_loudness (0–1) or the column's
    /// 0 default, and anything below −70 LUFS is under the absolute gate. Use this everywhere a stored
    /// loudness is shown or used for gain, so old rows don't show "0.8 LUFS" until re-analysed.
    /// </summary>
    public static double? AsLufs(double? stored) =>
        stored is double v && double.IsFinite(v) && v < 0 && v >= AbsoluteGateLufs ? v : null;

    /// <summary>"-9.4 LUFS" with an invariant decimal point (the culture gave "0,8 LUFS"), or "—".</summary>
    public static string Format(double? stored) =>
        AsLufs(stored) is double lufs ? lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS" : "—";

    private static double PowerToLufs(double power) => -0.691 + 10.0 * Math.Log10(power);
    private static double LufsToPower(double lufs) => Math.Pow(10.0, (lufs + 0.691) / 10.0);

    /// <summary>BS.1770 K-weighting filters for any sample rate (the coefficients in the standard are
    /// for 48 kHz; these are the analogue prototypes re-derived per rate, as libebur128 does).</summary>
    private static (Biquad Shelf, Biquad HighPass) KWeighting(int sampleRate)
    {
        double fs = sampleRate;

        // Stage 1: high shelf, +4 dB above ~1.7 kHz (head acoustics).
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / fs);
        double vh = Math.Pow(10.0, g / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        var shelf = new Biquad(
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);

        // Stage 2: RLB high pass at ~38 Hz.
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        double d = 1.0 + k / q + k * k;
        var highPass = new Biquad(1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / d, (1.0 - k / q + k * k) / d);

        return (shelf, highPass);
    }

    private struct BiquadState { public double Z1, Z2; }

    private readonly struct Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        public Biquad(double b0, double b1, double b2, double a1, double a2) { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        // Transposed direct form II.
        public double Process(double x, ref BiquadState s)
        {
            double y = _b0 * x + s.Z1;
            s.Z1 = _b1 * x - _a1 * y + s.Z2;
            s.Z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
