using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Immutable, validated snapshot of a MilkDrop (<c>.milk</c>) preset (#426).
/// Every numeric parameter is clamped to safe finite ranges at parse time so untrusted
/// community <c>.milk</c> files can never crash or NaN-poison the visualizer render thread.
/// </summary>
public sealed record MilkDropPreset(
    string Name,
    string? SourcePath,
    float Decay,
    float Zoom,
    float Rot,
    float Cx,
    float Cy,
    float Dx,
    float Dy,
    float Warp,
    int WaveMode,
    float WaveAlpha,
    float WaveScale,
    float WaveR,
    float WaveG,
    float WaveB,
    float WaveX,
    float WaveY,
    bool AdditiveWaves,
    bool WaveDots,
    float VideoEchoZoom,
    float VideoEchoAlpha,
    float OuterBorderSize,
    float OuterBorderR,
    float OuterBorderG,
    float OuterBorderB,
    float OuterBorderA,
    float InnerBorderSize,
    float InnerBorderR,
    float InnerBorderG,
    float InnerBorderB,
    float InnerBorderA,
    int PerFrameEquationCount,
    int PerPixelEquationCount);

/// <summary>
/// Defensive parser for MilkDrop <c>.milk</c> preset files (#426).
/// </summary>
public static class MilkDropPresetParser
{
    public const int MaxFileBytes = 512 * 1024; // 512 KB cap for untrusted .milk files

    public static bool TryLoadFile(string filePath, out MilkDropPreset? preset, out string? error)
    {
        preset = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "Preset file not found.";
                return false;
            }

            var info = new FileInfo(filePath);
            if (info.Length == 0)
            {
                error = "Preset file is empty.";
                return false;
            }
            if (info.Length > MaxFileBytes)
            {
                error = $"Preset file exceeds {MaxFileBytes / 1024} KB limit.";
                return false;
            }

            var content = File.ReadAllText(filePath);
            var name = Path.GetFileNameWithoutExtension(filePath);
            return TryParse(name, content, out preset, out error, filePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParse(
        string name,
        string content,
        out MilkDropPreset? preset,
        out string? error,
        string? sourcePath = null)
    {
        preset = null;
        error = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Preset content is empty.";
            return false;
        }

        // Reject binary garbage (NUL bytes)
        if (content.IndexOf('\0') >= 0)
        {
            error = "Preset contains binary NUL bytes.";
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perFrameCount = 0;
        var perPixelCount = 0;
        var hasHeaderOrKnownKey = false;

        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (line.Contains("preset", StringComparison.OrdinalIgnoreCase))
                    hasHeaderOrKnownKey = true;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key.StartsWith("per_frame_", StringComparison.OrdinalIgnoreCase))
            {
                perFrameCount++;
                hasHeaderOrKnownKey = true;
                continue;
            }
            if (key.StartsWith("per_pixel_", StringComparison.OrdinalIgnoreCase))
            {
                perPixelCount++;
                hasHeaderOrKnownKey = true;
                continue;
            }

            map[key] = val;
            if (IsKnownMilkDropKey(key))
                hasHeaderOrKnownKey = true;
        }

        if (!hasHeaderOrKnownKey)
        {
            error = "No MilkDrop [preset] header or recognized parameters found.";
            return false;
        }

        preset = new MilkDropPreset(
            Name: string.IsNullOrWhiteSpace(name) ? "Untitled MilkDrop" : name.Trim(),
            SourcePath: sourcePath,
            Decay: GetFloat(map, "fDecay", 0.96f, 0.80f, 0.995f),
            Zoom: GetFloat(map, "zoom", 1.02f, 0.85f, 1.15f),
            Rot: GetFloat(map, "rot", 0.01f, -0.25f, 0.25f),
            Cx: GetFloat(map, "cx", 0.5f, 0.0f, 1.0f),
            Cy: GetFloat(map, "cy", 0.5f, 0.0f, 1.0f),
            Dx: GetFloat(map, "dx", 0.0f, -0.2f, 0.2f),
            Dy: GetFloat(map, "dy", 0.0f, -0.2f, 0.2f),
            Warp: GetFloat(map, "warp", 0.2f, 0.0f, 4.0f),
            WaveMode: GetInt(map, "nWaveMode", 0, 0, 7),
            WaveAlpha: GetFloat(map, "fWaveAlpha", 0.85f, 0.05f, 1.0f),
            WaveScale: GetFloat(map, "fWaveScale", 1.0f, 0.1f, 4.0f),
            WaveR: GetFloat(map, "wave_r", 0.2f, 0.0f, 1.0f),
            WaveG: GetFloat(map, "wave_g", 0.85f, 0.0f, 1.0f),
            WaveB: GetFloat(map, "wave_b", 0.95f, 0.0f, 1.0f),
            WaveX: GetFloat(map, "wave_x", 0.5f, 0.0f, 1.0f),
            WaveY: GetFloat(map, "wave_y", 0.5f, 0.0f, 1.0f),
            AdditiveWaves: GetBool(map, "bAdditiveWaves", true),
            WaveDots: GetBool(map, "bWaveDots", false),
            VideoEchoZoom: GetFloat(map, "fVideoEchoZoom", 1.05f, 0.5f, 2.0f),
            VideoEchoAlpha: GetFloat(map, "fVideoEchoAlpha", 0.35f, 0.0f, 1.0f),
            OuterBorderSize: GetFloat(map, "ob_size", 0.01f, 0.0f, 0.2f),
            OuterBorderR: GetFloat(map, "ob_r", 0.0f, 0.0f, 1.0f),
            OuterBorderG: GetFloat(map, "ob_g", 0.0f, 0.0f, 1.0f),
            OuterBorderB: GetFloat(map, "ob_b", 0.0f, 0.0f, 1.0f),
            OuterBorderA: GetFloat(map, "ob_a", 0.0f, 0.0f, 1.0f),
            InnerBorderSize: GetFloat(map, "ib_size", 0.01f, 0.0f, 0.2f),
            InnerBorderR: GetFloat(map, "ib_r", 0.25f, 0.0f, 1.0f),
            InnerBorderG: GetFloat(map, "ib_g", 0.25f, 0.0f, 1.0f),
            InnerBorderB: GetFloat(map, "ib_b", 0.25f, 0.0f, 1.0f),
            InnerBorderA: GetFloat(map, "ib_a", 0.0f, 0.0f, 1.0f),
            PerFrameEquationCount: perFrameCount,
            PerPixelEquationCount: perPixelCount);

        return true;
    }

    private static bool IsKnownMilkDropKey(string key) =>
        key.Equals("fDecay", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("zoom", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rot", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("warp", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("nWaveMode", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("fWaveAlpha", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("wave_r", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("wave_g", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("wave_b", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("fVideoEchoZoom", StringComparison.OrdinalIgnoreCase);

    private static float GetFloat(Dictionary<string, string> map, string key, float fallback, float min, float max)
    {
        if (!map.TryGetValue(key, out var raw)) return fallback;
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static int GetInt(Dictionary<string, string> map, string key, int fallback, int min, int max)
    {
        if (!map.TryGetValue(key, out var raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var raw)) return fallback;
        if (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }
}
