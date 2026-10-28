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

    public string CategoryLabel => Category switch
    {
        SecurityAuditCategory.Shield      => "Shield",
        SecurityAuditCategory.Gate        => "Gate",
        SecurityAuditCategory.ForensicLab => "Forensic Lab",
        SecurityAuditCategory.Blacklist   => "Blacklist",
        SecurityAuditCategory.Integrity   => "Integrity",
        _                                 => "Info",
    };

    // Severity drives semantic text classes in the view (TextBlock.warning / .error).
    public bool IsBlock => Severity == SecurityAuditSeverity.Block;
    public bool IsWarn  => Severity == SecurityAuditSeverity.Warn;

    public string SeverityLabel => Severity switch
    {
        SecurityAuditSeverity.Block => "BLOCK",
        SecurityAuditSeverity.Warn  => "WARN",
        _                           => "INFO",
    };
}
