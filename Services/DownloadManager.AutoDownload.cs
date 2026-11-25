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

/// <summary>Auto-download / auto-upgrade event handlers — extracted from DownloadManager (#307).</summary>
public partial class DownloadManager
{
    private void OnAutoDownloadTrack(AutoDownloadTrackEvent e)
    {
        _logger.LogInformation("Auto-Download triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        if (!string.IsNullOrWhiteSpace(e.CorrelationId))
        {
            ctx.CorrelationId = e.CorrelationId!;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                ctx.Model.Priority = 0;
                await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0);
                await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reprioritize download-queued track {TrackId}", ctx.Model.Id);
            }
        });
    }

    private void OnAutoDownloadUpgrade(AutoDownloadUpgradeEvent e)
    {
        _logger.LogInformation("Auto-Upgrade triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        if (!string.IsNullOrWhiteSpace(e.CorrelationId))
        {
            ctx.CorrelationId = e.CorrelationId!;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
                {
                    DeleteLocalFiles(ctx.Model.ResolvedFilePath);
                }

                ctx.Model.Bitrate = null;
                ctx.Model.SpectralHash = null;
                ctx.Model.IsTrustworthy = null;

                ctx.Model.Priority = 0;
                await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0);

                await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process auto-upgrade for track {TrackId}", ctx.Model.Id);
            }
        });
    }

    private void OnUpgradeAvailable(UpgradeAvailableEvent e)
    {
        // For now just log, could trigger a notification in future
        _logger.LogInformation("Upgrade Available (Manual Approval Needed): {TrackId} - {BestMatch}",
            e.TrackGlobalId, e.BestMatch.Filename);
    }
}
