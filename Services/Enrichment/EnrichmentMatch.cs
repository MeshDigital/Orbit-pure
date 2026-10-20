using System;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Enrichment;

/// <summary>
/// Strict "is this search hit the same recording?" score for text searches (an ISRC lookup needs
/// none). Artist and title are compared after the Discover normaliser (feat. and "Original Mix"
/// removed, remix tags kept, so "X (VIP)" never matches "X"); the duration, when both sides know
/// it, must agree within <see cref="MaxDurationDelta"/> seconds.
/// </summary>
public static class EnrichmentMatch
{
    public const double AcceptScore = 0.85;
    public const double MaxDurationDelta = 10;

    /// <summary>0..1; 0 when the duration rules it out.</summary>
    public static double Score(string? wantArtist, string? wantTitle, double? wantSeconds, string? gotArtist, string? gotTitle, double? gotSeconds)
    {
        if (string.IsNullOrWhiteSpace(wantTitle) || string.IsNullOrWhiteSpace(gotTitle)) return 0;

        double title = Similarity(PlaylistDiscoveryService.NormalizeTitle(wantTitle), PlaylistDiscoveryService.NormalizeTitle(gotTitle));
        double artist = ArtistSimilarity(wantArtist, gotArtist);

        double duration = 0.5; // unknown: neutral
        if (wantSeconds is > 0 && gotSeconds is > 0)
        {
            var delta = Math.Abs(wantSeconds.Value - gotSeconds.Value);
            if (delta > MaxDurationDelta) return 0;
            duration = delta <= 3 ? 1.0 : 1.0 - (delta - 3) / (MaxDurationDelta - 3);
        }

        if (title < 0.8 || artist < 0.7) return Math.Min(title, artist) * 0.5;
        return 0.45 * title + 0.45 * artist + 0.10 * duration;
    }

    public static bool Accept(double score) => score >= AcceptScore;

    private static double ArtistSimilarity(string? want, string? got)
    {
        if (EnrichmentTrack.IsPlaceholderArtist(want)) return 0.75; // nothing to compare; title must carry it
        if (string.IsNullOrWhiteSpace(got)) return 0;
        var w = PlaylistDiscoveryService.NormalizeArtist(PlaylistDiscoveryService.PrimaryArtist(want));
        var g = PlaylistDiscoveryService.NormalizeArtist(PlaylistDiscoveryService.PrimaryArtist(got));
        if (w.Length > 0 && g.Length > 0 && (w == g || w.Contains(g) || g.Contains(w))) return 1.0;
        // Full credit strings ("A & B" vs "B, A") may still overlap.
        var wf = PlaylistDiscoveryService.NormalizeArtist(want);
        var gf = PlaylistDiscoveryService.NormalizeArtist(got);
        if (wf.Length > 0 && gf.Length > 0 && (wf.Contains(g) || gf.Contains(w))) return 0.95;
        return Similarity(w, g);
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;
        return StringDistanceUtils.GetNormalizedMatchScore(a, b);
    }
}
