using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for import source plugins (Spotify, CSV, YouTube, etc.).
/// Implementations provide a consistent way to import tracks from different sources.
/// </summary>
public interface IImportProvider
{
    /// <summary>
    /// Display name of this import source (e.g., "Spotify", "CSV File", "YouTube").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Icon or emoji to display in UI (e.g., "🎵", "📄", "▶️").
    /// </summary>
    string IconGlyph { get; }

    /// <summary>
    /// Optional: Pattern to detect if this provider can handle the input.
    /// For example, Spotify URLs start with "https://open.spotify.com".
    /// Return true if this provider should handle the given input.
    /// </summary>
    bool CanHandle(string input);

    /// <summary>
    /// Import tracks from the given input (URL, file path, etc.).
    /// </summary>
    /// <param name="input">Source-specific input (playlist URL, file path, etc.)</param>
    /// <returns>Result containing tracks or error information</returns>
    Task<ImportResult> ImportAsync(string input);
}

/// <summary>
/// Extended interface for providers that can stream results page-by-page.
/// Allows for immediate UI updates during large imports.
/// </summary>
public interface IStreamingImportProvider : IImportProvider
{
    /// <summary>
    /// Stream tracks as they are discovered/fetched.
    /// Yields batches of tracks (e.g. pages from an API).
    /// </summary>
    IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input);
}

public class ImportBatchResult
{
    public required List<SearchQuery> Tracks { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public int TotalEstimated { get; set; }
}
