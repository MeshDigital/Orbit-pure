using System;
using System.Collections.Generic;
using System.Linq;
using NWaves.Transforms;
using NWaves.Windows;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// What the drums do, independent of genre labels (epic #245, GA4 / #248). Evidence for the
/// rhythm &amp; tempo gate (#251). Every score is 0..1.
/// </summary>
/// <param name="FourOnFloorScore">A kick onset on (nearly) every beat, regularly.</param>
/// <param name="BackbeatScore">Snare/clap periodicity of two beats. House claps count too, so this alone never means DnB.</param>
/// <param name="HalfTimeScore">Snare periodicity of four beats (once per bar) and weak at two.</param>
/// <param name="BreakIrregularity">The kick repeats per bar but not per beat (breaks, two-step, half-time).</param>
/// <param name="TempoLow">The slower octave of the tempo (e.g. 87 for a 174 track).</param>
/// <param name="TempoHigh">The faster octave, always in [100, 200): the grid the scores are measured on.</param>
/// <param name="AnalyzedBars">Drop-energy bars the scores come from.</param>
/// <param name="Reliability">Low for beatless, very short or drum-less audio: trust the scores only when this is high.</param>
public sealed record RhythmSignature(
    double FourOnFloorScore,
    double BackbeatScore,
    double HalfTimeScore,
    double BreakIrregularity,
    double TempoLow,
    double TempoHigh,
    int AnalyzedBars,
    double Reliability)
{
    /// <summary>No usable rhythm evidence (no tempo, too short, silent).</summary>
    public static RhythmSignature Unknown(double tempoLow = 0, double tempoHigh = 0) =>
        new(0, 0, 0, 0, tempoLow, tempoHigh, 0, 0);
}

/// <summary>Raw evidence behind a <see cref="RhythmSignature"/>: envelope autocorrelations at 1/2/4 beats, kick regularity, mean onset energy.</summary>
public sealed record RhythmFeatures(
    double Kick1, double Kick2, double Kick4,
    double Snare1, double Snare2, double Snare4,
    double KickRegularity, double OnsetEnergy);

/// <summary>
/// Tells four-on-the-floor from backbeat/breakbeat (two-step) from half-time drums, plus an
/// octave-folded tempo (GA4 / #248). Pure and stateless; not wired into analysis yet (#251 does).
/// <para>
/// How: the tempo is folded into [100, 200) BPM so both octaves are known and the scores never
/// depend on the tracker picking the right one. Only drop-energy bars are measured (the loudest
/// ~40 %, at most <see cref="MaxAnalyzedBars"/>): intros are often kick-only and breakdowns have no
/// drums. Those bars are low-passed and decimated to ~11 kHz, then one STFT pass (512 points, 10 ms hop) gives three
/// log-flux onset envelopes: kick 40–120 Hz, snare body 150–400 Hz and snare crack 1.5–4.5 kHz.
/// The features are <b>phase-invariant</b> (autocorrelation at 0.5/1/2/4 beats and the regularity
/// of the kick from beat to beat), because the downbeat guess is often wrong.
/// </para>
/// <para>
/// Hats (&gt; 6 kHz) are not measured: they sit above the decimated band and no score needs them.
/// Known limit: a genuine four-on-the-floor below 100 BPM is measured on the doubled grid and reads
/// as half-time; none of the target buckets live there.
/// </para>
/// </summary>
public static class RhythmSignatureAnalyzer
{
    /// <summary>Upper bound on measured bars, which bounds the cost (≈ 90 s of drop at 128 BPM).</summary>
    public const int MaxAnalyzedBars = 48;

    private const double MinFoldedTempo = 100.0;
    private const double MaxFoldedTempo = 200.0;
    private const double TargetRate = 11025.0;
    private const double DropPercentile = 0.60; // keep the top 40 % of bars by RMS
    private const double HopSeconds = 0.010;

    /// <param name="mono">Mono PCM, −1..1.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="mono"/>.</param>
    /// <param name="bpm">The tracker's BPM; any octave is fine.</param>
    /// <param name="beatTicks">Beat times in seconds, if known (gives a more precise period than <paramref name="bpm"/>).</param>
    /// <param name="rmsCurve1s">RMS per second, if already computed; otherwise bar RMS is measured here.</param>
    public static RhythmSignature Analyze(
        float[] mono, int sampleRate, double bpm,
        IReadOnlyList<double>? beatTicks = null, IReadOnlyList<float>? rmsCurve1s = null)
        => AnalyzeDetailed(mono, sampleRate, bpm, beatTicks, rmsCurve1s).Signature;

