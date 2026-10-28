using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// One selectable Flow weighting, surfaced in the Now Playing card so the user can see (and change)
/// what Flow is optimising for instead of guessing.
/// </summary>
public sealed record FlowPresetOption(VibePreset Preset, string Name, string Description)
{
    /// <summary>The four presets, in the order they are offered.</summary>
    public static IReadOnlyList<FlowPresetOption> All { get; } =
        new[]
        {
            VibePreset.SilkySmooth,
            VibePreset.ClubBanger,
            VibePreset.RadioPerfect,
            VibePreset.GenreBender,
        }.Select(p => new FlowPresetOption(p, p.ToDisplayName(), p.ToDescription())).ToList();

    public static FlowPresetOption For(VibePreset preset) =>
        All.FirstOrDefault(o => o.Preset == preset) ?? All[0];
}
