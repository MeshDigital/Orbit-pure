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
            VibePreset.SilkySmooth => "Follows the Camelot wheel for seamless harmonic blends",
            VibePreset.ClubBanger => "Chases the energy target — high-impact picks over subtle blends",
            VibePreset.RadioPerfect => "Balanced key and tempo, with gentle energy moves",
            VibePreset.GenreBender => "Tempo first and genre-blind — jumps between styles",
            _ => string.Empty
        };
    }
}
