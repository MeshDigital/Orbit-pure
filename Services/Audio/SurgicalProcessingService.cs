using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services.Audio
{
    /// <summary>
    /// Phase 2: Surgical Editing Engine.
    /// Handles lossless audio stitching, segment cutting, and range-selective stem separation.
    /// </summary>
    public interface ISurgicalProcessingService
    {
        Task<string> CutAndCombineSegmentsAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default);
        Task<string> IsolateStemInRangeAsync(string sourcePath, float startTime, float duration, string stemType, CancellationToken ct = default);
        Task<bool> RenderPreviewAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default);

        /// <summary>
        /// Crossfades the tail of trackA into the head of trackB and renders the result to a temp
        /// file, for previewing a mix transition before committing to it.
        /// </summary>
        Task<string> RenderTransitionPreviewAsync(
            string trackAPath, double trackATailStartSeconds,
            string trackBPath, double trackBHeadDurationSeconds,
            double overlapSeconds, CancellationToken ct = default);

        /// <summary>
        /// Same idea as <see cref="RenderTransitionPreviewAsync"/>, but built from the real
        /// preset DSP (<see cref="SLSKDONET.Services.Timeline.TransitionDsp"/>) instead of a
        /// fixed triangular FFmpeg crossfade — so the Mix editor's Preview button actually hears
        /// the selected preset (Fade/Rise/Blend/Wave/Melt/...), not a generic approximation.
        /// Both tracks start from their own analysis-suggested (or saved) trigger point —
        /// <paramref name="trackBStartSeconds"/> is <em>not</em> always 0, e.g. a suggested
        /// mix-in that skips a low-energy intro straight to the first drop.
        /// </summary>
        Task<string> RenderTransitionPreviewAsync(
            string trackAPath, double trackATailStartSeconds,
            string trackBPath, double trackBStartSeconds,
            SLSKDONET.Models.Timeline.TransitionModel model,
            double projectBpm,
            CancellationToken ct = default,
            double leadInSeconds = 0,
            double tailSeconds = 0,
            double incomingPlaybackRate = 1.0);
    }

    public class SurgicalProcessingService : ISurgicalProcessingService
    {
        private readonly ILogger<SurgicalProcessingService> _logger;
        private readonly StemCacheService _stemCache;
        private readonly IStemSeparationService? _stemSeparator;
        private readonly string _surgicalWorkDir;
        private readonly string _ffmpegPath;

        public SurgicalProcessingService(
            ILogger<SurgicalProcessingService> logger,
            StemCacheService stemCache,
            IStemSeparationService? stemSeparator = null)
        {
            _logger = logger;
            _stemCache = stemCache;
            _stemSeparator = stemSeparator;
            _surgicalWorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "SurgicalTemp");
            Directory.CreateDirectory(_surgicalWorkDir);
            _ffmpegPath = AudioIngestionPipeline.ResolveFfmpegPath();
        }

        public async Task<string> CutAndCombineSegmentsAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("✂️ Surgical Surgery: Orchestrating FFmpeg for {Path}", sourcePath);

            var sortedSegments = segments.OrderBy(s => s.Start).ToList();
            if (!sortedSegments.Any()) return string.Empty;

            string outputFileName = $"Surgical_{Path.GetFileNameWithoutExtension(sourcePath)}_{DateTime.Now:yyyyMMddHHmmss}.flac";
            string outputPath = Path.Combine(_surgicalWorkDir, outputFileName);

            // FFmpeg Strategy:
            // 1. Trim each segment from the source into its own labelled stream.
            // 2. Chain acrossfade between consecutive segments (50ms) so cuts aren't audible clicks.
            var filterParts = new List<string>();
            for (int i = 0; i < sortedSegments.Count; i++)
            {
                var seg = sortedSegments[i];
                string start = seg.Start.ToString("F3", CultureInfo.InvariantCulture);
                string end = (seg.Start + seg.Duration).ToString("F3", CultureInfo.InvariantCulture);
                filterParts.Add($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[s{i}]");
            }

            string outputLabel;
            if (sortedSegments.Count == 1)
            {
                outputLabel = "s0";
            }
            else
            {
                string currentLabel = "s0";
                for (int i = 1; i < sortedSegments.Count; i++)
                {
                    string nextLabel = (i == sortedSegments.Count - 1) ? "out" : $"merged{i}";
                    filterParts.Add($"[{currentLabel}][s{i}]acrossfade=d=0.05:c1=tri:c2=tri[{nextLabel}]");
                    currentLabel = nextLabel;
                }
                outputLabel = currentLabel;
            }

            string fullFilter = string.Join(";", filterParts);
            string arguments = $"-y -i \"{sourcePath}\" -filter_complex \"{fullFilter}\" -map \"[{outputLabel}]\" -c:a flac \"{outputPath}\"";

            _logger.LogDebug("🎬 FFmpeg Surgical Command: {Args}", arguments);

            var (exitCode, stderr) = await RunFfmpegAsync(arguments, ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                TryDelete(outputPath);
                var summary = LastNonEmptyLine(stderr);
                _logger.LogError("Surgical cut FFmpeg failed (exit {Code}) for {Path}: {Summary}", exitCode, sourcePath, summary);
                throw new InvalidOperationException($"FFmpeg exit {exitCode}: {summary}");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                TryDelete(outputPath);
                throw new InvalidOperationException($"FFmpeg produced no output for '{sourcePath}'.");
            }

            return outputPath;
        }

        public async Task<string> IsolateStemInRangeAsync(string sourcePath, float startTime, float duration, string stemType, CancellationToken ct = default)
        {
            _logger.LogInformation("🔬 Surgical Surgery: Isolating {Stem} from {Start}s for {Duration}s", stemType, startTime, duration);

            string trackHash = Path.GetFileNameWithoutExtension(sourcePath);
            string modelTag = _stemSeparator?.ModelTag ?? "default";

            var cached = await _stemCache.TryGetCachedStemAsync(trackHash, startTime, duration, stemType, modelTag);
            if (cached != null) return cached;

            if (_stemSeparator == null || !_stemSeparator.IsAvailable)
            {
                throw new InvalidOperationException("No stem separation engine is available.");
            }

            if (!Enum.TryParse<StemType>(stemType, ignoreCase: true, out var parsedStemType))
            {
                throw new ArgumentException($"Unknown stem type '{stemType}'.", nameof(stemType));
            }

            // Trim the requested range first — separating just the needed slice is far cheaper
            // than separating the whole track for a short preview window.
            string trimmedPath = Path.Combine(_surgicalWorkDir, $"trim_{Guid.NewGuid():N}.wav");
            string start = startTime.ToString("F3", CultureInfo.InvariantCulture);
            string dur = duration.ToString("F3", CultureInfo.InvariantCulture);
            string trimArgs = $"-y -i \"{sourcePath}\" -ss {start} -t {dur} -ar 44100 -ac 2 -c:a pcm_f32le \"{trimmedPath}\"";

            var (trimExit, trimStderr) = await RunFfmpegAsync(trimArgs, ct).ConfigureAwait(false);
            if (trimExit != 0 || !File.Exists(trimmedPath))
            {
                TryDelete(trimmedPath);
                throw new InvalidOperationException($"FFmpeg trim failed (exit {trimExit}): {LastNonEmptyLine(trimStderr)}");
            }

            string separationOutputDir = Path.Combine(_surgicalWorkDir, $"sep_{Guid.NewGuid():N}");
            Directory.CreateDirectory(separationOutputDir);
            try
            {
                var stems = await _stemSeparator.SeparateWithProgressAsync(trimmedPath, separationOutputDir, progress: null, ct).ConfigureAwait(false);
                if (!stems.TryGetValue(parsedStemType, out var stemPath) || !File.Exists(stemPath))
                {
                    throw new InvalidOperationException($"Stem separation did not produce a '{stemType}' stem.");
                }

                await _stemCache.StoreStemAsync(trackHash, startTime, duration, stemType, stemPath, modelTag).ConfigureAwait(false);
                var cachedResult = await _stemCache.TryGetCachedStemAsync(trackHash, startTime, duration, stemType, modelTag).ConfigureAwait(false);
                return cachedResult ?? stemPath;
            }
            finally
            {
                TryDelete(trimmedPath);
                try { Directory.Delete(separationOutputDir, recursive: true); } catch { /* best effort cleanup */ }
            }
        }

        public async Task<bool> RenderPreviewAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Surgical Surgery: Preparing render preview for {Path}", sourcePath);

            var previewPath = await CutAndCombineSegmentsAsync(sourcePath, segments, ct);
            return !string.IsNullOrEmpty(previewPath);
        }

        public async Task<string> RenderTransitionPreviewAsync(
            string trackAPath, double trackATailStartSeconds,
            string trackBPath, double trackBHeadDurationSeconds,
            double overlapSeconds, CancellationToken ct = default)
        {
            _logger.LogInformation("🎚️ Surgical Surgery: Rendering transition preview (overlap {Overlap}s)", overlapSeconds);

            string outputPath = Path.Combine(_surgicalWorkDir, $"Transition_{Guid.NewGuid():N}.flac");

            string tailStart = trackATailStartSeconds.ToString("F3", CultureInfo.InvariantCulture);
            string headDuration = trackBHeadDurationSeconds.ToString("F3", CultureInfo.InvariantCulture);
            string overlap = overlapSeconds.ToString("F3", CultureInfo.InvariantCulture);

            string filter =
                $"[0:a]atrim=start={tailStart},asetpts=PTS-STARTPTS[a];" +
                $"[1:a]atrim=end={headDuration},asetpts=PTS-STARTPTS[b];" +
                $"[a][b]acrossfade=d={overlap}:c1=tri:c2=tri[out]";

            string arguments = $"-y -i \"{trackAPath}\" -i \"{trackBPath}\" -filter_complex \"{filter}\" -map \"[out]\" -c:a flac \"{outputPath}\"";

            var (exitCode, stderr) = await RunFfmpegAsync(arguments, ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                TryDelete(outputPath);
                throw new InvalidOperationException($"FFmpeg transition render failed (exit {exitCode}): {LastNonEmptyLine(stderr)}");
            }

            return outputPath;
        }

        public Task<string> RenderTransitionPreviewAsync(
            string trackAPath, double trackATailStartSeconds,
            string trackBPath, double trackBStartSeconds,
            SLSKDONET.Models.Timeline.TransitionModel model,
            double projectBpm,
            CancellationToken ct = default,
            double leadInSeconds = 0,
            double tailSeconds = 0,
            double incomingPlaybackRate = 1.0)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Rendering preset-driven transition preview ({Preset} DSP, {Bpm:0} BPM), A@{TailStart}s B@{HeadStart}s, lead-in {LeadIn}s, tail {Tail}s, B rate {Rate:0.000}x",
                    model.Type, projectBpm, trackATailStartSeconds, trackBStartSeconds, leadInSeconds, tailSeconds, incomingPlaybackRate);

                string outputPath = Path.Combine(_surgicalWorkDir, $"TransitionPreset_{Guid.NewGuid():N}.wav");

                using var readerA = new NAudio.Wave.AudioFileReader(trackAPath);
                using var readerB = new NAudio.Wave.AudioFileReader(trackBPath);

                // ExactSeek, not CurrentTime: Media Foundation (FLAC/M4A) drops a seek made before
                // the first read, which started the preview at 0:00 instead of the mix point.
                var leadStart = Math.Max(0, trackATailStartSeconds - Math.Max(0, leadInSeconds));
                ExactSeek.Seek(readerA, trackAPath, leadStart);
                ExactSeek.Seek(readerB, trackBPath, Math.Max(0, trackBStartSeconds));

                // TransitionDsp requires both providers to share one WaveFormat — real files
                // routinely differ in sample rate/channel count, so normalize B onto A's format.
                var target = readerA.WaveFormat;
                NAudio.Wave.ISampleProvider outgoing = readerA;
                NAudio.Wave.ISampleProvider incoming = NormalizeFormat(
                    ApplyPlaybackRate(readerB, incomingPlaybackRate), target);

                using var writer = new NAudio.Wave.WaveFileWriter(outputPath, target);
                var buffer = new float[target.Channels * 4096];

                // 1. Lead-in: the outgoing track alone, up to the mix-out point.
                long leadFrames = (long)Math.Round((trackATailStartSeconds - leadStart) * target.SampleRate);
                CopyFrames(outgoing, writer, buffer, leadFrames, target.Channels, ct);

                // 2. The blend itself.
                var mixed = SLSKDONET.Services.Timeline.TransitionDsp.Build(outgoing, incoming, model, projectBpm);
                long windowFrames = SLSKDONET.Services.Timeline.TransitionDsp.BeatsToSamples(
                    model.DurationBeats, projectBpm, target.SampleRate, target.Channels) / target.Channels;
                if (model.Type == SLSKDONET.Models.Timeline.TransitionType.DoubleDrop)
                {
                    windowFrames += SLSKDONET.Services.Timeline.TransitionDsp.BeatsToSamples(
                        model.LoopBars * 4.0, projectBpm, target.SampleRate, target.Channels) / target.Channels
                        * (Math.Max(1, model.LoopRepeats) + 1);
                }
                CopyFrames(mixed, writer, buffer, windowFrames, target.Channels, ct);

                // 3. Tail: the incoming track alone, so you hear where the blend lands.
                long tailFrames = (long)Math.Round(Math.Max(0, tailSeconds) * target.SampleRate);
                CopyFrames(incoming, writer, buffer, tailFrames, target.Channels, ct);

                return outputPath;
            }, ct);
        }

        private static void CopyFrames(NAudio.Wave.ISampleProvider source, NAudio.Wave.WaveFileWriter writer, float[] buffer, long frames, int channels, CancellationToken ct)
        {
            long written = 0;
            while (written < frames)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, (frames - written) * channels);
                int read = source.Read(buffer, 0, want);
                if (read == 0) break;
                writer.WriteSamples(buffer, 0, read);
                written += read / channels;
            }
        }

        /// <summary>Offline equivalent of the live deck's varispeed provider. WDL performs the
        /// resampling; the wrapper keeps the original output format so N samples are consumed at
        /// the original output rate, making playback faster or slower by the requested ratio.</summary>
        internal static NAudio.Wave.ISampleProvider ApplyPlaybackRate(NAudio.Wave.ISampleProvider source, double playbackRate)
        {
            if (playbackRate is not > 0 || double.IsNaN(playbackRate) || double.IsInfinity(playbackRate) || Math.Abs(playbackRate - 1.0) < 0.000001)
                return source;
            int resampledRate = (int)Math.Clamp(Math.Round(source.WaveFormat.SampleRate / playbackRate), 1000, 384000);
            var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, resampledRate);
            return new OriginalFormatSampleProvider(resampled, source.WaveFormat);
        }

        private sealed class OriginalFormatSampleProvider : NAudio.Wave.ISampleProvider
        {
            private readonly NAudio.Wave.ISampleProvider _source;
            public OriginalFormatSampleProvider(NAudio.Wave.ISampleProvider source, NAudio.Wave.WaveFormat format)
            {
                _source = source;
                WaveFormat = format;
            }
            public NAudio.Wave.WaveFormat WaveFormat { get; }
            public int Read(float[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
        }

        private static NAudio.Wave.ISampleProvider NormalizeFormat(NAudio.Wave.ISampleProvider source, NAudio.Wave.WaveFormat target)
        {
            NAudio.Wave.ISampleProvider result = source;

            if (result.WaveFormat.SampleRate != target.SampleRate)
            {
                result = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(result, target.SampleRate);
            }

            if (result.WaveFormat.Channels != target.Channels)
            {
                if (result.WaveFormat.Channels == 1 && target.Channels == 2)
                {
                    result = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(result);
                }
                else if (result.WaveFormat.Channels == 2 && target.Channels == 1)
                {
                    result = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(result);
                }
            }

            return result;
        }

        // ──────────────────────────────────── helpers ──────────────────────────

        private async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpegPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;
            var stderrBuilder = new StringBuilder();

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
                throw;
            }

            return (process.ExitCode, stderrBuilder.ToString());
        }

        private static string LastNonEmptyLine(string text)
        {
            return text.Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.Length > 0) ?? text.Trim();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
