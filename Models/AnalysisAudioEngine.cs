using System;

namespace SLSKDONET.Models;

/// <summary>
/// Which backend produces BPM, key and the beat grid during analysis. This is the "audio analysis
/// profile" selectable from Settings and from the Analysis page.
/// </summary>
public enum AnalysisAudioEngine
{
    /// <summary>The external <c>essentia_streaming_extractor_music</c> CLI (the long-standing default).</summary>
    Essentia = 0,

    /// <summary>In-process NWaves/NAudio DSP — no external binary, no temp-WAV round trip.</summary>
    InProcess = 1,
}

public static class AnalysisAudioEngineExtensions
{
    /// <summary>Parses the persisted config string; anything unrecognised falls back to Essentia.</summary>
    public static AnalysisAudioEngine ParseAudioEngine(string? value)
        => string.Equals(value, nameof(AnalysisAudioEngine.InProcess), StringComparison.OrdinalIgnoreCase)
            ? AnalysisAudioEngine.InProcess
            : AnalysisAudioEngine.Essentia;
}
