using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Wraps NAudio's <see cref="WdlResamplingSampleProvider"/> so it is only ever read into the start
/// of its own buffer. Read straight into a caller's buffer at a non-zero offset (the mix bus reads
/// 256-frame blocks, so every second block lands mid-buffer), NAudio's WDL port corrupts its output:
/// a 44.1 → 48 kHz tone came out at −19 dB THD+N, an audible crackle about 100 times a second,
/// instead of −68 dB. Measured 2026-11-28; this is what made 0.5.2-beta.2 crackle on 48 kHz devices.
/// Always create resamplers through <see cref="Create"/>.
/// </summary>
public sealed class OffsetSafeResampler : ISampleProvider
{
    private readonly ISampleProvider _inner;
    private float[] _scratch = new float[4096];

    public OffsetSafeResampler(ISampleProvider resampler) => _inner = resampler;

    /// <summary>A resampler to <paramref name="sampleRate"/> that is safe to read at any offset.</summary>
    public static ISampleProvider Create(ISampleProvider source, int sampleRate) =>
        new OffsetSafeResampler(new WdlResamplingSampleProvider(source, sampleRate));

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        if (_scratch.Length < count) _scratch = new float[count];
        int read = _inner.Read(_scratch, 0, count);
        // Element-wise, not Array.Copy: NAudio's WaveBuffer hands out byte[] storage typed as float[]
        // (SampleToWaveProvider), which Array.Copy rejects with ArrayTypeMismatchException.
        for (int i = 0; i < read; i++) buffer[offset + i] = _scratch[i];
        return read;
    }
}
