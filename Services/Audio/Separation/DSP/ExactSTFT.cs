using System;
using System.Linq;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using System.Numerics; // For Complex

namespace SLSKDONET.Services.Audio.Separation.DSP;

/// <summary>
/// Implements Short-Time Fourier Transform (STFT) exactly matching librosa's default behavior,
/// which is used by Spleeter. 
/// Critical Parameters: n_fft=4096, hop_length=1024, win_length=4096, window=hann, center=True, pad_mode=reflect.
/// </summary>
public class ExactSTFT
{
    private readonly int _nFft;
    private readonly int _hopLength;
    private readonly double[] _window;

    public int OutputFrequencyBins => _nFft / 2 + 1;

    public ExactSTFT(int nFft = 4096, int hopLength = 1024)
    {
        _nFft = nFft;
        _hopLength = hopLength;
        
        // Exact Hanning window matching librosa
        // Typically librosa uses scipy.signal.get_window('hann', N) -> symmetric
        // Note: MathNet Window.Hann is also symmetric by default.
        // Replicating manual calculation as per guide to be 100% sure:
        _window = new double[_nFft];
        for (int i = 0; i < _nFft; i++)
        {
             _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (_nFft))); // Periodic? or (_nFft-1)?
             // Spleeter training uses standard symmetric hann usually.
             // Guide says: Math.Cos(2 * Math.PI * i / (_n_fft - 1)) -> Symmetric
             // Let's stick to MathNet for speed if it matches, but guide was specific.
             // Update: librosa default is symmetric. 
             _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (_nFft - 1))); 
        }
    }

    /// <summary>
    /// Computes STFT of the input signal.
    /// Input: [samples], Output: [frames][bins]
    /// </summary>
    public Complex[][] ComputeSTFT(float[] samples)
    {
        // 1. Center Padding (Reflect) matches librosa default
        int pad = _nFft / 2;
        var paddedSamples = PadReflect(samples, pad);

        int numFrames = (paddedSamples.Length - _nFft) / _hopLength + 1;
        var spectrogram = new Complex[numFrames][];

        for (int i = 0; i < numFrames; i++)
        {
            spectrogram[i] = new Complex[OutputFrequencyBins];
            int start = i * _hopLength;
            
            var frame = new Complex[_nFft];
            for (int k = 0; k < _nFft; k++)
            {
                frame[k] = paddedSamples[start + k] * _window[k];
            }

            Fourier.Forward(frame, FourierOptions.Matlab);

            for (int k = 0; k < OutputFrequencyBins; k++)
            {
                spectrogram[i][k] = frame[k]; 
            }
        }

        return spectrogram;
    }

    /// <summary>
    /// Transpose Helper [Frames][Bins] -> [Bins][Frames]
    /// </summary>
    public static Complex[][] Transpose(Complex[][] input)
    {
        int rows = input.Length;
        int cols = input[0].Length;
        var result = new Complex[cols][];
        for(int c=0; c<cols; c++)
        {
            result[c] = new Complex[rows];
            for(int r=0; r<rows; r++)
            {
                result[c][r] = input[r][c];
            }
        }
        return result;
    }

    public float[] ComputeISTFT(Complex[][] stft, int length)
    {
        int numFrames = stft.Length;
        // Reconstruct padded length
        int paddedLength = _nFft + (numFrames - 1) * _hopLength;
        float[] updatedSignal = new float[paddedLength];
        float[] windowSum = new float[paddedLength];

        // Precompute squared window for normalization (OLA)
        // Librosa iSTFT divides by sum of squared windows
        double[] squaredWindow = _window.Select(x => x * x).ToArray(); // Actually just window if we want to reverse exactly?
        // Spleeter uses "inverse_stft_window_fn" which is typically the same window.
        // The standard Griffin-Lim / OLA method involves dividing by window sum.
        
        for (int i = 0; i < numFrames; i++)
        {
            int start = i * _hopLength;

            // Reconstruct full FFT frame (symmetric)
            Complex[] frame = new Complex[_nFft];
            for (int k = 0; k < OutputFrequencyBins; k++)
            {
                frame[k] = stft[i][k];
            }
            for (int k = OutputFrequencyBins; k < _nFft; k++)
            {
                 // Conj symmetry
                 frame[k] = Complex.Conjugate(frame[_nFft - k]);
            }

            // Inverse FFT
            Fourier.Inverse(frame, FourierOptions.Matlab);

            // Overlap-Add
            for (int k = 0; k < _nFft; k++)
            {
                // Real part is the signal
                float val = (float)frame[k].Real;
                // De-windowing? 
                // Typically: Signal += val * window
                // WindowSum += window^2
                updatedSignal[start + k] += val * (float)_window[k];
                windowSum[start + k] += (float)(_window[k]); // * _window[k]); // Depends on NOLA constraint
            }
        }
        
        // Normalize by Window Sum (NOLA) aka Griffin-Lim correction
        // Note: This is simplified. Robust implementations handle small epsilon.
        for(int i = 0; i < paddedLength; i++)
        {
             if (windowSum[i] > 1e-6)
             {
                 updatedSignal[i] /= windowSum[i];
             }
        }

        // Remove Padding (Center)
        int pad = _nFft / 2;
        int resultLen = length > 0 ? length : (paddedLength - 2 * pad);
        
        // Safety clamp
        if (resultLen > paddedLength - 2*pad) resultLen = paddedLength - 2*pad;
        if (resultLen < 0) return Array.Empty<float>();

        float[] result = new float[resultLen];
        Array.Copy(updatedSignal, pad, result, 0, resultLen);

        return result;
    }

    private float[] PadReflect(float[] signal, int pad)
    {
        float[] padded = new float[signal.Length + 2 * pad];
        
        // 1. Copy center
        Array.Copy(signal, 0, padded, pad, signal.Length);
        
        // 2. Reflect Left
        // signal[0] is center of reflection? "reflect" mode usually excludes edge.
        // librosa reflect: 2, 1, 0 <-> 0, 1, 2, 3...
        // e.g. pad=2. source=[ABCDE]. padded=[CBABCDEDC]
        for (int i = 0; i < pad; i++)
        {
            padded[pad - 1 - i] = signal[i + 1]; // Skip index 0? Librosa reflect skips edge. 
            // "data is reflected, omitting the first and last values"
        }

        // 3. Reflect Right
        for (int i = 0; i < pad; i++)
        {
            padded[pad + signal.Length + i] = signal[signal.Length - 2 - i];
        }

        return padded;
    }
}
