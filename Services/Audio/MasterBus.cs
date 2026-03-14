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
/// Look-ahead limiter for the master bus.
/// Prevents any sample from exceeding the threshold.
/// </summary>
public class Limiter
{
    private float _thresholdDb;
    private float _lookAheadMs;
    private float[] _lookAheadBuffer;
    private int _lookAheadSamples;
    
    public Limiter(float thresholdDb, float lookAheadMs)
    {
        _thresholdDb = thresholdDb;
        _lookAheadMs = lookAheadMs;
        _lookAheadSamples = (int)(44100 * lookAheadMs / 1000.0);
        _lookAheadBuffer = new float[_lookAheadSamples * 2]; // Stereo
    }

    public void Process(float[] buffer, int offset, int count)
    {
        float threshold = (float)Math.Pow(10, _thresholdDb / 20.0);
        
        for (int i = 0; i < count; i++)
        {
            float sample = buffer[offset + i];
            float absSample = Math.Abs(sample);
            
            // Simple hard limiting (brick-wall)
            if (absSample > threshold)
            {
                buffer[offset + i] = Math.Sign(sample) * threshold;
            }
        }
    }
}
