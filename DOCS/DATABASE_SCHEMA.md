# Database Schema - The Memory Layer

**See also:** [DATABASE_LIBRARY_FLOW.md](DATABASE_LIBRARY_FLOW.md) for end-to-end data flow from ingest → queue → download → library/playlist/queue UI.

## Overview

ORBIT's database schema is designed around the **Dual-Truth** principle: Trust user input, but verify with multiple sources. This allows DJs to override auto-detected metadata while preserving the original source data for verification.

---

## Core Entities

### 1. LibraryEntryEntity (The Source of Truth)

**Purpose**: Represents a single audio file in the user's library.

```csharp
public class LibraryEntryEntity
{
    // Primary Key
    public Guid Id { get; set; }
    public string UniqueHash { get; set; } // MD5(Artist + Title + Album)
    
    // File Metadata
    public string FilePath { get; set; }
    public long FileSize { get; set; }
    public DateTime AddedAt { get; set; }
    
    // Basic Metadata
    public string Title { get; set; }
    public string Artist { get; set; }
    public string Album { get; set; }
    public string Format { get; set; } // "mp3", "flac", etc.
    public int Bitrate { get; set; }
    public int? DurationSeconds { get; set; }
    
    // === DUAL-TRUTH METADATA (Phase 3B) ===
    
    // Analyzed (File Tags)
    public double? BPM { get; set; }
    public string? MusicalKey { get; set; } // e.g., "8A" (Camelot)
    
    // Spotify (API Source)
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public int? SpotifyDuration { get; set; }
    public string? SpotifyTrackId { get; set; }
    
    // Manual (User Override)
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }
    
    // === INTEGRITY TRACKING (Phase 3B) ===
    public IntegrityLevel IntegrityLevel { get; set; } = IntegrityLevel.Pending;
    
    // === UPGRADE TRACKING (Phase 5) ===
    public DateTime? LastUpgradeScanAt { get; set; }
    public DateTime? LastUpgradeAt { get; set; }
    public string? UpgradeSource { get; set; } // "Auto" or "Manual"
}
```

---

## IntegrityLevel Enum

### Definition

```csharp
public enum IntegrityLevel
{
    Pending = 0,  // Initial state
    Bronze = 1,   // File downloaded, basic tags written
    Silver = 2,   // Spotify metadata enriched
    Gold = 3      // All metadata verified and aligned
}
```

### Progression Rules

| Level | Criteria |
|-------|----------|
| **Pending** | Track exists in database but file not yet downloaded |
| **Bronze** | File downloaded successfully, basic ID3 tags written |
| **Silver** | Spotify enrichment complete (BPM, Key, Album Art) |
| **Gold** | User verified OR all sources aligned (±0.5 BPM tolerance) |

### Gold Status Requirements

A track reaches **Gold** when:
1. **BPM Alignment**: `|SpotifyBPM - BPM| ≤ 0.5`
2. **Key Alignment**: `SpotifyKey == MusicalKey` (after Camelot conversion)
3. **Duration Match**: `|SpotifyDuration - DurationSeconds| ≤ 5`
4. **OR**: User manually sets `IntegrityLevel = Gold`

**Importance**: Gold Status tracks are the only candidates for automated upgrades in Phase 5, ensuring the Self-Healing Library only improves verified tracks.

---

## Dual-Truth Resolution

When displaying or exporting metadata, ORBIT uses the following priority order:

### For BPM
```csharp
public double GetResolvedBPM()
{
    return ManualBPM       // User override wins
        ?? BPM             // Analyzed value (file tags)
        ?? SpotifyBPM      // Spotify fallback
        ?? 0.0;
}
```

### For Musical Key
```csharp
public string GetResolvedKey()
{
    return ManualKey       // User override wins
        ?? MusicalKey      // Analyzed value (file tags)
        ?? SpotifyKey      // Spotify fallback
        ?? "Unknown";
}
```

**UI Indicator**: Tracks with `ManualBPM` or `ManualKey` display a "📌" icon to show user override.

