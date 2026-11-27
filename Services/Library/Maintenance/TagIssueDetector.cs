using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// The identity tags of one track, as the detector sees them. <see cref="Remixer"/> is the file's
/// Remixer tag when known; null/empty means "empty or not read" (the DB has no Remixer column).
/// </summary>
public sealed record TagIdentity(string Hash, string? Artist, string? Title, string? Album, string? Label, string? FilePath, string? Remixer = null);

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
    /// <summary>Library genre and a strong audio genre disagree (GA9 #254, <see cref="GenreConflictRule"/>); never fired by the detector itself.</summary>
    GenreConflict,
    /// <summary>LP1 (#552): bracketed or bare web addresses ("[www.site.com]", "(site.net)").</summary>
    Urls,
    /// <summary>LP1 (#552): promo text ("(Free Download)", "[Out Now]", "(Buy on Beatport)", "[PREVIEW]").</summary>
    PromoText,
    /// <summary>LP1 (#552): URL escapes, HTML entities and mojibake ("Ã©"); mojibake is review-only.</summary>
    EncodedText,
    /// <summary>LP1 (#552): control / zero-width characters, repeated separators, stray edge dashes.</summary>
    GarbageChars,
    /// <summary>LP1 (#552): "Title - Extended Mix" / "Title [Original Mix]" → "Title (Extended Mix)".</summary>
    MixParentheses,
    /// <summary>LP1 (#552): copies "Bicep" from "(Bicep Remix)" into an empty Remixer field; the title is kept.</summary>
    RemixerField,
    /// <summary>LP1 (#552): all-lowercase titles/artists to title case (opt-in, like <see cref="AllCaps"/>).</summary>
    Casing,
    /// <summary>LP1 (#552): text every track in a selection shares; selection-level, always NeedsReview.</summary>
    SharedText,
}

public enum ProposalConfidence { NeedsReview, Medium, High }

