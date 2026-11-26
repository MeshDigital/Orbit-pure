using System;
using System.Text.RegularExpressions;

namespace SLSKDONET.Services.Matching;

public enum EditLengthKind
{
    Full,
    Unlabelled,
    Short
}

public readonly record struct VersionGateVerdict(int ScoreDelta, string? Reason, bool IsDerivativeMismatch);

/// <summary>
/// Single shared version &amp; edit-length gate used by BOTH <see cref="SearchResultMatcher"/>
/// and strict-mode <see cref="AutoDownload.MatchScorer"/> (Epic #543 / #547 SQ3).
/// </summary>
public static class VersionTag
{
    /// <summary>Score taken off a remix/VIP/bootleg/edit candidate for a mismatched request (#333/#386).</summary>
    public const int DerivativeMismatchPenalty = 50;

    /// <summary>Score taken off a Radio/Short edit when the request explicitly asks for Extended/Original/Club.</summary>
    public const int ShortForFullRequestPenalty = 35;

    private static readonly Regex StrongVersionWord = new(
        @"\b(remix|rmx|re-?mix|vip|bootleg|mash-?up|re-?work|refix|re-?fix)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketGroup = new(
        @"[\(\[]([^\)\]]+)[\)\]]",
        RegexOptions.Compiled);

    private static readonly Regex BracketVersionWord = new(
        @"\b(edit|re-?edit|flip|remake|dub|live|cover|acoustic|instrumental|acapella|a cappella|version|mix)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NeutralVersion = new(
        @"\b(original|extended|radio|short|clean|explicit|album|single|main|club|remaster(ed)?)\s*(mix|edit|version|cut)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FullLengthPattern = new(
        @"\b(extended(\s+(mix|version|edit|club\s+mix))?|club\s+mix|original(\s+(mix|version))?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShortLengthPattern = new(
        @"\b(radio(\s+(edit|mix|version|cut))?|short(\s+(edit|mix|version))?|single\s+edit)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The derivative version a title or file name names ("remix", "vip", "edit"…), or null for the original track.
    /// Original/Extended/Radio/Club Mix and similar are the same recording, so they are not derivatives.
    /// </summary>
    public static string? DerivativeVersionTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalised = text.Replace('_', ' ');
        foreach (Match group in BracketGroup.Matches(normalised))
        {
            var inner = group.Groups[1].Value;
            var cleaned = NeutralVersion.Replace(inner, " ");
            var strongInBracket = StrongVersionWord.Match(cleaned);
            if (strongInBracket.Success) return strongInBracket.Value.ToLowerInvariant().Replace("-", string.Empty);
            var word = BracketVersionWord.Match(cleaned);
            if (word.Success) return word.Value.ToLowerInvariant();
        }
        var strong = StrongVersionWord.Match(NeutralVersion.Replace(BracketGroup.Replace(normalised, " "), " "));
        return strong.Success ? strong.Value.ToLowerInvariant().Replace("-", string.Empty) : null;
    }

    /// <summary>
    /// Detects the edit-length label: Full (extended/club/original), Short (radio/short/single edit),
    /// or Unlabelled.
    /// </summary>
    public static EditLengthKind DetectEditLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EditLengthKind.Unlabelled;
        var normalised = text.Replace('_', ' ');
        if (ShortLengthPattern.IsMatch(normalised)) return EditLengthKind.Short;
        if (FullLengthPattern.IsMatch(normalised)) return EditLengthKind.Full;
        return EditLengthKind.Unlabelled;
    }

    /// <summary>
    /// Evaluates derivative version match and edit-length preference between a requested title and a candidate.
    /// </summary>
    public static VersionGateVerdict Evaluate(string? requestedTitle, string? candidateText)
    {
        var requestedDerivative = DerivativeVersionTag(requestedTitle);
        var offeredDerivative = DerivativeVersionTag(candidateText);

        if (requestedDerivative is null && offeredDerivative is not null)
        {
            return new VersionGateVerdict(
                -DerivativeMismatchPenalty,
                $"Version: candidate is a {offeredDerivative}, the request isn't (-{DerivativeMismatchPenalty})",
                IsDerivativeMismatch: true);
        }

        if (requestedDerivative is not null && offeredDerivative is not null &&
            !string.Equals(requestedDerivative, offeredDerivative, StringComparison.OrdinalIgnoreCase))
        {
            return new VersionGateVerdict(
                -DerivativeMismatchPenalty,
                $"Version: candidate is {offeredDerivative} but requested {requestedDerivative} (-{DerivativeMismatchPenalty})",
                IsDerivativeMismatch: true);
        }

        var reqLength = DetectEditLength(requestedTitle);
        var candLength = DetectEditLength(candidateText);

        if (reqLength == EditLengthKind.Full && candLength == EditLengthKind.Short)
        {
            return new VersionGateVerdict(
                -ShortForFullRequestPenalty,
                $"Version: candidate is a short/radio edit, request asks for full/extended (-{ShortForFullRequestPenalty})",
                IsDerivativeMismatch: false);
        }

        if (reqLength == EditLengthKind.Short)
        {
            if (candLength == EditLengthKind.Short)
                return new VersionGateVerdict(4, "Version: radio/short edit matches request (+4)", false);
            if (candLength == EditLengthKind.Full)
                return new VersionGateVerdict(-8, "Version: full/extended candidate for radio edit request (-8)", false);
            return new VersionGateVerdict(0, null, false);
        }

        if (reqLength == EditLengthKind.Unlabelled)
        {
            return candLength switch
            {
                EditLengthKind.Full => new VersionGateVerdict(3, "Version: full/extended tie-break (+3)", false),
                EditLengthKind.Short => new VersionGateVerdict(-3, "Version: radio/short edit tie-break (-3)", false),
                _ => new VersionGateVerdict(0, null, false)
            };
        }

        // Request explicitly asks for Full and candidate is Full (small confirmation tie-break)
        if (reqLength == EditLengthKind.Full && candLength == EditLengthKind.Full)
            return new VersionGateVerdict(3, "Version: full/extended matches request (+3)", false);

        return new VersionGateVerdict(0, null, false);
    }
}
