using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>The identity tags of one track, as the detector sees them.</summary>
public sealed record TagIdentity(string Hash, string? Artist, string? Title, string? Album, string? Label, string? FilePath);

/// <summary>Normalised known-artist and known-label sets, built once per scan.</summary>
public sealed record LibraryIdentityIndex(IReadOnlySet<string> KnownArtists, IReadOnlySet<string> KnownLabels);

/// <summary>The detector rules, in application order.</summary>
public enum TagIssueRule
{
    CombinedArtistTitle,
    TitleEqualsFilename,
    SwappedFields,
    LabelInArtist,
    LeadingTrackNumber,
    Underscores,
    JunkSuffixes,
    FeatInTitle,
    AllCaps,
}

public enum ProposalConfidence { NeedsReview, Medium, High }

/// <summary>Toggleable detector options. The two reversible rules default off.</summary>
public sealed record TagIssueOptions(bool EnableFeatInTitle = false, bool EnableAllCaps = false);

/// <summary>
/// A proposed tag change for one track, produced by the detector / genre-fill and applied (after
/// review) by <see cref="TagChangeService"/>. <see cref="Rule"/> is the rule name — a
/// <see cref="TagIssueRule"/> value, or "GenreFill" / "UseAsGenre" for the style flows.
/// </summary>
public sealed record ProposedTagChange(
    string Hash,
    string Rule,
    IReadOnlyList<TagPatchField> Changes,
    ProposalConfidence Confidence,
    string Evidence,
    bool DefaultSelected,
    string? FilePath = null)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Pure, deterministic detector that turns a track's identity tags into *proposed* changes. No I/O,
