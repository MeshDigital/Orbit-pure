using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services;

public class StemSeparationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StemSeparationService> _logger;
    private readonly AnalysisQueueService? _analysisQueue;
    private readonly string _stemsBaseDirectory;

    /// <summary>Throttle delay applied in stealth mode so GPU work does not starve the UI.</summary>
    private static readonly TimeSpan StealthModeThrottleDelay = TimeSpan.FromMilliseconds(500);

    public StemSeparationService(
        IServiceProvider serviceProvider,
        ILogger<StemSeparationService> logger,
        AnalysisQueueService? analysisQueue = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _analysisQueue = analysisQueue;

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
        Directory.CreateDirectory(outputDir);

        // Honour stealth mode: yield before launching GPU-intensive work so the UI thread stays responsive.
        if (_analysisQueue?.IsStealthMode == true)
        {
            _logger.LogDebug("[StemSeparation] Stealth mode active – throttling before separation of {TrackId}", trackId);
            await Task.Delay(StealthModeThrottleDelay).ConfigureAwait(false);
        }

        // Strategy Selector (Priority: ONNX DirectML → Spleeter CLI → Mock)
        // ONNX is preferred: it is GPU-accelerated via DirectML, bundled with the application,
        // and does not require an external Python package installation.

        // 1. Try ONNX DirectML (GPU-accelerated – requires spleeter-5stems.onnx model file)
        var onnx = new Audio.Separation.OnnxStemSeparator();
        if (onnx.IsAvailable)
        {
            try
            {
                _logger.LogInformation("[StemSeparation] Starting ONNX DirectML separation for track {TrackId}", trackId);
                var onnxResult = await onnx.SeparateAsync(trackFilePath, outputDir);
                _logger.LogInformation("[StemSeparation] ONNX separation completed successfully for track {TrackId}", trackId);
                return onnxResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StemSeparation] ONNX separator failed for {TrackId}. Falling back to Spleeter CLI.", trackId);
            }
        }
        else
        {
            _logger.LogDebug("[StemSeparation] ONNX model not available for {TrackId}. Checking Spleeter CLI.", trackId);
        }

        // 2. Try Spleeter CLI (requires spleeter Python package)
        var cli = new Audio.Separation.SpleeterCliSeparator();
        if (cli.IsAvailable)
        {
            try 
            {
                _logger.LogInformation("[StemSeparation] Starting Spleeter CLI separation for track {TrackId}", trackId);
                var cliResult = await cli.SeparateAsync(trackFilePath, outputDir);
                _logger.LogInformation("[StemSeparation] Spleeter CLI separation completed successfully for track {TrackId}", trackId);
                return cliResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StemSeparation] Spleeter CLI failed for {TrackId}. Falling back to mock.", trackId);
            }
        }
        else
        {
            _logger.LogDebug("[StemSeparation] Spleeter CLI not available for {TrackId}. Using mock fallback.", trackId);
        }

        // 3. Fallback to Mock (zero external dependencies)
        _logger.LogWarning("[StemSeparation] All real separators unavailable for {TrackId}. Generating silent mock stems.", trackId);
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
