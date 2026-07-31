using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for pasted tracklists from YouTube comments, SoundCloud, etc.
/// </summary>
public class TracklistImportProvider : IStreamingImportProvider
{
    private readonly ILogger<TracklistImportProvider> _logger;

    public string Name => "Pasted Tracklist";
    public string IconGlyph => "📋";

    public TracklistImportProvider(ILogger<TracklistImportProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if input looks like a tracklist (multiple lines).
    /// </summary>
    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Check for multi-line input
        var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(l => !string.IsNullOrWhiteSpace(l))
                         .ToList();
        
        return lines.Count >= 2;
    }

    /// <summary>
    /// Parse pasted tracklist text into tracks.
    /// </summary>
    public Task<ImportResult> ImportAsync(string rawText)
    {
        try
        {
            _logger.LogInformation("Parsing pasted tracklist ({Length} chars, {Lines} lines)", 
                rawText.Length, 
                rawText.Split('\n').Length);

            var tracks = Utils.CommentTracklistParser.Parse(rawText, out var detectedTitle, out var detectedSourceUrl);

            if (!tracks.Any())
            {
                return Task.FromResult(new ImportResult
                {
                    Success = false,
                    ErrorMessage = "No valid tracks found in pasted text.\n\nExpected format:\nArtist - Title\n0:00 Artist - Title"
                });
            }

            var sourceType = DetectSpecificSourceType(rawText);
            _logger.LogInformation("Successfully parsed {Count} tracks from pasted text (detected title: {Title}, source: {Source}, url: {Url})", tracks.Count, detectedTitle ?? "(none)", sourceType ?? Name, detectedSourceUrl ?? "(none)");

            return Task.FromResult(new ImportResult
            {
                Success = true,
                SourceTitle = !string.IsNullOrWhiteSpace(detectedTitle)
                    ? detectedTitle
                    : $"Pasted Tracklist ({DateTime.Now:yyyy-MM-dd HH:mm})",
                SourceType = sourceType,
                SourceUrl = detectedSourceUrl,
                Tracks = tracks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse pasted tracklist");
            return Task.FromResult(new ImportResult
            {
                Success = false,
                ErrorMessage = $"Parse error: {ex.Message}"
            });
        }
    }
    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        var result = await ImportAsync(input);

        if (result.Success && result.Tracks.Any())
        {
            yield return new ImportBatchResult
            {
                Tracks = result.Tracks,
                SourceTitle = result.SourceTitle,
                SourceType = result.SourceType,
                SourceUrl = result.SourceUrl,
                TotalEstimated = result.Tracks.Count
            };
            _logger.LogInformation("ImportStreamAsync completed: yielded {Count} tracks from pasted tracklist", result.Tracks.Count);
        }
        else if (!result.Success)
        {
            _logger.LogWarning("ImportStreamAsync: no tracks yielded — {Error}", result.ErrorMessage);
        }
    }

    /// <summary>
    /// Pasted text carries no explicit source metadata, but well-known tracklist sites leave
    /// identifiable fingerprints (e.g. 1001Tracklists' own backlink) — detect these so the
    /// library can show a more specific badge than the generic "Pasted Tracklist".
    /// </summary>
    private static string? DetectSpecificSourceType(string rawText)
    {
        if (rawText.Contains("1001tracklists.com", StringComparison.OrdinalIgnoreCase) ||
            rawText.Contains("1001.tl", StringComparison.OrdinalIgnoreCase))
        {
            return "1001Tracklists";
        }

        return null;
    }
}
