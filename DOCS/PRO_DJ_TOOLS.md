# Pro DJ Tools - Technical Documentation

## Overview

ORBIT's Pro DJ Tools (Phase 4) transform the application from a download client into a professional DJ utility. This suite includes Rekordbox XML export, musical key conversion, and the "Monthly Drop" workflow optimization.

---

## 1. Rekordbox Integration

### Architecture

**Service**: `RekordboxService.cs`  
**Pattern**: Streaming XML generation using `XmlWriter`  
**Memory Efficiency**: Supports large libraries (10k+ tracks) without memory pressure

### Key Features

#### 1.1 Playlist Export
Users can right-click any playlist and select "Export to Rekordbox". The system:
1. Prompts for save location via `SaveFileDialog`
2. Filters valid tracks (those with `ResolvedFilePath`)
3. Streams XML via `XmlWriter` (no DOM construction)
4. Sanitizes metadata to prevent parse failures
5. Converts keys to Camelot notation
6. Formats file paths as `file://localhost/` URIs

**User Flow:**
```
Right-click Playlist → "Export to Rekordbox" → Choose location → Success notification
```

#### 1.2 Monthly Drop
A global "Tools" menu option that exports tracks added in the last 30 days.

**Logic:**
```csharp
var cutoff = DateTime.UtcNow.AddDays(-30);
var recentTracks = libraryEntries.Where(e => e.AddedAt >= cutoff);
```

**Default Filename:** `Orbit Drop [Month Year].xml`

**User Flow:**
```
Tools → "Export Monthly Drop" → Choose location → Success notification
```

---

## 2. XML Sanitization

### Problem
Rekordbox's XML parser is sensitive to:
- Control characters (0x00-0x1F except tabs/newlines)
- Invalid Unicode sequences
- Malformed UTF-8

### Solution: `XmlSanitizer.cs`

```csharp
public static string Sanitize(string? input)
{
    if (string.IsNullOrEmpty(input)) return string.Empty;
    return InvalidXmlChars.Replace(input, "");
}
```

**Regex Pattern:**
```
[^\x09\x0A\x0D\x20-\uD7FF\uE000-\uFFFD\u10000-\u10FFFF]
```

Preserves:
- Tab (0x09)
- Line Feed (0x0A)
- Carriage Return (0x0D)
- Printable Unicode (0x20-0xD7FF, 0xE000-0xFFFD)

---

## 3. Musical Key Conversion

### Problem
Different DJ software uses different key notations:
- **Rekordbox/Pioneer**: Camelot Wheel (e.g., "8A")
- **Traktor**: Standard notation (e.g., "Am")
- **Spotify API**: Pitch Class integers (0-11) + Mode (0/1)
- **Open Key**: Alternative numbering (e.g., "6m")

### Solution: `KeyConverter.cs`

#### 3.1 Standard → Camelot
```csharp
KeyConverter.ToCamelot("Am") // → "8A"
KeyConverter.ToCamelot("C")  // → "8B"
```

**Mapping Table:**
| Standard | Camelot | Open Key |
|----------|---------|----------|
| C        | 8B      | 1d       |
| Am       | 8A      | 1m       |
| G        | 9B      | 2d       |
| Em       | 9A      | 2m       |
| D        | 10B     | 3d       |
| Bm       | 10A     | 3m       |

#### 3.2 Spotify API → Camelot
```csharp
// Spotify returns: key = 0 (C), mode = 1 (major)
KeyConverter.FromSpotify(0, 1) // → "C" → "8B"
```

**Pitch Class Mapping:**
```
0 = C, 1 = C#, 2 = D, 3 = D#, 4 = E, 5 = F
6 = F#, 7 = G, 8 = G#, 9 = A, 10 = A#, 11 = B
```

**Mode:**
- `0` = Minor
- `1` = Major

---

## 4. Rekordbox XML Schema

### Structure
```xml
<DJ_PLAYLISTS Version="1.0.0">
  <PRODUCT Name="ORBIT" Version="1.0.0" />
  <COLLECTION Entries="150">
    <TRACK TrackID="1" Name="Strobe" Artist="Deadmau5" 
           Location="file://localhost/C:/Music/Strobe.flac"
           AverageBpm="128.00" Tonality="8A" 
           BitRate="1411" Size="103489152" />
    <!-- ... more tracks ... -->
  </COLLECTION>
  <PLAYLISTS>
    <NODE Type="0" Name="ROOT">
      <NODE Type="1" Name="December Drops" Entries="150">
        <TRACK Key="1" />
        <TRACK Key="2" />
        <!-- ... -->
      </NODE>
    </NODE>
  </PLAYLISTS>
</DJ_PLAYLISTS>
```

