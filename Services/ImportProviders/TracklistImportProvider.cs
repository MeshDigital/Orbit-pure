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

            var tracks = Utils.CommentTracklistParser.Parse(rawText);

            if (!tracks.Any())
            {
                return Task.FromResult(new ImportResult
                {
                    Success = false,
                    ErrorMessage = "No valid tracks found in pasted text.\n\nExpected format:\nArtist - Title\n0:00 Artist - Title"
                });
            }

            _logger.LogInformation("Successfully parsed {Count} tracks from pasted text", tracks.Count);

            return Task.FromResult(new ImportResult
            {
                Success = true,
                SourceTitle = $"Pasted Tracklist ({DateTime.Now:yyyy-MM-dd HH:mm})",
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
                TotalEstimated = result.Tracks.Count
            };
        }
    }
}
