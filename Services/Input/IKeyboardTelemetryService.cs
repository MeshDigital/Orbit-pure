using System.Collections.Generic;

namespace SLSKDONET.Services.Input;

/// <summary>
/// Tracks local keyboard action usage for the opt-in usage-statistics feature
/// (Epic #119, Task 19).  All data stays on-device.
/// </summary>
public interface IKeyboardTelemetryService
{
    /// <summary>Record a single action trigger (no-op when telemetry is disabled).</summary>
    void RecordAction(KeyboardAction action);

    /// <summary>Record that a named built-in preset was loaded.</summary>
    void RecordPresetLoad(string presetName);

    /// <summary>Return the top-<paramref name="n"/> most triggered actions and their counts.</summary>
    IReadOnlyList<(string ActionName, int Count)> GetTopActions(int n = 5);

    /// <summary>Return the most-loaded built-in presets and their load counts.</summary>
    IReadOnlyList<(string PresetName, int Count)> GetTopPresets(int n = 3);
}
