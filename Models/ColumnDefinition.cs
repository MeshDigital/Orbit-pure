using System;

namespace SLSKDONET.Models;

public class ColumnDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Header { get; set; } = string.Empty;
    public double? Width { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsVisible { get; set; } = true;
    public string? PropertyPath { get; set; }
    public bool CanSort { get; set; } = true;
    public string? CellTemplateKey { get; set; } // For "Vibe" coloring or Star Ratings
    public Type? DataType { get; set; } // Helps with numeric sorting (BPM vs Artist)
    
    // Phase 25: Helper for SharedSizeGroup alignment
    public string SharedSizeGroup => $"Col_{Id}";
}
