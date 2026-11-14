using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Sets;

/// <summary>One event preset: an energy curve over the set's timeline (0–100 per step).</summary>
public sealed record EventPreset(string Key, string Name, EventAudienceType Audience, IReadOnlyList<double> EnergyCurve, string Description);

/// <summary>
/// The simple event presets of rebuild plan P6 (#207): Club, Warm-up, Peak time, Radio. Each is an
/// energy curve the Sets page's Set Health compares the slots' energy targets against. They carry no
/// BPM range on purpose: an absolute range is genre-specific (a DnB warm-up isn't 122 BPM), and
/// the energy curve is the part that means the same in every genre.
/// </summary>
public static class EventPresets
{
    /// <summary>Stored in <see cref="EventProfileEntity.Name"/> as this prefix + the key.</summary>
    public const string NamePrefix = "preset:";

    public static readonly IReadOnlyList<EventPreset> All = new[]
    {
        new EventPreset("club", "Club", EventAudienceType.Club,
            new double[] { 45, 55, 65, 75, 85, 90, 80, 85, 70, 60 }, "Build up, peak in the middle, a breather, land it"),
        new EventPreset("warmup", "Warm-up", EventAudienceType.Warmup,
            new double[] { 30, 35, 40, 45, 50, 55, 55, 60, 60, 65 }, "Low and steady, handing over a room that's ready"),
        new EventPreset("peak", "Peak time", EventAudienceType.Closing,
            new double[] { 70, 80, 85, 90, 95, 90, 95, 90, 85, 80 }, "Start high and stay there"),
        new EventPreset("radio", "Radio", EventAudienceType.Radio,
            new double[] { 55, 60, 65, 65, 70, 70, 65, 70, 65, 60 }, "Even energy that holds a listener"),
    };

    public static EventPreset? ByKey(string? key) =>
        key is null ? null : All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset key of a stored profile name ("preset:club" → "club"), or null.</summary>
    public static string? KeyFromProfileName(string? name) =>
        name is not null && name.StartsWith(NamePrefix, StringComparison.Ordinal) && ByKey(name[NamePrefix.Length..]) is { } p ? p.Key : null;

    /// <summary>
    /// Energy-flow fit (0–100, 100 = the targets follow the curve) over the slots that have an energy
    /// target; each slot is compared with the curve at its position in the set. Null when fewer than
    /// two slots have a target.
    /// </summary>
    public static double? EnergyFlow(IReadOnlyList<double?> slotTargets, IReadOnlyList<double> curve)
    {
        if (curve.Count == 0 || slotTargets.Count == 0) return null;
        var n = slotTargets.Count;
        var squared = new List<double>();
        for (var i = 0; i < n; i++)
        {
            if (slotTargets[i] is not double target) continue;
            var expected = curve[Math.Min(curve.Count - 1, i * curve.Count / n)];
            squared.Add((target - expected) * (target - expected));
        }
        if (squared.Count < 2) return null;
        return Math.Clamp(100.0 - Math.Sqrt(squared.Average()), 0, 100);
    }
}
