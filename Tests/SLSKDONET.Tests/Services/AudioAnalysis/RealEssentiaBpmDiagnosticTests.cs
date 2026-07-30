using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.AudioAnalysis
{
    /// <summary>
    /// Manual diagnostic, not part of the regular pass/fail suite: point it at a real audio file
    /// via the ORBIT_TEST_AUDIO_FILE environment variable and it runs the real Essentia binary +
    /// BpmDetectionService against it, printing the raw rhythm data and the final corrected BPM.
    /// Skips (does not fail) when the env var isn't set, so it's safe to leave in the suite.
    /// </summary>
    public class RealEssentiaBpmDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public RealEssentiaBpmDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Manual_RealFileBpmDiagnostic()
        {
            var path = Environment.GetEnvironmentVariable("ORBIT_TEST_AUDIO_FILE");
            if (string.IsNullOrWhiteSpace(path))
            {
                _output.WriteLine("Set ORBIT_TEST_AUDIO_FILE to a real audio file path to run this diagnostic. Skipping.");
                return;
            }

            Assert.True(File.Exists(path), $"ORBIT_TEST_AUDIO_FILE points to a missing file: {path}");

            var essentia = new EssentiaRunner(NullLogger<EssentiaRunner>.Instance);
            Assert.True(essentia.IsAvailable, "Essentia binary not found — cannot run diagnostic.");

            var ingestion = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
            var tempWav = await ingestion.DecodeToTempWavAsync(new TrackAudioSource(path));

            try
            {
                var output = await essentia.RunAsync(tempWav);
                Assert.NotNull(output);

                var rhythm = output!.Rhythm;
                _output.WriteLine($"File: {path}");
                _output.WriteLine($"Raw Essentia BPM:      {rhythm?.Bpm}");
                _output.WriteLine($"Raw BPM confidence:    {rhythm?.BpmConfidence}");
                _output.WriteLine($"Onset rate (per sec):  {rhythm?.OnsetRate}");
                _output.WriteLine($"Danceability:          {rhythm?.Danceability}");
                _output.WriteLine($"Histogram length:      {rhythm?.BpmHistogram?.Length ?? 0}");

                if (rhythm?.BpmHistogram is { Length: > 0 } hist)
                {
                    // Print the top-5 histogram bins by weight so the half-time/double-time
                    // energy distribution is visible at a glance.
                    var topBins = new System.Collections.Generic.List<(int Bpm, float Weight)>();
                    for (int i = 0; i < hist.Length; i++)
                        if (hist[i] > 0f) topBins.Add((i + 1, hist[i]));
                    topBins.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                    _output.WriteLine("Top histogram bins (BPM: weight):");
                    foreach (var (bpm, weight) in topBins.GetRange(0, Math.Min(5, topBins.Count)))
                        _output.WriteLine($"  {bpm}: {weight}");
                }

                var bpmDetector = new BpmDetectionService();
                var features = new AudioFeaturesEntity();
                bpmDetector.Detect(output!, features);

                _output.WriteLine("");
                _output.WriteLine($"Final BPM:             {features.Bpm}");
                _output.WriteLine($"Final confidence:      {features.BpmConfidence}");
                _output.WriteLine($"Anomalies:             {features.AnomaliesJson}");
            }
            finally
            {
                try { File.Delete(tempWav); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Dumps the raw "rhythm" JSON object from the real Essentia binary, bypassing the
        /// strongly-typed EssentiaOutput mapping entirely — used to verify the actual field
        /// names/values the binary produces (e.g. confirming whether BpmConfidence=0 is a real
        /// Essentia output or a JSON property-name mismatch in EssentiaModels.cs).
        /// </summary>
        [Fact]
        public async Task Manual_RealFileRawEssentiaJsonDump()
        {
            var path = Environment.GetEnvironmentVariable("ORBIT_TEST_AUDIO_FILE");
            if (string.IsNullOrWhiteSpace(path))
            {
                _output.WriteLine("Set ORBIT_TEST_AUDIO_FILE to a real audio file path to run this diagnostic. Skipping.");
                return;
            }

            var ingestion = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
            var tempWav = await ingestion.DecodeToTempWavAsync(new TrackAudioSource(path));

            string binaryPath = Path.Combine(AppContext.BaseDirectory, "Tools", "essentia_streaming_extractor_music.exe");
            string jsonOut = Path.Combine(Path.GetTempPath(), $"orbit_raw_essentia_{Guid.NewGuid():N}.json");

            try
            {
                Assert.True(File.Exists(binaryPath), $"Essentia binary not found at {binaryPath}");

                var psi = new ProcessStartInfo(binaryPath, $"\"{tempWav}\" \"{jsonOut}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi)!;
                await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                _output.WriteLine($"Exit code: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    _output.WriteLine($"Stderr: {stderr}");

                Assert.True(File.Exists(jsonOut), "No JSON output produced");

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonOut));
                if (doc.RootElement.TryGetProperty("rhythm", out var rhythm))
                {
                    _output.WriteLine("Raw 'rhythm' object keys/values:");
                    foreach (var prop in rhythm.EnumerateObject())
                    {
                        var valuePreview = prop.Value.ValueKind == JsonValueKind.Array
                            ? $"[array, length={prop.Value.GetArrayLength()}]"
                            : prop.Value.ToString();
                        _output.WriteLine($"  {prop.Name}: {valuePreview}");
                    }
                }
                else
                {
                    _output.WriteLine("No 'rhythm' key found in root JSON object.");
                }
            }
            finally
            {
                try { File.Delete(tempWav); } catch { }
                try { File.Delete(jsonOut); } catch { }
            }
        }
    }
}
