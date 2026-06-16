using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SLSKDONET.Exporters.Rekordbox;

[XmlRoot("DJ_PLAYLISTS")]
public sealed class RekordboxPlaylistsXml
{
    [XmlAttribute("Version")]
    public string Version { get; set; } = "1.0.0";

    [XmlElement("PRODUCT")]
    public RekordboxProductXml Product { get; set; } = new();

    [XmlElement("COLLECTION")]
    public RekordboxCollectionXml Collection { get; set; } = new();

    [XmlElement("PLAYLISTS")]
    public RekordboxPlaylistsContainerXml Playlists { get; set; } = new();
}

public sealed class RekordboxProductXml
{
    [XmlAttribute("Name")]
    public string Name { get; set; } = "ORBIT";

    [XmlAttribute("Version")]
    public string Version { get; set; } = "0.1.0";

    [XmlAttribute("Company")]
    public string Company { get; set; } = "ORBIT";
}

public sealed class RekordboxCollectionXml
{
    [XmlAttribute("Entries")]
    public int EntriesCount { get; set; }

    [XmlElement("TRACK")]
    public List<RekordboxTrackXml> Tracks { get; set; } = new();
}

public sealed class RekordboxTrackXml
{
    [XmlAttribute("TrackID")]
    public int TrackId { get; set; }

    [XmlAttribute("Name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("Artist")]
    public string Artist { get; set; } = string.Empty;

    [XmlAttribute("Album")]
    public string Album { get; set; } = string.Empty;

    [XmlAttribute("Genre")]
    public string Genre { get; set; } = string.Empty;

    [XmlAttribute("Kind")]
    public string Kind { get; set; } = string.Empty; // e.g., "MP3 File", "FLAC File"

    [XmlAttribute("Size")]
    public long Size { get; set; }

    [XmlAttribute("BitRate")]
    public int BitRate { get; set; }

    [XmlAttribute("SampleRate")]
    public int SampleRate { get; set; }

    [XmlAttribute("Comments")]
    public string Comments { get; set; } = string.Empty;

    [XmlAttribute("Location")]
    public string Location { get; set; } = string.Empty; // URL-encoded path e.g. file://localhost/C:/...

    [XmlAttribute("AverageBpm")]
    public double AverageBpm { get; set; }

    [XmlAttribute("Tonality")]
    public string Tonality { get; set; } = string.Empty; // Camelot key

    [XmlAttribute("PlayCount")]
    public int PlayCount { get; set; }

    [XmlAttribute("Rating")]
    public int Rating { get; set; }

    [XmlElement("TEMPO")]
    public List<RekordboxTempoXml> Tempos { get; set; } = new();

    [XmlElement("POSITION_MARK")]
    public List<RekordboxPositionMarkXml> PositionMarks { get; set; } = new();
}

public sealed class RekordboxTempoXml
{
    [XmlAttribute("Beginning")]
    public double Beginning { get; set; } // seconds (downbeat anchor offset)

    [XmlAttribute("Bpm")]
    public double Bpm { get; set; }

    [XmlAttribute("Metro")]
    public string Metro { get; set; } = "4/4";

    [XmlAttribute("Battito")]
    public int Battito { get; set; } = 1;
}

public sealed class RekordboxPositionMarkXml
{
    [XmlAttribute("Name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("Start")]
    public double Start { get; set; } // seconds

    [XmlAttribute("End")]
    public double End { get; set; } // seconds (for loops)

    [XmlAttribute("Type")]
    public int Type { get; set; } // 0 = Memory/Hot Cue, 1 = Loop

    [XmlAttribute("Num")]
    public int Num { get; set; } = -1; // -1 = Memory Cue, 0-7 = Hot Cue A-H

    [XmlAttribute("Color")]
    public string Color { get; set; } = string.Empty; // Hex color (optional)

    // Helper properties to check serialization
    public bool ShouldSerializeEnd() => Type == 1;
    public bool ShouldSerializeNum() => Num >= 0;
    public bool ShouldSerializeColor() => !string.IsNullOrEmpty(Color);
}

public sealed class RekordboxPlaylistsContainerXml
{
    [XmlElement("NODE")]
    public List<RekordboxNodeXml> Nodes { get; set; } = new();
}

public sealed class RekordboxNodeXml
{
    [XmlAttribute("Type")]
    public int Type { get; set; } // 0 = Folder, 1 = Playlist

    [XmlAttribute("Name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("Count")]
    public int Count { get; set; } // items count in playlist

    [XmlElement("KEY")]
    public List<RekordboxKeyXml> TrackKeys { get; set; } = new();

    [XmlElement("NODE")]
    public List<RekordboxNodeXml> Children { get; set; } = new();

    public bool ShouldSerializeCount() => Type == 1;
    public bool ShouldSerializeTrackKeys() => Type == 1;
    public bool ShouldSerializeChildren() => Type == 0;
}

public sealed class RekordboxKeyXml
{
    [XmlAttribute("Type")]
    public string Type { get; set; } = "TrackID";

    [XmlAttribute("Key")]
    public int Key { get; set; } // maps to TRACK.TrackID
}
