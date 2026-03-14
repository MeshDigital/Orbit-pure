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
    
    Task<Dictionary<StemType, string>> SeparateAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default);
}