/// <summary>Toggleable detector options. The reversible casing/feat rules default off.</summary>
public sealed record TagIssueOptions(bool EnableFeatInTitle = false, bool EnableAllCaps = false, bool EnableCasing = false);

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

        // LP1 (#552): per-rule before/after evidence, and a cap that keeps review-only fixes unticked.
        var details = new Dictionary<TagIssueRule, string>();
        var reviewOnly = false;
        string Fix(string value, Func<string, string> fix, TagIssueRule rule, ProposalConfidence c, string field)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var fixedValue = fix(value);
            if (string.IsNullOrWhiteSpace(fixedValue) || string.Equals(fixedValue, value, StringComparison.Ordinal))
                return value;
            Raise(rule, c);
            var note = $"{field} \u201C{value}\u201D \u2192 \u201C{fixedValue}\u201D";
            details[rule] = details.TryGetValue(rule, out var existing) ? $"{existing}; {note}" : note;
            return fixedValue;
        }

        // ── 0. Cleanup pack (LP1 #552): decode, then strip URLs / promo text, then garbage chars ──
        artist = Fix(artist, DecodeEscapes, TagIssueRule.EncodedText, ProposalConfidence.High, "Artist");
        title = Fix(title, DecodeEscapes, TagIssueRule.EncodedText, ProposalConfidence.High, "Title");
        var beforeMojibake = artist + "\u0000" + title;
        artist = Fix(artist, FixMojibake, TagIssueRule.EncodedText, ProposalConfidence.NeedsReview, "Artist");
        title = Fix(title, FixMojibake, TagIssueRule.EncodedText, ProposalConfidence.NeedsReview, "Title");
        if (!string.Equals(beforeMojibake, artist + "\u0000" + title, StringComparison.Ordinal))
            reviewOnly = true; // a mojibake guess is never preselected
        artist = Fix(artist, StripUrls, TagIssueRule.Urls, ProposalConfidence.High, "Artist");
        title = Fix(title, StripUrls, TagIssueRule.Urls, ProposalConfidence.High, "Title");
        title = Fix(title, StripPromo, TagIssueRule.PromoText, ProposalConfidence.High, "Title");
        artist = Fix(artist, CleanGarbage, TagIssueRule.GarbageChars, ProposalConfidence.High, "Artist");
        title = Fix(title, CleanGarbage, TagIssueRule.GarbageChars, ProposalConfidence.High, "Title");

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

        // ── 10. Mix parentheses (LP1 #552): "Title - Extended Mix" / "[Original Mix]" → "(…)" ──
        if (!fired.Contains(TagIssueRule.CombinedArtistTitle))
        {
            title = Fix(title, ToMixParentheses, TagIssueRule.MixParentheses, ProposalConfidence.Medium, "Title");
        }

        // ── 11. Remixer field (LP1 #552): copy, never move — the title keeps its version text ──
        string? remixer = null;
        if (string.IsNullOrWhiteSpace(track.Remixer) && ExtractRemixer(title) is { } rx)
        {
            remixer = rx;
            Raise(TagIssueRule.RemixerField, ProposalConfidence.Medium);
            details[TagIssueRule.RemixerField] = $"Remixer \u2190 \u201C{rx}\u201D (title unchanged)";
        }

        // ── 12. Casing (LP1 #552, off by default): all-lowercase → Title Case ──
        if (options.EnableCasing)
        {
            title = Fix(title, LowerToTitleCase, TagIssueRule.Casing, ProposalConfidence.Medium, "Title");
            artist = Fix(artist, LowerToTitleCase, TagIssueRule.Casing, ProposalConfidence.Medium, "Artist");
        }

        // ── Build the (merged) proposal ───────────────────────────────────────
        if (fired.Count == 0)
            return Array.Empty<ProposedTagChange>();

        if (reviewOnly) confidence = ProposalConfidence.NeedsReview;

        var changes = new List<TagPatchField>();
        if (!string.Equals(artist, originalArtist, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Artist, artist));
        if (!string.Equals(title, originalTitle, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Title, title));
        if (!string.Equals(label, originalLabel, StringComparison.Ordinal))
            changes.Add(new TagPatchField(TagField.Label, label));
        if (remixer is not null)
            changes.Add(new TagPatchField(TagField.Remixer, remixer));

        var primaryRule = fired[0];
        var evidence = string.Join(" + ", fired.Select(f => details.TryGetValue(f, out var d) ? $"{f}: {d}" : f.ToString()));

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

    // ── LP1 (#552): Rule pack helpers + selection-level shared-text pass ──────

    private static readonly Regex PercentEscapeRegex = new(@"%[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private static readonly Regex BracketedUrlRegex = new(
        @"\s*[\(\[](?:https?://|www\.)[^\)\]]+[\)\]]|\s*[\(\[][a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|io|co|uk|de|nl|fr|ru|info|biz|me|tv|fm|dj|club|music)(?:/[^\)\]]*)?[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareUrlRegex = new(
        @"(?:^|\s)(?:https?://\S+|www\.[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:/\S*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PromoTextRegex = new(
        @"\s*[\(\[]\s*(?:free\s+d(?:own)?l(?:oad)?|out\s+now(?:\s+on\s+[^\)\]]+)?|buy\s+on\s+[^\)\]]+|preview|promo(?:\s+only)?|download\s+(?:link|free)|clip|teaser|full\s+track|new\s+\d{4})\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ControlOrZeroWidthRegex = new(@"[\p{Cc}\u200B\u200C\u200D\uFEFF]", RegexOptions.Compiled);
    private static readonly Regex RepeatedSeparatorRegex = new(@"[-_]{2,}|\s{2,}", RegexOptions.Compiled);
    private static readonly Regex TrailingDashMixRegex = new(
        @"^(?<base>.+?)\s+[-–—]\s+(?<mix>(?:[^-–—\(\)\[\]]+\s+)?(?:extended|original|club|radio|vocal|instrumental|dub|vip|acoustic|live|festival|main|remix|rework|bootleg|edit|flip|refix)(?:\s+(?:mix|version|edit|cut))?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketMixRegex = new(
        @"\s*\[(?<mix>(?:[^\[\]]+\s+)?(?:extended|original|club|radio|vocal|instrumental|dub|vip|acoustic|live|festival|main|remix|rework|bootleg|edit|flip|refix)(?:\s+(?:mix|version|edit|cut))?)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemixerParenRegex = new(
        @"[\(\[]\s*(?<name>[^\(\)\[\]]+?)\s+(?:remix|bootleg|rework|edit|flip|refix)\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> NonRemixerQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Original", "Extended", "Club", "Radio", "Vocal", "Instrumental", "Dub", "VIP",
        "Acoustic", "Live", "Festival", "Main", "Short", "Full", "Clean", "Dirty", "Album", "Single", "Special", "2024", "2025", "2026"
    };

    private static string DecodeEscapes(string s)
    {
        var current = s;
        if (PercentEscapeRegex.IsMatch(current))
        {
            current = PercentEscapeRegex.Replace(current, m => Uri.UnescapeDataString(m.Value));
        }

        if (current.Contains('&'))
        {
            current = System.Net.WebUtility.HtmlDecode(current);
        }

        return current.Trim();
    }

    private static string FixMojibake(string s)
    {
        // Classic UTF-8 bytes decoded as Windows-1252 / Latin-1 markers: Ã, Â, â€
        if (!s.Contains('Ã') && !s.Contains('Â') && !s.Contains("â€", StringComparison.Ordinal))
            return s;

        var bytes = System.Text.Encoding.Latin1.GetBytes(s);
        var decoded = System.Text.Encoding.UTF8.GetString(bytes);
        if (!string.IsNullOrWhiteSpace(decoded) && decoded.Length < s.Length && !decoded.Contains('\uFFFD'))
            return decoded;

        return s;
    }

    private static string StripUrls(string s)
    {
        var cleaned = BracketedUrlRegex.Replace(s, " ");
        cleaned = BareUrlRegex.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|');
        return cleaned;
    }

    private static string StripPromo(string s)
    {
        var cleaned = PromoTextRegex.Replace(s, " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return cleaned;
    }

    private static string CleanGarbage(string s)
    {
        var cleaned = ControlOrZeroWidthRegex.Replace(s, string.Empty);
        cleaned = RepeatedSeparatorRegex.Replace(cleaned, " ");
        cleaned = cleaned.Trim(' ', '-', '–', '—', '_');
        return cleaned;
    }

    private static string ToMixParentheses(string s)
    {
        var current = BracketMixRegex.Replace(s, m => $" ({m.Groups["mix"].Value.Trim()})");
        var dashMatch = TrailingDashMixRegex.Match(current);
        if (dashMatch.Success)
        {
            var basePart = dashMatch.Groups["base"].Value.Trim();
            var mixPart = dashMatch.Groups["mix"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(basePart) && !string.IsNullOrWhiteSpace(mixPart))
                current = $"{basePart} ({mixPart})";
        }
        return Regex.Replace(current, @"\s{2,}", " ").Trim();
    }

    private static string? ExtractRemixer(string title)
    {
        var m = RemixerParenRegex.Match(title);
        if (!m.Success) return null;
        var candidate = m.Groups["name"].Value.Trim();
        if (candidate.Length < 2 || NonRemixerQualifiers.Contains(candidate))
            return null;
        return candidate;
    }

    private static string LowerToTitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || !IsAllLower(s)) return s;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (Acronyms.Contains(w)) { words[i] = w.ToUpperInvariant(); continue; }
            if (w.Length > 0 && char.IsLetter(w[0]))
                words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }

    private static bool IsAllLower(string s)
    {
        var hasLetter = false;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                if (!char.IsLower(ch)) return false;
            }
        }
        return hasLetter;
    }

    /// <summary>
    /// Selection-level shared-text pass (LP1 #552): detects a common non-trivial prefix or suffix
    /// shared by EVERY track in a selection (minimum 3 tracks), proposing to strip it.
    /// Always <see cref="ProposalConfidence.NeedsReview"/> and never preselected.
    /// </summary>
    public static IReadOnlyList<ProposedTagChange> DetectSharedText(IReadOnlyList<TagIdentity> tracks)
    {
        if (tracks.Count < 3)
            return Array.Empty<ProposedTagChange>();

        var proposals = new List<ProposedTagChange>();
        DetectSharedOnField(tracks, t => (t.Title ?? string.Empty).Trim(), TagField.Title, proposals);
        DetectSharedOnField(tracks, t => (t.Artist ?? string.Empty).Trim(), TagField.Artist, proposals);
        return proposals;
    }

    private static void DetectSharedOnField(
        IReadOnlyList<TagIdentity> tracks,
        Func<TagIdentity, string> selector,
        TagField field,
        List<ProposedTagChange> proposals)
    {
        var values = tracks.Select(selector).ToList();
        if (values.Any(string.IsNullOrWhiteSpace))
            return;

        var commonPrefix = FindCommonPrefix(values);
        var commonSuffix = FindCommonSuffix(values);

        const int minSharedLen = 4;
        var stripPrefix = commonPrefix.Length >= minSharedLen && IsCleanBoundaryPrefix(commonPrefix);
        var stripSuffix = !stripPrefix && commonSuffix.Length >= minSharedLen && IsCleanBoundarySuffix(commonSuffix);

        if (!stripPrefix && !stripSuffix)
            return;

        var token = stripPrefix ? commonPrefix : commonSuffix;
        var candidateResults = new List<(TagIdentity Track, string Original, string Updated)>();

        for (int i = 0; i < tracks.Count; i++)
        {
            var original = values[i];
            var updated = stripPrefix
                ? original[token.Length..].TrimStart(' ', '-', '–', '—', '|', ':')
                : original[..^token.Length].TrimEnd(' ', '-', '–', '—', '|', ':');

            // Never propose stripping if any track would become empty or unchanged.
            if (string.IsNullOrWhiteSpace(updated) || string.Equals(updated, original, StringComparison.Ordinal))
                return;

            candidateResults.Add((tracks[i], original, updated));
        }

        foreach (var (track, original, updated) in candidateResults)
        {
            proposals.Add(new ProposedTagChange(
                Hash: track.Hash,
                Rule: TagIssueRule.SharedText.ToString(),
                Changes: new[] { new TagPatchField(field, updated) },
                Confidence: ProposalConfidence.NeedsReview,
                Evidence: $"SharedText: all {tracks.Count} tracks share \u201C{token.Trim()}\u201D ({field} \u201C{original}\u201D \u2192 \u201C{updated}\u201D)",
                DefaultSelected: false,
                FilePath: track.FilePath));
        }
    }

    private static string FindCommonPrefix(IReadOnlyList<string> values)
    {
        var first = values[0];
        var len = first.Length;
        for (int i = 1; i < values.Count; i++)
        {
            len = Math.Min(len, values[i].Length);
            for (int j = 0; j < len; j++)
            {
                if (first[j] != values[i][j])
                {
                    len = j;
                    break;
                }
            }
            if (len == 0) return string.Empty;
        }
        return first[..len];
    }

    private static string FindCommonSuffix(IReadOnlyList<string> values)
    {
        var first = values[0];
        var len = first.Length;
        for (int i = 1; i < values.Count; i++)
        {
            var other = values[i];
            len = Math.Min(len, other.Length);
            for (int j = 1; j <= len; j++)
            {
                if (first[^j] != other[^j])
                {
                    len = j - 1;
                    break;
                }
            }
            if (len == 0) return string.Empty;
        }
        return first[^len..];
    }

    private static bool IsCleanBoundaryPrefix(string prefix)
    {
        var last = prefix[^1];
        return char.IsWhiteSpace(last) || last is ']' or ')' or '-' or '–' or '—' or '|' or ':';
    }

    private static bool IsCleanBoundarySuffix(string suffix)
    {
        var first = suffix[0];
        return char.IsWhiteSpace(first) || first is '[' or '(' or '-' or '–' or '—' or '|' or ':';
    }
}
