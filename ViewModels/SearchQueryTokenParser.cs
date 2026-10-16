using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Pulls filter shortcuts out of an ad-hoc search query: "flac" / "mp3" / "wav" select formats
/// (several may be combined), "&gt;256" or "256+" sets a minimum bitrate. Everything else is the
/// network query. Pure and stateless so it can be unit tested.
/// </summary>
public static class SearchQueryTokenParser
{
    public sealed record Result(string Query, IReadOnlySet<string>? Formats, int? MinBitrate);

    private static readonly HashSet<string> FormatTokens = new(StringComparer.OrdinalIgnoreCase) { "flac", "mp3", "wav" };

    public static Result Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new Result(string.Empty, null, null);

        var kept = new List<string>();
        HashSet<string>? formats = null;
        int? minBitrate = null;

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (FormatTokens.Contains(token))
            {
                formats ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                formats.Add(token.ToUpperInvariant());
                continue;
            }

            if (token.StartsWith('>') && int.TryParse(token.AsSpan(1), out var gt) && gt > 0)
            {
                minBitrate = gt;
                continue;
            }

            if (token.Length > 1 && token.EndsWith('+') && int.TryParse(token.AsSpan(0, token.Length - 1), out var plus) && plus > 0)
            {
                minBitrate = plus;
                continue;
            }

            kept.Add(token);
        }

        return new Result(string.Join(" ", kept), formats, minBitrate);
    }
}
