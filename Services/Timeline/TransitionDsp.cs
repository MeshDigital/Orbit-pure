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

    private readonly bool _rising;
    private float _highPassPrevIn;
    private float _highPassState;

    public FilterSweepProvider(
        ISampleProvider outgoing,
        ISampleProvider incoming,
        long durationSamples,
        float freqStart = 20_000f,
        float freqEnd = 200f,
        bool rising = false)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        _freqStart = freqStart;
        _freqEnd = freqEnd;
        _rising = rising;
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

            for (int ch = 0; ch < channels && (i + ch) < frames; ch++)
            {
                float outSample = (i + ch) < outRead ? outBuf[i + ch] : 0f;
                float inSample = (i + ch) < inRead ? inBuf[i + ch] : 0f;

                if (_rising)
                {
                    // "Rise" preset: sweep a high-pass filter UP on the incoming clip (low
                    // frequency content withheld at first, opens up into the drop) while the
                    // outgoing clip fades out normally. Cutoff sweeps freqEnd (low) -> freqStart
                    // (high) as t goes 0->1, opposite direction of the falling low-pass below.
                    float cutoff = _freqEnd + (float)(t * (_freqStart - _freqEnd));
                    float rc = 1.0f / (2.0f * MathF.PI * cutoff);
                    float dt = 1.0f / sampleRate;
                    float alpha = rc / (rc + dt);
                    // One-pole IIR high-pass: y[n] = alpha * (y[n-1] + x[n] - x[n-1])
                    _highPassState = alpha * (_highPassState + inSample - _highPassPrevIn);
                    _highPassPrevIn = inSample;

                    buffer[offset + i + ch] = outSample * (1f - inGain) + _highPassState * inGain;
                }
                else
                {
                    // Falling low-pass sweep on the outgoing clip, freqStart (high) -> freqEnd (low).
                    float cutoff = _freqStart + (float)(t * (_freqEnd - _freqStart));
                    float rc = 1.0f / (2.0f * MathF.PI * cutoff);
                    float dt = 1.0f / sampleRate;
                    float alpha = dt / (rc + dt);
                    // One-pole IIR low-pass: y[n] = y[n-1] + alpha * (x[n] - y[n-1])
                    _filterState = _filterState + alpha * (outSample - _filterState);

                    buffer[offset + i + ch] = _filterState * (1f - inGain) + inSample * inGain;
                }
            }

            _positionSamples += channels;
        }

        return frames;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EqSwapProvider
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Two-band bass-handover crossfade ("Blend" preset / classic DJ bass swap): the low band
/// (below <see cref="LowCrossoverHz"/>) swaps from outgoing to incoming linearly over the
/// window while mids/highs crossfade with an equal-power curve, avoiding the muddy bass
/// collision a plain crossfade produces when both tracks' kicks/subs overlap.
/// </summary>
public sealed class EqSwapProvider : ISampleProvider
{
    private readonly ISampleProvider _outgoing;
    private readonly ISampleProvider _incoming;
    private readonly float _lowCrossoverHz;
    private long _positionSamples;
    private float _outLowState;
    private float _inLowState;

    public WaveFormat WaveFormat { get; }
    public long DurationSamples { get; }

    public EqSwapProvider(ISampleProvider outgoing, ISampleProvider incoming, long durationSamples, float lowCrossoverHz = 250f)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        _lowCrossoverHz = lowCrossoverHz;
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

        float rc = 1.0f / (2.0f * MathF.PI * _lowCrossoverHz);
        float dt = 1.0f / sampleRate;
        float alpha = dt / (rc + dt);

        for (int i = 0; i < frames; i += channels)
        {
            double t = Math.Min(1.0, (double)_positionSamples / DurationSamples);
            float lowOutGain = (float)(1.0 - t);
            float lowInGain = (float)t;
            float midHighOutGain = (float)Math.Cos(t * Math.PI / 2.0);
            float midHighInGain = (float)Math.Sin(t * Math.PI / 2.0);

            for (int ch = 0; ch < channels && (i + ch) < frames; ch++)
            {
                float outSample = (i + ch) < outRead ? outBuf[i + ch] : 0f;
                float inSample = (i + ch) < inRead ? inBuf[i + ch] : 0f;

                // One-pole low-pass split: low band + (original - low) ≈ high/mid complement.
                _outLowState = _outLowState + alpha * (outSample - _outLowState);
                _inLowState = _inLowState + alpha * (inSample - _inLowState);
                float outHigh = outSample - _outLowState;
                float inHigh = inSample - _inLowState;

                buffer[offset + i + ch] =
                    (_outLowState * lowOutGain) + (outHigh * midHighOutGain) +
                    (_inLowState * lowInGain) + (inHigh * midHighInGain);
            }

            _positionSamples += channels;
        }

        return frames;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WaveDuckProvider
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Rhythmic gain ducking locked to the beat grid ("Wave" preset): on top of the normal
/// crossfade envelope, gain dips briefly on every beat, producing a pumping/sidechain-style
/// handover instead of a smooth continuous fade.
/// </summary>
public sealed class WaveDuckProvider : ISampleProvider
{
    private readonly ISampleProvider _outgoing;
    private readonly ISampleProvider _incoming;
    private readonly double _beatPeriodSeconds;
    private readonly float _duckDepth;
    private long _positionSamples;

    public WaveFormat WaveFormat { get; }
    public long DurationSamples { get; }

    public WaveDuckProvider(ISampleProvider outgoing, ISampleProvider incoming, long durationSamples, double beatPeriodSeconds, float duckDepth = 0.5f)
    {
        if (outgoing.WaveFormat.SampleRate != incoming.WaveFormat.SampleRate ||
            outgoing.WaveFormat.Channels != incoming.WaveFormat.Channels)
            throw new ArgumentException("Both providers must share the same WaveFormat.");

        _outgoing = outgoing;
        _incoming = incoming;
        _beatPeriodSeconds = Math.Max(0.05, beatPeriodSeconds);
        _duckDepth = Math.Clamp(duckDepth, 0f, 1f);
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
            float outGain = (float)Math.Cos(t * Math.PI / 2.0);
            float inGain = (float)Math.Sin(t * Math.PI / 2.0);

            // Phase within the current beat (0 = on the downbeat, dips deepest there).
            double elapsedSeconds = (_positionSamples / channels) / (double)sampleRate;
            double beatPhase = (elapsedSeconds % _beatPeriodSeconds) / _beatPeriodSeconds;
            // Raised-cosine pulse, sharply peaked near the downbeat (phase 0/1).
            double pulse = Math.Pow(0.5 * (1.0 + Math.Cos(2.0 * Math.PI * beatPhase)), 4.0);
            float duck = 1f - _duckDepth * (float)pulse;

            for (int ch = 0; ch < channels && (i + ch) < frames; ch++)
            {
                float outSample = (i + ch) < outRead ? outBuf[i + ch] : 0f;
                float inSample = (i + ch) < inRead ? inBuf[i + ch] : 0f;
                buffer[offset + i + ch] = (outSample * outGain + inSample * inGain) * duck;
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
                model.FilterEndFrequency,
                model.FilterSweepRising),
            TransitionType.EqSwap => new EqSwapProvider(outgoing, incoming, durationSamples),
            TransitionType.WaveDuck => new WaveDuckProvider(
                outgoing, incoming, durationSamples,
                beatPeriodSeconds: 60.0 / projectBpm,
                model.WaveDuckDepth),
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
