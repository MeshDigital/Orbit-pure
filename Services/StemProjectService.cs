using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services;

public class StemProjectService
{
    private readonly string _projectsDirectory;

    public StemProjectService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _projectsDirectory = Path.Combine(appData, "Antigravity", "Projects");
        Directory.CreateDirectory(_projectsDirectory);
    }

    public async Task SaveProjectAsync(StemEditProject project)
    {
        var filePath = Path.Combine(_projectsDirectory, $"{project.ProjectId}.json");
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<StemEditProject?> LoadProjectAsync(Guid projectId)
    {
        var filePath = Path.Combine(_projectsDirectory, $"{projectId}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<StemEditProject>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StemProjectService] Error loading project {projectId}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<StemEditProject>> GetProjectsForTrackAsync(string trackId)
    {
        var results = new List<StemEditProject>();
        
        // Naive implementation: read all JSONs and filter. 
        // Optimization: Store secondary index or organize folders by trackId.
        // For now, folder by trackId is cleaner.
        
        // Use directory search if possible, else scan all.
        // Let's implement flat "scan all" for simplicity, or change storage strategy.
        // IMPROVEMENT: Scan all .json files.
        
        var files = Directory.GetFiles(_projectsDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var project = JsonSerializer.Deserialize<StemEditProject>(json);
                if (project != null && project.OriginalTrackId == trackId)
                {
                    results.Add(project);
                }
            }
            catch { /* Ignore corrupt files */ }
        }
        
        return results.OrderByDescending(p => p.Modified).ToList();
    }
}