    /// <summary>As <see cref="Analyze"/>, plus the raw features behind the scores (benchmark traces, #249).</summary>
    public static (RhythmSignature Signature, RhythmFeatures? Features) AnalyzeDetailed(
        float[] mono, int sampleRate, double bpm,
        IReadOnlyList<double>? beatTicks = null, IReadOnlyList<float>? rmsCurve1s = null)
    {
        if (mono is null || mono.Length == 0 || sampleRate < 4000)
            return (RhythmSignature.Unknown(), null);

        var beatPeriod = ResolveBeatPeriod(bpm, beatTicks);
        if (beatPeriod is not { } period)
            return (RhythmSignature.Unknown(), null);

        var tempoHigh = 60.0 / period;
        var tempoLow = tempoHigh / 2.0;

        // ── 1. Bars and drop-region selection ───────────────────────────────
        var barSamples = (int)Math.Round(4 * period * sampleRate);
        var barCount = barSamples > 0 ? mono.Length / barSamples : 0;
        if (barCount < 2) return (RhythmSignature.Unknown(tempoLow, tempoHigh), null);

        var barRms = BarRms(mono, sampleRate, barSamples, barCount, rmsCurve1s);
        var selected = SelectDropBars(barRms);
        if (selected.Count < 2) return (RhythmSignature.Unknown(tempoLow, tempoHigh), null);

        // ── 2. Onset envelopes per contiguous run of selected bars ───────────
        var factor = Math.Max(1, (int)Math.Floor(sampleRate / TargetRate));
        var rate = (double)sampleRate / factor;
        var hop = Math.Max(1, (int)Math.Round(HopSeconds * rate));
        var hopSeconds = hop / rate;
        var framesPerBeat = period / hopSeconds;

        var stft = new BandStft(rate, rate >= 10000 ? 512 : 256, hop); // 512: ~21 Hz bins keep the kick apart from the bass
        var segments = new List<Segment>();
        foreach (var (firstBar, bars) in Runs(selected))
        {
            var start = firstBar * barSamples;
            var length = bars * barSamples;
            var decimated = LowpassAndDecimate(mono, start, length, sampleRate, factor);
            segments.Add(stft.Process(decimated, bars));
        }

        // Detrend each envelope (keep onset peaks above the local one-beat mean), then build the
        // snare envelope from the crack band plus half the body band.
        var detrendRadius = Math.Max(1, (int)Math.Round(framesPerBeat / 2));
        foreach (var s in segments)
        {
            Detrend(s.Kick, detrendRadius);
            Detrend(s.Body, detrendRadius);
            Detrend(s.Crack, detrendRadius);
        }
        NormalizeByMean(segments, s => s.Kick);
        NormalizeByMean(segments, s => s.Body);
        NormalizeByMean(segments, s => s.Crack);
        foreach (var s in segments)
        {
            s.Snare = new double[s.Kick.Length];
            // Kick bleed (a big kick or 808 reaches into 150–400 Hz): take half the kick out of the body band.
            for (var i = 0; i < s.Snare.Length; i++) s.Snare[i] = s.Crack[i] + 0.5 * Math.Max(0, s.Body[i] - 0.5 * s.Kick[i]);
        }

        // ── 3. Phase-invariant features ─────────────────────────────────────
        double K(double beats) => Autocorrelation(segments, s => s.Kick, beats * framesPerBeat);
        double S(double beats) => Autocorrelation(segments, s => s.Snare, beats * framesPerBeat);

        var k1 = K(1); var k2 = K(2); var k4 = K(4);
        var s1 = S(1); var s2 = S(2); var s4 = S(4);
        var regularity = KickRegularity(segments, framesPerBeat);

        // Real onsets at all? A pad's spectral wobble is periodic too, but tiny: without onsets the
        // scores are noise, so they shrink with this factor instead of reporting a confident guess.
        var analyzedBars = segments.Sum(s => s.Bars);
        var onsetEnergy = segments.Sum(s => s.RawOnsetSum) / Math.Max(1, segments.Sum(s => s.Kick.Length));
        var onsetFactor = Clamp01(onsetEnergy / 0.02);

        var fourOnFloor = Clamp01((k1 - 0.1) / 0.4) * regularity * onsetFactor;
        // A true two-beat snare repeats as strongly at 4 beats as at 2; a once-per-bar snare with a
        // little kick bleed has s2 well below s4 and is half-time, not backbeat.
        var twoBeatShare = s4 > 1e-6 ? Clamp01((s2 / s4 - 0.5) / 0.4) : 0;
        var backbeat = Clamp01((s2 - Math.Max(0, s1)) / 0.5) * twoBeatShare * onsetFactor;
        var halfTime = Clamp01((s4 - Math.Max(0, s2)) / 0.5) * onsetFactor;
        var breakIrregularity = Clamp01((Math.Max(k2, k4) - Math.Max(0, k1)) / 0.5) * onsetFactor;

        // ── 4. Reliability: enough drum bars, periodic drums, real onsets ───
        var periodicity = Clamp01((new[] { k1, k2, k4, s1, s2, s4 }.Max() - 0.2) / 0.4);
        var reliability = Clamp01(analyzedBars / 8.0) * periodicity * onsetFactor;

        return (new RhythmSignature(fourOnFloor, backbeat, halfTime, breakIrregularity,
                tempoLow, tempoHigh, analyzedBars, reliability),
            new RhythmFeatures(k1, k2, k4, s1, s2, s4, regularity, onsetEnergy));
    }

