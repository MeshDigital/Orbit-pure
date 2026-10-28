using System;
using System.Numerics;
using NWaves.Transforms;

namespace SLSKDONET.Engine.Analysis.CueDetr;

/// <summary>
/// Input side of CUE-DETR (ETH-DISCO, "Cue Point Estimation using Object Detection", MIT
/// licence): the model looks at a track's mel spectrogram rendered as a viridis image and
/// "detects" cue points as boxes. This reproduces the reference predict.py step for step:
///
///   librosa.load (22050 Hz mono) → melspectrogram(n_fft 2048, hop 512, 128 Slaney mels)
///   → power_to_db(ref=max, top_db 80) → flip vertically → viridis RGB bytes
///   → 355-frame windows at 75 % overlap, 266 frames of ramp padding at the edges
///   → DetrImageProcessor (rescale 1/255, ImageNet normalise), CHW.
///
/// Checked against Tools/cue-detr/reference.py by the CueDetr parity tests (bit-exact image
/// windows for the same audio; the resampler is the only intended difference from librosa).
/// </summary>
public static class CueDetrFrontEnd
{
    public const int SampleRate = 22050;
    public const int NFft = 2048;
    public const int Hop = 512;
    public const int Mels = 128;
    public const int WindowWidth = 355;
    public const int Padding = 266;
    public const double Overlap = 0.75;
    private const double Stride = WindowWidth * (1 - Overlap); // 88.75 frames

    public static double FramesToSeconds(int frame) => frame * (double)Hop / SampleRate;

    // ── Resampling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Band-limited resample of mono audio to 22050 Hz (Kaiser-windowed sinc, passband to ~91 % of
    /// the new Nyquist like libsoxr's HQ setting that librosa.load uses). ORBIT's analysis PCM is
    /// 44.1 kHz, where this is an exact 2:1 decimation with a fixed kernel.
    /// </summary>
    public static float[] ResampleTo22050(float[] mono, int sourceRate)
    {
        if (sourceRate == SampleRate || mono.Length == 0) return mono;
        double ratio = (double)sourceRate / SampleRate;
        if (ratio < 1) throw new ArgumentOutOfRangeException(nameof(sourceRate), "Upsampling is not needed for CUE-DETR input.");

        const double passband = 0.913, beta = 10.0;
        double cutoff = 0.5 * (1 + passband) / 2 / ratio;            // cycles per input sample (mid-transition)
        double transition = 0.5 * (1 - passband) / ratio;
        int half = (int)Math.Ceiling((96.0 - 8) / (2.285 * 2 * Math.PI * transition) / 2) + 1;
        int outLength = (int)Math.Ceiling(mono.Length / ratio);
        var output = new float[outLength];

        if (Math.Abs(ratio - Math.Round(ratio)) < 1e-9)
        {
            int step = (int)Math.Round(ratio);
            var kernel = new float[2 * half + 1];
            for (int k = -half; k <= half; k++) kernel[k + half] = (float)Tap(k, cutoff, half, beta);
            // Zero-padded copy so every output sample is one contiguous dot product.
            var padded = new float[mono.Length + 2 * half + 1];
            Array.Copy(mono, 0, padded, half, mono.Length);
            for (int i = 0; i < outLength; i++)
                output[i] = Dot(padded.AsSpan(i * step, kernel.Length), kernel);
            return output;
        }

        for (int i = 0; i < outLength; i++)
        {
            double centre = i * ratio;
            int c = (int)Math.Floor(centre);
            double acc = 0;
            for (int n = c - half; n <= c + half + 1; n++)
            {
                if ((uint)n >= (uint)mono.Length) continue;
                acc += mono[n] * Tap(n - centre, cutoff, half + 1, beta);
            }
            output[i] = (float)acc;
        }
        return output;
    }

