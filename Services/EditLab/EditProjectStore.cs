using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models.EditLab;

namespace SLSKDONET.Services.EditLab;

/// <summary>One row of the "recent projects" list.</summary>
public sealed record EditProjectSummary(Guid Id, string Name, DateTime ModifiedUtc, string? BaseTitle, int ClipCount);

/// <summary>
/// Saves Edit Lab projects as JSON files next to the library database
/// (<c>&lt;library dir&gt;/EditLab/projects/&lt;id&gt;.json</c>), so tests that point
/// <c>ORBIT_DB_PATH</c> at a temp database get a temp project folder too.
/// The constructor never throws (it is a DI singleton); I/O errors surface on save/load.
/// </summary>
public sealed class EditProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<EditProjectStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ProjectsDirectory { get; }

    public EditProjectStore(ILogger<EditProjectStore>? logger = null, string? rootDirectory = null)
    {
        _logger = logger ?? NullLogger<EditProjectStore>.Instance;
        ProjectsDirectory = Path.Combine(rootDirectory ?? DefaultRoot(), "projects");
    }

    /// <summary><c>&lt;directory of library.db&gt;/EditLab</c>.</summary>
    public static string DefaultRoot()
    {
        var dbDir = Path.GetDirectoryName(OrbitPaths.LibraryDbPath);
        return Path.Combine(string.IsNullOrEmpty(dbDir) ? AppContext.BaseDirectory : dbDir, "EditLab");
    }

    public string PathFor(Guid id) => Path.Combine(ProjectsDirectory, id.ToString("N") + ".json");

    public static string Serialize(EditProject project) => JsonSerializer.Serialize(project, JsonOptions);

    public static EditProject Deserialize(string json) =>
        JsonSerializer.Deserialize<EditProject>(json, JsonOptions)
        ?? throw new InvalidDataException("The project file is empty.");

    /// <summary>Writes the project atomically (temp file + replace) and stamps <see cref="EditProject.ModifiedUtc"/>.</summary>
    public async Task SaveAsync(EditProject project, CancellationToken ct = default)
    {
        project.ModifiedUtc = DateTime.UtcNow;
        var json = Serialize(project);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ProjectsDirectory);
            var path = PathFor(project.Id);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditProject?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Deserialize(json);
    }

    /// <summary>Most recently modified first. Unreadable files are logged and skipped.</summary>
    public async Task<List<EditProjectSummary>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        var result = new List<EditProjectSummary>();
        if (!Directory.Exists(ProjectsDirectory)) return result;

        foreach (var file in new DirectoryInfo(ProjectsDirectory).GetFiles("*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Max(1, limit) * 2))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var project = Deserialize(await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false));
                result.Add(new EditProjectSummary(project.Id, project.Name, project.ModifiedUtc,
                    project.BaseSource?.Title, project.AllClips.Count()));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Edit Lab: skipping unreadable project file {File}", file.Name);
            }
        }

        return result.OrderByDescending(r => r.ModifiedUtc).Take(limit).ToList();
    }

    public Task DeleteAsync(Guid id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
