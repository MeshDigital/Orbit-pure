using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio.Separation;

public class SpleeterCliSeparator : IStemSeparator
{
    public string Name => "Spleeter CLI";

    public bool IsAvailable 
    {
        get 
        {
            try 
            {
                using var p = Process.Start(new ProcessStartInfo("spleeter", "--version") 
                { 
                    RedirectStandardOutput = true, 
                    UseShellExecute = false, 
                    CreateNoWindow = true 
                });
                p?.WaitForExit(2000);
                return p?.ExitCode == 0;
            }
            catch 
            {
                return false;
            }
        }
    }

    public async Task<Dictionary<StemType, string>> SeparateAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<StemType, string>();
        
        // Ensure output directory exists (Spleeter creates a subdir by default if not careful, 
        // typically -o output_dir creates output_dir/filename/vocals.wav)
        // We want flat structure or known structure. 
        // Spleeter usage: spleeter separate -p spleeter:4stems -o {outputDirectory} {inputFilePath}
        
        var startInfo = new ProcessStartInfo("spleeter")
        {
            Arguments = $"separate -p spleeter:4stems -o \"{outputDirectory}\" \"{inputFilePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var tcs = new TaskCompletionSource<bool>();
        using var process = new Process { StartInfo = startInfo };
        
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode == 0);

        Console.WriteLine($"[SpleeterCLI] Starting separation for {Path.GetFileName(inputFilePath)}...");
        if (!process.Start())
        {
             throw new Exception("Failed to start Spleeter process.");
        }
        
        // Log output for debugging
        _ = ReadStreamAsync(process.StandardOutput, "[Spleeter OUT]");
        _ = ReadStreamAsync(process.StandardError, "[Spleeter ERR]");
        
        // Wait via TCS + Cancellation
        using var reg = cancellationToken.Register(() => process.Kill());
        var success = await tcs.Task;

        if (!success)
        {
            throw new Exception($"Spleeter process failed. Exit Code: {process.ExitCode}");
        }
        
        // Spleeter creates a folder named after the input file inside the output directory.
        // e.g. output/my_song/vocals.wav
        var filenameNoExt = Path.GetFileNameWithoutExtension(inputFilePath);
        var subDir = Path.Combine(outputDirectory, filenameNoExt);

        // Map files to StemTypes
        // spleeter:4stems -> vocals.wav, drums.wav, bass.wav, other.wav
        var mapping = new Dictionary<StemType, string>
        {
            { StemType.Vocals, "vocals.wav" },
            { StemType.Drums, "drums.wav" },
            { StemType.Bass, "bass.wav" },
            { StemType.Other, "other.wav" }
        };

        foreach (var kvp in mapping)
        {
            var expectedPath = Path.Combine(subDir, kvp.Value);
            if (File.Exists(expectedPath))
            {
                result[kvp.Key] = expectedPath;
            }
        }
        
        return result;
    }

    private async Task ReadStreamAsync(StreamReader reader, string prefix)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line != null) Console.WriteLine($"{prefix} {line}");
        }
    }
}
