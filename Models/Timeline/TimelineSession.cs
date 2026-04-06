using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLSKDONET.Models.Timeline;

/// <summary>
/// The root container for a DAW-style arrangement session.
/// Holds all lanes (<see cref="TimelineTrack"/>), global tempo settings,
/// and provides serialisation / deserialization support.
/// </summary>
public class TimelineSession
{
    // ── Identity ──────────────────────────────────────────────────────────
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible session name.</summary>
    public string Name { get; set; } = "New Session";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    // ── Tempo / grid ──────────────────────────────────────────────────────
    /// <summary>Project bpm — all clip beat positions are relative to this.</summary>
    public double ProjectBpm { get; set; } = 128.0;

    /// <summary>Time signature numerator (default 4 = 4/4).</summary>
    public int BeatsPerBar { get; set; } = 4;

    /// <summary>Total session length in bars.</summary>
    public double TotalBars { get; set; } = 32.0;

    // ── Tracks ────────────────────────────────────────────────────────────
    public List<TimelineTrack> Tracks { get; set; } = new();

    // ── Computed properties ───────────────────────────────────────────────
    [JsonIgnore]
    public double TotalBeats => TotalBars * BeatsPerBar;

    [JsonIgnore]
    public double BeatDurationSeconds => 60.0 / ProjectBpm;

    [JsonIgnore]
    public double TotalDurationSeconds => TotalBeats * BeatDurationSeconds;

    // ── Helper conversions ────────────────────────────────────────────────
    public double BeatsToSeconds(double beats) => beats * BeatDurationSeconds;
    public double SecondsToBeats(double seconds) => seconds / BeatDurationSeconds;
    public double BarsToBeats(double bars) => bars * BeatsPerBar;
    public double BeatsToBar(double beats) => beats / BeatsPerBar;

    // ── Track management ──────────────────────────────────────────────────
    /// <summary>Appends a new track and assigns its <c>Index</c> automatically.</summary>
    public TimelineTrack AddTrack(string name = "Track")
    {
        var track = new TimelineTrack { Name = name, Index = Tracks.Count };
        Tracks.Add(track);
        Touch();
        return track;
    }

    /// <summary>Removes a track by id. Returns false if not found.</summary>
    public bool RemoveTrack(Guid trackId)
    {
        int idx = Tracks.FindIndex(t => t.Id == trackId);
        if (idx < 0) return false;
        Tracks.RemoveAt(idx);
        // Re-index remaining tracks
        for (int i = idx; i < Tracks.Count; i++) Tracks[i].Index = i;
        Touch();
        return true;
    }

    /// <summary>Updates <see cref="ModifiedAt"/> to now.</summary>
    public void Touch() => ModifiedAt = DateTime.UtcNow;

    // ── JSON serialisation ────────────────────────────────────────────────
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serialises this session to a UTF-8 JSON string.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, _jsonOptions);

    /// <summary>Deserialises a session from a JSON string. Returns null on failure.</summary>
    public static TimelineSession? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TimelineSession>(json, _jsonOptions); }
        catch { return null; }
    }
}
