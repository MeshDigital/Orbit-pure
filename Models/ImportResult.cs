using System.Collections.Generic;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Result of an import operation from any source.
/// </summary>
public class ImportResult
{
    /// <summary>
    /// Whether the import succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Title/name extracted from the source (e.g., playlist name, filename).
    /// </summary>
    public string SourceTitle { get; set; } = string.Empty;

    /// <summary>
    /// List of tracks/queries parsed from the source.
    /// </summary>
    public List<SearchQuery> Tracks { get; set; } = new();

    /// <summary>
    /// Error message if import failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Optional metadata about the import (e.g., cover art URL, description).
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Type of import source (e.g., "Spotify", "CSV", "Pasted Tracklist").
    /// </summary>
    public string? SourceType { get; set; }
}
