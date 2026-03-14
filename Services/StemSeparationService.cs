using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services;

public class StemSeparationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _stemsBaseDirectory;

    public StemSeparationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        
        // Default storage location
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _stemsBaseDirectory = Path.Combine(appData, "Antigravity", "Stems");
        Directory.CreateDirectory(_stemsBaseDirectory);
    }

    public bool HasStems(string trackId)
    {
        var trackDir = Path.Combine(_stemsBaseDirectory, trackId);
        return Directory.Exists(trackDir) && Directory.GetFiles(trackDir, "*.wav").Length >= 4;
    }

    public async Task<Dictionary<StemType, string>> SeparateTrackAsync(string trackFilePath, string trackId)
    {
        var outputDir = Path.Combine(_stemsBaseDirectory, trackId);
        // Clean start or resume? If exists, maybe return cached?
        // Typically we assume if HasStems is false, we separate.
        
        Directory.CreateDirectory(outputDir);

        // Strategy Selector
        // 1. Try Spleeter CLI
        var cli = new Audio.Separation.SpleeterCliSeparator();
        if (cli.IsAvailable)
        {
            try 
            {
                // Spleeter needs access to the file.
                // It will output to outputDir/filename/vocals.wav
                return await cli.SeparateAsync(trackFilePath, outputDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StemSeparationService] Spleeter CLI failed: {ex.Message}. Falling back to mock.");
            }
        }

        // 2. Fallback to Mock (Zero-dependency)
        await CreateMockStemsAsync(outputDir);

        var result = new Dictionary<StemType, string>();
        foreach (StemType type in Enum.GetValues(typeof(StemType)))
        {
            var path = Path.Combine(outputDir, $"{type.ToString().ToLower()}.wav");
            result[type] = path;
        }

        return result;
    }

    private async Task CreateMockStemsAsync(string outputDir)
    {
        // Simulate processing time
        await Task.Delay(2000); 

        // Create a 1-second silent WAV file header for each stem so AudioFileReader doesn't crash
        foreach (StemType type in Enum.GetValues(typeof(StemType)))
        {
            var path = Path.Combine(outputDir, $"{type.ToString().ToLower()}.wav");
            if (!File.Exists(path))
            {
                await CreateSilentWavAsync(path);
            }
        }
    }

    private async Task CreateSilentWavAsync(string path)
    {
        // Minimal valid WAV header for 44.1kHz 16-bit Mono (44 bytes + data)
        // This allows NAudio/FFmpeg to open it without exception.
        byte[] wavHeader = new byte[44 + 88200]; // 1 second of silence
        
        using (var fs = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(fs))
        {
             // RIFF header
            writer.Write("RIFF".ToCharArray());
            writer.Write(wavHeader.Length - 8); // File size - 8
            writer.Write("WAVE".ToCharArray());
            
            // fmt chunk
            writer.Write("fmt ".ToCharArray());
            writer.Write(16); // Chunk size
            writer.Write((short)1); // Audio format (1 = PCM)
            writer.Write((short)1); // Channels (Mono)
            writer.Write(44100); // Sample rate
            writer.Write(88200); // Byte rate
            writer.Write((short)2); // Block align
            writer.Write((short)16); // Bits per sample
            
            // data chunk
            writer.Write("data".ToCharArray());
            writer.Write(wavHeader.Length - 44);
            
            // Write silence
            writer.Write(new byte[wavHeader.Length - 44]);
        }
    }

    public Dictionary<StemType, string> GetStemPaths(string trackId)
    {
        var outputDir = Path.Combine(_stemsBaseDirectory, trackId);
        var result = new Dictionary<StemType, string>();
        
        if (!Directory.Exists(outputDir)) return result;

        foreach (StemType type in Enum.GetValues(typeof(StemType)))
        {
            var path = Path.Combine(outputDir, $"{type.ToString().ToLower()}.wav");
            if (File.Exists(path))
            {
                result[type] = path;
            }
        }
        return result;
    }
}
