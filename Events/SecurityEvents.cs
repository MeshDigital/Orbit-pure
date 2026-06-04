namespace SLSKDONET.Models;

// Security transparency events
/// <summary>
/// Represents a single security/guardrail decision made by the engine.
/// Category: The system that fired (Shield, Gate, ForensicLab, Blacklist).
/// Severity: Info | Warn | Block.
/// </summary>
public record SecurityAuditEvent(
    SecurityAuditCategory Category,
    SecurityAuditSeverity Severity,
    string Summary,
    string? Detail = null,
    string? AssociatedHash = null);

public enum SecurityAuditCategory
{
    Shield,      // ProtocolHardeningService - query sanitization / peer reputation
    Gate,        // SafetyFilterService - bitrate / size gate rejection
    ForensicLab, // Fake-FLAC / suspicious-lossless detection
    Blacklist,   // Peer blacklist hit
    Integrity,   // Post-download hash / file checks (future)
}

public enum SecurityAuditSeverity
{
    Info,
    Warn,
    Block,
}
