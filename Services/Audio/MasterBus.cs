using System;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Master Bus for the DAW timeline.
/// Provides gain staging, glue compression, and look-ahead limiting
/// to ensure professional loudness without clipping.
/// </summary>
public class MasterBus : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly Compressor _glueCompressor;
    private readonly Limiter _limiter;
    
    /// <summary>
    /// Input gain in dB (default -6dB for headroom).
    /// </summary>
    public float InputGainDb { get; set; } = -6.0f;
    
    /// <summary>
    /// Output gain in dB.
    /// </summary>
    public float OutputGainDb { get; set; } = 0.0f;
    
    /// <summary>
    /// Enable/disable the glue compressor.
    /// </summary>
    public bool CompressorEnabled { get; set; } = true;
    
    /// <summary>
    /// Enable/disable the limiter.
    /// </summary>
    public bool LimiterEnabled { get; set; } = true;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MasterBus(ISampleProvider source)
    {
        _source = source;
        _glueCompressor = new Compressor(ratio: 1.5f, thresholdDb: -10f, attackMs: 10f, releaseMs: 100f);
        _limiter = new Limiter(thresholdDb: -0.1f, lookAheadMs: 5.0f, sampleRate: source.WaveFormat.SampleRate);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // 1. Read from source (MultiTrackEngine)
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead <= 0) return samplesRead;
        
        // 2. Apply input gain (headroom)
        float inputGain = (float)Math.Pow(10, InputGainDb / 20.0);
        for (int i = 0; i < samplesRead; i++)
        {
            buffer[offset + i] *= inputGain;
        }
        
        // 3. Apply glue compressor
        if (CompressorEnabled)
        {
            _glueCompressor.Process(buffer, offset, samplesRead);
        }
        
        // 4. Apply look-ahead limiter (safety net)
        if (LimiterEnabled)
        {
            _limiter.Process(buffer, offset, samplesRead);
        }
        
        // 5. Apply output gain
        float outputGain = (float)Math.Pow(10, OutputGainDb / 20.0);
        for (int i = 0; i < samplesRead; i++)
        {
            buffer[offset + i] *= outputGain;
        }
        
        return samplesRead;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Simple glue compressor for the master bus.
/// Uses RMS detection with soft-knee compression.
/// </summary>
public class Compressor
{
    private float _ratio;
    private float _thresholdDb;
    private float _attackMs;
    private float _releaseMs;
    private float _envelope = 0f;
    
    public Compressor(float ratio, float thresholdDb, float attackMs, float releaseMs)
    {
        _ratio = ratio;
        _thresholdDb = thresholdDb;
        _attackMs = attackMs;
        _releaseMs = releaseMs;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        float threshold = (float)Math.Pow(10, _thresholdDb / 20.0);
        // Approximation of coefficients for 44.1kHz
        float attackCoeff = (float)Math.Exp(-1.0 / (44100 * _attackMs / 1000.0));
        float releaseCoeff = (float)Math.Exp(-1.0 / (44100 * _releaseMs / 1000.0));
        
        for (int i = 0; i < count; i++)
        {
            float input = Math.Abs(buffer[offset + i]);
            
            // Envelope follower
            if (input > _envelope)
                _envelope = attackCoeff * _envelope + (1 - attackCoeff) * input;
            else
                _envelope = releaseCoeff * _envelope + (1 - releaseCoeff) * input;
            
            // Gain reduction
            if (_envelope > threshold)
            {
                float dbOver = 20 * (float)Math.Log10(_envelope / threshold);
                float dbReduction = dbOver - (dbOver / _ratio);
                float gain = (float)Math.Pow(10, -dbReduction / 20.0);
                buffer[offset + i] *= gain;
            }
        }
    }
}

/// <summary>
/// Allocation-free, zero-latency, stereo-linked brickwall peak limiter for master/mix buses.
/// Gain drops instantly to whatever keeps the current sample under <c>thresholdDb</c> (so no
/// sample ever exceeds it), is then <b>held</b> for <c>holdMs</c>, and only then released. The
/// hold is what keeps it clean: without it the gain recovered between the peaks of every bass
/// cycle and was pulled down again at the next one, modulating the music with its own waveform
/// (−46 dB THD+N on a hot master, audible grit). Peaks of a 30 Hz+ bass note arrive within
/// 20 ms, so on sustained material the gain settles and stays put (measured &lt; −60 dB).
/// No look-ahead: the mix bus is sample-exact and must not delay its output.
/// </summary>
public class Limiter
{
    private readonly float _threshold;
    private readonly float _releaseCoeff;
    private readonly int _holdFrames;
    private float _currentGain = 1.0f;
    private int _holdRemaining;

    public float ThresholdLinear => _threshold;

    public Limiter(float thresholdDb, float lookAheadMs = 0f, int sampleRate = 44100, float releaseMs = 150f, float holdMs = 60f)
    {
        _threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
        int sr = Math.Max(8000, sampleRate);
        float relSec = Math.Max(0.005f, releaseMs / 1000f);
        _releaseCoeff = (float)Math.Exp(-1.0 / (sr * relSec));
        _holdFrames = Math.Max(0, (int)Math.Round(sr * holdMs / 1000f));
        _ = lookAheadMs; // retained for API compatibility; the limiter adds no latency
    }

    public void Reset()
    {
        _currentGain = 1.0f;
        _holdRemaining = 0;
    }

    /// <summary>Processes interleaved stereo samples in-place with zero heap allocations.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        int end = offset + count;
        int i = offset;
        for (; i + 1 < end; i += 2)
        {
            float l = buffer[i];
            float r = buffer[i + 1];
            float gain = NextGain(Math.Max(Math.Abs(l), Math.Abs(r)));
            if (gain < 1.0f)
            {
                buffer[i] = Math.Clamp(l * gain, -_threshold, _threshold);
                buffer[i + 1] = Math.Clamp(r * gain, -_threshold, _threshold);
            }
        }

        // A trailing odd sample (never produced by the stereo buses, handled for safety).
        if (i < end)
        {
            float s = buffer[i];
            float gain = NextGain(Math.Abs(s));
            if (gain < 1.0f) buffer[i] = Math.Clamp(s * gain, -_threshold, _threshold);
        }
    }

    private float NextGain(float peak)
    {
        float target = peak > _threshold ? _threshold / peak : 1.0f;
        if (target <= _currentGain)
        {
            _currentGain = target;
            _holdRemaining = _holdFrames;
        }
        else if (_holdRemaining > 0)
        {
            _holdRemaining--;
        }
        else
        {
            _currentGain = _releaseCoeff * _currentGain + (1.0f - _releaseCoeff) * target;
            if (_currentGain > 0.99999f) _currentGain = 1.0f;
        }
        return _currentGain;
    }
}
