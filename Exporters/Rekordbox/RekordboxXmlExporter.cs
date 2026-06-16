using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;

namespace SLSKDONET.Exporters.Rekordbox;

/// <summary>
/// Exports ORBIT playlists, tracks, cues, and loops to Rekordbox-compatible XML format.
/// </summary>
public sealed class RekordboxXmlExporter
{
    /// <summary>
    /// Serializes a set of tracks and playlists into a Rekordbox XML format and writes to file.
    /// </summary>
    public void ExportToXml(
        string targetFilePath, 
        IReadOnlyList<TrackEntity> tracks, 
        Dictionary<string, List<CuePointEntity>> trackCues,
        string playlistName = "ORBIT Curation Export")
    {
        if (string.IsNullOrEmpty(targetFilePath))
            throw new ArgumentNullException(nameof(targetFilePath));

        var xmlObj = new RekordboxPlaylistsXml();
        xmlObj.Collection.EntriesCount = tracks.Count;

        int currentTrackId = 1;
        var trackIdMap = new Dictionary<string, int>();

        // 1. Build Collection tracks
        foreach (var t in tracks)
        {
            trackIdMap[t.GlobalId] = currentTrackId;

            // Resolve file location in URI format
            string locationUri = ResolveLocationUri(t.LocalFilePath ?? t.Filename);

            // Pack comments
            string commentsPayload = PackCurationTelemetryComments(t);

            var trackXml = new RekordboxTrackXml
            {
                TrackId = currentTrackId,
                Name = t.Title,
                Artist = t.Artist,
                Album = string.Empty,
                Genre = t.PrimaryGenre ?? "General",
                Kind = ResolveFileKind(t.LocalFilePath ?? t.Filename),
                Size = t.Size,
                BitRate = t.Bitrate > 0 ? t.Bitrate : 320,
                SampleRate = t.SpectralSampleRateHz ?? 44100,
                Comments = commentsPayload,
                Location = locationUri,
                AverageBpm = t.BPM ?? 120.0,
                Tonality = t.MusicalKey ?? "8A",
                PlayCount = t.PlayCount,
                Rating = t.Rating
            };

            // Set Tempo Grid
            trackXml.Tempos.Add(new RekordboxTempoXml
            {
                Beginning = t.AnalysisOffset ?? 0.0,
                Bpm = t.BPM ?? 120.0
            });

            // Map Cues & Loops
            if (trackCues.TryGetValue(t.GlobalId, out var cuesList))
            {
                int hotCueIndex = 0;
                foreach (var cue in cuesList.OrderBy(c => c.TimestampInSeconds))
                {
                    // Map Cue
                    if (cue.Type == CuePointType.Drop || cue.Type == CuePointType.Intro || cue.Type == CuePointType.Outro)
                    {
                        // Map to Hot Cue (0-7) if it is a major action trigger
                        if (hotCueIndex < 8)
                        {
                            trackXml.PositionMarks.Add(new RekordboxPositionMarkXml
                            {
                                Name = cue.Label,
                                Start = cue.TimestampInSeconds,
                                Type = 0, // Cue
                                Num = hotCueIndex, // Hot Cue Index
                                Color = cue.Color
                            });
                            hotCueIndex++;
                        }
                    }

                    // Always add to Memory Cue list for visual CDJ reference (Num = -1)
                    trackXml.PositionMarks.Add(new RekordboxPositionMarkXml
                    {
                        Name = cue.Label,
                        Start = cue.TimestampInSeconds,
                        Type = 0, // Cue
                        Num = -1, // Memory Cue
                        Color = cue.Color
                    });

                    // Check if this cue has an associated active loop (represented as a 4-bar or 8-bar loop)
                    if (cue.Label.Contains("Loop") || cue.Label.Contains("Active Loop"))
                    {
                        double loopLengthSeconds = 15.0; // default loop length fallback
                        if (t.BPM > 0)
                        {
                            double beatDuration = 60.0 / t.BPM.Value;
                            loopLengthSeconds = beatDuration * 16.0; // 4-bar loop
                        }

                        // Add Hot Loop
                        if (hotCueIndex < 8)
                        {
                            trackXml.PositionMarks.Add(new RekordboxPositionMarkXml
                            {
                                Name = $"{cue.Label} (Hot Loop)",
                                Start = cue.TimestampInSeconds,
                                End = cue.TimestampInSeconds + loopLengthSeconds,
                                Type = 1, // Loop
                                Num = hotCueIndex, // Hot Loop Index
                                Color = "#FF9900" // Orange-ish Loop color
                            });
                            hotCueIndex++;
                        }

                        // Add Memory Loop
                        trackXml.PositionMarks.Add(new RekordboxPositionMarkXml
                        {
                            Name = cue.Label,
                            Start = cue.TimestampInSeconds,
                            End = cue.TimestampInSeconds + loopLengthSeconds,
                            Type = 1, // Loop
                            Num = -1, // Memory Loop
                            Color = "#FF9900"
                        });
                    }
                }
            }

            xmlObj.Collection.Tracks.Add(trackXml);
            currentTrackId++;
        }

        // 2. Build Playlists hierarchy
        var mainPlaylistNode = new RekordboxNodeXml
        {
            Type = 1, // Playlist
            Name = playlistName,
            Count = tracks.Count
        };

        foreach (var t in tracks)
        {
            if (trackIdMap.TryGetValue(t.GlobalId, out int id))
            {
                mainPlaylistNode.TrackKeys.Add(new RekordboxKeyXml { Key = id });
            }
        }

        xmlObj.Playlists.Nodes.Add(mainPlaylistNode);

        // 3. Serialize to File
        var serializer = new XmlSerializer(typeof(RekordboxPlaylistsXml));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
        using var writer = XmlWriter.Create(stream, settings);
        
        // Use empty namespaces to match Pioneer's simple format
        var ns = new XmlSerializerNamespaces();
        ns.Add("", "");
        
        serializer.Serialize(writer, xmlObj, ns);
    }

