using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;



/// <summary>
/// #285 sanity check: runs the voice/instrumental head over audio files (opened read-only, decoded
/// to a temp WAV that is deleted) and prints density, span and class per file. No database.
/// Usage: GenreBenchmark --vocals file1.flac file2.mp3 …
/// </summary>
internal static class VocalBench
{
    public static async Task<int> RunAsync(string[] files)
    {
        using var extractor = new DiscogsEffnetEmbeddingExtractor(NullLogger<DiscogsEffnetEmbeddingExtractor>.Instance);
        using var heads = new EffnetClassifierHeadService(NullLogger<EffnetClassifierHeadService>.Instance);
        if (!extractor.IsAvailable || !heads.IsVoiceAvailable)
        {
            Console.Error.WriteLine("EffNet or voice_instrumental model missing under Tools/Essentia/models.");
            return 2;
        }
        var decoder = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
        Console.WriteLine("density  start    end      type          intensity  file");
        foreach (var file in files)
        {
            string? wav = null;
            try
            {
                wav = await decoder.DecodeToTempWavAsync(new TrackAudioSource(file));
                var (samples, rate, channels) = AudioIngestionPipeline.ReadPcmFloat(wav);
                var mono = new float[samples.Length / Math.Max(1, channels)];
                for (var i = 0; i < mono.Length; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
                    mono[i] = sum / channels;
                }
                var effnet = await extractor.ExtractAsync(EffnetMelSpectrogramExtractor.ResampleTo16k(mono, rate));
                if (effnet is not { } e) { Console.WriteLine($"   —                                        {Path.GetFileName(file)} (no embedding)"); continue; }
                var voice = heads.PredictVoiceByPatch(e.PatchEmbeddings);
                if (voice is null) { Console.WriteLine($"   —                                        {Path.GetFileName(file)} (voice head failed)"); continue; }
                var r = VocalActivity.FromPatchProbabilities(voice, EffnetMelSpectrogramExtractor.PatchSeconds);
                string T(float? s) => s is float v ? TimeSpan.FromSeconds(v).ToString(@"m\:ss", CultureInfo.InvariantCulture) : "—";
                string D(float th) => (voice.Count(p => p > th) / (float)voice.Length).ToString("0.00", CultureInfo.InvariantCulture);
                Console.WriteLine($"{r.Density,6:0.00}   {T(r.StartSeconds),-7}  {T(r.EndSeconds),-7}  {r.Type,-12}  {r.Intensity,8:0.00}   >.8 {D(0.8f)} >.9 {D(0.9f)} >.95 {D(0.95f)} >.98 {D(0.98f)}  mean {voice.Average():0.00}  {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  skipped {Path.GetFileName(file)}: {ex.Message}");
            }
            finally
            {
                if (wav is not null)
                {
                    try { File.Delete(wav); }
                    catch (IOException ex) { Console.Error.WriteLine($"  could not delete temp file {wav}: {ex.Message}"); }
                }
            }
        }
        return 0;
    }
}
