using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services.Input;

/// <summary>
/// Opt-in local keyboard telemetry backed by LiteDB.
/// Subscribes to <see cref="IKeyboardMappingService.ActionTriggered"/> and counts
/// how often each action is triggered.  No data is ever sent externally.
/// (Epic #119, Task 19)
/// </summary>
public sealed class KeyboardTelemetryService : IKeyboardTelemetryService, IDisposable
{
    private readonly AppConfig                          _config;
    private readonly ILogger<KeyboardTelemetryService> _logger;
    private readonly LiteDatabase                      _db;
    private readonly ILiteCollection<ActionRecord>     _actions;
    private readonly ILiteCollection<PresetRecord>     _presets;
    private          bool                              _disposed;

    public KeyboardTelemetryService(
        AppConfig                          config,
        IKeyboardMappingService            mapping,
        ILogger<KeyboardTelemetryService>  logger)
    {
        _config = config;
        _logger = logger;

        var dbDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT-Pure");
        Directory.CreateDirectory(dbDir);
        _db = new LiteDatabase(Path.Combine(dbDir, "keyboard-telemetry.db"));

        _actions = _db.GetCollection<ActionRecord>("actions");
        _actions.EnsureIndex(x => x.ActionName);

        _presets = _db.GetCollection<PresetRecord>("presets");
        _presets.EnsureIndex(x => x.PresetName);

        // Wire up to the mapping service event
        mapping.ActionTriggered += OnActionTriggered;
    }

    // ─── IKeyboardTelemetryService ────────────────────────────────────────────

    public void RecordAction(KeyboardAction action)
    {
        if (!_config.EnableKeyboardTelemetry) return;
        UpsertAction(action.ToString());
    }

    public void RecordPresetLoad(string presetName)
    {
        if (!_config.EnableKeyboardTelemetry) return;
        UpsertPreset(presetName);
    }

    public IReadOnlyList<(string ActionName, int Count)> GetTopActions(int n = 5)
    {
        return _actions.FindAll()
            .OrderByDescending(x => x.Count)
            .Take(n)
            .Select(x => (x.ActionName, x.Count))
            .ToList();
    }

    public IReadOnlyList<(string PresetName, int Count)> GetTopPresets(int n = 3)
    {
        return _presets.FindAll()
            .OrderByDescending(x => x.Count)
            .Take(n)
            .Select(x => (x.PresetName, x.Count))
            .ToList();
    }

    // ─── Internals ────────────────────────────────────────────────────────────

    private void OnActionTriggered(object? sender, KeyboardAction action)
    {
        try { RecordAction(action); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Telemetry] Failed to record action {Action}", action);
        }
    }

    private void UpsertAction(string name)
    {
        var r = _actions.FindOne(x => x.ActionName == name);
        if (r == null)
            _actions.Insert(new ActionRecord { ActionName = name, Count = 1, LastUsed = DateTime.UtcNow });
        else { r.Count++; r.LastUsed = DateTime.UtcNow; _actions.Update(r); }
    }

    private void UpsertPreset(string name)
    {
        var r = _presets.FindOne(x => x.PresetName == name);
        if (r == null)
            _presets.Insert(new PresetRecord { PresetName = name, Count = 1, LastUsed = DateTime.UtcNow });
        else { r.Count++; r.LastUsed = DateTime.UtcNow; _presets.Update(r); }
    }

    // ─── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }

    // ─── LiteDB document models ───────────────────────────────────────────────

    private sealed class ActionRecord
    {
        public int      Id         { get; set; }
        public string   ActionName { get; set; } = string.Empty;
        public int      Count      { get; set; }
        public DateTime LastUsed   { get; set; }
    }

    private sealed class PresetRecord
    {
        public int      Id         { get; set; }
        public string   PresetName { get; set; } = string.Empty;
        public int      Count      { get; set; }
        public DateTime LastUsed   { get; set; }
    }
}
