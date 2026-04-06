using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Timeline;

namespace SLSKDONET.Services.Timeline;

// ─────────────────────────────────────────────────────────────────────────────
// CrossfadeProvider
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Equal-power crossfade between two <see cref="ISampleProvider"/> sources.
/// During the transition window of <see cref="DurationSamples"/> the outgoing
/// provider fades from 1→0 and the incoming provider fades from 0→1 using
/// a cos²/sin² power curve (constant-power crossfade).
/// After the window the outgoing provider is discarded.
/// </summary>
public sealed class CrossfadeProvider : ISampleProvider
{
    private readonly ISampleProvider _outgoing;
    private readonly ISampleProvider _incoming;
    private long _positionSamples;

    public WaveFormat WaveFormat { get; }
    public long DurationSamples { get; }

    public CrossfadeProvider(ISampleProvider outgoing, ISampleProvider incoming, long durationSamples)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        DurationSamples = Math.Max(1, durationSamples);
        WaveFormat = outgoing.WaveFormat;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_positionSamples >= DurationSamples)
            return _incoming.Read(buffer, offset, count);

        var outBuf = new float[count];
        var inBuf = new float[count];

        int outRead = _outgoing.Read(outBuf, 0, count);
        int inRead = _incoming.Read(inBuf, 0, count);
        int frames = Math.Max(outRead, inRead);

        int channels = WaveFormat.Channels;

        for (int i = 0; i < frames; i += channels)
        {
            long framePos = _positionSamples / channels;
            double t = Math.Min(1.0, (double)framePos / (DurationSamples / channels));

            // Constant-power taper: out = cos(t·π/2), in = sin(t·π/2)
            float outGain = (float)Math.Cos(t * Math.PI / 2.0);
            float inGain = (float)Math.Sin(t * Math.PI / 2.0);

            for (int ch = 0; ch < channels && (i + ch) < frames; ch++)
            {
                float outSample = (i + ch) < outRead ? outBuf[i + ch] : 0f;
                float inSample = (i + ch) < inRead ? inBuf[i + ch] : 0f;
                buffer[offset + i + ch] = outSample * outGain + inSample * inGain;
            }

            _positionSamples += channels;
        }

        return frames;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EchoOutProvider
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Outgoing clip decays with a simple feedback delay (echo) while the
/// incoming clip fades in linearly over the same window.
/// The echo effect uses a single-tap feedback delay of one beat period.
/// </summary>
public sealed class EchoOutProvider : ISampleProvider
{
    private readonly ISampleProvider _outgoing;
    private readonly ISampleProvider _incoming;
    private readonly float _decayFactor;
    private long _positionSamples;
    private readonly float[] _delayBuffer;
    private int _delayBufferPos;

    public WaveFormat WaveFormat { get; }
    public long DurationSamples { get; }

    /// <param name="outgoing">Outgoing (ending) clip provider.</param>
    /// <param name="incoming">Incoming (starting) clip provider.</param>
    /// <param name="durationSamples">Transition window in samples.</param>
    /// <param name="decayFactor">Echo feedback factor (0–1).</param>
    /// <param name="delaySamples">Delay line length in samples (typically 1 beat).</param>
    public EchoOutProvider(
        ISampleProvider outgoing,
        ISampleProvider incoming,
        long durationSamples,
        float decayFactor = 0.55f,
        int delaySamples = 22050)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        _decayFactor = Math.Clamp(decayFactor, 0f, 0.99f);
        DurationSamples = Math.Max(1, durationSamples);
        WaveFormat = outgoing.WaveFormat;

        int bufLen = Math.Max(1, delaySamples) * WaveFormat.Channels;
        _delayBuffer = new float[bufLen];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_positionSamples >= DurationSamples)
            return _incoming.Read(buffer, offset, count);

        var outBuf = new float[count];
        var inBuf = new float[count];

        int outRead = _outgoing.Read(outBuf, 0, count);
        int inRead = _incoming.Read(inBuf, 0, count);
        int frames = Math.Max(outRead, inRead);

        int channels = WaveFormat.Channels;
        int delayLen = _delayBuffer.Length;

        for (int i = 0; i < frames; i++)
        {
            double t = Math.Min(1.0, (double)_positionSamples / DurationSamples);
            float outGain = (float)(1.0 - t);
            float inGain = (float)t;

            // Echo: mix dry + decayed delay tap
            float dryOut = (i < outRead ? outBuf[i] : 0f);
            float delayed = _delayBuffer[_delayBufferPos];
            float echoOut = dryOut + delayed * _decayFactor;

            _delayBuffer[_delayBufferPos] = echoOut;
            _delayBufferPos = (_delayBufferPos + 1) % delayLen;

            float inSample = (i < inRead ? inBuf[i] : 0f);
            buffer[offset + i] = echoOut * outGain + inSample * inGain;

            _positionSamples++;
        }

        return frames;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FilterSweepProvider
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Simple first-order IIR low-pass filter sweep on the outgoing clip,
/// while the incoming clip fades in linearly.
/// The cutoff frequency decreases linearly from
/// <see cref="TransitionModel.FilterStartFrequency"/> to
/// <see cref="TransitionModel.FilterEndFrequency"/> over the window.
/// </summary>
public sealed class FilterSweepProvider : ISampleProvider
{
    private readonly ISampleProvider _outgoing;
    private readonly ISampleProvider _incoming;
    private readonly float _freqStart;
    private readonly float _freqEnd;
    private long _positionSamples;
    private float _filterState; // IIR one-pole state

