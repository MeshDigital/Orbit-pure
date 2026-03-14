using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SLSKDONET.Models.Stem;

public class StemEditProject
{
    public Guid ProjectId { get; set; } = Guid.NewGuid();
    public string OriginalTrackId { get; set; } = string.Empty;
    public string Name { get; set; } = "New Project";
    
    /// <summary>
    /// The current state of the mixer for this project.
    /// </summary>
    public Dictionary<StemType, StemSettings> CurrentSettings { get; set; } = new();

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public void UpdateSetting(StemType type, StemSettings setting)
    {
        CurrentSettings[type] = setting;
        Modified = DateTime.UtcNow;
    }

    public static async Task SaveAsync(StemEditProject project, string directoryPath)
    {
        var filePath = Path.Combine(directoryPath, $"{project.ProjectId}.orbitstem");
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }

    public static async Task<StemEditProject?> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<StemEditProject>(json);
    }
}
