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

    /// <summary>
    /// Optional, more specific source label than the provider's own <see cref="IImportProvider.Name"/>
    /// (e.g. a "Pasted Tracklist" provider detecting the paste actually came from 1001Tracklists).
    /// Null means "use the provider's own name" — most providers never set this.
    /// </summary>
    public string? SourceType { get; set; }

    /// <summary>
    /// A URL identifying the original source (e.g. a 1001Tracklists backlink found inside a
    /// pasted tracklist), so the user can navigate back to it later. Null when none was found.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>Playlist owner or album artist, shown in the preview header. Null when unknown.</summary>
    public string? SourceOwner { get; set; }
}

/// <summary>
/// Thrown by a streaming provider when the source could not be read at all (bad link, private or
/// deleted playlist, unreadable file, nothing parseable, not signed in). Providers used to log
/// these and yield nothing, so the preview finished as "Loaded 0 tracks — ready to import" with
/// the real reason only in the log. The orchestrator turns this into the preview's error state.
/// </summary>
public class ImportSourceException : System.Exception
{
    public ImportSourceException(string message, bool requiresSignIn = false, System.Exception? inner = null)
        : base(message, inner)
    {
        RequiresSignIn = requiresSignIn;
    }

    /// <summary>True when connecting (or reconnecting) the account would fix it.</summary>
    public bool RequiresSignIn { get; }
}
