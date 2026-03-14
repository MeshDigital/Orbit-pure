using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Data;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services
{
    public class MissionControlService : IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly DownloadManager _downloadManager;
        private readonly CrashRecoveryJournal _crashJournal;
        private readonly SearchOrchestrationService _searchOrchestrator;
        private readonly ILogger<MissionControlService> _logger;
        
        private readonly CancellationTokenSource _cts = new();
        private readonly IForensicLockdownService _lockdown;
        private readonly ConfigManager _configManager;
        private readonly SpotifyAuthService _spotifyAuth;
        private readonly DashboardService _dashboardService;
        private Task? _monitorTask;

        // Caching for expensive stats
        private SystemHealthStats _cachedHealth;
        private int _cachedZombieCount;
        private int _tickCounter = 0;

        public MissionControlService(
            IEventBus eventBus, 
            DownloadManager downloadManager,
            CrashRecoveryJournal crashJournal,
            SearchOrchestrationService searchOrchestrator,
            IForensicLockdownService lockdown,
            ConfigManager configManager,
            SpotifyAuthService spotifyAuth,
            DashboardService dashboardService,
            ILogger<MissionControlService> logger)
        {
            _eventBus = eventBus;
            _downloadManager = downloadManager;
            _crashJournal = crashJournal;
            _searchOrchestrator = searchOrchestrator;
            _lockdown = lockdown;
            _configManager = configManager;
            _spotifyAuth = spotifyAuth;
            _dashboardService = dashboardService;
            _logger = logger;
        }

        public void Start()
        {
            _monitorTask = Task.Run(ProcessThrottledUpdatesAsync);
            _logger.LogInformation("Mission Control Service started");
        }

        private async Task ProcessThrottledUpdatesAsync()
        {
            // Heartbeat: 500ms (Real-time tier)
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            
            _logger.LogInformation("Mission Control Heartbeat started (500ms)");

            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    _tickCounter++;
                    
                    // Tiered Updates:
                    // 1. Real-time (Every tick - 500ms): Downloads, Active Operations, CPU
                    // 2. System (Every 10 ticks - 5s): Storage, Zombie Processes, Spotify Auth
                    // 3. Library (Every 600 ticks - 5min): Full Library Health Audit

                    bool isSystemTick = _tickCounter % 10 == 0;
                    bool isLibraryTick = _tickCounter % 600 == 0;

                    if (isSystemTick || _tickCounter == 1)
                    {
                        await UpdateSystemStatsAsync();
                    }

                    if (isLibraryTick || _tickCounter == 1)
                    {
                        await UpdateLibraryStatsAsync();
                    }

                    var snapshot = await GetCurrentStateAsync();
                    
                    // The "GetHashCode" Trick: Only publish if state actually changed
                    if (snapshot != _lastSnapshot)
                    {
                        _lastSnapshot = snapshot;
                        _eventBus.Publish(snapshot);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Mission Control heartbeat loop");
                }
            }
        }

        private DashboardSnapshot? _lastSnapshot;

        private async Task UpdateSystemStatsAsync()
        {
            try
            {
                _cachedHealth = await _crashJournal.GetSystemHealthAsync();
                _cachedZombieCount = GetZombieProcessCount();
                
                // Get Storage Info
                var storage = _dashboardService.GetStorageInsight();
                _cachedFreeSpace = storage.FreeBytes;
                
                _logger.LogDebug("Mission Control: System stats updated");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update system stats");
            }
        }

        private async Task UpdateLibraryStatsAsync()
        {
            try
            {
                _logger.LogInformation("Mission Control: Performing scheduled library health audit...");
                await _dashboardService.RecalculateLibraryHealthAsync();
                _cachedLibraryHealth = await _dashboardService.GetLibraryHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update library stats");
            }
        }

        private LibraryHealthEntity? _cachedLibraryHealth;
        private long _cachedFreeSpace;

        public async Task<DashboardSnapshot> GetCurrentStateAsync()
        {
            // Real-time Cheap Stats
            var activeDownloads = _downloadManager.ActiveDownloads.ToList(); 
            var downloadCount = activeDownloads.Count;
            
            // Calculate overall health (Real-time logic)
            var health = SystemHealth.Excellent;
            if ((_cachedHealth.DeadLetterCount) > 0 || _cachedZombieCount > 2)
            {
                health = SystemHealth.Warning;
            }
            if (activeDownloads.Any(d => d.State == PlaylistTrackState.Failed))
            {
                health = SystemHealth.Warning;
            }

            // Build Active Operations List
            var operations = new List<MissionOperation>();
            foreach (var dl in activeDownloads.Take(5)) 
            {
                operations.Add(new MissionOperation 
                {
                    Id = dl.GlobalId,
                    Type = SLSKDONET.Models.OperationType.Download,
                    Title = $"{dl.Model.Artist} - {dl.Model.Title}",
                    Subtitle = dl.State.ToString(),
                    Progress = dl.Progress / 100.0,
                    StatusText = $"{dl.Progress:F0}%",
                    CanCancel = dl.IsActive
                });
            }

            if (_searchOrchestrator.GetActiveSearchCount() > 0)
            {
                operations.Add(new MissionOperation
                {
                    Type = SLSKDONET.Models.OperationType.Search,
                    Title = "P2P Radar",
                    Subtitle = $"{_searchOrchestrator.GetActiveSearchCount()} active queries",
                    Progress = 0.5,
                    StatusText = "Broadcasting..."
                });
            }
            
            // Add Analysis Operations removed

            // Resilience Log
            var resilienceLog = new List<string>();
            if ((_cachedHealth.RecoveredCount) > 0)
            {
                resilienceLog.Add($"✅ Recovered {_cachedHealth.RecoveredCount} files");
            }
            if (_cachedZombieCount > 0)
            {
                resilienceLog.Add($"🧟 {_cachedZombieCount} zombie processes detected");
            }

            return new DashboardSnapshot
            {
                CapturedAt = DateTime.UtcNow,
                SystemHealth = health,
                ActiveDownloads = downloadCount,
                DeadLetterCount = _cachedHealth.DeadLetterCount,
                RecoveredFileCount = _cachedHealth.RecoveredCount,
                ZombieProcessCount = _cachedZombieCount,
                ActiveOperations = operations,
                ResilienceLog = resilienceLog,
                IsForensicLockdownActive = _lockdown.IsLockdownActive,
                CurrentCpuLoad = _lockdown.CurrentCpuLoad,
                Topology = SystemInfoHelper.Topology,
                LibraryHealth = _cachedLibraryHealth,
                AvailableFreeSpaceBytes = _cachedFreeSpace,
                IsSpotifyAuthenticated = _spotifyAuth.IsAuthenticated
            };
        }

        private int GetZombieProcessCount()
        {
            try
            {
                var ffmpegs = Process.GetProcessesByName("ffmpeg");
                var activeConversions = _downloadManager.ActiveDownloads.Count(d => d.State == PlaylistTrackState.Downloading);
                
                if (ffmpegs.Length > activeConversions)
                {
                    return ffmpegs.Length - activeConversions;
                }
                return 0;
            }
            catch 
            {
                return 0;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
