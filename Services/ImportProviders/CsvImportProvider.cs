using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for CSV files. The file is validated first (<see cref="CsvInputSource.Inspect"/>):
/// a file ORBIT can't read sensibly is refused with an explanation instead of filling the preview
/// with empty rows.
/// </summary>
public class CsvImportProvider : IStreamingImportProvider
{
    private readonly ILogger<CsvImportProvider> _logger;
    private readonly CsvInputSource _csvInputSource;
    private readonly ISpotifyMetadataService _metadataService;

    public string Name => "CSV";
    public string IconGlyph => "file";

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
            var inspection = Inspect(filePath);
            if (!inspection.IsValid)
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = string.Join(" ", inspection.Errors)
                };
            }

            return await Task.FromResult(new ImportResult
            {
                Success = true,
                SourceTitle = SourceTitleFor(filePath, inspection),
                Tracks = inspection.Tracks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from CSV: {FilePath}", filePath);
            return new ImportResult
            {
                Success = false,
                ErrorMessage = ex is FileNotFoundException ? $"File not found: {filePath}" : $"CSV import failed: {ex.Message}"
            };
        }
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        CsvInspection inspection;
        try
        {
            inspection = Inspect(input);
        }
        catch (FileNotFoundException)
        {
            throw new ImportSourceException($"File not found: {input}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportSourceException($"Couldn't open the file: {ex.Message}", inner: ex);
        }

        if (!inspection.IsValid)
            throw new ImportSourceException(string.Join(" ", inspection.Errors));

        await Task.CompletedTask;
        var sourceTitle = SourceTitleFor(input, inspection);
        var notice = inspection.Summary;

        // Stream raw tracks directly - enrichment happens in the background worker.
        const int batchSize = 50;
        var total = inspection.Tracks.Count;
        for (int i = 0; i < total; i += batchSize)
        {
            yield return new ImportBatchResult
            {
                Tracks = inspection.Tracks.Skip(i).Take(batchSize).ToList(),
                SourceTitle = sourceTitle,
                TotalEstimated = total,
                Notice = i == 0 ? notice : null
            };
        }
    }

    private CsvInspection Inspect(string filePath)
    {
        _logger.LogInformation("Importing from CSV: {FilePath}", filePath);
        var inspection = _csvInputSource.Inspect(filePath);
        _logger.LogInformation(
            "CSV {File}: delimiter '{Delimiter}', columns [{Headers}] mapped as {Mapping}; {Count} track(s), {Errors} error(s), {Warnings} warning(s)",
            Path.GetFileName(filePath), inspection.Delimiter == '\t' ? "tab" : inspection.Delimiter.ToString(),
            string.Join(", ", inspection.Headers),
            string.Join(", ", inspection.Mapping.Select(m => $"{m.Key}={m.Value}")),
            inspection.Tracks.Count, inspection.Errors.Count, inspection.Warnings.Count);
        foreach (var error in inspection.Errors) _logger.LogWarning("CSV refused: {Error}", error);
        return inspection;
    }

    private static string SourceTitleFor(string filePath, CsvInspection inspection)
    {
        if (!string.IsNullOrWhiteSpace(inspection.PlaylistName)) return inspection.PlaylistName!;
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? "CSV Import" : fileName;
    }
}
