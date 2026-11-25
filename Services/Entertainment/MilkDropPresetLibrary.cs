using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Curated starter pack of MilkDrop (<c>.milk</c>) presets plus automatic discovery of user-dropped
/// <c>.milk</c> files in <see cref="GetUserPresetsDirectory"/> (#426).
/// </summary>
public static class MilkDropPresetLibrary
{
    private static readonly (string Name, string Milk)[] BuiltInDefinitions =
    {
        ("Geiss Classic", "[preset00]\nfDecay=0.97\nzoom=1.03\nrot=0.015\nwarp=0.35\nnWaveMode=0\nfWaveAlpha=0.9\nfWaveScale=1.15\nwave_r=0.18\nwave_g=0.88\nwave_b=1.00\nbAdditiveWaves=1\nfVideoEchoZoom=1.06\nfVideoEchoAlpha=0.45\n"),
        ("Rovastar Solarize", "[preset00]\nfDecay=0.96\nzoom=1.04\nrot=-0.02\nwarp=0.55\nnWaveMode=1\nfWaveAlpha=0.92\nfWaveScale=1.3\nwave_r=1.00\nwave_g=0.58\nwave_b=0.16\nbAdditiveWaves=1\nfVideoEchoZoom=1.08\nfVideoEchoAlpha=0.5\nob_size=0.02\nob_r=0.95\nob_g=0.35\nob_b=0.10\nob_a=0.45\n"),
        ("Flexi Neon Tunnel", "[preset00]\nfDecay=0.98\nzoom=1.06\nrot=0.025\nwarp=0.45\nnWaveMode=2\nfWaveAlpha=0.88\nfWaveScale=1.2\nwave_r=0.85\nwave_g=0.22\nwave_b=0.98\nbAdditiveWaves=1\nfVideoEchoZoom=1.10\nfVideoEchoAlpha=0.4\n"),
        ("Martin Deep Space", "[preset00]\nfDecay=0.985\nzoom=1.015\nrot=0.006\nwarp=0.20\nnWaveMode=3\nfWaveAlpha=0.80\nfWaveScale=1.0\nwave_r=0.35\nwave_g=0.65\nwave_b=1.00\nbWaveDots=1\nbAdditiveWaves=1\nfVideoEchoZoom=1.04\nfVideoEchoAlpha=0.35\n"),
        ("Aderrasi Prism", "[preset00]\nfDecay=0.95\nzoom=0.98\nrot=-0.03\nwarp=0.60\nnWaveMode=4\nfWaveAlpha=0.90\nfWaveScale=1.4\nwave_r=0.20\nwave_g=0.98\nwave_b=0.68\nbAdditiveWaves=1\nfVideoEchoZoom=1.09\nfVideoEchoAlpha=0.45\n"),
        ("Krash Beat Pulse", "[preset00]\nfDecay=0.94\nzoom=1.05\nrot=0.00\nwarp=0.25\nnWaveMode=5\nfWaveAlpha=0.95\nfWaveScale=1.5\nwave_r=1.00\nwave_g=0.20\nwave_b=0.42\nbAdditiveWaves=1\nfVideoEchoZoom=1.05\nfVideoEchoAlpha=0.5\nib_size=0.015\nib_r=1.0\nib_g=0.2\nib_b=0.4\nib_a=0.35\n"),
        ("Unchained Plasma", "[preset00]\nfDecay=0.975\nzoom=1.02\nrot=0.018\nwarp=0.80\nnWaveMode=6\nfWaveAlpha=0.85\nfWaveScale=1.25\nwave_r=0.65\nwave_g=0.35\nwave_b=1.00\nbAdditiveWaves=1\nfVideoEchoZoom=1.07\nfVideoEchoAlpha=0.4\n"),
        ("Orbit Horizon", "[preset00]\nfDecay=0.965\nzoom=1.025\nrot=0.008\nwarp=0.30\nnWaveMode=7\nfWaveAlpha=0.90\nfWaveScale=1.1\nwave_r=0.13\nwave_g=0.83\nwave_b=0.93\nbAdditiveWaves=1\nfVideoEchoZoom=1.04\nfVideoEchoAlpha=0.4\n"),
        ("Zylot Starfield", "[preset00]\nfDecay=0.98\nzoom=1.07\nrot=-0.012\nwarp=0.15\nnWaveMode=3\nfWaveAlpha=0.88\nfWaveScale=1.35\nwave_r=0.95\nwave_g=0.95\nwave_b=1.00\nbWaveDots=1\nbAdditiveWaves=1\nfVideoEchoZoom=1.12\nfVideoEchoAlpha=0.3\n"),
        ("Shifter Spiral", "[preset00]\nfDecay=0.96\nzoom=1.035\nrot=0.04\nwarp=0.50\nnWaveMode=1\nfWaveAlpha=0.90\nfWaveScale=1.2\nwave_r=0.18\nwave_g=0.96\nwave_b=0.84\nbAdditiveWaves=1\nfVideoEchoZoom=1.06\nfVideoEchoAlpha=0.45\n"),
        ("EoS Liquid Gold", "[preset00]\nfDecay=0.97\nzoom=1.01\nrot=-0.01\nwarp=0.40\nnWaveMode=0\nfWaveAlpha=0.85\nfWaveScale=1.05\nwave_r=1.00\nwave_g=0.82\nwave_b=0.25\nbAdditiveWaves=1\nfVideoEchoZoom=1.05\nfVideoEchoAlpha=0.4\n"),
        ("Bmelgren Cosmic Web", "[preset00]\nfDecay=0.98\nzoom=1.03\nrot=0.022\nwarp=0.65\nnWaveMode=2\nfWaveAlpha=0.92\nfWaveScale=1.3\nwave_r=0.42\nwave_g=0.90\nwave_b=1.00\nbAdditiveWaves=1\nfVideoEchoZoom=1.08\nfVideoEchoAlpha=0.5\n"),
    };

    private static readonly Lazy<IReadOnlyList<MilkDropPreset>> LazyBuiltIn = new(() =>
    {
        var list = new List<MilkDropPreset>(BuiltInDefinitions.Length);
        foreach (var (name, milk) in BuiltInDefinitions)
        {
            if (MilkDropPresetParser.TryParse(name, milk, out var preset, out _) && preset is not null)
                list.Add(preset);
        }
        return list;
    });

    public static IReadOnlyList<MilkDropPreset> BuiltIn => LazyBuiltIn.Value;

    public static string GetUserPresetsDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT",
            "Presets",
            "MilkDrop");

    /// <summary>
    /// Returns all built-in starter presets plus any valid user <c>.milk</c> files found in
    /// <paramref name="userDirectory"/> (defaulting to <see cref="GetUserPresetsDirectory"/>).
    /// Malformed user files are logged and skipped without throwing.
    /// </summary>
    public static IReadOnlyList<MilkDropPreset> LoadAll(string? userDirectory = null, ILogger? logger = null)
    {
        var result = new List<MilkDropPreset>(BuiltIn);
        var dir = userDirectory ?? GetUserPresetsDirectory();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.milk", SearchOption.TopDirectoryOnly))
            {
                if (MilkDropPresetParser.TryLoadFile(file, out var preset, out var error) && preset is not null)
                {
                    result.Add(preset);
                }
                else
                {
                    logger?.LogWarning("Skipping malformed MilkDrop preset '{File}': {Error}", Path.GetFileName(file), error);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not scan user MilkDrop presets directory: {Dir}", dir);
        }

        return result;
    }
}
