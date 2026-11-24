using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SLSKDONET.Services.Library.Files;

/// <summary>The facts a path template can interpolate (#343, LF1).</summary>
public sealed record TrackFacts(
    string? Artist = null,
    string? AlbumArtist = null,
    string? Title = null,
    string? Mix = null,
    string? Album = null,
    int? TrackNumber = null,
    int? Year = null,
    string? Genre = null,
    string? Label = null,
    double? Bpm = null,
    string? BpmBracket = null,
    string? Key = null,
    double? Energy = null,
    string? Format = null,
    int? Bitrate = null,
    string? User = null,
    string? Filename = null,
    int? LengthSeconds = null,
    long? SizeBytes = null);

/// <summary>Rendering options: ASCII-safe (USB) mode and a per-segment length budget.</summary>
public sealed record RenderOptions(bool AsciiSafe = false, int MaxSegmentLength = 255);

/// <summary>A rendered path, split into sanitised segments (the first segments are folders, the last is the leaf).</summary>
public sealed record RenderedPath(IReadOnlyList<string> Segments)
{
    public string Leaf => Segments.Count == 0 ? string.Empty : Segments[^1];
    public string RelativePath => string.Join('/', Segments);
}

/// <summary>
/// One path-template engine for downloads, Bulk Rename, Smart Sorter and Reorganise (#343, LF1).
/// Pure (no I/O) and unit-testable. A template is a mix of literal text, <c>{token}</c> expressions,
/// and optional <c>[group]</c> / <c>(group)</c> blocks that drop themselves when their content is empty.
/// </summary>
public static class PathTemplateEngine
{
    private static readonly char[] InvalidChars = { '?', ':', '*', '<', '>', '|', '"', '/', '\\' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Renders a template into sanitised path segments. The last segment is the leaf (filename sans extension).</summary>
    public static RenderedPath Render(string template, TrackFacts facts, RenderOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return new RenderedPath(Array.Empty<string>());

        options ??= new RenderOptions();

        var rendered = RenderContent(template, facts, options);
        var segments = rendered
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => SanitizeSegment(s, options))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return new RenderedPath(segments);
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    private static string RenderContent(string input, TrackFacts facts, RenderOptions options)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == '{')
            {
                var end = FindClosing(input, i, '{', '}');
                if (end < 0) { sb.Append(c); i++; continue; }
                sb.Append(ResolveToken(input[(i + 1)..end], facts, options));
                i = end + 1;
            }
            else if (c is '[' or '(')
            {
                var close = c == '[' ? ']' : ')';
                var end = FindClosing(input, i, c, close);
                if (end < 0) { sb.Append(c); i++; continue; }
                var resolved = RenderContent(input[(i + 1)..end], facts, options);
                if (HasContent(resolved))
                {
                    sb.Append(c);
                    sb.Append(resolved);
                    sb.Append(close);
                }
                i = end + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return CleanSeparators(sb.ToString());
    }

