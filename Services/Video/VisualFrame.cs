using SkiaSharp;

namespace SLSKDONET.Services.Video;

/// <summary>
/// Per-frame audio feature snapshot consumed by <see cref="VisualEngine"/>.
/// All values are normalised to [0, 1] unless noted.
/// </summary>
public sealed class VisualFrame
{
    /// <summary>Fraction of track played (0 = start, 1 = end).</summary>
    public float Progress { get; init; }

    /// <summary>Instantaneous overall energy (RMS-derived).</summary>
    public float Energy { get; init; }

    /// <summary>
    /// Per-band spectral magnitudes.
    /// Index 0 = sub-bass (20-60 Hz), 1 = bass (60-250 Hz),
    /// 2 = low-mid (250-500 Hz), 3 = mid (500-2 kHz),
    /// 4 = high-mid (2-6 kHz), 5 = treble (6-20 kHz).
    /// </summary>
    public float[] FrequencyBands { get; init; } = new float[6];

    /// <summary>Beat pulse intensity (0 = no beat, 1 = strong beat).</summary>
    public float BeatPulse { get; init; }

    /// <summary>BPM of the current segment (may interpolate across transitions).</summary>
    public float Bpm { get; init; }
}

/// <summary>Visual primitive types supported by the engine.</summary>
public enum VisualPreset
{
    Bars,
    Circles,
    Waveform,
    Particles,
    /// <summary>
    /// Task 8.5 — Renders a Shadertoy-compatible GLSL fragment shader
    /// compiled via SkiaSharp's <see cref="SkiaSharp.SKRuntimeEffect"/> (SkSL).
    /// Load the shader source with <see cref="VisualEngine.LoadGlslShader"/>.
    /// </summary>
    CustomGlsl,
}
