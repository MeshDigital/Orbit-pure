using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using TagLib;
using File = System.IO.File;

using SLSKDONET.Services.IO; // Added explicit using

namespace SLSKDONET.Services;

/// <summary>
/// Concrete implementation of ITaggerService.
/// Writes metadata tags to audio files using TagLibSharp.
/// Supports ID3v2 (MP3), Vorbis (OGG/FLAC), APEv2, and other formats.
/// </summary>
public class MetadataTaggerService : ITaggerService
{
    private readonly ILogger<MetadataTaggerService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IFileWriteService _fileWriteService; // Phase 1A

    // Supported audio formats
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".wav", ".ape", ".opus"
    };

    public MetadataTaggerService(
        ILogger<MetadataTaggerService> logger,
        IFileWriteService fileWriteService) // Phase 1A
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _fileWriteService = fileWriteService; // Phase 1A
    }

    /// <summary>
    /// Tags a single audio file with metadata from a Track model.
    /// Phase 1A: Refactored to use SafeWrite for atomic tag operations.
    /// </summary>
    public async Task<bool> TagFileAsync(Track track, string filePath)
    {
        try
        {
            // Validate file exists
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found for tagging: {FilePath}", filePath);
                return false;
            }

            // Check if format is supported
            var extension = Path.GetExtension(filePath);
            if (!SupportedFormats.Contains(extension))
            {
                _logger.LogWarning("Unsupported audio format for tagging: {Format}", extension);
                return false;
            }

            _logger.LogDebug("Tagging file with SafeWrite: {FilePath}", filePath);

            // Phase 1A: Use SafeWrite for atomic tag operations
            var success = await _fileWriteService.WriteAtomicAsync(
                filePath,
                async (tempPath) =>
                {
                    // STEP 1: Copy original file to temp location
                    File.Copy(filePath, tempPath, overwrite: true);

                    // STEP 2: Write tags to temp file
                    await Task.Run(() =>
                    {
                        var fileInfo = new FileInfo(tempPath);
                        if (fileInfo.Length == 0)
                            throw new InvalidDataException($"Temp file is empty before tagging: {tempPath}");

                        TagLib.File? file = null;
                        try 
                        {
                            // Fix for Atomic Write .tmp files: Explicitly specify MIME type based on original extension
                            var originalExtension = Path.GetExtension(filePath);
                            var mimeType = GetMimeTypeFromExtension(originalExtension);
                            
                            // Use the overload that accepts MIME type
                            file = TagLib.File.Create(new TagLib.File.LocalFileAbstraction(tempPath), mimeType, ReadStyle.Average);
                        }
                        catch (TagLib.UnsupportedFormatException)
                        {
                            _logger.LogWarning("TagLib rejected format for {Path}. Skipping metadata write.", tempPath);
                            return; // Exit action, effectively doing nothing but copying the file
                        }
                        catch (TagLib.CorruptFileException)
                        {
                             _logger.LogWarning("TagLib detected corrupt file {Path}. Skipping metadata write.", tempPath);
                             return;
                        }

                        if (file == null)
                        {
                            throw new InvalidOperationException($"Failed to open temp file for tagging: {tempPath}");
                        }


                        // Ensure disposal via try-finally manually since we can't use 'using' easily with the early returns above
                        try
                        {
                            // Write basic metadata tags
                            if (!string.IsNullOrWhiteSpace(track.Title))
                                file.Tag.Title = track.Title;

                            if (!string.IsNullOrWhiteSpace(track.Artist))
                            {
                                file.Tag.Performers = new[] { track.Artist };
                            }

                            if (!string.IsNullOrWhiteSpace(track.Album))
                                file.Tag.Album = track.Album;

                            // Write track number if available
                            if (track.Metadata != null && track.Metadata.TryGetValue("TrackNumber", out var trackNumObj))
                            {
                                if (uint.TryParse(trackNumObj.ToString(), out var trackNum))
                                    file.Tag.Track = trackNum;
                            }

                            // Write year if available
                            if (track.Metadata != null && track.Metadata.TryGetValue("ReleaseDate", out var releaseObj))
                            {
                                if (DateTime.TryParse(releaseObj.ToString(), out var releaseDate))
                                    file.Tag.Year = (uint)releaseDate.Year;
                            }
                            else if (track.Metadata != null && track.Metadata.TryGetValue("Year", out var yearObj))
                            {
                                if (uint.TryParse(yearObj.ToString(), out var year))
                                    file.Tag.Year = year;
                            }

                            // Add genre if available
                            if (track.Metadata != null && track.Metadata.TryGetValue("Genre", out var genreObj))
                            {
                                if (genreObj is string genreStr && !string.IsNullOrWhiteSpace(genreStr))
                                {
                                    file.Tag.Genres = new[] { genreStr };
                                }
                            }

                            // Write standard DJ tags
                            if (track.Metadata != null)
                            {
                                // 1. Initial Key (TKEY)
                                if (track.Metadata.TryGetValue("MusicalKey", out var keyObj) && keyObj is string keyStr && !string.IsNullOrWhiteSpace(keyStr))
                                {
                                    file.Tag.InitialKey = keyStr;
                                }

                                // 1b. Comment (COMM)
                                if (track.Metadata.TryGetValue("Comment", out var commentObj) && commentObj is string commentStr)
                                {
                                    file.Tag.Comment = commentStr;
                                }

                                // 2. BPM (TBPM)
                                if (track.Metadata.TryGetValue("BPM", out var bpmObj))
                                {
                                    if (bpmObj is double bpmDouble)
                                        file.Tag.BeatsPerMinute = (uint)Math.Round(bpmDouble);
                                    else if (bpmObj is int bpmInt)
                                        file.Tag.BeatsPerMinute = (uint)bpmInt;
                                }

                                // 3. Custom Tags via TXXX (Spotify Anchors)
                                var customTags = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
                                if (customTags != null)
                                {
                                    if (track.Metadata.TryGetValue("SpotifyTrackId", out var tid) && tid is string tidStr)
                                    {
                                        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_TRACK_ID", true);
                                        frame.Text = new[] { tidStr };
                                    }
                                    
                                    if (track.Metadata.TryGetValue("SpotifyAlbumId", out var aid) && aid is string aidStr)
                                    {
                                        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_ALBUM_ID", true);
                                        frame.Text = new[] { aidStr };
                                    }
                                    
                                    if (track.Metadata.TryGetValue("SpotifyArtistId", out var arid) && arid is string aridStr)
                                    {
                                        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_ARTIST_ID", true);
                                        frame.Text = new[] { aridStr };
                                    }
                                }
                            }

                            // Embed album art if available (run synchronously in temp file context)
                            if (track.Metadata != null && track.Metadata.TryGetValue("AlbumArtUrl", out var artUrlObj) && artUrlObj is string artUrl)
                            {
                                TryAddAlbumArtAsync(file, artUrl).GetAwaiter().GetResult();
                            }

                            // Save tags to temp file
                            file.Save();
                        }
                        finally
                        {
                            file.Dispose();
                        }
                    });
                },
                async (tempPath) =>
                {
                    // STEP 3: Verify tagged file is still valid
                    var isValid = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyAudioFormatAsync(tempPath);
                    if (!isValid)
                    {
                        _logger.LogWarning("Tagging verification failed: {TempPath}", tempPath);
                        return false;
                    }
                    return true;
                }
            );

            if (success)
            {
                _logger.LogInformation("✅ Successfully tagged file (atomic): {FilePath} ({Artist} - {Title})",
                    filePath, track.Artist ?? "Unknown", track.Title ?? "Unknown");
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to tag file (SafeWrite failed): {FilePath}", filePath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while tagging {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Tags multiple audio files concurrently.
    /// </summary>
    public async Task<int> TagFilesAsync(IEnumerable<Track> tracks, IEnumerable<string> filePaths)
    {
        var trackList = tracks.ToList();
        var fileList = filePaths.ToList();

        if (trackList.Count != fileList.Count)
        {
            _logger.LogWarning("Track count ({TrackCount}) does not match file path count ({FileCount})",
                trackList.Count, fileList.Count);
            return 0;
        }

        _logger.LogInformation("Tagging {Count} files", trackList.Count);

        var tasks = trackList.Zip(fileList, (track, path) => TagFileAsync(track, path));
        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r);
        _logger.LogInformation("Successfully tagged {SuccessCount}/{TotalCount} files",
            successCount, trackList.Count);

        return successCount;
    }

    /// <summary>
    /// Attempts to download and embed album art from a URL.
    /// Gracefully handles failures without aborting the entire tagging operation.
    /// </summary>
    private async Task TryAddAlbumArtAsync(TagLib.File file, string artUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artUrl))
                return;

            _logger.LogDebug("Downloading album art from: {Url}", artUrl);

            var imageData = await _httpClient.GetByteArrayAsync(artUrl);
            if (imageData == null || imageData.Length == 0)
            {
                _logger.LogDebug("No image data received from {Url}", artUrl);
                return;
            }

            // Determine MIME type based on URL or content
            var mimeType = DetermineMimeType(artUrl, imageData);

            var picture = new Picture()
            {
                Type = PictureType.FrontCover,
                MimeType = mimeType,
                Data = new ByteVector(imageData),
                Description = "Front Cover"
            };

            // Create new pictures list without existing front covers
            var newPictures = file.Tag.Pictures
                .Where(p => p.Type != PictureType.FrontCover)
                .ToList();
            
            newPictures.Add(picture);

            // Update the pictures array
            file.Tag.Pictures = newPictures.ToArray();
            
            _logger.LogDebug("Successfully embedded album art ({SizeKB} KB)", imageData.Length / 1024.0);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to download album art from {Url}", artUrl);
            // Don't abort tagging if art download fails
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Invalid MIME type for album art");
            // Don't abort tagging if art embedding fails
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected error embedding album art");
            // Don't abort tagging if art embedding fails
        }
    }

    /// <summary>
    /// Determines the MIME type of image data based on magic bytes or URL extension.
    /// </summary>
    /// <summary>
    /// Reads metadata tags from an audio file.
    /// </summary>
    public async Task<Track?> ReadTagsAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            return await Task.Run(() =>
            {
                using var file = TagLib.File.Create(filePath);
                if (file == null) return null;

                var track = new Track
                {
                    Filename = Path.GetFileName(filePath),
                    Format = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                    Size = (long)new FileInfo(filePath).Length,
                    Metadata = new Dictionary<string, object>()
                };

                // Add standard metadata fields if tags are available
                var tag = file.Tag;
                if (tag != null)
                {
                    track.Title = tag.Title;
                    track.Artist = tag.FirstPerformer;
                    track.Album = tag.Album;
                    track.Label = tag.Publisher;
                    track.Bitrate = file.Properties.AudioBitrate;
                    track.Length = (int)file.Properties.Duration.TotalSeconds;
                    
                    track.Metadata["Year"] = tag.Year;
                    track.Metadata["TrackNumber"] = tag.Track;
                    track.Metadata["Genres"] = tag.Genres;
                    track.Metadata["Comment"] = tag.Comment;
                    track.Metadata["SampleRate"] = file.Properties.AudioSampleRate;
                    track.Metadata["Channels"] = file.Properties.AudioChannels;
                }

                // Read custom TXXX tags (Spotify IDs)
                var customTags = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
                if (customTags != null)
                {
                    var spotifyIdFrame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_TRACK_ID", false);
                    if (spotifyIdFrame?.Text != null && spotifyIdFrame.Text.Length > 0)
                        track.Metadata["SpotifyTrackId"] = spotifyIdFrame.Text[0];
                    
                    var spotifyAlbumIdFrame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_ALBUM_ID", false);
                    if (spotifyAlbumIdFrame?.Text != null && spotifyAlbumIdFrame.Text.Length > 0)
                        track.Metadata["SpotifyAlbumId"] = spotifyAlbumIdFrame.Text[0];
                    
                    var spotifyArtistIdFrame = TagLib.Id3v2.UserTextInformationFrame.Get(customTags, "SPOTIFY_ARTIST_ID", false);
                    if (spotifyArtistIdFrame?.Text != null && spotifyArtistIdFrame.Text.Length > 0)
                        track.Metadata["SpotifyArtistId"] = spotifyArtistIdFrame.Text[0];
                }

                return track;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tags from {FilePath}", filePath);
            return null;
        }
    }

    private string DetermineMimeType(string url, byte[] imageData)
    {
        // Check magic bytes first (most reliable)
        if (imageData.Length >= 4)
        {
            // JPEG: FF D8 FF
            if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
                return "image/jpeg";

            // PNG: 89 50 4E 47
            if (imageData[0] == 0x89 && imageData[1] == 0x50 &&
                imageData[2] == 0x4E && imageData[3] == 0x47)
                return "image/png";

            // WebP: RIFF ... WEBP
            if (imageData[0] == 0x52 && imageData[1] == 0x49 &&
                imageData[2] == 0x46 && imageData[3] == 0x46)
            {
                if (imageData.Length >= 12 &&
                    imageData[8] == 0x57 && imageData[9] == 0x45 &&
                    imageData[10] == 0x42 && imageData[11] == 0x50)
                    return "image/webp";
            }
        }

        // Fall back to URL extension
        var extension = Path.GetExtension(url).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg" // Default to JPEG if uncertain
        };
    }

    private string GetMimeTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/x-flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ape" => "audio/x-ape",
            ".opus" => "audio/opus",
            ".wma" => "audio/x-ms-wma",
            ".aac" => "audio/aac",
            ".aiff" => "audio/x-aiff",
            _ => "application/octet-stream"
        };
    }
}
