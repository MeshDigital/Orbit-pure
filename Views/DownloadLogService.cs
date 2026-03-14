using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TagLib;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using TagLibFile = TagLib.File;

namespace SLSKDONET.Services;

/// <summary>
/// Manages a persistent log of downloaded tracks.
/// </summary>
public class DownloadLogService
{
    private readonly ILogger<DownloadLogService> _logger;
    private readonly string _logFilePath;
    private List<Track> _logEntries;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".alac", ".aiff", ".aif", ".ape"
    };

    public DownloadLogService(ILogger<DownloadLogService> logger)
    {
        _logger = logger;
        var configDir = Path.GetDirectoryName(ConfigManager.GetDefaultConfigPath());
        _logFilePath = Path.Combine(configDir ?? AppContext.BaseDirectory, "download_log.json");
        _logEntries = LoadLog();
    }

    public List<Track> GetEntries() => _logEntries.ToList();

    public void AddEntry(Track track)
    {
        bool exists = track.LocalPath != null
            ? _logEntries.Any(t => string.Equals(t.LocalPath, track.LocalPath, StringComparison.OrdinalIgnoreCase))
            : _logEntries.Any(t => t.Filename == track.Filename && t.Username == track.Username);

        if (!exists)
        {
            _logEntries.Add(track);
            SaveLog();
            _logger.LogInformation("Added '{Filename}' to download log.", track.Filename ?? track.LocalPath);
        }
    }

    /// <summary>
    /// Syncs the log with the current state of a folder: add missing files, remove entries whose local file is gone.
    /// Returns (added, removed).
    /// </summary>
        public (int added, int removed) SyncWithFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                _logger.LogWarning("Sync skipped - folder missing: {Folder}", folder);
                return (0, 0);
            }

            var filesOnDisk = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();

            // Add new files not present in log (by LocalPath)
            int added = 0;
            int updated = 0;
            foreach (var file in filesOnDisk)
            {
                if (!AudioExtensions.Contains(Path.GetExtension(file)))
                    continue;

                var existing = _logEntries.FirstOrDefault(t => string.Equals(t.LocalPath, file, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (NeedsMetadata(existing))
                    {
                        var enriched = CreateTrackFromFile(file);
                        if (ApplyMetadata(existing, enriched))
                            updated++;
                    }
                    continue;
                }

                var track = CreateTrackFromFile(file);
                _logEntries.Add(track);
                added++;
            }

            // Remove entries whose LocalPath file is missing
            int removed = _logEntries.RemoveAll(t =>
                !string.IsNullOrEmpty(t.LocalPath) && !System.IO.File.Exists(t.LocalPath));

            if (added > 0 || removed > 0 || updated > 0)
            {
                SaveLog();
                _logger.LogInformation("Sync complete: added {Added}, removed {Removed}, updated metadata {Updated}", added, removed, updated);
            }

            return (added, removed);
        }    public void RemoveEntries(IEnumerable<Track> tracks)
    {
        int count = 0;
        foreach (var track in tracks)
        {
            var entryToRemove = _logEntries.FirstOrDefault(t => t.Filename == track.Filename && t.Username == track.Username);
            if (entryToRemove != null)
            {
                _logEntries.Remove(entryToRemove);
                count++;
            }
        }
        if (count > 0)
        {
            SaveLog();
            _logger.LogInformation("Removed {Count} entries from download log.", count);
        }
    }

    private List<Track> LoadLog()
    {
        try
        {
            if (!System.IO.File.Exists(_logFilePath)) return new List<Track>();
            var json = System.IO.File.ReadAllText(_logFilePath);
            return JsonSerializer.Deserialize<List<Track>>(json) ?? new List<Track>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load download log from {LogFilePath}", _logFilePath);
            return new List<Track>();
        }
    }

    private void SaveLog()
    {
        var json = JsonSerializer.Serialize(_logEntries, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(_logFilePath, json);
    }

    private static bool NeedsMetadata(Track track)
    {
        return track.Bitrate <= 0
               || track.Length == null
               || string.IsNullOrWhiteSpace(track.Title)
               || string.IsNullOrWhiteSpace(track.Artist)
               || track.Metadata == null;
    }

    private static bool ApplyMetadata(Track target, Track enriched)
    {
        bool changed = false;

        if (string.IsNullOrWhiteSpace(target.Title) && !string.IsNullOrWhiteSpace(enriched.Title))
        {
            target.Title = enriched.Title;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Artist) && !string.IsNullOrWhiteSpace(enriched.Artist))
        {
            target.Artist = enriched.Artist;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Album) && !string.IsNullOrWhiteSpace(enriched.Album))
        {
            target.Album = enriched.Album;
            changed = true;
        }

        if (target.Bitrate <= 0 && enriched.Bitrate > 0)
        {
            target.Bitrate = enriched.Bitrate;
            changed = true;
        }

        if (target.Length == null && enriched.Length != null)
        {
            target.Length = enriched.Length;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.Format) && !string.IsNullOrWhiteSpace(enriched.Format))
        {
            target.Format = enriched.Format;
            changed = true;
        }

        if (target.Size == null && enriched.Size != null)
        {
            target.Size = enriched.Size;
            changed = true;
        }

        if (target.Metadata == null && enriched.Metadata != null)
        {
            target.Metadata = enriched.Metadata;
            changed = true;
        }

        return changed;
    }

    private Track CreateTrackFromFile(string file)
    {
        var fi = new FileInfo(file);
        var ext = fi.Extension.TrimStart('.');
        var title = Path.GetFileNameWithoutExtension(file);
        string? artist = "";
        string? album = "";
        int bitrate = 0;
        int? length = null;
        Dictionary<string, object>? metadata = null;

        try
        {
            using var tagFile = TagLibFile.Create(file);
            artist = tagFile.Tag.FirstPerformer
                     ?? tagFile.Tag.FirstAlbumArtist
                     ?? tagFile.Tag.Performers.FirstOrDefault()
                     ?? "";
            album = tagFile.Tag.Album ?? "";
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                title = tagFile.Tag.Title;

            bitrate = tagFile.Properties.AudioBitrate;
            length = (int)Math.Round(tagFile.Properties.Duration.TotalSeconds);
            metadata = new Dictionary<string, object>
            {
                { "Year", tagFile.Tag.Year },
                { "Track", tagFile.Tag.Track },
                { "AlbumArtists", tagFile.Tag.AlbumArtists },
                { "Performers", tagFile.Tag.Performers },
                { "Genres", tagFile.Tag.Genres }
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read metadata for {File}", file);
        }

        return new Track
        {
            LocalPath = file,
            Filename = fi.Name,
            Title = title,
            Artist = artist ?? "",
            Album = album ?? "",
            Format = ext,
            Size = fi.Length,
            Username = "local",
            Bitrate = bitrate,
            Length = length,
            Metadata = metadata
        };
    }
}