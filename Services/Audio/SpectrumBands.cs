using System;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Turns raw FFT magnitudes (AudioPlayerService's 2048-point, unscaled, Hann-windowed mono FFT)
/// into what a visualizer should draw: log-spaced bands (bass gets as much room as treble, like
/// hearing does), in decibels mapped to 0..1, with a +3 dB/octave tilt so the treble isn't
/// flat, fast-attack/slow-release smoothing, falling peak caps and a slow auto-gain so quiet
/// masters and loud ones both fill the display. UI-thread only; one instance per visualizer.
/// </summary>
public sealed class SpectrumBands
{
    private const double MinFrequency = 30;
    private const double MaxFrequency = 16000;
    private const double FloorDb = -62;
    private const double TiltDbPerOctave = 3.0;

    private readonly int _bandCount;
    private readonly double _sampleRate;
    private int[]? _binStart;
    private int[]? _binEnd;
    private int _mappedFor = -1;
    private double _gainDb;

    public float[] Levels { get; }
    public float[] Peaks { get; }
    private readonly float[] _peakHold;

    /// <summary>Loudness of the low end (roughly 30–150 Hz), 0..1 — drives beat-ish effects.</summary>
    public float Bass { get; private set; }
    public float Mid { get; private set; }
    public float Treble { get; private set; }
    public float Overall { get; private set; }

    public SpectrumBands(int bandCount, double sampleRate = 44100)
    {
        _bandCount = Math.Max(4, bandCount);
        _sampleRate = sampleRate;
        Levels = new float[_bandCount];
        Peaks = new float[_bandCount];
        _peakHold = new float[_bandCount];
    }

    /// <summary>Feeds a new FFT block. <paramref name="magnitudes"/> is half the FFT size.</summary>
    public void Push(float[]? magnitudes, double dtSeconds)
    {
        var target = new float[_bandCount];
        if (magnitudes is { Length: > 8 })
        {
            EnsureMapping(magnitudes.Length);
            int fftSize = magnitudes.Length * 2;
            double fullScale = fftSize / 4.0; // Hann-windowed full-scale sine peak magnitude

            double loudest = double.NegativeInfinity;
            var db = new double[_bandCount];
            for (int b = 0; b < _bandCount; b++)
            {
                double max = 0;
                for (int i = _binStart![b]; i <= _binEnd![b]; i++)
                    if (magnitudes[i] > max) max = magnitudes[i];
                double centre = BandCentre(b);
                double value = 20 * Math.Log10(Math.Max(max, 1e-9) / fullScale)
                               + TiltDbPerOctave * Math.Log2(centre / 1000.0);
                db[b] = value;
                if (value > loudest) loudest = value;
            }

            // Auto-gain: follow the loudest band up quickly and down slowly so the display sits
            // near the top on any master level without pumping on every beat.
            double wanted = Math.Clamp(-loudest - 3, -6, 30);
            double rate = wanted < _gainDb ? 8.0 : 0.5;
            _gainDb += (wanted - _gainDb) * Math.Min(1.0, rate * dtSeconds);

            for (int b = 0; b < _bandCount; b++)
                target[b] = (float)Math.Clamp((db[b] + _gainDb - FloorDb) / -FloorDb, 0, 1);
        }

        Smooth(target, dtSeconds);
    }

    /// <summary>Decays toward silence (no audio data this frame).</summary>
    public void Decay(double dtSeconds) => Smooth(new float[_bandCount], dtSeconds);

    private void Smooth(float[] target, double dt)
    {
        float attack = (float)Math.Min(1.0, dt * 30);   // ~33 ms rise
        float release = (float)Math.Min(1.0, dt * 6);   // ~170 ms fall
        float peakFall = (float)(dt * 0.9);

        double bass = 0, mid = 0, treble = 0, all = 0;
        int nb = 0, nm = 0, nt = 0;
        for (int b = 0; b < _bandCount; b++)
        {
            float cur = Levels[b];
            float t = target[b];
            cur += (t - cur) * (t > cur ? attack : release);
            Levels[b] = cur;

            if (cur >= Peaks[b]) { Peaks[b] = cur; _peakHold[b] = 0.35f; }
            else if ((_peakHold[b] -= (float)dt) <= 0) Peaks[b] = Math.Max(cur, Peaks[b] - peakFall);

            double f = BandCentre(b);
            all += cur;
            if (f < 150) { bass += cur; nb++; }
            else if (f < 2500) { mid += cur; nm++; }
            else { treble += cur; nt++; }
        }
        Bass = nb > 0 ? (float)(bass / nb) : 0;
        Mid = nm > 0 ? (float)(mid / nm) : 0;
        Treble = nt > 0 ? (float)(treble / nt) : 0;
        Overall = (float)(all / _bandCount);
    }

    /// <summary>Centre frequency of band <paramref name="b"/> (log spacing).</summary>
    public double BandCentre(int b) =>
        MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (b + 0.5) / _bandCount);

    private void EnsureMapping(int binCount)
    {
        if (_mappedFor == binCount) return;
        _binStart = new int[_bandCount];
        _binEnd = new int[_bandCount];
        double binHz = _sampleRate / (binCount * 2.0);
        for (int b = 0; b < _bandCount; b++)
        {
            double lo = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)b / _bandCount);
            double hi = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)(b + 1) / _bandCount);
            int start = Math.Clamp((int)Math.Floor(lo / binHz), 1, binCount - 1);
            int end = Math.Clamp((int)Math.Ceiling(hi / binHz), start, binCount - 1);
            _binStart[b] = start;
            _binEnd[b] = end;
        }
        _mappedFor = binCount;
    }
}
