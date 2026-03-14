using System;
using System.Text.RegularExpressions;

namespace SLSKDONET.Utils;

/// <summary>
/// Utility for sanitizing strings for use in XML attributes.
/// Rekordbox can fail to parse XML with control characters or invalid Unicode.
/// </summary>
public static class XmlSanitizer
{
    // Regex to match invalid XML chars (control codes except tabs/newlines)
    private static readonly Regex InvalidXmlChars = new Regex(
        @"[^\x09\x0A\x0D\x20-\uD7FF\uE000-\uFFFD\u10000-\u10FFFF]",
        RegexOptions.Compiled);

    /// <summary>
    /// Removes invalid characters from a string to make it safe for XML attributes.
    /// </summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return InvalidXmlChars.Replace(input, "");
    }
}