---

## 2. PlaylistJob (Collection Container)

**Purpose**: Represents an imported playlist (Spotify, CSV, or manual).

```csharp
public class PlaylistJob
{
    public Guid Id { get; set; }
    public string SourceTitle { get; set; }
    public string SourceType { get; set; } // "Spotify", "CSV", "Manual"
    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Stats (cached for performance)
    public int TotalTracks { get; set; }
    public int SuccessfulCount { get; set; }
    public int FailedCount { get; set; }
    public int MissingCount { get; set; }
    
    // Navigation
    public ICollection<PlaylistTrackEntity> Tracks { get; set; }
}
```

---

## 3. PlaylistTrackEntity (Join Table)

**Purpose**: Links playlists to library entries, allowing tracks to appear in multiple playlists.

```csharp
public class PlaylistTrackEntity
{
    public Guid Id { get; set; }
    
    // Foreign Keys
    public Guid PlaylistId { get; set; }
    public string TrackUniqueHash { get; set; } // References LibraryEntry
    
    // Metadata (copied from import source)
    public string Artist { get; set; }
    public string Title { get; set; }
    public string Album { get; set; }
    
    // Track-specific state
    public TrackStatus Status { get; set; }
    public string ResolvedFilePath { get; set; }
    
    // User Engagement
    public int Rating { get; set; } // 1-5 stars
    public bool IsLiked { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    
    // Dual-Truth Metadata (denormalized for performance)
    public double? BPM { get; set; }
    public string? MusicalKey { get; set; }
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
}
```

---

## Schema Evolution

### Phase 0-1: Foundation
- Basic library structure
- Spotify integration

### Phase 3A: Atomic Downloads
- Added `.part` file tracking
- Download resume state

### Phase 3B: Dual-Truth Schema (Current)
- `IntegrityLevel` enum
- `SpotifyBPM`, `SpotifyKey`, `SpotifyDuration`
- `ManualBPM`, `ManualKey`

### Phase 5: Self-Healing (Planned)
- `LastUpgradeScanAt`
- `LastUpgradeAt`
- `UpgradeSource`

---

## Indexing Strategy

### Critical Indexes

```sql
-- Unique constraint for deduplication
CREATE UNIQUE INDEX IX_LibraryEntries_UniqueHash 
ON LibraryEntries (UniqueHash);

-- Query optimization for playlist loading
CREATE INDEX IX_PlaylistTracks_PlaylistId 
ON PlaylistTracks (PlaylistId);

-- Fast track lookups
CREATE INDEX IX_PlaylistTracks_TrackUniqueHash 
ON PlaylistTracks (TrackUniqueHash);

-- Integrity-based queries (Phase 5)
CREATE INDEX IX_LibraryEntries_IntegrityLevel 
ON LibraryEntries (IntegrityLevel);

-- Upgrade scanning (Phase 5)
CREATE INDEX IX_LibraryEntries_LastUpgradeScanAt 
ON LibraryEntries (LastUpgradeScanAt) 
WHERE LastUpgradeScanAt IS NOT NULL;
```

### Database Optimizations

**SQLite Configuration** (AppDbContext.OnConfiguring):
- **WAL Mode**: Write-Ahead Logging for better concurrency
- **Shared Cache**: Enables cache sharing across connections
- **Busy Timeout**: 2 seconds (prevents "Database is locked")
- **Command Timeout**: 30 seconds for long operations
- **Synchronous Mode**: NORMAL (safe with WAL, faster than FULL)
- **Cache Size**: 10MB per connection
- **Auto-Checkpoint**: 1000 pages (~4MB)

---

## New Entities (Jan 2026)

### SmartCrateDefinitionEntity
- Table: `smart_crate_definitions`
- Fields: `Id (guid, pk)`, `Name`, `RulesJson` (serialized criteria), `SortOrder`, `CreatedAt`, `UpdatedAt`
- Purpose: Rule-driven smart crates/playlists that auto-refresh based on BPM/Energy/Mood/Integrity filters.

