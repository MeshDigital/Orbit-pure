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
        _limiter = new Limiter(thresholdDb: -0.1f, lookAheadMs: 5.0f);
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
/// Allocation-free stereo-linked brickwall peak limiter for master/mix buses.
/// Uses instantaneous peak-envelope attack (guaranteeing no sample ever exceeds
/// <c>thresholdDb</c>) with smooth exponential release so overlapping peaks are
/// attenuated cleanly rather than hard-clipped.
/// </summary>
public class Limiter
{
    private readonly float _threshold;
    private readonly float _releaseCoeff;
    private float _currentGain = 1.0f;

    public float ThresholdLinear => _threshold;

    public Limiter(float thresholdDb, float lookAheadMs = 0f, int sampleRate = 44100, float releaseMs = 60f)
    {
        _threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
        int sr = Math.Max(8000, sampleRate);
        float relSec = Math.Max(0.005f, releaseMs / 1000f);
        _releaseCoeff = (float)Math.Exp(-1.0 / (sr * relSec));
        _ = lookAheadMs; // retained for API compatibility; zero-latency stereo-linked envelope needs no delay line
    }

    public void Reset() => _currentGain = 1.0f;

    /// <summary>Processes interleaved stereo samples in-place with zero heap allocations.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        int end = offset + count;
        float threshold = _threshold;
        float releaseCoeff = _releaseCoeff;
        float gain = _currentGain;

        int i = offset;
        for (; i + 1 < end; i += 2)
        {
            float l = buffer[i];
            float r = buffer[i + 1];
            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            float targetGain = peak > threshold ? threshold / peak : 1.0f;

            if (targetGain < gain)
                gain = targetGain;
            else
                gain = releaseCoeff * gain + (1.0f - releaseCoeff) * targetGain;

            buffer[i] = Math.Clamp(l * gain, -threshold, threshold);
            buffer[i + 1] = Math.Clamp(r * gain, -threshold, threshold);
        }

        // Handle any trailing odd sample safely.
        if (i < end)
        {
            float s = buffer[i];
            float peak = Math.Abs(s);
            float targetGain = peak > threshold ? threshold / peak : 1.0f;
            gain = targetGain < gain ? targetGain : releaseCoeff * gain + (1.0f - releaseCoeff) * targetGain;
            buffer[i] = Math.Clamp(s * gain, -threshold, threshold);
        }

        _currentGain = gain;
    }
}
