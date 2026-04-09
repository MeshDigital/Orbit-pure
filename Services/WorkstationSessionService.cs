using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services;

/// <summary>
/// Persists and restores the Workstation session state (loaded tracks, timeline
/// position, active mode) so the user can resume where they left off after an
/// unexpected app close or normal exit.
///
/// Session file: %APPDATA%\Antigravity\workstation-session.json
/// </summary>
public class WorkstationSessionService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly string _sessionFilePath;

    public WorkstationSessionService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Antigravity");
        Directory.CreateDirectory(dir);
        _sessionFilePath = Path.Combine(dir, "workstation-session.json");
    }

    /// <summary>
    /// Atomically writes the session to disk using a temp-file swap so a crash
    /// during the write can never corrupt the previous good snapshot.
    /// </summary>
    public async Task SaveAsync(WorkstationSession session)
    {
        session.LastSaved = DateTime.UtcNow;
        var tmp = _sessionFilePath + ".tmp";
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, _sessionFilePath, overwrite: true);
    }

    /// <summary>
    /// Returns the last saved session, or <c>null</c> if none exists or the
    /// file is corrupt.
    /// </summary>
    public async Task<WorkstationSession?> LoadAsync()
    {
        if (!File.Exists(_sessionFilePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_sessionFilePath);
            return JsonSerializer.Deserialize<WorkstationSession>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the session file (e.g. after the user deliberately clears the session).</summary>
    public void Delete()
    {
        if (File.Exists(_sessionFilePath))
            File.Delete(_sessionFilePath);
    }

    public bool HasSession => File.Exists(_sessionFilePath);
}
