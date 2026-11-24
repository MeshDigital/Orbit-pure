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

/// <summary>Download/search lane autotuning — extracted from DownloadManager (#307).</summary>
public partial class DownloadManager
{
    private async Task LaneAutotuneLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);

                if (!_config.EnableAdaptiveLanes)
                {
                    continue;
                }

                var snapshot = _peerReliability.GetGlobalSnapshot();
                if (snapshot.TotalStarts < 6)
                {
                    continue;
                }

                var targetDownloadLanes = MaxActiveDownloads;
                var targetSearchLanes = _currentSearchLanes;

                if (snapshot.CompletionRatio < 0.45 || snapshot.StallRatio > 0.45)
                {
                    targetSearchLanes = Math.Max(_config.MinAdaptiveSearchLanes, _currentSearchLanes - 1);
                }
                else if (snapshot.CompletionRatio > 0.80 && snapshot.StallRatio < 0.20)
                {
                    targetSearchLanes = Math.Min(_config.MaxAdaptiveSearchLanes, _currentSearchLanes + 1);
                }

                // STRICT CONCURRENCY: keep download worker slots pinned to user slider.
                _ = targetDownloadLanes;

                ResizeSearchLanes(targetSearchLanes);

                _logger.LogDebug(
                    "Adaptive lanes: completion={Completion:P0}, stalls={Stall:P0}, download={DownloadLanes}, search={SearchLanes}",
                    snapshot.CompletionRatio,
                    snapshot.StallRatio,
                    MaxActiveDownloads,
                    _currentSearchLanes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adaptive lane autotuner cycle failed");
            }
        }
    }

    private void ResizeSearchLanes(int target)
    {
        target = Math.Clamp(target, _config.MinAdaptiveSearchLanes, _config.MaxAdaptiveSearchLanes);
        if (target == _currentSearchLanes)
        {
            return;
        }

        var diff = target - _currentSearchLanes;
        _currentSearchLanes = target;

        if (diff > 0)
        {
            try
            {
                _searchSemaphore.Release(diff);
            }
            catch (SemaphoreFullException)
            {
                _logger.LogDebug("Search lane semaphore already at max while resizing to {Target}", target);
            }
            return;
        }

        var reduceBy = Math.Abs(diff);
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < reduceBy; i++)
                {
                    await _searchSemaphore.WaitAsync(_globalCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during search lane reduction");
            }
        }, _globalCts.Token);
    }
}
