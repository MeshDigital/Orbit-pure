using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for ForensicLockdownService to support DI and testing.
/// Manages both file blacklisting (The Immune System) and performance safety (The CPU Watchdog).
/// </summary>
public interface IForensicLockdownService
{
    // --- File Lockdown (The Immune System) ---
    bool IsBlacklisted(string? hash);
    Task BlacklistAsync(string hash, string reason, string? originalTitle = null);
    Task UnblacklistAsync(string hash);

    // --- Performance Lockdown (The CPU Watchdog) ---
    /// <summary>
    /// Gets whether a performance lockdown is currently active (e.g., during live DJ playback).
    /// </summary>
    bool IsLockdownActive { get; }

    /// <summary>
    /// Forcefully enters or exits a performance lockdown mode.
    /// </summary>
    void SetPerformanceLockdown(bool active);

    /// <summary>
    /// Gets the current system CPU load percentage (0.0 - 1.0).
    /// </summary>
    double CurrentCpuLoad { get; }

    /// <summary>
    /// Automatically monitors system state (CPU, Audio Buffer) to trigger lockdown.
    /// </summary>
    Task MonitorSystemHealthAsync();
}