    private static double Tap(double x, double cutoff, int half, double beta)
    {
        double r = x / half;
        if (Math.Abs(r) > 1) return 0;
        double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
        return sinc * BesselI0(beta * Math.Sqrt(1 - r * r)) / BesselI0(beta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++) { term *= q / (k * k); sum += term; if (term < 1e-12 * sum) break; }
        return sum;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0; var acc = Vector<float>.Zero;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // ── Mel spectrogram (librosa defaults) ─────────────────────────────────

    /// <summary>
    /// librosa.power_to_db(melspectrogram(y, sr=22050, n_fft=2048), ref=np.max): row-major
    /// [<see cref="Mels"/> × frames], row 0 = lowest band. Frames = 1 + len(y) / 512.
    /// </summary>
    public static float[] MelDb(float[] y, out int frames)
    {
        frames = 1 + y.Length / Hop;
        int bins = NFft / 2 + 1;
        var filters = SlaneyMelFilterbank();
        var window = new float[NFft];
        for (int n = 0; n < NFft; n++) window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / NFft)); // periodic Hann

        var fft = new RealFft(NFft);
        var frame = new float[NFft];
        var re = new float[NFft];
        var im = new float[NFft];
        var power = new float[bins];
        var mel = new float[Mels * frames];
        int pad = NFft / 2;

        for (int t = 0; t < frames; t++)
        {
            int start = t * Hop - pad; // center=True, zero ("constant") padding
            for (int n = 0; n < NFft; n++)
            {
                int s = start + n;
                frame[n] = (uint)s < (uint)y.Length ? y[s] * window[n] : 0f;
            }
            fft.Direct(frame, re, im);
            for (int k = 0; k < bins; k++) power[k] = re[k] * re[k] + im[k] * im[k];
            for (int m = 0; m < Mels; m++)
            {
                var (first, weights) = filters[m];
                float e = 0;
                for (int k = 0; k < weights.Length; k++) e += weights[k] * power[first + k];
                mel[m * frames + t] = e;
            }
        }

