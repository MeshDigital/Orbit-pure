namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// Defines the 10+ visualizer presets available in the ORBIT Pure Entertainment engine.
/// Each preset corresponds to a unique SkiaSharp rendering mode in OrbitVisualizerCanvas.
/// </summary>
public enum VisualizerPreset
{
    /// <summary>Classic spectrum bar chart — the WMP-2000 homage.</summary>
    SpectrumBars,

    /// <summary>Circular waveform ring that pulses with the beat.</summary>
    CircularWave,

    /// <summary>Neon particle system reacting to audio energy.</summary>
    NeonParticles,

    /// <summary>Oscilloscope-style waveform of the live audio signal.</summary>
    Oscilloscope,

    /// <summary>Radial frequency spokes emanating from center.</summary>
    StarBurst,

    /// <summary>Flowing plasma/lava-lamp gradient mesh driven by spectrum.</summary>
    PlasmaMesh,

    /// <summary>Aurora borealis-inspired vertical ribbons.</summary>
    AuroraBands,

    /// <summary>Spectrogram waterfall — frequency vs. time heatmap.</summary>
    Waterfall,

    /// <summary>Stereo goniometer / Lissajous phase scope.</summary>
    PhaseScope,

    /// <summary>Matrix-rain style columns driven by spectrum bins.</summary>
    DigitalRain,

    /// <summary>Reflective ripple pool reacting to beats.</summary>
    RipplePool,

    /// <summary>Soft, slow‑breathing ambient gradient (used in Ambient Mode).</summary>
    AmbientBreath,
}

/// <summary>
/// Represents which high-level mode the visualizer is operating in,
/// affecting preset selection and rendering intensity.
/// </summary>
public enum VisualizerEngineMode
{
    /// <summary>Standard beat-reactive visualizer following the current preset.</summary>
    Standard,

    /// <summary>Metadata-driven mode — preset and intensity adapt to track metadata.</summary>
    MetadataDriven,

    /// <summary>Ambient mode — slow, meditative, minimal motion.</summary>
    Ambient,
}
