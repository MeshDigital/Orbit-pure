# File Path Resolution Implementation

## Overview
This implementation adds robust file path resolution to handle moved or renamed music files in the library. It uses a multi-step matching strategy with fuzzy logic to automatically locate missing files.

## What Was Implemented

### 1. **StringDistanceUtils** (`Utils/StringDistanceUtils.cs`)
A utility class for fuzzy string matching using the Levenshtein Distance algorithm.

**Features:**
- `ComputeLevenshteinDistance(s, t)`: Calculates edit distance between two strings
- `GetNormalizedMatchScore(s, t)`: Returns similarity score from 0.0 to 1.0 (higher = better match)
- `Normalize(input)`: Normalizes strings for robust comparison

**Algorithm:** Removes special characters, converts to lowercase, computes minimum edit distance.

### 2. **AppConfig Extensions** (`Configuration/AppConfig.cs`)
Added three new configuration properties:

```csharp
public List<string> LibraryRootPaths { get; set; } = new(); 
public bool EnableFilePathResolution { get; set; } = true;
public double FuzzyMatchThreshold { get; set; } = 0.85;
```

**Purpose:**
- `LibraryRootPaths`: Directories to scan when searching for moved files
- `EnableFilePathResolution`: Master toggle for the feature
- `FuzzyMatchThreshold`: Minimum similarity score (0.85 = 85% match required)

### 3. **FilePathResolverService** (`Services/FilePathResolverService.cs`)
Extracted from `LibraryService` to follow Single Responsibility Principle.

#### Main Method: `ResolveMissingFilePathAsync(LibraryEntry missingTrack)`
Multi-step resolution process:

**Step 0: Fast Check**
- Verifies if original path still exists (quick exit)

**Step 1: Filename Match**
- Searches for exact filename in all configured library root paths
- Fast recursive directory enumeration
- Case: File moved but kept same name

**Step 2: Fuzzy Metadata Match**
- Constructs search query: `"{Artist} - {Title}"`
- Scans all music files (`.mp3`, `.flac`, `.m4a`, etc.)
- Uses filename as metadata proxy (faster than reading tags)
- Returns best match above threshold

#### Helper Methods:
- `SearchByFilename()`: Exact filename search across root paths
- `SearchByFuzzyMetadata()`: Levenshtein-based similarity search

### 3b. **LibraryService Updates** (`Services/LibraryService.cs`)
- `UpdateLibraryEntryPathAsync()`: Persists resolved path to database
- Delegates actual resolution logic to `IFilePathResolverService`

### 4. **TrackEntity Schema Updates** (`Data/TrackEntity.cs`)
Enhanced `LibraryEntryEntity` with:

```csharp
public string? OriginalFilePath { get; set; }
public DateTime? FilePathUpdatedAt { get; set; }
```

**Purpose:**
- Track original path for audit/debugging
- Record when path was last resolved

## Usage Example

```csharp
// In your library scanning logic:
var missingEntry = await _libraryService.FindLibraryEntryAsync(uniqueHash);

if (missingEntry != null && !File.Exists(missingEntry.FilePath))
{
    // Attempt to resolve
    string? newPath = await _libraryService.ResolveMissingFilePathAsync(missingEntry);
    
    if (newPath != null)
    {
        // Update the database
        await _libraryService.UpdateLibraryEntryPathAsync(uniqueHash, newPath);
        _logger.LogInformation("Resolved missing file: {NewPath}", newPath);
    }
    else
    {
        _logger.LogWarning("Could not resolve: {Artist} - {Title}", 
            missingEntry.Artist, missingEntry.Title);
    }
}
```

## Configuration Setup

Add library root paths to `config.ini` or via UI settings:

```ini
LibraryRootPaths=C:\Music;D:\Downloads\Music;E:\iTunes
EnableFilePathResolution=true
FuzzyMatchThreshold=0.85
```

## Performance Considerations

1. **Filename Search**: Fast - uses `EnumerateFiles` with early exit on first match
2. **Fuzzy Search**: Expensive - scans all music files in library
   - Only runs if filename search fails
   - Skipped if metadata is incomplete
   - Consider limiting to recently added files for large libraries

## Future Enhancements

1. **Tag-Based Matching**: Read actual ID3/FLAC tags instead of filenames (more accurate but slower)
2. **Incremental Scanning**: Cache file list and only scan new/changed files
3. **User Confirmation**: Present match suggestions to user before auto-updating
4. **Acoustic Fingerprinting**: Use audio fingerprinting (e.g., Chromaprint) for 100% accuracy
5. **Background Service**: Run resolution checks periodically in background

## Database Migration Note

**Important:** The schema changes to `LibraryEntryEntity` require a database migration.

If using EF Core migrations:
```bash
dotnet ef migrations add AddFilePathResolutionFields
dotnet ef database update
```

If managing SQLite manually, add these columns:
```sql
ALTER TABLE LibraryEntries ADD COLUMN OriginalFilePath TEXT;
ALTER TABLE LibraryEntries ADD COLUMN FilePathUpdatedAt TEXT;
```

## Testing Checklist

- [ ] Test with file moved to different folder (same name)
- [ ] Test with file renamed (same location)
- [ ] Test with file moved AND renamed
- [ ] Test with missing metadata (Artist/Title)
- [ ] Test with multiple similar files (threshold validation)
- [ ] Test with empty LibraryRootPaths
- [ ] Test with disabled EnableFilePathResolution
- [ ] Performance test with 10,000+ library entries

## Next Steps (From Roadmap)

This completes **Phase 1 - Week 1: Fix File Path Resolution**. 

Next priority tasks:
1. **Library Scan Service**: Add automatic scanning/validation on startup
2. **UI Integration**: Add settings page for LibraryRootPaths configuration
3. **User Feedback**: Show toast notifications for successful resolutions
4. **Batch Resolution**: Add "Scan & Fix All Missing Files" button in Library view
