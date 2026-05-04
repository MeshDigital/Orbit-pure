using System.Collections.Generic;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace SLSKDONET.Services.Input;

/// <summary>
/// A single key → action binding (Epic #119, Task 2).
/// </summary>
public record KeyboardBinding : IDJAction
{
    /// <summary>The primary key (Avalonia Key enum).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Key Key { get; init; }

    /// <summary>Required modifier mask (Shift / Ctrl / Alt / None).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyModifiers Modifiers { get; init; }

    /// <summary>0 = Global (focused deck), 1–4 = specific deck slot.</summary>
    public int Deck { get; init; }

    /// <summary>The action this binding triggers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyboardAction Action { get; init; }

    // IDJAction
    int IDJAction.DeckTarget => Deck;

    // ─── Matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the pressed <paramref name="key"/> + <paramref name="modifiers"/>
    /// matches this binding. Strips irrelevant flags (NumLock, Scroll) before comparing.
    /// </summary>
    public bool Matches(Key key, KeyModifiers modifiers)
    {
        const KeyModifiers relevant = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        return Key == key && Modifiers == (modifiers & relevant);
    }

    // ─── Validation ───────────────────────────────────────────────────────────

    /// <summary>Throws if the binding has invalid field values.</summary>
    public void Validate()
    {
        if (Key == Key.None)
            throw new System.ArgumentException("Key cannot be None.", nameof(Key));
        if (Deck is < 0 or > 4)
            throw new System.ArgumentOutOfRangeException(nameof(Deck), "Deck must be 0–4 (0 = Global).");
    }

    // ─── Display ──────────────────────────────────────────────────────────────

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & KeyModifiers.Shift)   != 0) parts.Add("Shift");
        if ((Modifiers & KeyModifiers.Alt)     != 0) parts.Add("Alt");
        if ((Modifiers & KeyModifiers.Meta)    != 0) parts.Add("Meta");
        parts.Add(Key.ToString());

        var combo    = string.Join("+", parts);
        var deckPart = Deck == 0 ? "Global" : $"Deck {Deck}";
        return $"{combo} [{deckPart}] → {Action}";
    }
}
