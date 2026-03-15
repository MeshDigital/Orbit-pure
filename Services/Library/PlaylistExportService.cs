using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.Models.Export;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Library;

public class PlaylistExportService
{
    private readonly ILogger<PlaylistExportService> _logger;

    public PlaylistExportService(ILogger<PlaylistExportService> logger)
    {
        _logger = logger;
    }

    public async Task ExportToRekordboxXmlAsync(string playlistName, IEnumerable<PlaylistTrack> tracks, string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to Rekordbox XML: {Path}", playlistName, targetPath);

            var rbTracks = new List<RekordboxTrack>();
            int trackId = 1;

            foreach (var track in tracks)
            {
                if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
                    continue;

                var fileInfo = new FileInfo(track.ResolvedFilePath);
                var rbTrack = new RekordboxTrack
                {
                    TrackID = trackId++,
                    Name = track.Title ?? "Unknown Title",
                    Artist = track.Artist ?? "Unknown Artist",
                    Album = track.Album ?? "Unknown Album",
                    Genre = track.Genres ?? "",
                    Size = fileInfo.Length,
                    TotalTime = Math.Max(0, track.CanonicalDuration.GetValueOrDefault() / 1000),
                    DateAdded = track.AddedAt.ToString("yyyy-MM-dd"),
                    BitRate = track.Bitrate ?? 0,
                    AverageBpm = track.BPM ?? 0,
                    Tonality = track.MusicalKey ?? "",
                    Location = "file://localhost/" + track.ResolvedFilePath.Replace("\\", "/")
                };
                rbTracks.Add(rbTrack);
            }

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("DJ_PLAYLISTS",
                    new XAttribute("Version", "1.0.0"),
                    new XElement("PRODUCT", 
                        new XAttribute("Name", "rekordbox"),
                        new XAttribute("Version", "6.0.0"),
                        new XAttribute("Company", "Pioneer DJ")),
                    new XElement("COLLECTION", 
                        new XAttribute("Entries", rbTracks.Count),
                        rbTracks.Select(t => new XElement("TRACK",
                            new XAttribute("TrackID", t.TrackID),
                            new XAttribute("Name", t.Name),
                            new XAttribute("Artist", t.Artist),
                            new XAttribute("Album", t.Album),
                            new XAttribute("Genre", t.Genre),
                            new XAttribute("Kind", t.Kind),
                            new XAttribute("Size", t.Size),
                            new XAttribute("TotalTime", t.TotalTime),
                            new XAttribute("DateAdded", t.DateAdded),
                            new XAttribute("BitRate", t.BitRate),
                            new XAttribute("SampleRate", t.SampleRate),
                            new XAttribute("AverageBpm", t.AverageBpm.ToString("F2")),
                            new XAttribute("Tonality", t.Tonality),
                            new XAttribute("Location", t.Location)
                        ))
                    ),
                    new XElement("PLAYLISTS",
                        new XElement("NODE",
                            new XAttribute("Type", "0"),
                            new XAttribute("Name", "ROOT"),
                            new XElement("NODE",
                                new XAttribute("Name", playlistName),
                                new XAttribute("Type", "1"),
                                new XAttribute("Entries", rbTracks.Count),
                                rbTracks.Select(t => new XElement("TRACK", new XAttribute("Key", t.TrackID)))
                            )
                        )
                    )
                )
            );

            await Task.Run(() => doc.Save(targetPath));
            _logger.LogInformation("Rekordbox XML export completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to Rekordbox XML");
            throw;
        }
    }

    /// <summary>
    /// Phase 12: Enhanced CSV Export with Forensic Metrics
    /// </summary>
    public async Task ExportToCsvWithForensicsAsync(string playlistName, IEnumerable<LibraryEntryEntity> entries, string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to CSV with forensics: {Path}", playlistName, targetPath);

            var csvLines = new List<string>
            {
                // Phase 12: Enhanced CSV headers with forensic data
                "Title,Artist,Album,Genre,BPM,Key,Bitrate,Duration,FilePath,AddedAt,IsTranscoded,HighFreqEnergyDb,LowFreqEnergyDb,EnergyRatio,ForensicReason"
            };

            foreach (var entry in entries)
            {
                // Escape commas and quotes in CSV fields
                string EscapeCsvField(string? field) =>
                    field?.Replace("\"", "\"\"").Replace(",", ";") ?? "";

                var line = string.Join(",", new[]
                {
                    $"\"{EscapeCsvField(entry.Title)}\"",
                    $"\"{EscapeCsvField(entry.Artist)}\"",
                    $"\"{EscapeCsvField(entry.Album)}\"",
                    $"\"{EscapeCsvField(entry.Genres)}\"",
                    entry.BPM?.ToString() ?? "",
                    $"\"{EscapeCsvField(entry.MusicalKey)}\"",
                    entry.Bitrate.ToString(),
                    entry.DurationSeconds?.ToString() ?? "",
                    $"\"{EscapeCsvField(entry.FilePath)}\"",
                    entry.AddedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    entry.IsTranscoded?.ToString() ?? "false",
                    entry.HighFreqEnergyDb?.ToString("F2") ?? "",
                    entry.LowFreqEnergyDb?.ToString("F2") ?? "",
                    entry.EnergyRatio?.ToString("F2") ?? "",
                    $"\"{EscapeCsvField(entry.ForensicReason)}\""
                });

                csvLines.Add(line);
            }

            await File.WriteAllLinesAsync(targetPath, csvLines);
            _logger.LogInformation("CSV export with forensics completed successfully. Exported {Count} tracks.", entries.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to CSV with forensics");
            throw;
        }
    }
}
