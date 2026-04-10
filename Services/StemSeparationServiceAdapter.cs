using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.Services;

public sealed class StemSeparationServiceAdapter : IStemSeparationService
{
    private readonly CachedStemSeparator _separator;

    public StemSeparationServiceAdapter(CachedStemSeparator separator)
    {
        _separator = separator;
    }

    public string Name => _separator.Name;
    public bool IsAvailable => _separator.IsAvailable;
    public string ModelTag => _separator.ModelTag;

    public Task<Dictionary<StemType, string>> SeparateWithProgressAsync(
        string inputFilePath,
        string outputDirectory,
        IProgress<float>? progress,
        CancellationToken cancellationToken = default)
        => _separator.SeparateWithProgressAsync(inputFilePath, outputDirectory, progress, cancellationToken);
}
