using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.Models.Stem;
using SLSKDONET.Views;

namespace SLSKDONET.Services.Audio
{
    public class BatchStemExportService
    {
        private readonly StemSeparationService _separationService;
        private readonly INotificationService _notificationService;

        public BatchStemExportService(
            StemSeparationService separationService,
            INotificationService notificationService)
        {
            _separationService = separationService;
            _notificationService = notificationService;
        }

        public async Task ExportBatchAsync(
            IEnumerable<PlaylistTrackViewModel> tracks, 
            string targetFolder, 
            bool acapellaOnly,
            CancellationToken ct = default)
        {
            int total = tracks.Count();
            int current = 0;

            foreach (var track in tracks)
            {
                if (ct.IsCancellationRequested) break;
                
                current++;
                try 
                {
                    // 1. Ensure Stems exist
                    Dictionary<StemType, string> stems;
                    if (_separationService.HasStems(track.GlobalId))
                    {
                        stems = _separationService.GetStemPaths(track.GlobalId);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(track.Model.ResolvedFilePath) || !File.Exists(track.Model.ResolvedFilePath))
                            continue;

                        stems = await _separationService.SeparateTrackAsync(track.Model.ResolvedFilePath, track.GlobalId);
                    }

                    // 2. Export requested stem
                    if (acapellaOnly)
                    {
                        if (stems.TryGetValue(StemType.Vocals, out var vocalPath))
                        {
                            ExportFile(track, vocalPath, targetFolder, "Acapella");
                        }
                    }
                    else // Instrumental
                    {
                        // Instrumental is often technically "Everything but vocals" 
                        // Spleeter 5-stem doesn't give a single "Instrumental" file, 
                        // but 4-stem or 2-stem does.
                        // For now, if we have 5-stems, we can't easily merge here without ffmpeg or similar.
                        // Optimization: Check if accompaniment exists
                        var firstStemPath = stems.Values.FirstOrDefault();
                        if (firstStemPath != null)
                        {
                            var dir = Path.GetDirectoryName(firstStemPath);
                            if (dir != null)
                            {
                                var accompaniment = Path.Combine(dir, "accompaniment.wav");
                                if (File.Exists(accompaniment))
                                {
                                    ExportFile(track, accompaniment, targetFolder, "Instrumental");
                                }
                                else if (stems.TryGetValue(StemType.Other, out var otherPath))
                                {
                                    // Fallback to 'Other' if that's all we have for non-vocal
                                    ExportFile(track, otherPath, targetFolder, "Instrumental_Partial");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BatchStemExport] Failed for {track.Title}: {ex.Message}");
                }
            }

            _notificationService.Show("Batch Export Complete", $"Processed {current} of {total} tracks.", Views.NotificationType.Success);
        }

        private void ExportFile(PlaylistTrackViewModel track, string sourcePath, string targetFolder, string suffix)
        {
            string baseFileName = $"{track.Artist} - {track.Title}";
            // Sanitize filename
            foreach (char c in Path.GetInvalidFileNameChars()) baseFileName = baseFileName.Replace(c, '_');

            string extension = Path.GetExtension(sourcePath);
            string destinationPath = Path.Combine(targetFolder, $"{baseFileName} ({suffix}){extension}");

            File.Copy(sourcePath, destinationPath, true);
        }
    }
}