    private static string ResolveLocationUri(string localPath)
    {
        if (string.IsNullOrEmpty(localPath)) return string.Empty;

        try
        {
            // Normalize path separators
            string fullPath = Path.GetFullPath(localPath).Replace('\\', '/');
            
            // Format to file://localhost/C:/... URI format
            if (!fullPath.StartsWith("/"))
            {
                fullPath = "/" + fullPath;
            }

            // Uri.EscapeDataString will encode spaces and special characters but keep slashes
            string escaped = string.Join("/", fullPath.Split('/').Select(Uri.EscapeDataString));
            
            // Rekordbox expects file://localhost/ prefix
            return "file://localhost" + escaped;
        }
        catch
        {
            return "file://localhost/" + Uri.EscapeDataString(localPath.Replace('\\', '/'));
        }
    }

    private static string ResolveFileKind(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "Audio File";
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "MP3 File",
            ".flac" => "FLAC File",
            ".wav" => "WAV File",
            ".m4a" => "AAC File",
            ".aif" or ".aiff" => "AIFF File",
            _ => "Audio File"
        };
    }

    private static string PackCurationTelemetryComments(TrackEntity t)
    {
        // Pack: Energy, Mood, Confidence
        var sb = new StringBuilder();
        sb.Append($"[ORBIT | Energy: {t.ManualEnergy ?? (int)((t.Energy ?? 0.5) * 10.0)}/10");
        
        if (!string.IsNullOrEmpty(t.MoodTag))
        {
            sb.Append($" | Mood: {t.MoodTag}");
        }

        if (t.QualityConfidence.HasValue)
        {
            sb.Append($" | Confidence: {(t.QualityConfidence.Value * 100.0):F0}%");
        }
        
        sb.Append("]");

        if (!string.IsNullOrEmpty(t.Comments))
        {
            sb.Append(" ").Append(t.Comments);
        }

        return sb.ToString();
    }
}
