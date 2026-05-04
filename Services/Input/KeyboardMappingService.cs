using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Input;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Input;

/// <summary>
/// Singleton that owns the active <see cref="KeyboardProfile"/> and resolves key presses.
/// Supports hot-reload via <see cref="FileSystemWatcher"/> (Epic #119, Tasks 4 + 13).
/// </summary>
public sealed class KeyboardMappingService : IKeyboardMappingService, IDisposable
{
    private readonly ILogger<KeyboardMappingService> _logger;
    private readonly string _profilePath;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;
    private KeyboardProfile _activeProfile;
    private bool _disposed;

    // ─── Events ───────────────────────────────────────────────────────────────

    public event EventHandler? ProfileChanged;
    public event EventHandler<KeyboardAction>? ActionTriggered;

    // ─── Construction ─────────────────────────────────────────────────────────

    public KeyboardMappingService(ILogger<KeyboardMappingService> logger)
    {
        _logger = logger;

        _profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT-Pure",
            "keyboard-profile.json");

        _activeProfile = TryLoadProfile();
        SetupHotReload();
    }

    // ─── IKeyboardMappingService ──────────────────────────────────────────────

    public KeyboardProfile ActiveProfile => _activeProfile;

    /// <inheritdoc/>
    public KeyboardAction? Resolve(Key key, KeyModifiers modifiers, int deck = 0)
    {
        var b = ResolveBinding(key, modifiers, deck);
        return b?.Action;
    }

    /// <inheritdoc/>
    public KeyboardBinding? ResolveBinding(Key key, KeyModifiers modifiers, int focusedDeck = 0)
    {
        // Priority 1: focused-deck specific binding
        foreach (var b in _activeProfile.Bindings)
            if (b.Deck == focusedDeck && b.Matches(key, modifiers))
            {
                _logger.LogDebug("[Keyboard] Resolved {Key}+{Mods} (deck={Deck}) → {Action}",
                    key, modifiers, focusedDeck, b.Action);
                ActionTriggered?.Invoke(this, b.Action);
                return b;
            }

        // Priority 2: global binding (Deck 0 → applies to focused deck).
        //             Global beats cross-deck-specific so that profiles that mix
        //             deck-specific overrides with global fallbacks behave correctly.
        foreach (var b in _activeProfile.Bindings)
            if (b.Deck == 0 && b.Matches(key, modifiers))
            {
                _logger.LogDebug("[Keyboard] Resolved {Key}+{Mods} (global) → {Action}",
                    key, modifiers, b.Action);
                ActionTriggered?.Invoke(this, b.Action);
                return b;
            }

        // Priority 3: cross-deck hardwired binding (supports 4-deck layouts where
        //             keys are hardwired to specific channels and have NO global
        //             equivalent — pressing CH1's key always routes to Deck 1).
        if (focusedDeck != 0)
            foreach (var b in _activeProfile.Bindings)
                if (b.Deck != 0 && b.Deck != focusedDeck && b.Matches(key, modifiers))
                {
                    _logger.LogDebug("[Keyboard] Resolved {Key}+{Mods} (hardwired deck={Deck}) → {Action}",
                        key, modifiers, b.Deck, b.Action);
                    ActionTriggered?.Invoke(this, b.Action);
                    return b;
                }

        return null;
    }

    /// <inheritdoc/>
    public IEnumerable<(KeyboardBinding a, KeyboardBinding b)> GetConflicts()
    {
        var list = _activeProfile.Bindings;
        for (int i = 0; i < list.Count; i++)
        for (int j = i + 1; j < list.Count; j++)
        {
            var a = list[i];
            var b = list[j];
            if (a.Deck == b.Deck
                && a.Matches(b.Key, b.Modifiers)
                && a.Action != b.Action)
            {
                yield return (a, b);
            }
        }
    }

    /// <inheritdoc/>
    public void LoadProfile(KeyboardProfile profile)
    {
        _activeProfile = profile;
        _logger.LogInformation("[Keyboard] Profile loaded: '{Name}'", profile.Name);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── Profile loading ──────────────────────────────────────────────────────

    private KeyboardProfile TryLoadProfile()
    {
        if (File.Exists(_profilePath))
        {
            try
            {
                var p = KeyboardProfile.LoadFromFile(_profilePath);
                _logger.LogInformation("[Keyboard] Loaded profile '{Name}' from {Path}", p.Name, _profilePath);
                return p;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Keyboard] Failed to load profile from {Path}, using Orbit Default", _profilePath);
            }
        }
        else
        {
            _logger.LogInformation("[Keyboard] No profile found at {Path}, using Orbit Default", _profilePath);
        }

        var defaultProfile = KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault);
        try { defaultProfile.SaveToFile(_profilePath); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Keyboard] Could not persist default profile"); }
        return defaultProfile;
    }

    // ─── Hot reload (Task 13) ─────────────────────────────────────────────────

    private void SetupHotReload()
    {
        var dir  = Path.GetDirectoryName(_profilePath);
        var file = Path.GetFileName(_profilePath);
        if (dir == null || !Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnProfileFileChanged;
    }

    private void OnProfileFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce editors that write the file in multiple flushes
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(_ =>
        {
            try
            {
                var p = KeyboardProfile.LoadFromFile(_profilePath);
                _activeProfile = p;
                _logger.LogInformation("[Keyboard] Hot-reloaded profile '{Name}'", p.Name);
                ProfileChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Keyboard] Hot-reload failed; keeping previous profile");
            }
        }, null, dueTime: 300, period: Timeout.Infinite);
    }

    // ─── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();
    }
}
