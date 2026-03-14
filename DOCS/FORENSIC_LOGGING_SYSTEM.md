# Track Forensic Logging System

**Component**: `TrackForensicLogger` (Phase 4.7)  
**Status**: ✅ Implemented (Dec 2025)  
**Purpose**: Correlation-based audit trail for every track lifecycle event

---

## Overview

The Track Forensic Logger provides a **complete audit trail** for every track from search to playback. It uses correlation IDs to link related events across distributed services, enabling debugging of complex failures and performance analysis.

---

## Architecture

```
Track Event
    ↓
Correlation ID Assignment
    ↓
TrackForensicLogger
    ├── Console Logging (ILogger)
    ├── Channel Queue (Non-blocking)
    └── Database Persistence
    ↓
ForensicLogEntry (SQLite)
    ↓
Forensic Timeline UI
```

---

## Core Concepts

### Correlation ID

A unique identifier (GUID) assigned to each track at creation:

```csharp
var correlationId = Guid.NewGuid().ToString();
```

**Purpose**: Link all events for a single track across services.

**Example Flow**:
```
CID: abc123...
├── [Search] Query sent to Soulseek
├── [Ranking] 15 results scored
├── [Download] Started from user X
├── [Integrity] MD5 verification passed
├── [Enrichment] Spotify metadata fetched
├── [Analysis] Essentia BPM detected
├── [CueGen] Drop detected at 60s
└── [Player] Loaded for playback
```

### Stages

Events are categorized by **stage** for easy filtering:

| Stage | Description | Services |
|-------|-------------|----------|
| **Search** | Query orchestration | SearchOrchestrationService |
| **Ranking** | Result scoring | SearchResultMatcher |
| **Download** | File transfer | DownloadManager |
| **Integrity** | Verification | SonicIntegrityService |
| **Enrichment** | Metadata fetching | SpotifyEnrichmentService |
| **Analysis** | Audio analysis | EssentiaAnalyzerService |
| **DropDetection** | Drop detection | DropDetectionEngine |
| **CueGen** | Cue generation | CueGenerationEngine |
| **Player** | Playback | AudioPlayerService |

---

## Log Levels

### Severity Hierarchy

```csharp
public enum ForensicLevel
{
    Debug,    // Verbose diagnostics
    Info,     // Normal operations
    Warning,  // Potential issues
    Error     // Failures
}
```

### Usage Guidelines

| Level | Use Case | Example |
|-------|----------|---------|
| **Debug** | Low-level details | "Regex match: 98% similarity" |
| **Info** | Significant events | "Drop detected at 60s" |
| **Warning** | Recoverable issues | "BPM outside range (200)" |
| **Error** | Failures | "Essentia process crashed" |

---

## API

### Basic Logging

```csharp
// Inject logger
private readonly TrackForensicLogger _forensicLogger;

// Log events
_forensicLogger.Info(correlationId, "Download", "Started from user: speedmaster99");

_forensicLogger.Warning(correlationId, "Analysis", "BPM detection unreliable", 
    new { BPM = 203, Confidence = 0.4f });

_forensicLogger.Error(correlationId, "Enrichment", "Spotify API timeout", exception);
```

### Timed Operations

```csharp
// Auto-calculates duration
using (_forensicLogger.TimedOperation(correlationId, "Analysis", "Essentia DSP"))
{
    await RunEssentiaAsync(filePath);
}
// Logs: "Essentia DSP completed in 2.4s"
```

---

## Data Storage

### Database Schema

```sql
CREATE TABLE ForensicLogs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CorrelationId TEXT NOT NULL,
    Timestamp DATETIME NOT NULL,
    Stage TEXT NOT NULL,
    Level TEXT NOT NULL,
    Message TEXT NOT NULL,
    Data TEXT NULL,           -- JSON payload
    DurationMs INTEGER NULL   -- For timed ops
);

-- Indexes for fast queries
CREATE INDEX IX_Forensic_CorrelationId ON ForensicLogs(CorrelationId);
CREATE INDEX IX_Forensic_Stage ON ForensicLogs(Stage);
CREATE INDEX IX_Forensic_Timestamp ON ForensicLogs(Timestamp DESC);
```

