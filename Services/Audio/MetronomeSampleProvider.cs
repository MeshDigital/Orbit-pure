using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Mixes a metronome click into another <see cref="ISampleProvider"/>, scheduled from the beat list
/// with an accent on bar downbeats (issue #222). Pure sample-level DSP: the caller owns the player
/// graph, and volume is applied via a <c>VolumeSampleProvider</c> — never <c>IWavePlayer.Volume</c>.
/// Seeking re-clocks the metronome via <see cref="Seek"/> (used together with ExactSeek).
/// </summary>
public sealed class MetronomeSampleProvider : ISampleProvider
{
    private const double ClickDurationSeconds = 0.030;
    private const double DecaySeconds = 0.006;

    private readonly ISampleProvider _source;
    private IReadOnlyList<double> _beatTimes;
    private int _downbeatIndex;
    private readonly float _accentVolume;
    private readonly float _beatVolume;
    private long _framePosition;
    private int _beatCursor;

    /// <summary>When false (default) the provider is a pure pass-through.</summary>
    public bool Enabled { get; set; }

    public MetronomeSampleProvider(
        ISampleProvider source,
        IReadOnlyList<double> beatTimes,
        double downbeatSeconds,
        float accentVolume = 0.5f,
        float beatVolume = 0.25f)
    {
        _source = source;
        _beatTimes = beatTimes;
        _accentVolume = accentVolume;
        _beatVolume = beatVolume;
        _downbeatIndex = NearestBeatIndex(beatTimes, downbeatSeconds);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Updates the grid the clicks are scheduled from (and re-clocks it).</summary>
    public void SetGrid(IReadOnlyList<double> beatTimes, double downbeatSeconds)
    {
        _beatTimes = beatTimes;
        _downbeatIndex = NearestBeatIndex(beatTimes, downbeatSeconds);
        _beatCursor = 0;
    }

    /// <summary>Re-clocks the metronome to <paramref name="seconds"/> (call after a seek).</summary>
    public void Seek(double seconds)
    {
        _framePosition = (long)Math.Round(seconds * WaveFormat.SampleRate);
        _beatCursor = LowerBound(seconds);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled) return read;

        int channels = WaveFormat.Channels;
        int sampleRate = WaveFormat.SampleRate;

        for (int n = 0; n < read; n++)
        {
            int frame = (int)(_framePosition + n / channels);
            double t = (double)frame / sampleRate;

            while (_beatCursor < _beatTimes.Count && _beatTimes[_beatCursor] + ClickDurationSeconds < t)
                _beatCursor++;

            for (int b = _beatCursor; b < _beatTimes.Count && _beatTimes[b] <= t + ClickDurationSeconds; b++)
            {
                double dt = t - _beatTimes[b];
                if (dt < 0 || dt >= ClickDurationSeconds) continue;

                bool accent = ((b - _downbeatIndex) % 4 + 4) % 4 == 0;
                float click = Click(dt, accent ? 1500.0 : 1000.0) * (accent ? _accentVolume : _beatVolume);
                buffer[offset + n] += click;
            }
        }

        _framePosition += read / channels;
        return read;
    }

    private static float Click(double dt, double frequency)
    {
        double envelope = Math.Exp(-dt / DecaySeconds);
        return (float)(Math.Sin(2 * Math.PI * frequency * dt) * envelope);
    }

    private int LowerBound(double seconds)
    {
        int lo = 0, hi = _beatTimes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_beatTimes[mid] < seconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int NearestBeatIndex(IReadOnlyList<double> beats, double time)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < beats.Count; i++)
        {
            double d = Math.Abs(beats[i] - time);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }
}