    /// <summary>
    /// The beat period in seconds, folded so the tempo is in [100, 200) BPM. Uses the median beat
    /// interval when there are enough ticks, else <paramref name="bpm"/>. Null when neither is usable.
    /// </summary>
    public static double? ResolveBeatPeriod(double bpm, IReadOnlyList<double>? beatTicks)
    {
        double? period = null;
        if (beatTicks is { Count: >= 8 })
        {
            var intervals = new List<double>(beatTicks.Count - 1);
            for (var i = 1; i < beatTicks.Count; i++)
            {
                var d = beatTicks[i] - beatTicks[i - 1];
                if (d > 0.1 && d < 2.0) intervals.Add(d);
            }
            if (intervals.Count >= 4)
            {
                intervals.Sort();
                period = intervals[intervals.Count / 2];
            }
        }
        if (period is null && bpm is > 20 and < 1000) period = 60.0 / bpm;
        if (period is null) return null;

        var tempo = 60.0 / period.Value;
        while (tempo >= MaxFoldedTempo) tempo /= 2;
        while (tempo < MinFoldedTempo) tempo *= 2;
        return 60.0 / tempo;
    }

    /// <summary>Folds any BPM into its two octaves: (low in [50, 100), high in [100, 200)).</summary>
    public static (double Low, double High) FoldTempo(double bpm)
    {
        var p = ResolveBeatPeriod(bpm, null);
        if (p is not { } period) return (0, 0);
        var high = 60.0 / period;
        return (high / 2, high);
    }

    // ── bars ────────────────────────────────────────────────────────────────

    private static double[] BarRms(float[] mono, int sampleRate, int barSamples, int barCount, IReadOnlyList<float>? rmsCurve1s)
    {
        var rms = new double[barCount];
        var durationSeconds = (double)mono.Length / sampleRate;
        if (rmsCurve1s is { Count: > 0 } curve && curve.Count >= (int)durationSeconds - 1)
        {
            for (var b = 0; b < barCount; b++)
            {
                var from = (int)Math.Floor((double)b * barSamples / sampleRate);
                var to = Math.Min(curve.Count - 1, (int)Math.Ceiling((double)(b + 1) * barSamples / sampleRate) - 1);
                double sum = 0; var n = 0;
                for (var i = from; i <= to; i++) { sum += curve[i]; n++; }
                rms[b] = n > 0 ? sum / n : 0;
            }
            return rms;
        }

        for (var b = 0; b < barCount; b++)
        {
            // Every 4th sample: plenty for a bar-level loudness estimate, a quarter of the cost.
            double sum = 0; var n = 0;
            var start = b * barSamples;
            for (var i = start; i < start + barSamples; i += 4) { sum += (double)mono[i] * mono[i]; n++; }
            rms[b] = Math.Sqrt(sum / Math.Max(1, n));
        }
        return rms;
    }

    /// <summary>Bars at or above the 60th RMS percentile (and not near-silent), loudest first when capping.</summary>
    private static List<int> SelectDropBars(double[] barRms)
    {
        var max = barRms.Max();
        if (max <= 1e-6) return new List<int>();

        var sorted = barRms.OrderBy(r => r).ToArray();
        var threshold = Math.Max(sorted[(int)Math.Floor(DropPercentile * (sorted.Length - 1))], 0.2 * max);

        var chosen = Enumerable.Range(0, barRms.Length).Where(b => barRms[b] >= threshold).ToList();
        if (chosen.Count > MaxAnalyzedBars)
            chosen = chosen.OrderByDescending(b => barRms[b]).Take(MaxAnalyzedBars).ToList();
        chosen.Sort();
        return chosen;
    }

