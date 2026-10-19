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
        int frames = interleaved.Length / channels;
        int step = sampleRate / 10;              // 100 ms hop
        int blockFrames = step * 4;              // 400 ms block
        if (frames < blockFrames || step == 0) return null;

        // Mean square of the K-weighted signal per 100 ms segment, summed over channels.
        int segments = frames / step;
        var segmentEnergy = new double[segments];
        var (shelf, highPass) = KWeighting(sampleRate);
        for (int ch = 0; ch < channels; ch++)
        {
            var s1 = new BiquadState();
            var s2 = new BiquadState();
            for (int seg = 0; seg < segments; seg++)
            {
                double sum = 0;
                int start = seg * step;
                for (int i = start; i < start + step; i++)
                {
                    double x = interleaved[i * channels + ch];
                    double y = highPass.Process(shelf.Process(x, ref s1), ref s2);
                    sum += y * y;
                }
                segmentEnergy[seg] += sum;
            }
        }

        int blocks = segments - 3;
        if (blocks <= 0) return null;
        var blockPower = new double[blocks];
        for (int b = 0; b < blocks; b++)
            blockPower[b] = (segmentEnergy[b] + segmentEnergy[b + 1] + segmentEnergy[b + 2] + segmentEnergy[b + 3]) / blockFrames;

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
