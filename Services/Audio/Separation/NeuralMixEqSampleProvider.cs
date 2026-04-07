using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>
/// Per-stem 3-band parametric EQ sample provider using NAudio BiQuadFilter:
///
///   Band 1: Low shelf   @ 200 Hz   (±12 dB)
///   Band 2: Peaking EQ  @ 1 000 Hz (±12 dB, Q = 0.707)
///   Band 3: High shelf  @ 8 000 Hz (±12 dB)
///
/// The filter chain is built lazily on first Read() and rebuilt whenever a gain
/// property changes.  Stereo sources use one filter pair (L+R) per band.
///
/// Thread safety: <see cref="LowGainDb"/>, <see cref="MidGainDb"/>, and
/// <see cref="HighGainDb"/> may be set from the UI thread while <see cref="Read"/>
/// runs on the audio thread.  Volatile reads on the gain fields are sufficient;
/// the worst case is one slightly-stale buffer.
/// </summary>
public sealed class NeuralMixEqSampleProvider : ISampleProvider
{
    private const float MinGainDb = -12f;
    private const float MaxGainDb =  12f;

    // Centre frequencies / filter constants
    private const float LowShelfFreq  =  200f;
    private const float MidPeakFreq   = 1000f;
    private const float HighShelfFreq = 8000f;
    private const float MidQ          = 0.707f;
    private const float ShelfSlope    = 1.0f;

    private readonly ISampleProvider _source;

    // BiQuadFilter is not thread-safe internally, but we only ever rebuild on the
    // same thread that the gain properties are written from (UI sets _dirty=true,
    // audio thread rebuilds before next sample block).
    private BiQuadFilter? _lowL,  _lowR;
    private BiQuadFilter? _midL,  _midR;
    private BiQuadFilter? _highL, _highR;
    private volatile bool _dirty = true;

    private volatile float _lowDb  = 0f;
    private volatile float _midDb  = 0f;
    private volatile float _highDb = 0f;

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    // ── Band gains ────────────────────────────────────────────────────────

    /// <summary>Low-shelf gain in dB.  Range [−12, +12].  0 = flat.</summary>
    public float LowGainDb
    {
        get => _lowDb;
        set { _lowDb  = Math.Clamp(value, MinGainDb, MaxGainDb); _dirty = true; }
    }

    /// <summary>Mid peaking-EQ gain in dB.  Range [−12, +12].  0 = flat.</summary>
    public float MidGainDb
    {
        get => _midDb;
        set { _midDb  = Math.Clamp(value, MinGainDb, MaxGainDb); _dirty = true; }
    }

    /// <summary>High-shelf gain in dB.  Range [−12, +12].  0 = flat.</summary>
    public float HighGainDb
    {
        get => _highDb;
        set { _highDb = Math.Clamp(value, MinGainDb, MaxGainDb); _dirty = true; }
    }

    // ─────────────────────────────────────────────────────────────────────

    public NeuralMixEqSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // Rebuild filters on the audio thread whenever a gain changed
        if (_dirty)
        {
            RebuildFilters();
            _dirty = false;
        }

        // All bands flat → skip processing
        if (IsFlat()) return read;

        bool isStereo = WaveFormat.Channels == 2;

        for (int i = offset; i < offset + read;)
        {
            if (isStereo)
            {
                float l = buffer[i];
                float r = buffer[i + 1];

                l = _lowL!.Transform(l);   r = _lowR!.Transform(r);
                l = _midL!.Transform(l);   r = _midR!.Transform(r);
                l = _highL!.Transform(l);  r = _highR!.Transform(r);

                buffer[i]     = l;
                buffer[i + 1] = r;
                i += 2;
            }
            else
            {
                float s = buffer[i];
                s = _lowL!.Transform(s);
                s = _midL!.Transform(s);
                s = _highL!.Transform(s);
                buffer[i] = s;
                i++;
            }
        }

        return read;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void RebuildFilters()
    {
        float sr = WaveFormat.SampleRate;

        _lowL  = BiQuadFilter.LowShelf(sr, LowShelfFreq,  ShelfSlope, _lowDb);
        _midL  = BiQuadFilter.PeakingEQ(sr, MidPeakFreq,  MidQ,       _midDb);
        _highL = BiQuadFilter.HighShelf(sr, HighShelfFreq, ShelfSlope, _highDb);

        if (WaveFormat.Channels == 2)
        {
            _lowR  = BiQuadFilter.LowShelf(sr, LowShelfFreq,  ShelfSlope, _lowDb);
            _midR  = BiQuadFilter.PeakingEQ(sr, MidPeakFreq,  MidQ,       _midDb);
            _highR = BiQuadFilter.HighShelf(sr, HighShelfFreq, ShelfSlope, _highDb);
        }
    }

    private bool IsFlat() =>
        Math.Abs(_lowDb) < 0.05f && Math.Abs(_midDb) < 0.05f && Math.Abs(_highDb) < 0.05f;
}
