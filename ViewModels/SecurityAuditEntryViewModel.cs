using System;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Lightweight display model for a single Security Audit event shown in the
/// Settings → Security Transparency panel.
/// </summary>
public class SecurityAuditEntryViewModel
{
    public SecurityAuditEntryViewModel(SecurityAuditEvent e)
    {
        Timestamp  = DateTime.Now;
        Category   = e.Category;
        Severity   = e.Severity;
        Summary    = e.Summary;
        Detail     = e.Detail ?? string.Empty;
        Hash       = e.AssociatedHash ?? string.Empty;
    }

    public DateTime             Timestamp   { get; }
    public SecurityAuditCategory Category   { get; }
    public SecurityAuditSeverity Severity   { get; }
    public string               Summary     { get; }
    public string               Detail      { get; }
    public string               Hash        { get; }

    // ─── Display helpers ────────────────────────────────────
    public string TimeLabel    => Timestamp.ToString("HH:mm:ss");

    public string CategoryIcon => Category switch
    {
        SecurityAuditCategory.Shield      => "🛡",
        SecurityAuditCategory.Gate        => "🚧",
        SecurityAuditCategory.ForensicLab => "🔬",
        SecurityAuditCategory.Blacklist   => "🚫",
        SecurityAuditCategory.Integrity   => "🔐",
        _                                 => "ℹ",
    };

    public string SeverityColor => Severity switch
    {
        SecurityAuditSeverity.Block => "#F44336",  // red
        SecurityAuditSeverity.Warn  => "#FFA500",  // amber
        _                           => "#4EC9B0",  // teal-green (info)
    };

    public string SeverityLabel => Severity switch
    {
        SecurityAuditSeverity.Block => "BLOCK",
        SecurityAuditSeverity.Warn  => "WARN",
        _                           => "INFO",
    };
}
