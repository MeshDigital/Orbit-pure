namespace SLSKDONET.Models;

// Soulseek Adapter and connectivity events
public record SoulseekStateChangedEvent(
    string State,
    bool IsConnected,
    bool? IsConnecting = null,
    bool? IsLoggingIn = null,
    bool? IsLoggedIn = null,
    bool? IsDisconnecting = null,
    bool? IsDisconnected = null);
public record SoulseekConnectionStatusEvent(string Status, string Username, string? Reason = null);
// Phase B: Connection lifecycle state machine
public record ConnectionLifecycleStateChangedEvent(
    string Previous,
    string Current,
    string Reason,
    string? CorrelationId = null);
public record SharedFilesStatusEvent(int Count, string Directory);

// Beta 2026: Network Resilience Events
/// <summary>
/// Fired by the Parent Health Monitor when search fertility drops below threshold,
/// indicating the distributed parent connection may have degraded.
/// </summary>
public record NetworkHealthWarningEvent(double SearchFertilityRate, string Message);

// Phase 10: Connectivity & Background Events
public record GlobalStatusEvent(string Message, bool IsActive, bool IsError = false);
public record AdaptiveLaneStatusEvent(int CurrentLanes, int ActiveLanes, string Reason);
public record SearchPressureStatusEvent(string PressureLevel, int ResponseLimit, int FileLimit, int VariationCap, int AdditionalDelayMs);

// Phase 6: Share Health
/// <summary>
/// Emitted when the user's local share changes (folder count, file count, connection).
/// StatusBarViewModel subscribes to drive the Share Health LED.
/// </summary>
public record ShareHealthUpdatedEvent(
    int SharedFolderCount,
    int SharedFileCount,
    bool IsSharing,
    string? Note = null);