        // power_to_db(ref=max, amin=1e-10, top_db=80)
        float peak = 0;
        foreach (var v in mel) if (v > peak) peak = v;
        double refDb = 10 * Math.Log10(Math.Max(1e-10, peak));
        float maxDb = float.NegativeInfinity;
        for (int i = 0; i < mel.Length; i++)
        {
            mel[i] = (float)(10 * Math.Log10(Math.Max(1e-10, mel[i])) - refDb);
            if (mel[i] > maxDb) maxDb = mel[i];
        }
        float floor = maxDb - 80f;
        for (int i = 0; i < mel.Length; i++) if (mel[i] < floor) mel[i] = floor;
        return mel;
    }

    /// <summary>librosa.filters.mel(sr=22050, n_fft=2048, n_mels=128) — Slaney scale and area
    /// normalisation — stored sparsely as (first bin, weights).</summary>
    internal static (int First, float[] Weights)[] SlaneyMelFilterbank()
    {
        int bins = NFft / 2 + 1;
        var melF = new double[Mels + 2];
        double maxMel = HzToMel(SampleRate / 2.0);
        for (int i = 0; i < melF.Length; i++) melF[i] = MelToHz(maxMel * i / (Mels + 1));
        var fftFreqs = new double[bins];
        for (int k = 0; k < bins; k++) fftFreqs[k] = (double)SampleRate / 2 * k / (bins - 1);

        var result = new (int, float[])[Mels];
        for (int m = 0; m < Mels; m++)
        {
            double lowDiff = melF[m + 1] - melF[m], highDiff = melF[m + 2] - melF[m + 1];
            double enorm = 2.0 / (melF[m + 2] - melF[m]);
            int first = -1, last = -1;
            var w = new float[bins];
            for (int k = 0; k < bins; k++)
            {
                double lower = (fftFreqs[k] - melF[m]) / lowDiff;
                double upper = (melF[m + 2] - fftFreqs[k]) / highDiff;
                double v = Math.Max(0, Math.Min(lower, upper));
                w[k] = (float)(v * enorm);
                if (w[k] != 0) { if (first < 0) first = k; last = k; }
            }
            result[m] = first < 0 ? (0, Array.Empty<float>()) : (first, w[first..(last + 1)]);
        }
        return result;
    }

    private const double FSp = 200.0 / 3, MinLogHz = 1000.0, MinLogMel = MinLogHz / FSp;
    private static readonly double LogStep = Math.Log(6.4) / 27.0;

    private static double HzToMel(double hz) =>
        hz >= MinLogHz ? MinLogMel + Math.Log(hz / MinLogHz) / LogStep : hz / FSp;

    private static double MelToHz(double mel) =>
        mel >= MinLogMel ? MinLogHz * Math.Exp(LogStep * (mel - MinLogMel)) : FSp * mel;

    // ── Image ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The dB spectrogram flipped vertically (high bands on top) and coloured with matplotlib's
    /// viridis over its own min..max, as RGB bytes [128 × frames × 3] (row-major, HWC).
    /// </summary>
    public static byte[] ToImage(float[] melDb, int frames)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (var v in melDb) { if (v < min) min = v; if (v > max) max = v; }
        float range = max - min;
        var image = new byte[Mels * frames * 3];
        for (int row = 0; row < Mels; row++)
        {
            int src = (Mels - 1 - row) * frames;
            for (int t = 0; t < frames; t++)
            {
                // matplotlib: Normalize in float32, × N (256), N → N-1, truncate to int.
                float x = range > 0 ? (melDb[src + t] - min) / range : 0f;
                x *= 256f;
                int idx = x >= 256f ? 255 : (int)x;
                int o = (row * frames + t) * 3, l = idx * 3;
                image[o] = Viridis[l]; image[o + 1] = Viridis[l + 1]; image[o + 2] = Viridis[l + 2];
            }
        }
        return image;
    }

    // ── Windows ─────────────────────────────────────────────────────────────

    /// <summary>Left edge (in frames, may be negative) of every model window over an image
    /// <paramref name="frames"/> wide.</summary>
    public static int[] WindowBorders(int frames)
    {
        int n = (int)Math.Floor((frames + Padding) / Stride);
        var borders = new int[n];
        for (int i = 0; i < n; i++) borders[i] = (int)Math.Floor(i * Stride) - Padding;
        return borders;
    }

    /// <summary>
    /// The 128 × 355 RGB window starting at frame <paramref name="left"/>, padded where it runs off
    /// either end with numpy's 'linear_ramp' (a ramp from 0 up to the edge column, floored).
    /// </summary>
    public static byte[] Window(byte[] image, int frames, int left)
    {
        const int W = WindowWidth;
        var win = new byte[Mels * W * 3];
        int right = left + W;
        int from = Math.Max(0, left), to = Math.Min(frames, right);
        for (int row = 0; row < Mels; row++)
        {
            int rowBase = row * frames * 3, winBase = row * W * 3;
            Buffer.BlockCopy(image, rowBase + from * 3, win, winBase + (from - left) * 3, (to - from) * 3);

            if (left < 0)
            {
                // np.pad(..., ((0,0), (pad,0), (0,0)), mode='linear_ramp'): column k of the pad is
                // floor(k * edge / pad). (Only reached when the segment is image[:, :right].)
                int pad = -left;
                for (int c = 0; c < 3; c++)
                {
                    double step = image[rowBase + c] / (double)pad;
                    for (int k = 0; k < pad; k++) win[winBase + k * 3 + c] = (byte)Math.Floor(k * step);
                }
            }
            else if (right > frames)
            {
                // Right ramp is the reversed linspace: column j after the data is floor((pad-1-j) * edge / pad).
                int pad = right - frames, dataCols = frames - left;
                for (int c = 0; c < 3; c++)
                {
                    double step = image[rowBase + (frames - 1) * 3 + c] / (double)pad;
                    for (int j = 0; j < pad; j++) win[winBase + (dataCols + j) * 3 + c] = (byte)Math.Floor((pad - 1 - j) * step);
                }
            }
        }
        return win;
    }

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>DetrImageProcessor normalisation of one HWC window into CHW floats.</summary>
    public static void Normalise(byte[] window, Span<float> chw)
    {
        int plane = Mels * WindowWidth;
        for (int p = 0; p < plane; p++)
            for (int c = 0; c < 3; c++)
                chw[c * plane + p] = (window[p * 3 + c] / 255f - Mean[c]) / Std[c];
    }

    // matplotlib viridis, (lut * 255).astype(uint8) — Tools/cue-detr/reference.py lut
    private static readonly byte[] Viridis =
    {
        0x44, 0x01, 0x54, 0x44, 0x02, 0x55, 0x44, 0x03, 0x57, 0x45, 0x05, 0x58, 0x45, 0x06, 0x5A, 0x45, 0x08, 0x5B, 0x46, 0x09, 0x5C, 0x46, 0x0B, 0x5E,
        0x46, 0x0C, 0x5F, 0x46, 0x0E, 0x61, 0x47, 0x0F, 0x62, 0x47, 0x11, 0x63, 0x47, 0x12, 0x65, 0x47, 0x14, 0x66, 0x47, 0x15, 0x67, 0x47, 0x16, 0x69,
        0x47, 0x18, 0x6A, 0x48, 0x19, 0x6B, 0x48, 0x1A, 0x6C, 0x48, 0x1C, 0x6E, 0x48, 0x1D, 0x6F, 0x48, 0x1E, 0x70, 0x48, 0x20, 0x71, 0x48, 0x21, 0x72,
        0x48, 0x22, 0x73, 0x48, 0x23, 0x74, 0x47, 0x25, 0x75, 0x47, 0x26, 0x76, 0x47, 0x27, 0x77, 0x47, 0x28, 0x78, 0x47, 0x2A, 0x79, 0x47, 0x2B, 0x7A,
        0x47, 0x2C, 0x7B, 0x46, 0x2D, 0x7C, 0x46, 0x2F, 0x7C, 0x46, 0x30, 0x7D, 0x46, 0x31, 0x7E, 0x45, 0x32, 0x7F, 0x45, 0x34, 0x7F, 0x45, 0x35, 0x80,
        0x45, 0x36, 0x81, 0x44, 0x37, 0x81, 0x44, 0x39, 0x82, 0x43, 0x3A, 0x83, 0x43, 0x3B, 0x83, 0x43, 0x3C, 0x84, 0x42, 0x3D, 0x84, 0x42, 0x3E, 0x85,
        0x42, 0x40, 0x85, 0x41, 0x41, 0x86, 0x41, 0x42, 0x86, 0x40, 0x43, 0x87, 0x40, 0x44, 0x87, 0x3F, 0x45, 0x87, 0x3F, 0x47, 0x88, 0x3E, 0x48, 0x88,
        0x3E, 0x49, 0x89, 0x3D, 0x4A, 0x89, 0x3D, 0x4B, 0x89, 0x3D, 0x4C, 0x89, 0x3C, 0x4D, 0x8A, 0x3C, 0x4E, 0x8A, 0x3B, 0x50, 0x8A, 0x3B, 0x51, 0x8A,
        0x3A, 0x52, 0x8B, 0x3A, 0x53, 0x8B, 0x39, 0x54, 0x8B, 0x39, 0x55, 0x8B, 0x38, 0x56, 0x8B, 0x38, 0x57, 0x8C, 0x37, 0x58, 0x8C, 0x37, 0x59, 0x8C,
        0x36, 0x5A, 0x8C, 0x36, 0x5B, 0x8C, 0x35, 0x5C, 0x8C, 0x35, 0x5D, 0x8C, 0x34, 0x5E, 0x8D, 0x34, 0x5F, 0x8D, 0x33, 0x60, 0x8D, 0x33, 0x61, 0x8D,
        0x32, 0x62, 0x8D, 0x32, 0x63, 0x8D, 0x31, 0x64, 0x8D, 0x31, 0x65, 0x8D, 0x31, 0x66, 0x8D, 0x30, 0x67, 0x8D, 0x30, 0x68, 0x8D, 0x2F, 0x69, 0x8D,
        0x2F, 0x6A, 0x8D, 0x2E, 0x6B, 0x8E, 0x2E, 0x6C, 0x8E, 0x2E, 0x6D, 0x8E, 0x2D, 0x6E, 0x8E, 0x2D, 0x6F, 0x8E, 0x2C, 0x70, 0x8E, 0x2C, 0x71, 0x8E,
        0x2C, 0x72, 0x8E, 0x2B, 0x73, 0x8E, 0x2B, 0x74, 0x8E, 0x2A, 0x75, 0x8E, 0x2A, 0x76, 0x8E, 0x2A, 0x77, 0x8E, 0x29, 0x78, 0x8E, 0x29, 0x79, 0x8E,
        0x28, 0x7A, 0x8E, 0x28, 0x7A, 0x8E, 0x28, 0x7B, 0x8E, 0x27, 0x7C, 0x8E, 0x27, 0x7D, 0x8E, 0x27, 0x7E, 0x8E, 0x26, 0x7F, 0x8E, 0x26, 0x80, 0x8E,
        0x26, 0x81, 0x8E, 0x25, 0x82, 0x8E, 0x25, 0x83, 0x8D, 0x24, 0x84, 0x8D, 0x24, 0x85, 0x8D, 0x24, 0x86, 0x8D, 0x23, 0x87, 0x8D, 0x23, 0x88, 0x8D,
        0x23, 0x89, 0x8D, 0x22, 0x89, 0x8D, 0x22, 0x8A, 0x8D, 0x22, 0x8B, 0x8D, 0x21, 0x8C, 0x8D, 0x21, 0x8D, 0x8C, 0x21, 0x8E, 0x8C, 0x20, 0x8F, 0x8C,
        0x20, 0x90, 0x8C, 0x20, 0x91, 0x8C, 0x1F, 0x92, 0x8C, 0x1F, 0x93, 0x8B, 0x1F, 0x94, 0x8B, 0x1F, 0x95, 0x8B, 0x1F, 0x96, 0x8B, 0x1E, 0x97, 0x8A,
        0x1E, 0x98, 0x8A, 0x1E, 0x99, 0x8A, 0x1E, 0x99, 0x8A, 0x1E, 0x9A, 0x89, 0x1E, 0x9B, 0x89, 0x1E, 0x9C, 0x89, 0x1E, 0x9D, 0x88, 0x1E, 0x9E, 0x88,
        0x1E, 0x9F, 0x88, 0x1E, 0xA0, 0x87, 0x1F, 0xA1, 0x87, 0x1F, 0xA2, 0x86, 0x1F, 0xA3, 0x86, 0x20, 0xA4, 0x85, 0x20, 0xA5, 0x85, 0x21, 0xA6, 0x85,
        0x21, 0xA7, 0x84, 0x22, 0xA7, 0x84, 0x23, 0xA8, 0x83, 0x23, 0xA9, 0x82, 0x24, 0xAA, 0x82, 0x25, 0xAB, 0x81, 0x26, 0xAC, 0x81, 0x27, 0xAD, 0x80,
        0x28, 0xAE, 0x7F, 0x29, 0xAF, 0x7F, 0x2A, 0xB0, 0x7E, 0x2B, 0xB1, 0x7D, 0x2C, 0xB1, 0x7D, 0x2E, 0xB2, 0x7C, 0x2F, 0xB3, 0x7B, 0x30, 0xB4, 0x7A,
        0x32, 0xB5, 0x7A, 0x33, 0xB6, 0x79, 0x35, 0xB7, 0x78, 0x36, 0xB8, 0x77, 0x38, 0xB9, 0x76, 0x39, 0xB9, 0x76, 0x3B, 0xBA, 0x75, 0x3D, 0xBB, 0x74,
        0x3E, 0xBC, 0x73, 0x40, 0xBD, 0x72, 0x42, 0xBE, 0x71, 0x44, 0xBE, 0x70, 0x45, 0xBF, 0x6F, 0x47, 0xC0, 0x6E, 0x49, 0xC1, 0x6D, 0x4B, 0xC2, 0x6C,
        0x4D, 0xC2, 0x6B, 0x4F, 0xC3, 0x69, 0x51, 0xC4, 0x68, 0x53, 0xC5, 0x67, 0x55, 0xC6, 0x66, 0x57, 0xC6, 0x65, 0x59, 0xC7, 0x64, 0x5B, 0xC8, 0x62,
        0x5E, 0xC9, 0x61, 0x60, 0xC9, 0x60, 0x62, 0xCA, 0x5F, 0x64, 0xCB, 0x5D, 0x67, 0xCC, 0x5C, 0x69, 0xCC, 0x5B, 0x6B, 0xCD, 0x59, 0x6D, 0xCE, 0x58,
        0x70, 0xCE, 0x56, 0x72, 0xCF, 0x55, 0x74, 0xD0, 0x54, 0x77, 0xD0, 0x52, 0x79, 0xD1, 0x51, 0x7C, 0xD2, 0x4F, 0x7E, 0xD2, 0x4E, 0x81, 0xD3, 0x4C,
        0x83, 0xD3, 0x4B, 0x86, 0xD4, 0x49, 0x88, 0xD5, 0x47, 0x8B, 0xD5, 0x46, 0x8D, 0xD6, 0x44, 0x90, 0xD6, 0x43, 0x92, 0xD7, 0x41, 0x95, 0xD7, 0x3F,
        0x97, 0xD8, 0x3E, 0x9A, 0xD8, 0x3C, 0x9D, 0xD9, 0x3A, 0x9F, 0xD9, 0x38, 0xA2, 0xDA, 0x37, 0xA5, 0xDA, 0x35, 0xA7, 0xDB, 0x33, 0xAA, 0xDB, 0x32,
        0xAD, 0xDC, 0x30, 0xAF, 0xDC, 0x2E, 0xB2, 0xDD, 0x2C, 0xB5, 0xDD, 0x2B, 0xB7, 0xDD, 0x29, 0xBA, 0xDE, 0x27, 0xBD, 0xDE, 0x26, 0xBF, 0xDF, 0x24,
        0xC2, 0xDF, 0x22, 0xC5, 0xDF, 0x21, 0xC7, 0xE0, 0x1F, 0xCA, 0xE0, 0x1E, 0xCD, 0xE0, 0x1D, 0xCF, 0xE1, 0x1C, 0xD2, 0xE1, 0x1B, 0xD4, 0xE1, 0x1A,
        0xD7, 0xE2, 0x19, 0xDA, 0xE2, 0x18, 0xDC, 0xE2, 0x18, 0xDF, 0xE3, 0x18, 0xE1, 0xE3, 0x18, 0xE4, 0xE3, 0x18, 0xE7, 0xE4, 0x19, 0xE9, 0xE4, 0x19,
        0xEC, 0xE4, 0x1A, 0xEE, 0xE5, 0x1B, 0xF1, 0xE5, 0x1C, 0xF3, 0xE5, 0x1E, 0xF6, 0xE6, 0x1F, 0xF8, 0xE6, 0x21, 0xFA, 0xE6, 0x22, 0xFD, 0xE7, 0x24,
    };
}
