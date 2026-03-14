namespace SLSKDONET.Models;

/// <summary>
/// Represents a single entry in the Forensic Intelligence panel.
/// Parsed from MentorReasoningBuilder output for visual display.
/// </summary>
public class ForensicVerdictEntry
{
    public string Content { get; set; } = string.Empty;
    public ForensicEntryType Type { get; set; }
    public bool IsHighlighted { get; set; }
    
    /// <summary>
    /// Creates a section header entry.
    /// </summary>
    public static ForensicVerdictEntry Section(string title) => new()
    {
        Content = title.ToUpperInvariant(),
        Type = ForensicEntryType.Section
    };
    
    /// <summary>
    /// Creates a standard bullet entry.
    /// </summary>
    public static ForensicVerdictEntry Bullet(string text) => new()
    {
        Content = text,
        Type = ForensicEntryType.Bullet
    };
    
    /// <summary>
    /// Creates a warning entry (displayed in orange).
    /// </summary>
    public static ForensicVerdictEntry Warning(string text) => new()
    {
        Content = text,
        Type = ForensicEntryType.Warning
    };
    
    /// <summary>
    /// Creates a success entry (displayed in green).
    /// </summary>
    public static ForensicVerdictEntry Success(string text) => new()
    {
        Content = text,
        Type = ForensicEntryType.Success
    };
    
    /// <summary>
    /// Creates a detail/sub-item entry.
    /// </summary>
    public static ForensicVerdictEntry Detail(string text) => new()
    {
        Content = text,
        Type = ForensicEntryType.Detail
    };
    
    /// <summary>
    /// Creates a final verdict entry (prominent display).
    /// </summary>
    public static ForensicVerdictEntry Verdict(string text) => new()
    {
        Content = text,
        Type = ForensicEntryType.Verdict
    };
}

/// <summary>
/// Type of forensic entry for visual styling.
/// </summary>
public enum ForensicEntryType
{
    /// <summary>Section header (▓ prefix)</summary>
    Section,
    /// <summary>Standard bullet point (• prefix)</summary>
    Bullet,
    /// <summary>Warning indicator (⚠ prefix, orange)</summary>
    Warning,
    /// <summary>Success indicator (✓ prefix, green)</summary>
    Success,
    /// <summary>Sub-detail (→ prefix, indented)</summary>
    Detail,
    /// <summary>Final verdict (prominent box)</summary>
    Verdict
}
