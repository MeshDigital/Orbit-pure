using System.IO;
using System.Text.RegularExpressions;

namespace SLSKDONET.Utils;

/// <summary>
/// Validation utilities for search queries and filters.
/// </summary>
public static class ValidationUtils
{
    /// <summary>
    /// Validates a search query.
    /// </summary>
    public static (bool isValid, string? error) ValidateSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (false, "Search query cannot be empty");

        if (query.Length < 2)
            return (false, "Search query must be at least 2 characters");

        if (query.Length > 200)
            return (false, "Search query cannot exceed 200 characters");

        return (true, null);
    }

    /// <summary>
    /// Validates bitrate value in kbps.
    /// </summary>
    public static (bool isValid, string? error) ValidateBitrate(int? bitrate)
    {
        if (bitrate == null)
            return (true, null);

        if (bitrate < 0)
            return (false, "Bitrate cannot be negative");

        if (bitrate > 3000) // Increased to allow for high-res lossless files (e.g., FLAC)
            return (false, "Bitrate seems unusually high (max is 3000 kbps)");

        return (true, null);
    }

    /// <summary>
    /// Validates audio format/extension.
    /// </summary>
    public static (bool isValid, string? error) ValidateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return (true, null);

        var validFormats = new[] { "mp3", "flac", "m4a", "aac", "ogg", "wav", "wma", "opus" };
        var lowerFormat = format.ToLower().TrimStart('.');

        if (!validFormats.Contains(lowerFormat))
            return (false, $"Unsupported format: {format}");

        return (true, null);
    }

    /// <summary>
    /// Validates listening port.
    /// </summary>
    public static (bool isValid, string? error) ValidatePort(int port)
    {
        if (port < 1024)
            return (false, "Port must be >= 1024");

        if (port > 65535)
            return (false, "Port must be <= 65535");

        return (true, null);
    }

    /// <summary>
    /// Validates file path.
    /// </summary>
    public static (bool isValid, string? error) ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Path cannot be empty");

        try
        {
            Path.GetFullPath(path);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}");
        }
    }
}
