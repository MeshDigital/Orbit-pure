namespace SLSKDONET.Models.Musical
{
    /// <summary>
    /// DJ-friendly presets for flow weighting.
    /// These are the user-facing names; each maps to a FlowWeightSettings configuration.
    /// </summary>
    public enum VibePreset
    {
        /// <summary>Harmonic focus for smooth, seamless transitions.</summary>
        SilkySmooth,

        /// <summary>Energy focus for high-intensity club sets.</summary>
        ClubBanger,

        /// <summary>Vocal safety for radio-friendly sets.</summary>
        RadioPerfect,

        /// <summary>Rhythm/BPM focus for genre-crossing sets.</summary>
        GenreBender
    }

    /// <summary>
    /// Helper class for mapping VibePreset to FlowWeightSettings.
    /// </summary>
    public static class VibePresetHelper
    {
        public static FlowWeightSettings ToSettings(this VibePreset preset) => preset switch
        {
            VibePreset.SilkySmooth => FlowWeightSettings.SmoothBlend,
            VibePreset.ClubBanger => FlowWeightSettings.HighEnergy,
            VibePreset.RadioPerfect => FlowWeightSettings.RadioStyle,
            VibePreset.GenreBender => FlowWeightSettings.GenreBender,
            _ => FlowWeightSettings.SmoothBlend
        };

        public static string ToDisplayName(this VibePreset preset) => preset switch
        {
            VibePreset.SilkySmooth => "Silky Smooth",
            VibePreset.ClubBanger => "Club Banger",
            VibePreset.RadioPerfect => "Radio Perfect",
            VibePreset.GenreBender => "Genre Bender",
            _ => preset.ToString()
        };

        public static string ToDescription(this VibePreset preset) => preset switch
        {
            VibePreset.SilkySmooth => "Prioritizes harmonic compatibility for seamless blends",
            VibePreset.ClubBanger => "Prioritizes energy alignment for high-impact drops",
            VibePreset.RadioPerfect => "Strict vocal protection to prevent lyric clashes",
            VibePreset.GenreBender => "Prioritizes rhythm for genre-crossing creativity",
            _ => string.Empty
        };
    }
}
