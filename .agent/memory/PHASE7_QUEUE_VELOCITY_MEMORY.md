# Phase 7: Queue Velocity & Heartbeat Bug Fixes — Memory

## Summary
This phase hardened the P2P download pipeline against two critical failure modes:
1. **Heartbeat Stall Trap (10s Bug)**: The heartbeat loop in `DownloadManager.DownloadFileAsync` was incorrectly aborting queued downloads after ~10 seconds by applying a zero-throughput stall check to downloads that were in the `Queued` state. Zero throughput is *expected* while waiting in a peer's queue.
2. **Static Queue Timeout**: `SoulseekAdapter` had a hardcoded 5-minute (300s) queue timeout. On Soulseek, popular peers can have 500+ user queues with 2-hour waits. This was causing the engine to aggressively drop high-quality FLACs and fall back to inferior MP3s.

---

## Architecture Decisions

### Queue Velocity (Not Static Timeout)
Instead of timing out on *elapsed time in queue*, we now time out on *queue position stagnation*. A healthy queue advances. A zombie peer's position never moves.

**Three-layer defense:**
1. **Initial Grace Period (120s)**: Peer must report *any* queue position within 2 minutes or it's dropped as unresponsive.
2. **Position Stagnation (15 min)**: If a known position hasn't improved in 15 minutes, the peer is considered a zombie.
3. **Absolute Cap**: `MaxQueueWaitTimeMinutes` config (from Phase 6 additions) provides a user-configurable hard ceiling.

### Heartbeat State Guard
When `ctx.State == PlaylistTrackState.Queued`:
- Stall detection is skipped entirely.
- `stallCount` is reset to 0 on every tick (no "debt" accumulates while queued).
- The `SoulseekAdapter`'s own zombie detection handles queue timeouts.

---

## Files Changed

### `Services/ISoulseekAdapter.cs`
- Added `QueuePositionUpdate` to `TransferLifecyclePhase` enum.
- Added `QueuePosition` (nullable int) to `TransferLifecycleUpdate` record.

### `Services/Models/DownloadContext.cs`
- Added `CurrentQueuePosition` (int, -1 = unknown).
- Added `QueuePositionLastUpdated` (DateTime?) — velocity clock, resets when position changes.
- Added `QueueEnteredAt` (DateTime?) — tracks when we first entered the peer's queue.

### `Services/SoulseekAdapter.cs` (DownloadAsync method)
- Removed `const int QUEUE_TIMEOUT_SECONDS = 300`.
- Added `lastKnownQueuePosition`, `queuePositionLastChanged`, `maxQueueWaitSeconds`, `QUEUE_INITIAL_GRACE_SECONDS`, `QUEUE_STAGNATION_WINDOW_SECONDS`.
- Replaced static zombie check with 3-tier velocity check in the monitoring loop.
- `queuePositionLastChanged` resets when we first enter the queued state.

### `Services/DownloadManager.cs` (DownloadFileAsync method)
- **Heartbeat loop**: Added `if (ctx.State == PlaylistTrackState.Queued)` guard — bypasses stall detection, resets stallCount.
- **Lifecycle handler**: On `RemoteQueued`, sets `ctx.QueueEnteredAt` and `ctx.QueuePositionLastUpdated` (first entry only). On `QueuePositionUpdate`, updates `ctx.CurrentQueuePosition` and `ctx.QueuePositionLastUpdated`. On `Transferring`, sets `ctx.CurrentQueuePosition = 0`.
- Improved stall monitor error message to include configured timeout value.

### `Services/DownloadHealthMonitor.cs` (CheckHealthAsync)
- Now monitors both `Downloading` and `Queued` downloads.
- For Queued downloads: checks if `QueuePositionLastUpdated` hasn't changed in 15 minutes → triggers `AutoRetryStalledDownloadAsync` as safety net.
- Resets `QueuePositionLastUpdated` after triggering to avoid tight retry loops.

---

## Key Constants (all inline in SoulseekAdapter.cs)
| Constant | Value | Meaning |
|---|---|---|
| `QUEUE_INITIAL_GRACE_SECONDS` | 120 | How long before a position report is required |
| `QUEUE_STAGNATION_WINDOW_SECONDS` | 900 | How long position can freeze before zombie trigger |
| `maxQueueWaitSeconds` | max(300, `MaxQueueWaitTimeMinutes * 60`) | Absolute maximum queue wait |

---

## Related Previous Memory Files
- [Phase 6 Download Diagnostics Memory](.agent/memory/PHASE6_DOWNLOAD_DIAGNOSTICS_MEMORY.md)
- [P2P Network Improvements Plan (Artifact)](file:///C:/Users/quint/.gemini/antigravity/brain/370a23dd-12fd-4dd2-9e6b-5a3ebbb65ea6/p2p_network_improvements.md)
