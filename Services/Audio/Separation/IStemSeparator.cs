using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio.Separation;

public interface IStemSeparator
{
    string Name { get; }
    bool IsAvailable { get; } // Check if model/cli exists

    /// <summary>
    /// Short identifier used as the stem cache model tag (e.g. "spleeter-5stems", "spleeter-cli").
    /// Changes whenever the underlying model file changes so stale cache entries are detected.
    /// </summary>
    string ModelTag { get; }

    Task<Dictionary<StemType, string>> SeparateAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default);
}
