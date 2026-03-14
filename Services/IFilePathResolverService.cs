using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Service dedicated to checking and resolving file paths for library entries.
/// Handles logic for finding files that may have been moved or renamed.
/// </summary>
public interface IFilePathResolverService
{
    /// <summary>
    /// Attempts to find a missing track file using configured matching logic 
    /// (Exact filename match, Fuzzy metadata match, etc.).
    /// </summary>
    /// <param name="missingTrack">The library entry with potentially invalid path</param>
    /// <returns>The resolved full path, or null if not found</returns>
    Task<string?> ResolveMissingFilePathAsync(LibraryEntry missingTrack);
}
