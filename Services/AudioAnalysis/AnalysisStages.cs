namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// The fixed, ordered stages a single track passes through during analysis. The pipeline reports
/// progress by step label; this maps that label to a stable "Stage N of M" so the UI can show how
/// far through the run it is, not just a percentage that can sit still during a long native step.
/// </summary>
public static class AnalysisStages
{
    public static readonly string[] Ordered =
    {
        "Checking integrity",
        "Decoding audio",
        "Extracting waveform",
        "Measuring loudness & energy",
        "AI genre/mood",
        "BPM / key / grid",
        "Detecting structure & cues",
        "Saving & fingerprint",
    };

    public static int Count => Ordered.Length;

    /// <summary>
    /// Maps a pipeline step's label to its 1-based stage. Unknown labels map to stage 1 so a
    /// "Stage 1 of N" is never out of range.
    /// </summary>
    public static (int Index, int Count, string Label) Describe(string? step)
    {
        var key = (step ?? string.Empty).ToLowerInvariant();
        var index = key switch
        {
            _ when key.Contains("integrity") || key.Contains("checking") => 0,
            _ when key.Contains("decod") => 1,
            _ when key.Contains("waveform") => 2,
            _ when key.Contains("loudness") || key.Contains("energy") => 3,
            _ when key.Contains("genre") || key.Contains("mood") || key.Contains("ai ") => 4,
            _ when key.Contains("bpm") || key.Contains("key") || key.Contains("grid") || key.Contains("essentia") => 5,
            _ when key.Contains("structure") || key.Contains("cue") || key.Contains("segment") => 6,
            _ when key.Contains("sav") || key.Contains("fingerprint") || key.Contains("done") || key.Contains("complet") => 7,
            _ => 0,
        };

        return (index + 1, Ordered.Length, Ordered[index]);
    }
}
