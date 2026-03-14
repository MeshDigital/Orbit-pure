using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.ViewModels;
using SLSKDONET.Data;

namespace SLSKDONET.Models;

public enum SystemHealth
{
    Excellent,
    Good,
    Warning,
    Critical
}

/// <summary>
/// Immutable snapshot of the system state for the Mission Control Dashboard.
/// Implements IEquatable for high-performance change detection in the polling engine.
/// </summary>
public class DashboardSnapshot : IEquatable<DashboardSnapshot>
{
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public SystemHealth SystemHealth { get; init; } = SystemHealth.Excellent;
    
    // Resilience Metrics
    public int ActiveDownloads { get; init; }
    public int DeadLetterCount { get; init; }
    public int RecoveredFileCount { get; init; }
    public int ZombieProcessCount { get; init; }
    public List<string> ResilienceLog { get; init; } = new();

    // Library & Engine Stats (Expanded Phase 7)
    public LibraryHealthEntity? LibraryHealth { get; init; }
    public long AvailableFreeSpaceBytes { get; init; }
    public bool IsSpotifyAuthenticated { get; init; }

    // Forensic Telemetry
    public bool IsForensicLockdownActive { get; init; }
    public double CurrentCpuLoad { get; init; }
    public Services.SystemInfoHelper.CpuTopology Topology { get; init; }

    // Active Operations
    public List<MissionOperation> ActiveOperations { get; init; } = new();

    public bool Equals(DashboardSnapshot? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        
        return GetHashCode() == other.GetHashCode();
    }

    public override bool Equals(object? obj) => Equals(obj as DashboardSnapshot);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SystemHealth);
        hash.Add(ActiveDownloads);
        hash.Add(DeadLetterCount);
        hash.Add(RecoveredFileCount);
        hash.Add(ZombieProcessCount);
        hash.Add(IsForensicLockdownActive);
        hash.Add(AvailableFreeSpaceBytes);
        hash.Add(IsSpotifyAuthenticated);
        
        // Add log hash (most recent entry)
        if (ResilienceLog.Count > 0)
            hash.Add(ResilienceLog[0]);
            
        // Add health hash
        if (LibraryHealth != null)
        {
            hash.Add(LibraryHealth.TotalTracks);
            hash.Add(LibraryHealth.GoldCount);
            hash.Add(LibraryHealth.SilverCount);
        }

        // Add operations count and first item hash
        hash.Add(ActiveOperations.Count);
        if (ActiveOperations.Count > 0)
        {
            hash.Add(ActiveOperations[0].Id);
            hash.Add(ActiveOperations[0].Progress);
            hash.Add(ActiveOperations[0].StatusText);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(DashboardSnapshot? left, DashboardSnapshot? right) => Equals(left, right);
    public static bool operator !=(DashboardSnapshot? left, DashboardSnapshot? right) => !Equals(left, right);
}
