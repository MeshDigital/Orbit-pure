using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

/// <summary>
/// "The Immune System": Manages the blacklist of unwanted files (e.g., bad rips, fake upscales).
/// Provides fast, cached lookups to block these files from search results and imports.
/// </summary>
public class ForensicLockdownService : IForensicLockdownService
{
    private readonly ILogger<ForensicLockdownService> _logger;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAudioPlayerService _audioPlayer;
    
    // In-memory cache for ultra-fast lookups during high-volume search results
    // Key: Hash, Value: dummy byte
    private readonly ConcurrentDictionary<string, byte> _blacklistedHashes = new();

    private bool _isPerformanceLockdownActive = false;
    private double _currentCpuLoad = 0;
    private const double CPU_THRESHOLD = 0.85; // 85% load triggers safe mode
    private readonly System.Diagnostics.Stopwatch _cpuStopwatch = new();
    private TimeSpan _lastCpuTime = TimeSpan.Zero;

    public bool IsLockdownActive => _isPerformanceLockdownActive || (_audioPlayer?.IsPlaying ?? false) || _currentCpuLoad > CPU_THRESHOLD;
    public double CurrentCpuLoad => _currentCpuLoad;

    public ForensicLockdownService(
        ILogger<ForensicLockdownService> logger,
        IDbContextFactory<AppDbContext> contextFactory,
        IAudioPlayerService audioPlayer)
    {
        _logger = logger;
        _contextFactory = contextFactory;
        _audioPlayer = audioPlayer;
        
        _cpuStopwatch.Start();
        _lastCpuTime = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

        // Hydrate cache on startup (fire and forget)
        Task.Run(HydrateCacheAsync);
    }
    
    public void SetPerformanceLockdown(bool active)
    {
        _isPerformanceLockdownActive = active;
        _logger.LogWarning("🛡️ Forensic Lockdown: {State}", active ? "FORCED ACTIVE" : "FORCED OFF");
    }

    public async Task MonitorSystemHealthAsync()
    {
        _logger.LogInformation("🛡️ CPU Watchdog: Active monitoring started");

        while (true)
        {
            try
            {
                await Task.Delay(2000); // Sample every 2 seconds

                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var currentProcessorTime = currentProcess.TotalProcessorTime;
                var elapsedMs = _cpuStopwatch.ElapsedMilliseconds;
                
                // Calculate CPU % since last sample
                // Note: This is simplified and doesn't account for core count perfectly 
                // but gives a good relative indicator for the app's own impact.
                double cpuUsedMs = (currentProcessorTime - _lastCpuTime).TotalMilliseconds;
                double totalMs = elapsedMs; // Total wall clock time since start (needs reset to be accurate per sample)
                
                // Better sampling:
                _cpuStopwatch.Restart();
                _currentCpuLoad = Math.Clamp(cpuUsedMs / (Environment.ProcessorCount * 2000.0), 0, 1.0);
                _lastCpuTime = currentProcessorTime;

                if (_currentCpuLoad > CPU_THRESHOLD && !_isPerformanceLockdownActive)
                {
                    _logger.LogCritical("🔥 High CPU detected ({Load:P0})! Auto-triggering Performance Lockdown.", _currentCpuLoad);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CPU Watchdog error");
                await Task.Delay(5000);
            }
        }
    }

    private async Task HydrateCacheAsync()
    {
        try
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var hashes = await context.Blacklist
                .Select(b => b.Hash)
                .ToListAsync();
            
            foreach (var hash in hashes)
            {
                _blacklistedHashes.TryAdd(hash, 0);
            }
            
            _logger.LogInformation("Forensic Lockdown: Hydrated {Count} blacklisted hashes", hashes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate blacklist cache");
        }
    }

    /// <summary>
    /// Checks if a file hash is blacklisted.
    /// Thread-safe and extremely fast (memory lookup).
    /// </summary>
    public bool IsBlacklisted(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return _blacklistedHashes.ContainsKey(hash);
    }

    /// <summary>
    /// Adds a hash to the blacklist.
    /// </summary>
    public async Task BlacklistAsync(string hash, string reason, string? originalTitle = null)
    {
        if (string.IsNullOrEmpty(hash)) return;

        if (_blacklistedHashes.ContainsKey(hash)) return; // Already blocked

        try
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            
            // Check usage in DB to prevent duplicates if cache was cold
            var exists = await context.Blacklist.AnyAsync(b => b.Hash == hash);
            if (!exists)
            {
                var entity = new BlacklistedItemEntity
                {
                    Hash = hash,
                    Reason = reason,
                    OriginalTitle = originalTitle,
                    BlockedAt = DateTime.UtcNow
                };
                
                context.Blacklist.Add(entity);
                await context.SaveChangesAsync();
            }
            
            _blacklistedHashes.TryAdd(hash, 0);
            
            _logger.LogInformation("Blacklisted {Hash} ({Reason})", hash, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to blacklist hash {Hash}", hash);
            throw;
        }
    }

    /// <summary>
    /// Removes a hash from the blacklist (Undo).
    /// </summary>
    public async Task UnblacklistAsync(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;

        try
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            
            var entity = await context.Blacklist.FirstOrDefaultAsync(b => b.Hash == hash);
            if (entity != null)
            {
                context.Blacklist.Remove(entity);
                await context.SaveChangesAsync();
            }
            
            _blacklistedHashes.TryRemove(hash, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unblacklist hash {Hash}", hash);
        }
    }
}
