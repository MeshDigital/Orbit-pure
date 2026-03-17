using System;
using System.IO;

namespace SLSKDONET.Services;

/// <summary>
/// Produces stable dedup keys for incoming Soulseek results.
/// Professional beta contract: FileName + FileSize + Duration.
/// </summary>
public sealed class ResultFingerprinter
{
    public string Create(string? fileName, long? fileSize, int? durationSeconds)
    {
        var normalizedFileName = Path.GetFileName(fileName ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        var normalizedSize = fileSize.GetValueOrDefault(0);
        var normalizedDuration = Math.Max(0, durationSeconds.GetValueOrDefault(0));

        return $"{normalizedFileName}|{normalizedSize}|{normalizedDuration}";
    }
}