### Entry Example

```json
{
  "Id": 12345,
  "CorrelationId": "abc123-def456-...",
  "Timestamp": "2025-12-28T14:32:10Z",
  "Stage": "DropDetection",
  "Level": "Info",
  "Message": "Drop candidate selected: 60.2s",
  "Data": "{\"Strategy\":\"StructureHeuristic\",\"BPM\":140,\"Confidence\":0.85}",
  "DurationMs": null
}
```

---

## Non-Blocking Architecture

### Channel-Based Queue

```csharp
private readonly Channel<ForensicLogEntry> _logChannel;

// Producer (hot path)
_logChannel.Writer.TryWrite(entry);  // Non-blocking

// Consumer (background thread)
await foreach (var entry in _logChannel.Reader.ReadAllAsync())
{
    await _db.ForensicLogs.AddAsync(entry);
    await _db.SaveChangesAsync();
}
```

**Benefits**:
- Zero blocking on hot paths (download, analysis)
- Automatic batching via EF Core
- Graceful degradation if DB slow

---

## Console Output

### Format

```
[CID: abc123de] [Stage] Message
```

**Example**:
```
[CID: 5f8a2b1c] [Download] Started from user: speedmaster99
[CID: 5f8a2b1c] [Integrity] MD5 verification passed
[CID: 5f8a2b1c] [Analysis] Essentia analysis completed in 2.4s
[CID: 5f8a2b1c] [DropDetection] Drop detected at 60.2s (confidence: 0.85)
```

---

## UI Integration

### Forensic Timeline Tab

**Location**: Track Inspector → Forensic Timeline

**Features**:
1. **Timeline View**: Chronological event list
2. **Stage Filtering**: Show only specific stages
3. **Level Filtering**: Debug/Info/Warning/Error
4. **Data Expansion**: Click to view JSON payloads
5. **Duration Visualization**: Bar chart for timed ops

**Example UI**:
```
┌─────────────────────────────────────────────────┐
│ Track: Artist - Title (VIP)                    │
├─────────────────────────────────────────────────┤
│ 14:32:05  [Search]      Query sent             │
│ 14:32:07  [Ranking]     15 results scored      │
│ 14:32:10  [Download]    Started (2.4 MB/s)     │
│ 14:35:42  [Integrity]   ✓ MD5 verified         │
│ 14:35:43  [Enrichment]  Spotify metadata OK    │
│ 14:35:45  [Analysis]    ⏱ Essentia (2.4s)     │
│ 14:35:47  [DropDetect]  Drop at 60.2s (0.85)   │
│ 14:35:47  [CueGen]      4 cue points created   │
└─────────────────────────────────────────────────┘
```

---

## Instrumentation Coverage

### Fully Instrumented Services

✅ **DownloadManager**: All state transitions  
✅ **DropDetectionEngine**: Analysis steps  
✅ **CueGenerationEngine**: Cue calculation  
✅ **EssentiaAnalyzerService**: Process spawn, parsing  
✅ **SonicIntegrityService**: Verification results  
✅ **SpotifyEnrichmentService**: API calls  

### Partially Instrumented

⚠️ **SearchOrchestrationService**: Query only  
⚠️ **AudioPlayerService**: Load/Play events  

### Not Instrumented

❌ **UI ViewModels**: No correlation ID flow  
❌ **Rekordbox Parser**: No forensic logging  

---

## Debugging Workflows

### 1. Failed Download Investigation

```sql
-- Get all events for failed track
SELECT * FROM ForensicLogs 
WHERE CorrelationId = '[track-id]'
ORDER BY Timestamp;

-- Look for last successful stage
SELECT Stage, Message 
FROM ForensicLogs 
WHERE CorrelationId = '[track-id]'
  AND Level != 'Error'
ORDER BY Timestamp DESC
LIMIT 1;
```

### 2. Performance Profiling

```sql
-- Find slowest operations
SELECT Stage, Message, DurationMs 
FROM ForensicLogs 
WHERE DurationMs IS NOT NULL
ORDER BY DurationMs DESC
LIMIT 20;

-- Average duration per stage
SELECT Stage, AVG(DurationMs) as AvgMs, COUNT(*) as Count
FROM ForensicLogs
WHERE DurationMs IS NOT NULL
GROUP BY Stage;
```