### GenreCueTemplateEntity
- Table: `GenreCueTemplates`
- Fields: `Id`, `GenreName`, `DisplayName`, `IsBuiltIn`, Cue1-8 targets/offsets/colors/labels, optional cues 5-8.
- Purpose: Genre-aware cue templates for drop/build/outro placement.

### TrackPhraseEntity
- Table: `TrackPhrases`
- Fields: `Id`, `TrackUniqueHash`, `Type (PhraseType)`, `StartTimeSeconds`, `EndTimeSeconds`, `EnergyLevel`, `Confidence`, `OrderIndex`, `Label`.
- Purpose: Detected phrases/segments used for cue generation and visual overlays.

### LibraryFolderEntity
- Table: `LibraryFolders`
- Fields: `Id (guid)`, `FolderPath`, `IsEnabled`, `AddedAt`, `LastScannedAt`, `TracksFound`.
- Purpose: Configurable library source folders with scan history.

### AnalysisRunEntity (Expanded)
- Table: `analysis_runs`
- Fields: `RunId (guid, pk)`, `TrackUniqueHash`, `TrackTitle`, `FilePath`, `StartedAt`, `CompletedAt`, `DurationMs`, `Tier (AnalysisTier)`, `Status`, `RetryAttempt`, `WorkerThreadId`, `ErrorMessage/Stack`, `FailedStage`, `WaveformGenerated`, `FfmpegAnalysisCompleted`, `EssentiaAnalysisCompleted`, `DatabaseSaved`, `FfmpegDurationMs`, `EssentiaDurationMs`, `DatabaseSaveDurationMs`, `AnalysisVersion`, `TriggerSource`, `BpmConfidence`, `KeyConfidence`, `IntegrityScore`, `CurrentStage`.
- Purpose: Per-run audit trail for analysis with timing and confidence metrics.

### AudioFeaturesEntity (New Fields)
- Added columns: `InstrumentalProbability`, `MoodTag`, `MoodConfidence`, `Arousal`, `Valence`, `Sadness`, `VectorEmbeddingBytes`, `VggishEmbeddingJson`, `VisualizationVectorJson`.
- Purpose: Store vocal probability, mood/arousal/valence, and vector embeddings for Sonic Match/AI features.

---

## Migrations

### Phase 3B: AddIntegrityLevel

```csharp
migrationBuilder.AddColumn<int>(
    name: "IntegrityLevel",
    table: "LibraryEntries",
    nullable: false,
    defaultValue: 0); // Pending

migrationBuilder.AddColumn<double>(
    name: "SpotifyBPM",
    table: "LibraryEntries",
    nullable: true);

migrationBuilder.AddColumn<string>(
    name: "SpotifyKey",
    table: "LibraryEntries",
    nullable: true);

migrationBuilder.AddColumn<double>(
    name: "ManualBPM",
    table: "LibraryEntries",
    nullable: true);

migrationBuilder.AddColumn<string>(
    name: "ManualKey",
    table: "LibraryEntries",
    nullable: true);
```

---

## Transaction Safety

### Write-Ahead Logging (WAL)

All database operations use SQLite's **WAL mode** for concurrency:
- **Reads** never block **Writes**
- **Writes** never block **Reads**
- Auto-checkpoint at 1000 WAL pages

### Connection Pooling

ORBIT uses a dual-connection strategy:
1. **AppDbContext** (EF Core): Standard queries
2. **CrashRecoveryJournal** (ADO.NET): High-frequency writes

---

## Future Enhancements

### Phase 6: Audit Trail
- `LibraryEntryHistory` table for metadata changes
- Track user edits vs. automatic updates

### Phase 8: Spectral Analysis
- `SpectralHash` column for audio fingerprinting
- `QualityConfidence` score (0.0-1.0)

---

**Last Updated:** December 2024  
**Phase:** 3B (Dual-Truth Schema)  
**Status:** Complete
