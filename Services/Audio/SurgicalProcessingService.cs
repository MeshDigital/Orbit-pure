using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using SLSKDONET.Models;

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
    }

    public class SurgicalProcessingService : ISurgicalProcessingService
    {
        private readonly ILogger<SurgicalProcessingService> _logger;
        private readonly StemCacheService _stemCache;
        private readonly string _surgicalWorkDir;

        public SurgicalProcessingService(ILogger<SurgicalProcessingService> logger, StemCacheService stemCache)
        {
            _logger = logger;
            _stemCache = stemCache;
            _surgicalWorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "SurgicalTemp");
            Directory.CreateDirectory(_surgicalWorkDir);
        }

        public async Task<string> CutAndCombineSegmentsAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("✂️ Surgical Surgery: Orchestrating FFmpeg for {Path}", sourcePath);
            
            var sortedSegments = segments.OrderBy(s => s.Start).ToList();
            if (!sortedSegments.Any()) return string.Empty;

            string outputFileName = $"Surgical_{Path.GetFileNameWithoutExtension(sourcePath)}_{DateTime.Now:yyyyMMddHHmmss}.flac";
            string outputPath = Path.Combine(_surgicalWorkDir, outputFileName);

            // FFmpeg Strategy: 
            // 1. Create a filter_complex to trim each segment.
            // 2. Chain acrossfade for each transition (50ms).
            // Example: [0:a]atrim=start=10:end=20,asetpts=PTS-STARTPTS[v1]; [0:a]atrim=start=30:end=40,asetpts=PTS-STARTPTS[v2]; [v1][v2]acrossfade=d=0.05:c1=tri:c2=tri[out]

            var filterParts = new List<string>();
            var labels = new List<string>();

            for (int i = 0; i < sortedSegments.Count; i++)
            {
                var seg = sortedSegments[i];
                string label = $"s{i}";
                labels.Add(label);
                
                // Truncate to 3 decimal places for millisecond precision
                string start = seg.Start.ToString("F3");
                string end = (seg.Start + seg.Duration).ToString("F3");
                
                filterParts.Add($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[{label}]");
            }

            string crossfadeFilter = string.Empty;
            if (labels.Count > 1)
            {
                string currentOut = labels[0];
                for (int i = 1; i < labels.Count; i++)
                {
                    string nextOut = $"merged{i}";
                    crossfadeFilter += $"{currentOut}{labels[i]}acrossfade=d=0.05:c1=tri:c2=tri[{nextOut}];";
                    currentOut = nextOut;
                }
                // The last one is our final output
                crossfadeFilter = crossfadeFilter.Replace($"[{currentOut}];", "[out]");
                if (!crossfadeFilter.Contains("[out]")) crossfadeFilter = crossfadeFilter.TrimEnd(';') + "[out]";
            }
            else
            {
                crossfadeFilter = $"[{labels[0]}]copy[out]";
            }

            string fullFilter = string.Join(";", filterParts) + ";" + crossfadeFilter;
            
            // Build command: ffmpeg -i source -filter_complex "..." -map "[out]" output
            string arguments = $"-i \"{sourcePath}\" -filter_complex \"{fullFilter}\" -map \"[out]\" -c:a flac \"{outputPath}\"";
            
            _logger.LogDebug("🎬 FFmpeg Surgical Command: {Args}", arguments);

            // TODO: Execute FFmpeg Process
            await Task.Delay(500, ct); 
            
            return outputPath;
        }

        public async Task<string> IsolateStemInRangeAsync(string sourcePath, float startTime, float duration, string stemType, CancellationToken ct = default)
        {
            _logger.LogInformation("🔬 Surgical Surgery: Isolating {Stem} from {Start}s for {Duration}s", stemType, startTime, duration);
            
            string trackHash = Path.GetFileNameWithoutExtension(sourcePath); 
            
            var cached = await _stemCache.TryGetCachedStemAsync(trackHash, startTime, duration, stemType);
            if (cached != null) return cached;

            await Task.Delay(1000, ct); 
            
            string stemResultPath = Path.Combine(_surgicalWorkDir, $"stem_{Guid.NewGuid()}.flac");
            // Placeholder for actual extraction
            
            await _stemCache.StoreStemAsync(trackHash, startTime, duration, stemType, stemResultPath);
            
            return stemResultPath;
        }

        public async Task<bool> RenderPreviewAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Surgical Surgery: Preparing render preview for {Path}", sourcePath);
            
            var previewPath = await CutAndCombineSegmentsAsync(sourcePath, segments, ct);
            return !string.IsNullOrEmpty(previewPath);
        }
    }
}