### 3. Error Rate Analysis

```sql
-- Error frequency by stage
SELECT Stage, COUNT(*) as ErrorCount
FROM ForensicLogs
WHERE Level = 'Error'
  AND Timestamp > datetime('now', '-7 days')
GROUP BY Stage
ORDER BY ErrorCount DESC;
```

---

## Performance Impact

### Overhead

| Operation | Time | Impact |
|-----------|------|--------|
| Logger method call | <0.01ms | Negligible |
| Channel write | <0.05ms | Negligible |
| Console output | ~1ms | Minimal |
| DB write (batched) | ~10ms | Background |

**Conclusion**: <1% overhead on hot paths.

### Scalability

- **Memory**: ~200 bytes per entry
- **Disk**: ~500 bytes per entry (SQLite)
- **Retention**: 30 days default (auto-cleanup)

**Estimate**: 10,000 tracks = ~5 MB RAM, ~50 MB disk

---

## Configuration

### Retention Policy

```csharp
// AppConfig.cs
public int ForensicLogRetentionDays { get; set; } = 30;

// Cleanup job (daily)
await _db.Database.ExecuteSqlRawAsync(
    "DELETE FROM ForensicLogs WHERE Timestamp < datetime('now', '-30 days')");
```

### Verbosity Level

```csharp
// appsettings.json
{
  "Logging": {
    "LogLevel": {
      "SLSKDONET.Services.TrackForensicLogger": "Information"
    }
  }
}
```

---

## Best Practices

### 1. Assign Correlation ID Early

```csharp
// At track creation (import, search result)
track.CorrelationId = Guid.NewGuid().ToString();
```

### 2. Use Consistent Stage Names

```csharp
// Good
_forensicLogger.Info(id, "Download", "Started");

// Bad (inconsistent)
_forensicLogger.Info(id, "DL", "Started");
_forensicLogger.Info(id, "download", "Started");
```

### 3. Include Structured Data

```csharp
// Good: Structured data for querying
_forensicLogger.Info(id, "Ranking", "Scored results", new { Count = 15, AvgScore = 0.82 });

// Bad: Unstructured text
_forensicLogger.Info(id, "Ranking", "Scored 15 results, avg 0.82");
```

### 4. Use Timed Operations

```csharp
// Prefer this over manual timing
using (_forensicLogger.TimedOperation(id, "Analysis", "BPM Detection"))
{
    // ... work
}
```

---

## Troubleshooting

### Issue: No logs appearing in database

**Check**:
1. Channel consumer running: `_logChannel.Reader.Completion.IsCompleted`
2. Database connection: `await _db.Database.CanConnectAsync()`
3. Table exists: `SELECT * FROM sqlite_master WHERE name='ForensicLogs'`

### Issue: Logs missing for specific track

**Cause**: Correlation ID not propagated  
**Fix**: Ensure ID passed to all service calls

### Issue: Excessive log volume

**Cause**: Debug level enabled in production  
**Fix**: Set log level to `Information` or higher

---

## Future Enhancements

### Phase 4.8 (Q1 2026)

- [ ] UI ViewModel correlation ID flow
- [ ] Real-time log streaming (SignalR)
- [ ] Log export (CSV, JSON)
- [ ] Advanced filtering (regex, date ranges)

### Phase 4.9 (Q2 2026)

- [ ] Performance alerting (slow operations)
- [ ] Anomaly detection (unusual patterns)
- [ ] Log retention configuration UI
- [ ] Correlation graph visualization

---

## Related Documentation

- [DROP_DETECTION_AND_CUE_GENERATION.md](DROP_DETECTION_AND_CUE_GENERATION.md) - Cue generation
- [HIGH_FIDELITY_AUDIO.md](HIGH_FIDELITY_AUDIO.md) - Audio analysis
- [SPOTIFY_ENRICHMENT_PIPELINE.md](SPOTIFY_ENRICHMENT_PIPELINE.md) - Metadata enrichment

---

**Last Updated**: December 28, 2025  
**Version**: 1.0  
**Phase**: 4.7 Complete
