using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>
/// A DirectML-accelerated ONNX implementation of Spleeter.
/// Requires 'spleeter-4stems.onnx' model file.
/// WARNING: Requires implementation of STFT (Short-Time Fourier Transform) which is complex.
/// This class serves as the inference engine shell.
/// </summary>
public class OnnxStemSeparator : IStemSeparator
{
    private readonly string _modelPath;
    
    public string Name => "ONNX DirectML";
    
    public bool IsAvailable => File.Exists(_modelPath);

    public OnnxStemSeparator()
    {
        // Use the converted 5-stem model in the Tools directory
        _modelPath = Path.Combine("Tools", "Essentia", "models", "spleeter-5stems.onnx");
    }

    public async Task<Dictionary<StemType, string>> SeparateAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
             throw new FileNotFoundException("Spleeter ONNX model not found. Please ensure 'Tools\\Essentia\\models\\spleeter-5stems.onnx' exists.", _modelPath);
        }

        return await SeparateNativeAsync(inputFilePath, outputDirectory, _modelPath);
    }

    private async Task<Dictionary<StemType, string>> SeparateNativeAsync(string inputPath, string outputDir, string onnxPath)
    {
        // 1. Load Audio and De-interleave
        float[] audioData;
        int sampleRate;
        int channels;
        int totalFrames;

        using (var reader = new NAudio.Wave.AudioFileReader(inputPath))
        {
            sampleRate = reader.WaveFormat.SampleRate;
            channels = reader.WaveFormat.Channels;
            
            var buffer = new float[reader.Length / 4];
            int read = reader.Read(buffer, 0, buffer.Length);
            audioData = buffer;
            totalFrames = read / channels;
        }

        // 2. Prepare Tensor: [Samples, 2]
        // This specific ONNX model takes raw waveform samples.
        var inputTensor = new DenseTensor<float>(new[] { totalFrames, 2 });
        
        if (channels == 2)
        {
            // Direct copy for Interleaved Stereo to [Frames, 2]
            for (int i = 0; i < totalFrames; i++)
            {
                inputTensor[i, 0] = audioData[i * 2];
                inputTensor[i, 1] = audioData[i * 2 + 1];
            }
        }
        else
        {
            // Expand Mono to Stereo [Frames, 2]
            for (int i = 0; i < totalFrames; i++)
            {
                inputTensor[i, 0] = audioData[i];
                inputTensor[i, 1] = audioData[i];
            }
        }

        // 3. Inference
        using var session = new InferenceSession(onnxPath);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("waveform:0", inputTensor)
        };

        // Note: The model produces 5 separate outputs
        using var results = session.Run(inputs);
        
        // 4. Map Results
        var stemFiles = new Dictionary<StemType, string>();
        var outputNames = new Dictionary<string, StemType>
        {
            { "waveform_vocals:0", StemType.Vocals },
            { "waveform_drums:0", StemType.Drums },
            { "waveform_bass:0", StemType.Bass },
            { "waveform_piano:0", StemType.Piano },
            { "waveform_other:0", StemType.Other }
        };

        foreach (var result in results)
        {
            if (outputNames.TryGetValue(result.Name, out var type))
            {
                var outputTensor = result.AsTensor<float>();
                int frames = outputTensor.Dimensions[0];
                
                // Re-interleave to [Frames * 2]
                float[] stereoBuffer = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    stereoBuffer[i * 2] = outputTensor[i, 0];
                    stereoBuffer[i * 2 + 1] = outputTensor[i, 1];
                }

                string filename = $"{type.ToString().ToLower()}.wav";
                string path = Path.Combine(outputDir, filename);
                
                using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(sampleRate, 2)))
                {
                    writer.WriteSamples(stereoBuffer, 0, stereoBuffer.Length);
                }
                
                stemFiles[type] = path;
            }
        }

        return stemFiles;
    }
}
