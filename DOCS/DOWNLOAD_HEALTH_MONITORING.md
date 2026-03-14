# Download Health Monitoring System

**Component**: `DownloadHealthMonitor` (Phase 3B)  
**Status**: ✅ Implemented (Dec 2025)  
**Purpose**: Active monitoring and intervention for stalled downloads

---

## Overview

The Download Health Monitor is "The Heart" of Phase 3B - a background service that continuously monitors active downloads and intervenes when transfers stall. It distinguishes between **Queued** (passive waiting) and **Stalled** (active failure requiring intervention).

---

## Architecture

### Core Components

```
DownloadHealthMonitor
├── Monitor Loop (15s interval)
├── Stall Counter Tracking
├── Byte Progress Delta Analysis
└── Auto-Retry Logic
```

### Key Features

1. **Active Monitoring**
   - Runs on 15-second intervals
   - Tracks progress for all active downloads
   - Calculates byte transfer deltas

2. **Stall Detection**
   - Identifies downloads with 0 byte progress
   - Tracks consecutive stalled ticks
   - Distinguishes "Queued" vs "Stalled"

3. **Auto-Intervention**
   - Triggers retry after N stalled ticks
   - Clears stall counters on success
   - Logs all interventions

---

## Detection Logic

### Stall Criteria

A download is considered **stalled** when:
- State is `Downloading` or `Queued`
- Byte progress delta = 0 for multiple ticks
- Time exceeds configured threshold

### States

| State | Description | Action |
|-------|-------------|--------|
| **Queued** | Waiting in remote queue | Monitor only |
| **Stalled** | Active but not progressing | Auto-retry |
| **Healthy** | Bytes increasing | Reset counters |
| **Failed** | Exceeded retry limit | Mark failed |

---

## Configuration

### Thresholds

```csharp
// Monitor interval
TimeSpan.FromSeconds(15)

// Stall detection
int ConsecutiveStallTicksBeforeRetry = 4; // 60 seconds total

// Retry limits
int MaxAutoRetries = 3;
```

### Tuning Guidelines

- **Fast Connections**: Lower interval (10s)
- **Slow Connections**: Higher stall threshold (6 ticks)
- **High Concurrency**: Increase retry limits

---

## Integration

### Startup

```csharp
// App.xaml.cs or DI Container
services.AddSingleton<DownloadHealthMonitor>();

// After DownloadManager initialization
healthMonitor.StartMonitoring();
```

### Event Handling

```csharp
// Listens to DownloadManager events
_downloadManager.DownloadProgressChanged += OnProgressChanged;
_downloadManager.DownloadStateChanged += OnStateChanged;
```

---

## Monitoring Data

### Tracked Metrics

1. **Stall Counters**
   - Key: Download GlobalId
   - Value: Consecutive stalled ticks
   - Reset: On byte progress or state change

2. **Previous Bytes**
   - Key: Download GlobalId
   - Value: Last known byte count
   - Purpose: Calculate delta for stall detection

3. **Intervention History**
   - Logged to console
   - Includes: GlobalId, Artist, Title, Action

---

## Intervention Logic

### Auto-Retry Flow

```
1. Detect Stall
   ↓
2. Increment Stall Counter
   ↓
3. Check Threshold
   ↓
4. Trigger DownloadManager.Retry()
   ↓
5. Reset Counter
   ↓
6. Monitor New Attempt
```

### Failure Handling

If retries exhausted:
- Mark download as `Failed`
- Log final state
- Remove from active monitoring
- User can manually retry

---

## Logging

### Log Levels

| Level | Event | Example |
|-------|-------|---------|
| **Info** | Start/Stop | "💓 Download Health Monitor started" |
| **Warning** | Stall Detected | "Download stalled: Artist - Title (3 ticks)" |
| **Info** | Retry Triggered | "Auto-retrying: Artist - Title" |
| **Error** | Max Retries | "Max retries exceeded: Artist - Title" |

### Correlation

All logs include:
- GlobalId
- Artist/Title
- Current state
- Stall tick count

---

## Performance Impact

### Resource Usage

- **CPU**: Minimal (<1% on modern hardware)
- **Memory**: ~1KB per active download
- **I/O**: Read-only (no disk writes)

### Scalability

- Tested: Up to 500 concurrent downloads
- Recommended: <100 for optimal responsiveness
- Bottleneck: DownloadManager lock contention

---

## Edge Cases

### Remote Queue Position

**Problem**: File is legitimately queued remotely  
**Solution**: State remains `Queued`, no false positive

### Network Fluctuations

**Problem**: Brief connectivity loss  
**Solution**: 15s interval + multi-tick threshold absorbs transients

### Race Conditions

**Problem**: Download completes during stall check  
**Solution**: Thread-safe ConcurrentDictionary + state validation

---

## Troubleshooting

### Downloads Keep Getting Retried

**Cause**: Stall threshold too low  
**Fix**: Increase `ConsecutiveStallTicksBeforeRetry` to 6-8

### No Auto-Retry Happening

**Check**:
1. Monitor is started: `healthMonitor.StartMonitoring()`
2. Download state is `Downloading` or `Queued`
3. Logs show stall detection

### Memory Leak

**Cause**: Stale entries not cleaned  
**Fix**: Monitor `Dispose()` called on shutdown

---

## Future Enhancements

### Planned (Month 2)

- [ ] Per-user retry strategies
- [ ] Adaptive threshold based on connection speed
- [ ] Health score calculation
- [ ] Predictive failure detection

### Considered

- [ ] Circuit breaker for repeatedly failing users
- [ ] Blacklist/whitelist for auto-retry
- [ ] Integration with search result scoring

---

## Related Documentation

- [MULTI_LANE_ORCHESTRATION.md](MULTI_LANE_ORCHESTRATION.md) - Download priority system
- [DOWNLOAD_RESILIENCE.md](DOWNLOAD_RESILIENCE.md) - Crash recovery
- [ATOMIC_DOWNLOADS.md](ATOMIC_DOWNLOADS.md) - File integrity

---

**Last Updated**: December 28, 2025  
**Version**: 1.0  
**Phase**: 3B Complete
