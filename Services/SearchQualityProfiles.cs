using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Services;

public enum SearchQualityProfileKind
{
    NonStrict,
    Strict,
    Stricter,
    Custom
}

public sealed record SearchQualityProfileDefinition(
    SearchQualityProfileKind Kind,
    string[] Formats,
    int LossyMinBitrateKbps,
    int MaxBitrateKbps,
    int SearchResponseLimit,
    int SearchFileLimit,
    int MaxPeerQueueLength,
    bool LosslessOnly);

/// <summary>
/// Single source of truth for quality profiles and the verified-lossless bitrate rule
/// (Epic #543 / #545 SQ1). Shared by Settings, Downloads, SearchFilterPolicy, and DownloadManager
/// so every page and filter agrees on what Non-strict, Strict, Stricter, and Custom enforce.
/// </summary>
public static class SearchQualityProfiles
{
    /// <summary>
    /// Lossless bitrate floor (kbps): real 16-bit/44.1 kHz FLACs of quiet/acoustic material
    /// commonly report 401–700 kbps. Reported bitrates below 400 kbps on lossless extensions are
    /// likely transcodes; 0 (unreported) is allowed.
    /// </summary>
    public const int LosslessBitrateFloorKbps = 400;

    /// <summary>Legacy Stricter bitrate sentinel written by older ORBIT versions.</summary>
    public const int LegacyStricterBitrateSentinel = 701;

    public static readonly SearchQualityProfileDefinition NonStrict = new(
        SearchQualityProfileKind.NonStrict,
        "flac,wav,aiff,aif,mp3".Split(','),
        LossyMinBitrateKbps: 192,
        MaxBitrateKbps: 0,
        SearchResponseLimit: 300,
        SearchFileLimit: 300,
        MaxPeerQueueLength: 200,
        LosslessOnly: false);

    public static readonly SearchQualityProfileDefinition Strict = new(
        SearchQualityProfileKind.Strict,
        "flac,wav,aiff,aif".Split(','),
        LossyMinBitrateKbps: 320,
        MaxBitrateKbps: 0,
        SearchResponseLimit: 200,
        SearchFileLimit: 200,
        MaxPeerQueueLength: 120,
        LosslessOnly: true);

    public static readonly SearchQualityProfileDefinition Stricter = new(
        SearchQualityProfileKind.Stricter,
        "flac".Split(','),
        LossyMinBitrateKbps: 320,
        MaxBitrateKbps: 0,
        SearchResponseLimit: 100,
        SearchFileLimit: 100,
        MaxPeerQueueLength: 50,
        LosslessOnly: true);

    /// <summary>
    /// Evaluates whether a candidate's reported bitrate passes the single quality rule:
    /// lossless extensions use the verified-lossless floor (>400 kbps or unreported), never a
    /// 701 kbps hard cutoff; lossy extensions use the configured lossy floor.
    /// </summary>
    public static bool IsBitrateAcceptable(string? extension, int reportedBitrateKbps, int? lossyMinBitrateKbps)
    {
        if (reportedBitrateKbps <= 0)
            return true;

        var normalizedExt = (extension ?? string.Empty).Trim().TrimStart('.');
        if (AudioFormats.IsLossless(normalizedExt))
            return reportedBitrateKbps >= LosslessBitrateFloorKbps;

        if (lossyMinBitrateKbps is > 0)
            return reportedBitrateKbps >= lossyMinBitrateKbps.Value;

        return true;
    }

    public static void Apply(AppConfig config, SearchQualityProfileKind kind)
    {
        ArgumentNullException.ThrowIfNull(config);

        var def = kind switch
        {
            SearchQualityProfileKind.Stricter => Stricter,
            SearchQualityProfileKind.Strict => Strict,
            SearchQualityProfileKind.NonStrict => NonStrict,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Cannot apply Custom as a preset.")
        };

        config.PreferredFormats = def.Formats.ToList();
        config.PreferredMinBitrate = def.LossyMinBitrateKbps;
        config.PreferredMaxBitrate = def.MaxBitrateKbps;
        config.SearchResponseLimit = def.SearchResponseLimit;
        config.SearchFileLimit = def.SearchFileLimit;
        config.MaxPeerQueueLength = def.MaxPeerQueueLength;
    }

    public static SearchQualityProfileKind Detect(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var formats = NormalizeFormats(config.PreferredFormats);

        // Migrate legacy 701 kbps sentinel transparently when detecting Stricter.
        if (formats.Count == 1
            && formats.Contains("flac")
            && config.SearchResponseLimit <= Stricter.SearchResponseLimit
            && config.SearchFileLimit <= Stricter.SearchFileLimit
            && config.MaxPeerQueueLength <= Stricter.MaxPeerQueueLength)
        {
            return SearchQualityProfileKind.Stricter;
        }

        if (formats.SetEquals(Strict.Formats)
            && config.SearchResponseLimit == Strict.SearchResponseLimit
            && config.SearchFileLimit == Strict.SearchFileLimit)
        {
            return SearchQualityProfileKind.Strict;
        }

        if (formats.SetEquals(NonStrict.Formats)
            && config.PreferredMinBitrate <= NonStrict.LossyMinBitrateKbps
            && config.SearchResponseLimit >= NonStrict.SearchResponseLimit
            && config.SearchFileLimit >= NonStrict.SearchFileLimit)
        {
            return SearchQualityProfileKind.NonStrict;
        }

        return SearchQualityProfileKind.Custom;
    }

    public static string Describe(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Detect(config) switch
        {
            SearchQualityProfileKind.Stricter =>
                $"STRICTER · Verified lossless (FLAC only, ≥{LosslessBitrateFloorKbps} kbps) · max queue {config.MaxPeerQueueLength}",
            SearchQualityProfileKind.Strict =>
                $"STRICT · Verified lossless (FLAC/WAV/AIFF/AIF, ≥{LosslessBitrateFloorKbps} kbps) · max queue {config.MaxPeerQueueLength}",
            SearchQualityProfileKind.NonStrict =>
                $"NON-STRICT · Lossless + MP3 (lossy ≥{config.PreferredMinBitrate} kbps) · max queue {config.MaxPeerQueueLength}",
            _ =>
                $"CUSTOM · {string.Join("/", NormalizeFormats(config.PreferredFormats).Select(f => f.ToUpperInvariant()))} · lossy ≥{config.PreferredMinBitrate} kbps"
        };
    }

    public static HashSet<string> NormalizeFormats(IEnumerable<string>? formats) =>
        (formats ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
