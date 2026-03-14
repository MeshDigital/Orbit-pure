using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for CSV files.
/// </summary>
public class CsvImportProvider : IStreamingImportProvider
{
    private readonly ILogger<CsvImportProvider> _logger;
    private readonly CsvInputSource _csvInputSource;
    private readonly ISpotifyMetadataService _metadataService;

    public string Name => "CSV";
    public string IconGlyph => "📄";

    public CsvImportProvider(
        ILogger<CsvImportProvider> logger,
        CsvInputSource csvInputSource,
        ISpotifyMetadataService metadataService)
    {
        _logger = logger;
        _csvInputSource = csvInputSource;
        _metadataService = metadataService;
    }

    public bool CanHandle(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && 
               (input.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || 
                File.Exists(input));
    }

    public async Task<ImportResult> ImportAsync(string filePath)
    {
        try
        {
            _logger.LogInformation("Importing from CSV: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = $"File not found: {filePath}"
                };
            }

            var tracks = await _csvInputSource.ParseAsync(filePath);

            if (!tracks.Any())
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = "No tracks found in the CSV file"
                };
            }

            // Extract a meaningful title from the filename
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var sourceTitle = string.IsNullOrWhiteSpace(fileName) ? "CSV Import" : fileName;

            _logger.LogInformation("Successfully imported {Count} tracks from CSV file '{Title}'", 
                tracks.Count, sourceTitle);

            return new ImportResult
            {
                Success = true,
                SourceTitle = sourceTitle,
                Tracks = tracks
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from CSV: {FilePath}", filePath);
            return new ImportResult
            {
                Success = false,
                ErrorMessage = $"CSV import failed: {ex.Message}"
            };
        }
    }
    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        var result = await ImportAsync(input);
        
        if (result.Success && result.Tracks.Any())
        {
            // Stream raw tracks directly - Enrichment happens in background background worker
            const int batchSize = 50;
            var total = result.Tracks.Count;
            
            for (int i = 0; i < total; i += batchSize)
            {
                var chunk = result.Tracks.Skip(i).Take(batchSize).ToList();
                
                yield return new ImportBatchResult
                {
                    Tracks = chunk,
                    SourceTitle = result.SourceTitle,
                    TotalEstimated = total
                };
            }
        }
    }
}
