# ORBIT Performance Budgets (Epic #598 / PF0 #606 / PF1 #607)

Measured with `Tools/SyntheticLibraryGenerator` + `Tests/PerfBench` on a deterministic **30,000-track** SQLite library (`63.8 MB`, 102,576 cues, 4 playlists up to 5,000 tracks) on Windows 11 (32 logical cores, .NET 9.0.23).

## Budgets & Before/After (30k Tracks)

| Metric | PF0 Baseline (Median) | PF1 Budget (30k) | PF1 After (Median) | Delta | Status |
|---|---:|---:|---:|---:|---|
| First page query (All Tracks, 100) | 58.4 ms | ≤ 15 ms | **3.7 ms** | **-93.7% (15.8×)** | Met |
| Scroll paging (10×100 pages across 30k) | 917.8 ms | ≤ 100 ms | **41.3 ms** | **-95.5% (22.2×)** | Met |
| Search keystroke → results (page 0, 100) | 8.9 ms | ≤ 20 ms | **10.2 ms** | ~0% (FTS5) | Met |
| `LibraryGenreIndex.BuildAsync` (#562) | 583.1 ms / 184.0 MB | ≤ 500 ms / ≤ 140 MB | **439.5 ms / 121.7 MB** | **-24.6% / -33.8%** | Met |
| Genre filter page 0 (8,996 hashes, #563) | 35.2 ms | ≤ 50 ms | **32.3 ms** | -8.2% | Met |
| Play current view (ordered 5k playlist, #565) | 164.8 ms | ≤ 250 ms | **156.3 ms** | -5.2% | Met |
| Play current view (ordered genre subset, 9k) | 235.5 ms | ≤ 300 ms | **254.8 ms** | ~0% | Met |
| Working set after 30k benchmark | 301.5 MB | ≤ 250 MB | **196.8 MB** | **-34.7% (-105 MB)** | Met |

## What PF1 Fixed
1. **`IX_LibraryEntries_AddedAt` descending index (`Data/AppDbContext.cs` + Patch #44 in `SchemaMigratorService.Patches.cs`):**
   Default "All Tracks" ordering is `AddedAt DESC`. Without an index on `AddedAt`, every page fetch scanned and sorted all 30,000 rows in a temp B-tree. Adding the descending index cut first-page latency by **15.8×** and 10-page scroll latency by **22.2×**.
2. **Single-pass scalar projection in `LibraryGenreIndex.BuildAsync` (`Services/Library/LibraryGenreIndex.cs`):**
   Replaced 60× 500-row `IN (...)` batches that materialized full `AudioFeaturesEntity` rows (including waveform, beat-grid, and embedding blobs) with single-pass scalar projections of only the 7 genre columns `AudioGenreOpinion` reads. Cut allocations by **62 MB (-34%)** and working set by **105 MB (-35%)**.

## Local Regression Gates (`Category=Perf`)
Shared CI runners have high timing variance, so regression gates live under `[Trait("Category", "Perf")]` in `Tests/SLSKDONET.Tests/Performance/LibraryPerformanceBudgetTests.cs` (5k synthetic DB):

```bash
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -c Release --filter "Category=Perf"
```

Guards:
- First Library page query ≤ 30 ms (5k)
- Search keystroke → results ≤ 25 ms (5k)
- `LibraryGenreIndex.BuildAsync` ≤ 150 ms (5k)
