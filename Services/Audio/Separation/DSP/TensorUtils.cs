using System;
using System.Linq;
using MathNet.Numerics;
using System.Numerics;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SLSKDONET.Services.Audio.Separation.DSP;

public static class TensorUtils
{
    // Spleeter Input: (n_frames, n_bins, 2) where 2 is stereo? 
    // Spleeter model inputs are usually: 'waveform' or 'spectrogram_magnitude'.
    // Standard spleeter-4stems inputs: 'mix_stft' [batch, n_frames, n_bins, 2] (Real/Imag or Channels?)
    
    // Correction: Spleeter trained models take the *magnitude spectrogram*?
    // Actually, official Spleeter ONNX export usually takes:
    // "waveform" input [batch, samples, channel] (and does STFT inside model?)
    // OR 
    // "mix_stft" [batch, frame, bin, channel] (complex as 2 floats?)
    
    // WE ASSUME: Using the "Standard" 4-stems model exported from Orbit/Spleeter.
    // Most ONNX exports move STFT outside. 
    // Input is usually named "input" and shape is [batch, channels, frames, bins] or similar.
    
    // For this implementation, we assume input is Magnitude Spectrogram.
    // And we apply masking on the magnitude, then reconstruction using original phase.
    
    public static DenseTensor<float> CreateInputTensor(Complex[][] stft)
    {
        // Shape: [1, n_frames, n_bins, 2] ?
        // Usually trained on AUDIO directly if STFT is inside.
        // If STFT is OUTSIDE, we need to know the specific model signature.
        
        // Let's assume input is Magnitude Spectrogram [1, Frames, Bins, 1] (Mono) or [1, Frames, Bins, 2] (Stereo)
        // For simple Mono pipeline:
        int frames = stft.Length;
        int bins = stft[0].Length;
        
        var tensor = new DenseTensor<float>(new[] { 1, frames, bins, 1 }); // Batch=1, Channels=1 (Mono)
        
        for (int i = 0; i < frames; i++)
        {
            for (int k = 0; k < bins; k++)
            {
                // Log1p Magnitude Scaling (Common in Spleeter)
                 // T = log(1 + |S|)
                 double mag = stft[i][k].Magnitude;
                 // tensor[0, i, k, 0] = (float)Math.Log(1.0 + mag);
                 tensor[0, i, k, 0] = (float)mag; // Or raw magnitude
            }
        }
        return tensor;
    }
}