/// no DB. The wizard (P4) shows the proposals for review; P1's <see cref="TagChangeService"/> applies
/// them. Every rule preserves DJ convention: remix/version text stays in the title, and a split never
/// breaks hyphenated names or splits "Artist - Title - Remix" into three parts.
/// </summary>
public static class TagIssueDetector
{
    /// <summary>Video/upload junk the filename normaliser keeps: "(Official Video)", "(Official Audio)",
    /// "(Lyrics)", "[HD]", "(Visualizer)" … Remix/version text is never matched.</summary>
    private static readonly System.Text.RegularExpressions.Regex VideoJunk = new(
        @"\s*[\(\[](?:official\s+(?:music\s+)?(?:video|audio|visuali[sz]er|lyric\s+video)|lyrics?(?:\s+video)?|visuali[sz]er|hd|hq|4k)[\)\]]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Regex SpacedDash = new(@"\s[-–—]\s", RegexOptions.Compiled);
    private static readonly Regex LeadingTrackNumberRegex = new(@"^\d{1,3}\s*[-._)]\s+", RegexOptions.Compiled);
    private static readonly Regex FeatInTitleRegex = new(@"\b(feat\.?|ft\.?|featuring)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LabelSuffixes = { "Records", "Recordings", "Music", "Digital", "Audio", "Label" };
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "DJ", "MC", "VIP", "UK", "USA", "US", "EU", "EP", "LP", "CD", "TV", "RMX", "II", "III", "IV", "XL", "XXL",
    };

    public static IReadOnlyList<ProposedTagChange> Detect(TagIdentity track, LibraryIdentityIndex index, TagIssueOptions options)
    {
        var originalArtist = (track.Artist ?? string.Empty).Trim();
        var originalTitle = (track.Title ?? string.Empty).Trim();
        var originalLabel = (track.Label ?? string.Empty).Trim();

        var artist = originalArtist;
        var title = originalTitle;
        var label = originalLabel;

        var fired = new List<TagIssueRule>();
        var confidence = ProposalConfidence.NeedsReview;

        var fileName = string.IsNullOrWhiteSpace(track.FilePath) ? null : Path.GetFileNameWithoutExtension(track.FilePath);
        var titleLooksLikeFileName = TitleLooksLikeFileName(originalTitle, track.FilePath);

        void Raise(TagIssueRule rule, ProposalConfidence c)
        {
            if (!fired.Contains(rule)) fired.Add(rule);
            if (c > confidence) confidence = c;
        }

        // ── 1. Leading track number ───────────────────────────────────────────
        var stripped = StripLeadingTrackNumber(title);
        if (!string.Equals(stripped, title, StringComparison.Ordinal))
        {
            title = stripped;
            Raise(TagIssueRule.LeadingTrackNumber, ProposalConfidence.High);
        }

        // ── 2. Underscores (when the field has no real spaces) ────────────────
        if (HasUnderscoresNoSpaces(title))
        {
            var t = title.Replace('_', ' ');
            if (!string.Equals(t, title, StringComparison.Ordinal))
            {
                title = t;
                Raise(TagIssueRule.Underscores, ProposalConfidence.High);
            }
        }
        if (HasUnderscoresNoSpaces(artist))
        {
            var a = artist.Replace('_', ' ');
            if (!string.Equals(a, artist, StringComparison.Ordinal))
            {
                artist = a;
                Raise(TagIssueRule.Underscores, ProposalConfidence.High);
            }
        }

        // ── 3. Junk suffixes / uploader tags (title only) ─────────────────────
        var junkStripped = VideoJunk.Replace(FilenameNormalizer.Normalize(title) ?? title, "").Trim();
        if (!string.IsNullOrWhiteSpace(junkStripped) && !string.Equals(junkStripped, title, StringComparison.Ordinal))
        {
            title = junkStripped;
            Raise(TagIssueRule.JunkSuffixes, ProposalConfidence.High);
        }

        // ── 4. Title equals filename → parse the filename stem ────────────────
        var filenameSplit = fileName is null ? null : SplitFilename(fileName);
        if (titleLooksLikeFileName && filenameSplit is { } fn)
        {
            artist = fn.Artist;
            title = fn.Title;
            Raise(TagIssueRule.TitleEqualsFilename,
                IsKnownArtist(fn.Artist, index) ? ProposalConfidence.High : ProposalConfidence.Medium);
        }

        // ── 5. Combined artist+title in the title field ───────────────────────
        if (IsPlaceholder(artist) && HasSpacedDash(title) && TrySplitDash(title, out var combined))
        {
            var agreesWithFilename = filenameSplit is { } f && string.Equals(f.Artist, combined.Artist, StringComparison.OrdinalIgnoreCase);
            artist = combined.Artist;
            title = combined.Title;
            Raise(TagIssueRule.CombinedArtistTitle,
                (IsKnownArtist(combined.Artist, index) || agreesWithFilename) ? ProposalConfidence.High : ProposalConfidence.Medium);
        }

        // ── 6. Swapped fields ─────────────────────────────────────────────────
        var swappedByKnown = !string.IsNullOrWhiteSpace(title)
            && index.KnownArtists.Contains(NormalizeName(title))
            && !index.KnownArtists.Contains(NormalizeName(artist))
            && !IsPlaceholder(artist);
        var swappedByFilename = filenameSplit is { } fs
            && string.Equals(fs.Artist, title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fs.Title, artist, StringComparison.OrdinalIgnoreCase);
        if (swappedByKnown || swappedByFilename)
        {
            (artist, title) = (title, artist);
            Raise(TagIssueRule.SwappedFields, swappedByFilename ? ProposalConfidence.High : ProposalConfidence.NeedsReview);
        }

        // ── 7. Label stored in the artist field ───────────────────────────────
        if (IsLabelLike(artist, index))
        {
            var realArtist = filenameSplit?.Artist ?? (IsKnownArtist(title, index) ? title : null);
            if (!string.IsNullOrWhiteSpace(realArtist) && !IsPlaceholder(realArtist))
            {
                var oldArtist = artist;
                artist = realArtist;
                if (string.IsNullOrWhiteSpace(label)) label = oldArtist;
                Raise(TagIssueRule.LabelInArtist, ProposalConfidence.Medium);
            }
            else
            {
                // Needs review; no change can be derived safely.
                fired.Add(TagIssueRule.LabelInArtist);
                if (confidence < ProposalConfidence.NeedsReview) confidence = ProposalConfidence.NeedsReview;
            }
        }

        // ── 8. Feat in title (off by default) ─────────────────────────────────
        if (options.EnableFeatInTitle && FeatInTitleRegex.IsMatch(title) && !string.IsNullOrWhiteSpace(artist))
        {
            var moved = MoveFeatToArtist(artist, title);
            if (moved is { } m)
            {
                artist = m.Artist;
                title = m.Title;
                Raise(TagIssueRule.FeatInTitle, ProposalConfidence.Medium);
            }
        }

        // ── 9. All caps (off by default) ──────────────────────────────────────
        if (options.EnableAllCaps)
        {
            var tc = ToTitleCase(title);
            if (!string.Equals(tc, title, StringComparison.Ordinal))
            {
                title = tc;
                Raise(TagIssueRule.AllCaps, ProposalConfidence.Medium);
            }
            var ac = ToTitleCase(artist);
            if (!string.Equals(ac, artist, StringComparison.Ordinal))
            {
                artist = ac;
                Raise(TagIssueRule.AllCaps, ProposalConfidence.Medium);
            }
        }

        // ── Build the (merged) proposal ───────────────────────────────────────
        if (fired.Count == 0)
            return Array.Empty<ProposedTagChange>();

        var changes = new List<TagPatchField>();
        if (!string.Equals(artist, originalArtist, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Artist, artist));
        if (!string.Equals(title, originalTitle, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Title, title));
        if (!string.Equals(label, originalLabel, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Label, label));

        var primaryRule = fired[0];
        var evidence = string.Join(" + ", fired.Select(f => f.ToString()));

        return new[]
        {
            new ProposedTagChange(
                Hash: track.Hash,
                Rule: primaryRule.ToString(),
                Changes: changes,
                Confidence: confidence,
                Evidence: evidence,
                DefaultSelected: confidence == ProposalConfidence.High,
                FilePath: track.FilePath),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TitleLooksLikeFileName(string? title, string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) &&
        string.Equals(title?.Trim(), Path.GetFileNameWithoutExtension(filePath), StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaceholder(string artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "Unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "VA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "Various Artists", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownArtist(string artist, LibraryIdentityIndex index) =>
        !string.IsNullOrWhiteSpace(artist) && index.KnownArtists.Contains(NormalizeName(artist));

    private static bool IsLabelLike(string artist, LibraryIdentityIndex index)
    {
        if (string.IsNullOrWhiteSpace(artist) || IsPlaceholder(artist)) return false;
        if (index.KnownLabels.Contains(NormalizeName(artist))) return true;
        return LabelSuffixes.Any(s =>
            artist.EndsWith(s, StringComparison.OrdinalIgnoreCase) && artist.Length > s.Length + 1);
    }

    private static string NormalizeName(string? s) => PlaylistDiscoveryService.NormalizeArtist(s);

    private static bool HasSpacedDash(string s) => SpacedDash.IsMatch(s);

    private static bool TrySplitDash(string s, out (string Artist, string Title) result)
    {
        result = default;
        var match = SpacedDash.Match(s);
        if (!match.Success) return false;
        var artist = s[..match.Index].Trim();
        var title = s[(match.Index + match.Length)..].Trim();
        if (artist.Length == 0 || title.Length == 0) return false;
        result = (artist, title);
        return true;
    }

    private static string StripLeadingTrackNumber(string s) =>
        LeadingTrackNumberRegex.IsMatch(s) ? LeadingTrackNumberRegex.Replace(s, string.Empty, 1) : s;

    private static bool HasUnderscoresNoSpaces(string s) => s.Contains('_') && !s.Contains(' ');

    private static (string Artist, string Title)? SplitFilename(string fileName)
    {
        var cleaned = FilenameNormalizer.Normalize(fileName);
        cleaned = StripLeadingTrackNumber(cleaned);
        return TrySplitDash(cleaned, out var r) ? r : null;
    }

    private static (string Artist, string Title)? MoveFeatToArtist(string artist, string title)
    {
        var match = FeatInTitleRegex.Match(title);
        if (!match.Success) return null;

        var featStart = match.Index;
        var baseTitle = title[..featStart].TrimEnd(' ', '-', '–', '—', '(', '[', ',');
        var featPart = title[featStart..].Trim();

        // Keep any trailing qualifier like "(Original Mix)" attached to the title.
        var tailMatch = Regex.Match(featPart, @"[\(\[][^\)\]]*[\)\]]\s*$");
        string tail = tailMatch.Success ? tailMatch.Value : string.Empty;
        var featName = (tailMatch.Success ? featPart[..tailMatch.Index] : featPart).Trim().TrimEnd(',', ';');

        if (string.IsNullOrWhiteSpace(featName)) return null;

        var newArtist = string.Equals(artist, featName, StringComparison.OrdinalIgnoreCase)
            ? artist
            : $"{artist} feat. {featName}";
        return (newArtist, $"{baseTitle} {tail}".Trim());
    }

    private static string ToTitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || !IsAllCaps(s)) return s;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (Acronyms.Contains(w)) { words[i] = w; continue; }
            if (w.Length > 3)
                words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    private static bool IsAllCaps(string s)
    {
        var hasLetter = false;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                if (!char.IsUpper(ch)) return false;
            }
        }
        return hasLetter;
    }
}