    /// <summary>Consecutive runs of bar indices as (first bar, bar count).</summary>
    private static IEnumerable<(int First, int Count)> Runs(List<int> bars)
    {
        var first = bars[0]; var count = 1;
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i] == bars[i - 1] + 1) { count++; continue; }
            yield return (first, count);
            first = bars[i]; count = 1;
        }
        yield return (first, count);
    }

    // ── signal ──────────────────────────────────────────────────────────────

    /// <summary>4th-order Butterworth low-pass at 0.4 × the target rate, then keep every <paramref name="factor"/>-th sample.</summary>
    private static float[] LowpassAndDecimate(float[] mono, int start, int length, int sampleRate, int factor)
    {
        var outLength = length / factor;
        var output = new float[outLength];
        if (factor == 1)
        {
            Array.Copy(mono, start, output, 0, outLength);
            return output;
        }

        var cutoff = 0.4 * sampleRate / factor;
        var a = Biquad.LowPass(sampleRate, cutoff, 0.5412);
        var b = Biquad.LowPass(sampleRate, cutoff, 1.3066);
        var o = 0;
        for (var i = 0; i < outLength * factor; i++)
        {
            var y = b.Process(a.Process(mono[start + i]));
            if (i % factor == 0) output[o++] = (float)y;
        }
        return output;
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad LowPass(double sampleRate, double cutoff, double q)
        {
            var w0 = 2 * Math.PI * cutoff / sampleRate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha;
            return new Biquad
            {
                _b0 = (1 - cos) / 2 / a0, _b1 = (1 - cos) / a0, _b2 = (1 - cos) / 2 / a0,
                _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0,
            };
        }

        public double Process(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    private sealed class Segment
    {
        public required double[] Kick { get; init; }
        public required double[] Body { get; init; }
        public required double[] Crack { get; init; }
        public double[] Snare { get; set; } = Array.Empty<double>();
        public required int Bars { get; init; }
        /// <summary>Sum of the raw kick + crack flux, before normalisation (for the onset-energy floor).</summary>
        public required double RawOnsetSum { get; init; }
    }

    /// <summary>One STFT pass producing three band log-flux envelopes. Buffers are reused across frames.</summary>
    private sealed class BandStft
    {
        private readonly int _size, _hop;
        private readonly RealFft _fft;
        private readonly float[] _window, _frame, _re, _im;
        private readonly double[] _prevLog, _log;
        private readonly (int From, int To) _kick, _body, _crack;

        public BandStft(double rate, int size, int hop)
        {
            _size = size;
            _hop = hop;
            _fft = new RealFft(size);
            _window = Window.OfType(WindowType.Hann, size);
            _frame = new float[size];
            _re = new float[size];
            _im = new float[size];
            var bins = size / 2 + 1;
            _prevLog = new double[bins];
            _log = new double[bins];

            var binHz = rate / size;
            (int, int) Band(double lo, double hi)
            {
                var from = Math.Max(1, (int)Math.Ceiling(lo / binHz));
                var to = Math.Min(bins - 1, Math.Max(from, (int)Math.Floor(hi / binHz)));
                return (from, to);
            }
            _kick = Band(40, 120);
            _body = Band(150, 400);
            _crack = Band(1500, Math.Min(4500, 0.45 * rate));
        }

        public Segment Process(float[] signal, int bars)
        {
            var frames = signal.Length >= _size ? (signal.Length - _size) / _hop + 1 : 0;
            var kick = new double[frames];
            var body = new double[frames];
            var crack = new double[frames];
            double raw = 0;

            for (var f = 0; f < frames; f++)
            {
                var offset = f * _hop;
                for (var n = 0; n < _size; n++) _frame[n] = signal[offset + n] * _window[n];
                _fft.Direct(_frame, _re, _im);
                LogMagnitudes(_kick);
                LogMagnitudes(_body);
                LogMagnitudes(_crack);

                if (f > 0)
                {
                    kick[f] = Flux(_kick);
                    body[f] = Flux(_body);
                    crack[f] = Flux(_crack);
                    raw += kick[f] + crack[f];
                }
                Array.Copy(_log, _prevLog, _log.Length);
            }

            return new Segment { Kick = kick, Body = body, Crack = crack, Bars = bars, RawOnsetSum = raw };
        }

        private void LogMagnitudes((int From, int To) band)
        {
            for (var k = band.From; k <= band.To; k++)
                _log[k] = 0.5 * Math.Log(1 + _re[k] * _re[k] + _im[k] * _im[k]); // ≈ log magnitude, no sqrt
        }

        private double Flux((int From, int To) band)
        {
            double sum = 0;
            for (var k = band.From; k <= band.To; k++)
            {
                var d = _log[k] - _prevLog[k];
                if (d > 0) sum += d;
            }
            return sum / (band.To - band.From + 1);
        }
    }

    /// <summary>In place: keep only what rises above the centred moving mean (onset peaks).</summary>
    private static void Detrend(double[] e, int radius)
    {
        var n = e.Length;
        if (n == 0) return;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + e[i];
        var copy = (double[])e.Clone();
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - radius);
            var hi = Math.Min(n - 1, i + radius);
            var mean = (prefix[hi + 1] - prefix[lo]) / (hi - lo + 1);
            e[i] = Math.Max(0, copy[i] - mean);
        }
    }

    private static void NormalizeByMean(List<Segment> segments, Func<Segment, double[]> band)
    {
        double sum = 0; long n = 0;
        foreach (var s in segments) { foreach (var v in band(s)) sum += v; n += band(s).Length; }
        var mean = n > 0 ? sum / n : 0;
        if (mean <= 1e-12) return;
        foreach (var s in segments)
        {
            var e = band(s);
            for (var i = 0; i < e.Length; i++) e[i] /= mean;
        }
    }

    /// <summary>
    /// Pearson correlation of the mean-centred envelope with itself at <paramref name="lagFrames"/>
    /// (best of the two nearest integer lags ± 1, to absorb tempo jitter), pooled over segments.
    /// </summary>
    private static double Autocorrelation(List<Segment> segments, Func<Segment, double[]> band, double lagFrames)
    {
        double sum = 0; long n = 0;
        foreach (var s in segments) { foreach (var v in band(s)) sum += v; n += band(s).Length; }
        if (n == 0) return 0;
        var mean = sum / n;

        var best = double.NegativeInfinity;
        var center = (int)Math.Round(lagFrames);
        for (var lag = Math.Max(1, center - 1); lag <= center + 1; lag++)
        {
            double sxy = 0, sxx = 0, syy = 0;
            foreach (var s in segments)
            {
                var e = band(s);
                for (var t = 0; t + lag < e.Length; t++)
                {
                    var x = e[t] - mean;
                    var y = e[t + lag] - mean;
                    sxy += x * y; sxx += x * x; syy += y * y;
                }
            }
            if (sxx <= 1e-12 || syy <= 1e-12) continue;
            best = Math.Max(best, sxy / Math.Sqrt(sxx * syy));
        }
        return double.IsNegativeInfinity(best) ? 0 : best;
    }

    /// <summary>
    /// The share of beats that carry a real kick, at the beat phase where the kick is strongest
    /// (found per segment, so the downbeat guess doesn't matter), mapped so a kick on every beat
    /// gives 1 and one kick per bar gives 0. A beat counts when its kick reaches 40 % of a strong
    /// kick (the 90th percentile). A hit rate rather than a variance: a bass line under the kick
    /// makes real kick strengths swing from beat to beat without making the pattern less 4x4.
    /// </summary>
    private static double KickRegularity(List<Segment> segments, double framesPerBeat)
    {
        var strengths = new List<double>();
        var phases = Math.Max(1, (int)Math.Floor(framesPerBeat));
        foreach (var s in segments)
        {
            var e = s.Kick;
            var beats = (int)Math.Floor(e.Length / framesPerBeat) - 1;
            if (beats < 4) continue;

            var bestPhase = 0; var bestSum = double.NegativeInfinity;
            for (var phase = 0; phase < phases; phase++)
            {
                double sum = 0;
                for (var b = 0; b < beats; b++) sum += PeakNear(e, b * framesPerBeat + phase);
                if (sum > bestSum) { bestSum = sum; bestPhase = phase; }
            }
            for (var b = 0; b < beats; b++) strengths.Add(PeakNear(e, b * framesPerBeat + bestPhase));
        }

        if (strengths.Count < 4) return 0;
        var sorted = strengths.OrderBy(v => v).ToArray();
        var strong = sorted[(int)Math.Floor(0.9 * (sorted.Length - 1))];
        if (strong <= 1e-9) return 0;
        var hitRate = strengths.Count(v => v >= 0.4 * strong) / (double)strengths.Count;
        return Clamp01((hitRate - 0.25) / 0.65);
    }
    private static double PeakNear(double[] e, double position)
    {
        var c = (int)Math.Round(position);
        var best = 0.0;
        for (var i = Math.Max(0, c - 2); i <= Math.Min(e.Length - 1, c + 2); i++) best = Math.Max(best, e[i]);
        return best;
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