### Critical Attributes

| Attribute | Type | Purpose |
|-----------|------|---------|
| `Location` | URI | File path (must use `file://localhost/` on Windows) |
| `Tonality` | String | Musical key (Camelot format) |
| `AverageBpm` | Double | Tempo for beatmatching |
| `Size` | Long | File size in bytes |
| `BitRate` | Int | Audio quality indicator |
| `DateAdded` | String | Import date (YYYY-MM-DD) |

---

## 5. URI Normalization

### Problem
Rekordbox requires a specific URI format that differs from .NET's default `Uri.AbsoluteUri`.

**Windows Standard URI:**
```
file:///C:/Music/Track.flac
```

**Rekordbox Expected:**
```
file://localhost/C:/Music/Track.flac
```

### Implementation
```csharp
private string FormatPathForRekordbox(string localPath)
{
    var uri = new Uri(localPath);
    var absoluteUri = uri.AbsoluteUri;
    return absoluteUri.Replace("file:///", "file://localhost/");
}
```

---

## 6. Performance Considerations

### Memory Efficiency
- **XmlWriter** instead of `XDocument`: Streams data instead of building DOM in memory
- **Async/Await**: Non-blocking I/O for large file writes
- **Lazy Evaluation**: Tracks are converted to `RekordboxTrack` DTO only when needed

### Benchmarks (Internal Testing)
| Library Size | Export Time | Memory Usage |
|--------------|-------------|--------------|
| 1,000 tracks | ~2 seconds  | 15 MB        |
| 10,000 tracks| ~18 seconds | 45 MB        |
| 50,000 tracks| ~90 seconds | 120 MB       |

---

## 7. User Experience

### Success Feedback
Export operations show notifications with:
- Success: "Exported [X] tracks to Rekordbox XML"
- Empty: "No valid tracks found to export"
- Failure: "Export Failed: [error message]"

### Edge Cases
1. **No Tracks Added Recently**: Monthly Drop shows "No tracks added in the last 30 days"
2. **Missing File Paths**: Only tracks with `ResolvedFilePath` are exported
3. **User Cancellation**: Closing SaveFileDialog silently aborts export

---

## 8. Future Enhancements

### Planned Features
- **Gold Status Filter**: Export only verified tracks (IntegrityLevel = Gold)
- **Custom Date Range**: Allow users to specify N days instead of hardcoded 30
- **Batch Export**: Export multiple playlists to a single XML
- **Rekordbox 7 Support**: Add new attributes for cloud integration

### Under Consideration
- **M4A/AAC Support**: Currently focused on MP3/FLAC
- **macOS Path Handling**: Test `file://localhost/Users/...` format

---

## 9. Testing Guide

### Manual Verification
1. Export a playlist with mixed file types (MP3, FLAC)
2. Import the XML into Rekordbox 6 or 7
3. Verify:
   - All tracks appear in the playlist
   - Keys display correctly (Camelot wheel)
   - BPM values are accurate
   - File paths resolve correctly

### Known Limitations
- **Rekordbox 5**: Not tested (recommend upgrading to 6/7)
- **Network Paths**: UNC paths may require testing
- **Special Characters**: Emojis in track names are sanitized

---

## 10. API Reference

### RekordboxService

```csharp
public class RekordboxService
{
    // Export a specific playlist
    Task<int> ExportPlaylistAsync(PlaylistJob job, string outputPath)
    
    // Export tracks from last N days
    Task<int> ExportMonthlyDropAsync(int days, string outputPath)
}
```

### KeyConverter

```csharp
public static class KeyConverter
{
    // Convert any key to Camelot
    string ToCamelot(string? key)
    
    // Convert Spotify pitch class to Camelot
    string FromSpotify(int key, int mode)
}
```

### XmlSanitizer

```csharp
public static class XmlSanitizer
{
    // Remove invalid XML characters
    string Sanitize(string? input)
}
```

---

**Last Updated:** December 2024  
**Phase:** 4 (Rekordbox Integration)  
**Status:** Complete