    private static int FindClosing(string s, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            if (s[i] == open) depth++;
            else if (s[i] == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // ── Token resolution ──────────────────────────────────────────────────────

    private static string ResolveToken(string expr, TrackFacts facts, RenderOptions options)
    {
        var parts = SplitTopLevel(expr.Trim(), '|');
        if (parts.Count > 1)
        {
            foreach (var part in parts)
            {
                var value = ResolveSingle(part.Trim(), facts, options);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return string.Empty;
        }

        return ResolveSingle(expr.Trim(), facts, options);
    }

    private static string ResolveSingle(string expr, TrackFacts facts, RenderOptions options)
    {
        if (string.IsNullOrEmpty(expr)) return string.Empty;

        // Quoted literal fallback: {album|"Singles"}
        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
            return expr[1..^1];

        // Nested token: {artist|{filename}}
        if (expr.StartsWith('{') && expr.EndsWith('}'))
            return ResolveToken(expr[1..^1], facts, options);

        // Legacy conditional literal: {artist( - )} → " - " only when artist is non-empty.
        var conditional = Regex.Match(expr, @"^(\w+)\(([^)]*)\)$");
        if (conditional.Success)
        {
            var value = TokenValue(conditional.Groups[1].Value, facts, options);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value + conditional.Groups[2].Value;
        }

        // Padding: {track:00}
        var colon = expr.IndexOf(':');
        if (colon > 0)
        {
            var name = expr[..colon].Trim();
            var format = expr[(colon + 1)..].Trim();
            var raw = TokenValue(name, facts, options);
            return int.TryParse(raw, out var n) ? n.ToString(format) : raw;
        }

        return TokenValue(expr, facts, options);
    }

    private static string TokenValue(string name, TrackFacts facts, RenderOptions options)
    {
        return name.ToLowerInvariant() switch
        {
            "artist" => facts.Artist ?? string.Empty,
            "albumartist" => facts.AlbumArtist ?? string.Empty,
            "title" => facts.Title ?? string.Empty,
            "mix" or "remix" => facts.Mix ?? string.Empty,
            "album" => facts.Album ?? string.Empty,
            "track" or "tracknumber" => facts.TrackNumber?.ToString() ?? string.Empty,
            "year" => facts.Year?.ToString() ?? string.Empty,
            "genre" => facts.Genre ?? string.Empty,
            "label" => facts.Label ?? string.Empty,
            "bpm" => facts.Bpm?.ToString("0") ?? string.Empty,
            "bpm_bracket" or "bpmbracket" => facts.BpmBracket ?? string.Empty,
            "key" => facts.Key ?? string.Empty,
            "energy" => facts.Energy is { } e ? (e * 100).ToString("0") : string.Empty,
            "format" => facts.Format ?? string.Empty,
            "bitrate" => facts.Bitrate?.ToString() ?? string.Empty,
            "user" or "username" => facts.User ?? string.Empty,
            "filename" => facts.Filename ?? string.Empty,
            "length" => facts.LengthSeconds?.ToString() ?? string.Empty,
            "size" => facts.SizeBytes?.ToString() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static List<string> SplitTopLevel(string s, char separator)
    {
        var result = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuote) { if (c == '"') inQuote = false; continue; }
            if (c == '"') { inQuote = true; continue; }
            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (c == separator && depth == 0) { result.Add(s[start..i]); start = i + 1; }
        }
        result.Add(s[start..]);
        return result;
    }

    // ── Cleanup + sanitisation ────────────────────────────────────────────────

    private static bool HasContent(string s) => s.Any(char.IsLetterOrDigit);

    /// <summary>Removes the artifacts left by dropped optional groups: double spaces/dashes/underscores and
    /// leading/trailing separators, and trims spaces around path separators.</summary>
    private static string CleanSeparators(string s)
    {
        var result = s;
        for (var pass = 0; pass < 4; pass++)
        {
            var before = result;
            result = result
                .Replace("  ", " ")
                .Replace("--", "-")
                .Replace("__", "_")
                .Replace(" -  - ", " - ");
            if (result == before) break;
        }

        result = Regex.Replace(result, @"\s*([/\\])\s*", "$1"); // trim spaces around '/'
        result = result.Trim(' ', '-', '_');
        return result;
    }

    private static string SanitizeSegment(string segment, RenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(segment)) return string.Empty;

        // Unicode NFC first, so diacritic folding and length checks operate on normalised text.
        var result = segment.Normalize(NormalizationForm.FormC);

        if (options.AsciiSafe)
        {
            // Strip combining marks (Tiësto → Tiesto) and fold the remaining non-ASCII to '?'.
            result = new string(result.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray());
            result = result.Normalize(NormalizationForm.FormC);
        }

        foreach (var c in InvalidChars)
            result = result.Replace(c, '_');
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());

        // Dot-only segments are the path-traversal escape ("." / ".."); neutralise them.
        if (result.Length > 0 && result.All(c => c == '.'))
            result = "_";

        result = result.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(result)) return string.Empty;

        if (options.MaxSegmentLength > 0 && result.Length > options.MaxSegmentLength)
            result = result[..options.MaxSegmentLength];

        result = result.TrimEnd(' ', '.');

        var baseName = result.Split('.')[0];
        if (ReservedDeviceNames.Contains(baseName))
            result = "_" + result;

        return result;
    }
}
