using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace SLSKDONET.Services.Input;

/// <summary>
/// Resolves key presses to <see cref="KeyboardAction"/>s using the active
/// <see cref="KeyboardProfile"/> (Epic #119, Task 4).
/// </summary>
public interface IKeyboardMappingService
{
    /// <summary>The currently loaded profile.</summary>
    KeyboardProfile ActiveProfile { get; }

    /// <summary>
    /// Resolve a key press to an action, or null if no binding matches.
    /// Deck-specific bindings take priority over global (Deck 0) bindings.
    /// </summary>
    KeyboardAction? Resolve(Key key, KeyModifiers modifiers, int deck = 0);

    /// <summary>Returns pairs of bindings that share the same Key+Modifiers+Deck but map to different actions.</summary>
    IEnumerable<(KeyboardBinding a, KeyboardBinding b)> GetConflicts();

    /// <summary>Replace the active profile and raise <see cref="ProfileChanged"/>.</summary>
    void LoadProfile(KeyboardProfile profile);

    /// <summary>Fired after the active profile changes (hot reload or manual load).</summary>
    event EventHandler? ProfileChanged;

    /// <summary>Fired every time an action is successfully resolved (Task 14: debug overlay).</summary>
    event EventHandler<KeyboardAction>? ActionTriggered;

    /// <summary>
    /// Resolves a key press to the first matching <see cref="KeyboardBinding"/>, supporting 4-deck routing.
    /// Priority: (1) focused-deck specific → (2) any other deck-specific → (3) global (Deck 0).
    /// Returns null when nothing matches. Fires <see cref="ActionTriggered"/> on match.
    /// </summary>
    KeyboardBinding? ResolveBinding(Key key, KeyModifiers modifiers, int focusedDeck = 0);
}
