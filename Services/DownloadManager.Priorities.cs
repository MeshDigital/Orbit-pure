using System;
using SLSKDONET.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;

namespace SLSKDONET.Services;

/// <summary>Per-job priority, focus and manual-sort state — extracted from DownloadManager (#307).</summary>
public partial class DownloadManager
{
    /// <summary>
    /// Returns whether the given job currently has Focus Mode active.
    /// </summary>
    public bool GetJobFocused(Guid jobId) => _jobFocused.GetValueOrDefault(jobId, false);

    /// <summary>
    /// Sets the base playlist priority for a job. Active downloads are not interrupted;
    /// only future slot allocations are affected. Persists to DB.
    /// </summary>
    public async Task SetJobPriorityAsync(Guid jobId, PlaylistPriority priority)
    {
        var effectivePriority = _jobFocused.GetValueOrDefault(jobId) ? 0 : (int)priority;

        _jobBasePriority[jobId] = (int)priority;
        _jobEffectivePriority[jobId] = effectivePriority;

        StampJobPriorityOnInMemoryTracks(jobId, effectivePriority);
        await _databaseService.SetJobPriorityAsync(jobId, (int)priority);

        _logger.LogInformation("Job {JobId} priority → {Priority} (effective={Effective})",
            jobId, priority, (PlaylistPriority)effectivePriority);
    }

    /// <summary>
    /// Toggles Focus Mode for a job. When focused, effective priority is forced to Critical (0)
    /// without changing the stored base priority. Toggling off restores the base priority.
    /// Active downloads are not interrupted. Persists to DB.
    /// </summary>
    public async Task ToggleFocusModeAsync(Guid jobId, bool focused)
    {
        _jobFocused[jobId] = focused;
        var effectivePriority = focused ? 0 : _jobBasePriority.GetValueOrDefault(jobId, (int)PlaylistPriority.Normal);
        _jobEffectivePriority[jobId] = effectivePriority;

        StampJobPriorityOnInMemoryTracks(jobId, effectivePriority);
        await _databaseService.SetJobFocusAsync(jobId, focused);

        _logger.LogInformation("Job {JobId} Focus Mode {State} → effective priority {Priority}",
            jobId, focused ? "ON" : "OFF", (PlaylistPriority)effectivePriority);
    }

    /// <summary>
    /// Updates the manual drag-drop sort order for a job and re-stamps in-memory tracks.
    /// Persists to DB.
    /// </summary>
    public async Task SetJobManualSortOrderAsync(Guid jobId, int sortOrder)
    {
        _jobManualSortOrder[jobId] = sortOrder;
        lock (_collectionLock)
        {
            foreach (var ctx in _downloads.Where(d => d.Model.PlaylistId == jobId))
                ctx.Model.JobManualSortOrder = sortOrder;
        }
        await _databaseService.SetJobManualSortOrderAsync(jobId, sortOrder);
    }

    /// <summary>
    /// Removes a deleted job's priority state from all four tracking dictionaries. Without this,
    /// every playlist/job ever created (including deleted ones) kept 4 dictionary entries forever.
    /// </summary>
    private void RemoveJobPriorityState(Guid jobId)
    {
        _jobEffectivePriority.TryRemove(jobId, out _);
        _jobBasePriority.TryRemove(jobId, out _);
        _jobFocused.TryRemove(jobId, out _);
        _jobManualSortOrder.TryRemove(jobId, out _);
    }

    // ── Private priority helpers ──────────────────────────────────────────

    private int GetEffectiveJobPriority(Guid jobId)
    {
        if (_jobFocused.GetValueOrDefault(jobId))
            return 0; // Critical override
        return _jobEffectivePriority.GetValueOrDefault(jobId, (int)PlaylistPriority.Normal);
    }

    private void StampJobPriorityOnInMemoryTracks(Guid jobId, int effectivePriority)
    {
        lock (_collectionLock)
        {
            foreach (var ctx in _downloads.Where(d => d.Model.PlaylistId == jobId))
                ctx.Model.JobPriority = effectivePriority;
        }
    }

    private async Task LoadJobPriorityCacheAsync()
    {
        try
        {
            var data = await _databaseService.LoadJobPrioritiesAsync();
            foreach (var (id, info) in data)
            {
                _jobBasePriority[id] = info.JobPriority;
                _jobFocused[id]      = info.IsFocused;
                _jobManualSortOrder[id] = info.ManualSortOrder;
                // Effective = focus overrides to Critical, otherwise stored base
                _jobEffectivePriority[id] = info.IsFocused ? 0 : info.JobPriority;
            }
            _logger.LogDebug("Job priority cache loaded: {Count} jobs", data.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load job priority cache — defaulting all jobs to Normal");
        }
    }
}