    public WaveFormat WaveFormat { get; }
    public long DurationSamples { get; }

    public FilterSweepProvider(
        ISampleProvider outgoing,
        ISampleProvider incoming,
        long durationSamples,
        float freqStart = 20_000f,
        float freqEnd = 200f)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        _freqStart = freqStart;
        _freqEnd = freqEnd;
        DurationSamples = Math.Max(1, durationSamples);
        WaveFormat = outgoing.WaveFormat;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_positionSamples >= DurationSamples)
            return _incoming.Read(buffer, offset, count);

        var outBuf = new float[count];
        var inBuf = new float[count];

        int outRead = _outgoing.Read(outBuf, 0, count);
        int inRead = _incoming.Read(inBuf, 0, count);
        int frames = Math.Max(outRead, inRead);

        int sampleRate = WaveFormat.SampleRate;
        int channels = WaveFormat.Channels;

        for (int i = 0; i < frames; i += channels)
        {
            double t = Math.Min(1.0, (double)_positionSamples / DurationSamples);
            float inGain = (float)t;

            // Linearly interpolate cutoff frequency
            float cutoff = _freqStart + (float)(t * (_freqEnd - _freqStart));
            float rc = 1.0f / (2.0f * MathF.PI * cutoff);
            float dt = 1.0f / sampleRate;
            float alpha = dt / (rc + dt);

            for (int ch = 0; ch < channels && (i + ch) < frames; ch++)
            {
                float dry = (i + ch) < outRead ? outBuf[i + ch] : 0f;
                // One-pole IIR low-pass: y[n] = y[n-1] + alpha * (x[n] - y[n-1])
                _filterState = _filterState + alpha * (dry - _filterState);
                float inSample = (i + ch) < inRead ? inBuf[i + ch] : 0f;
                buffer[offset + i + ch] = _filterState * (1f - inGain) + inSample * inGain;
            }

            _positionSamples += channels;
        }

        return frames;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TransitionDsp  (factory)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Factory that constructs the appropriate <see cref="ISampleProvider"/> chain
/// for a given <see cref="TransitionModel"/> and project BPM.
/// </summary>
public static class TransitionDsp
{
    /// <summary>
    /// Builds the transition sample-provider that mixes
    /// <paramref name="outgoing"/> into <paramref name="incoming"/>
    /// according to <paramref name="model"/>.
    /// </summary>
    /// <param name="outgoing">Sample provider for the ending clip.</param>
    /// <param name="incoming">Sample provider for the starting clip.</param>
    /// <param name="model">Transition parameters.</param>
    /// <param name="projectBpm">Project BPM — used to convert beats → samples.</param>
    /// <returns>
    /// An <see cref="ISampleProvider"/> that produces the mixed output.
    /// For <see cref="TransitionType.Cut"/>, the outgoing provider is returned
    /// unchanged (no overlap).
    /// </returns>
    public static ISampleProvider Build(
        ISampleProvider outgoing,
        ISampleProvider incoming,
        TransitionModel model,
        double projectBpm = 128.0)
    {
        int sampleRate = outgoing.WaveFormat.SampleRate;
        int channels = outgoing.WaveFormat.Channels;
        long durationSamples = BeatsToSamples(model.DurationBeats, projectBpm, sampleRate, channels);

        return model.Type switch
        {
            TransitionType.Cut => outgoing,
            TransitionType.Crossfade => new CrossfadeProvider(outgoing, incoming, durationSamples),
            TransitionType.EchoOut => new EchoOutProvider(
                outgoing, incoming, durationSamples,
                model.EchoDecayFactor,
                delaySamples: (int)BeatsToSamples(1.0, projectBpm, sampleRate, 1)),
            TransitionType.FilterSweep => new FilterSweepProvider(
                outgoing, incoming, durationSamples,
                model.FilterStartFrequency,
                model.FilterEndFrequency),
            _ => new CrossfadeProvider(outgoing, incoming, durationSamples)
        };
    }

    /// <summary>Converts beat count to sample count (multi-channel).</summary>
    public static long BeatsToSamples(double beats, double bpm, int sampleRate, int channels)
    {
        double seconds = beats * (60.0 / bpm);
        return (long)(seconds * sampleRate) * channels;
    }
}
