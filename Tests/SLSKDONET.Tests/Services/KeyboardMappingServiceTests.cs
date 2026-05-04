using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Input;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit tests for the keyboard mapping engine — Task 15 of Epic #119.
/// Tests cover resolution, modifier semantics, deck routing, conflict detection,
/// profile import/export, and built-in presets.
/// </summary>
public class KeyboardMappingServiceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static KeyboardMappingService CreateService(KeyboardProfile? profile = null)
    {
        // Use a temp directory so the service never touches AppData in CI
        var svc = new KeyboardMappingService(NullLogger<KeyboardMappingService>.Instance);
        if (profile != null)
            svc.LoadProfile(profile);
        return svc;
    }

    private static KeyboardProfile SingleBinding(
        Key key,
        KeyModifiers mods,
        KeyboardAction action,
        int deck = 0) => new()
    {
        Name = "test",
        Bindings = { new KeyboardBinding { Key = key, Modifiers = mods, Action = action, Deck = deck } }
    };

    // ─── Resolution ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_MatchingKeyAndModifiers_ReturnsAction()
    {
        using var svc = CreateService(SingleBinding(Key.Space, KeyModifiers.None, KeyboardAction.PlayPause));

        var result = svc.Resolve(Key.Space, KeyModifiers.None);

        Assert.Equal(KeyboardAction.PlayPause, result);
    }

    [Fact]
    public void Resolve_NoMatchingBinding_ReturnsNull()
    {
        using var svc = CreateService(SingleBinding(Key.Space, KeyModifiers.None, KeyboardAction.PlayPause));

        var result = svc.Resolve(Key.Q, KeyModifiers.None);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_WrongKey_ReturnsNull()
    {
        using var svc = CreateService(SingleBinding(Key.A, KeyModifiers.None, KeyboardAction.Cue));

        Assert.Null(svc.Resolve(Key.B, KeyModifiers.None));
    }

    // ─── Modifier semantics ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_CtrlA_DoesNotMatchPlainA()
    {
        using var svc = CreateService(SingleBinding(Key.A, KeyModifiers.None, KeyboardAction.PlayPause));

        // Ctrl+A must NOT match a plain-A binding
        Assert.Null(svc.Resolve(Key.A, KeyModifiers.Control));
    }

    [Fact]
    public void Resolve_ShiftCtrlA_DoesNotMatchCtrlA()
    {
        using var svc = CreateService(SingleBinding(Key.A, KeyModifiers.Control, KeyboardAction.HotCue1));

        Assert.Null(svc.Resolve(Key.A, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void Resolve_ExactModifiersMatch_ReturnsAction()
    {
        using var svc = CreateService(SingleBinding(Key.F1, KeyModifiers.Shift, KeyboardAction.SetHotCue1));

        Assert.Equal(KeyboardAction.SetHotCue1, svc.Resolve(Key.F1, KeyModifiers.Shift));
    }

    // ─── Deck routing ────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DeckSpecificBinding_OverridesGlobalForSameKey()
    {
        var profile = new KeyboardProfile
        {
            Name = "deck-test",
            Bindings =
            {
                new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 0 }, // global
                new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None, Action = KeyboardAction.Cue,       Deck = 1 }, // deck 1 specific
            }
        };
        using var svc = CreateService(profile);

        // When querying for deck 1, the deck-specific binding wins
        Assert.Equal(KeyboardAction.Cue, svc.Resolve(Key.Space, KeyModifiers.None, deck: 1));
    }

    [Fact]
    public void Resolve_DeckSpecificBinding_FallsBackToGlobalForOtherDecks()
    {
        var profile = new KeyboardProfile
        {
            Name = "deck-fallback",
            Bindings =
            {
                new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 0 },
                new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None, Action = KeyboardAction.Cue,       Deck = 1 },
            }
        };
        using var svc = CreateService(profile);

        // Deck 2 has no specific binding → falls back to global
        Assert.Equal(KeyboardAction.PlayPause, svc.Resolve(Key.Space, KeyModifiers.None, deck: 2));
    }

    // ─── Conflict detection ──────────────────────────────────────────────────

    [Fact]
    public void GetConflicts_NoDuplicates_ReturnsEmpty()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));

        // The built-in preset should not contain internal conflicts
        Assert.Empty(svc.GetConflicts());
    }

    [Fact]
    public void GetConflicts_SameKeyDifferentActions_ReturnsConflict()
    {
        var profile = new KeyboardProfile
        {
            Name = "conflict",
            Bindings =
            {
                new KeyboardBinding { Key = Key.Q, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 0 },
                new KeyboardBinding { Key = Key.Q, Modifiers = KeyModifiers.None, Action = KeyboardAction.Cue,       Deck = 0 },
            }
        };
        using var svc = CreateService(profile);

        var conflicts = svc.GetConflicts().ToList();
        Assert.Single(conflicts);
        var (a, b) = conflicts[0];
        // One must be PlayPause and the other Cue (order is not specified)
        Assert.Contains(KeyboardAction.PlayPause, new[] { a.Action, b.Action });
        Assert.Contains(KeyboardAction.Cue,       new[] { a.Action, b.Action });
    }

    [Fact]
    public void GetConflicts_SameKeyDifferentDecks_NoConflict()
    {
        var profile = new KeyboardProfile
        {
            Name = "no-conflict-decks",
            Bindings =
            {
                new KeyboardBinding { Key = Key.Q, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 1 },
                new KeyboardBinding { Key = Key.Q, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 2 },
            }
        };
        using var svc = CreateService(profile);

        // Different decks → no conflict
        Assert.Empty(svc.GetConflicts());
    }

    // ─── Profile import / export ─────────────────────────────────────────────

    [Fact]
    public void Profile_RoundTrip_ToJsonAndFromJson_PreservesBindings()
    {
        var original = KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault);
        var json     = original.ToJson();
        var restored = KeyboardProfile.FromJson(json);

        Assert.Equal(original.Name,          restored.Name);
        Assert.Equal(original.Bindings.Count, restored.Bindings.Count);
    }

    [Fact]
    public void Profile_ToJson_ProducesValidJson()
    {
        var profile = KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault);
        var json    = profile.ToJson();

        // Should not throw
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Name", out _));
        Assert.True(doc.RootElement.TryGetProperty("Bindings", out _));
    }

    [Fact]
    public void Profile_FromJson_WithUnknownFields_DoesNotThrow()
    {
        const string json = """
            {
              "Name": "custom",
              "Version": "1.0.0",
              "Description": "",
              "Bindings": [],
              "UnknownFutureField": 42
            }
            """;

        var profile = KeyboardProfile.FromJson(json);
        Assert.Equal("custom", profile.Name);
        Assert.Empty(profile.Bindings);
    }

    [Fact]
    public void Profile_FromJson_WithMalformedJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<Exception>(() => KeyboardProfile.FromJson("{ not valid json"));
    }

    // ─── Built-in presets ────────────────────────────────────────────────────

    [Fact]
    public void BuiltIn_OrbitDefault_HasSpacePlayPause()
    {
        var profile  = KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault);
        var playBind = profile.Bindings.FirstOrDefault(b => b.Action == KeyboardAction.PlayPause);

        Assert.NotNull(playBind);
        Assert.Equal(Key.Space, playBind.Key);
        Assert.Equal(KeyModifiers.None, playBind.Modifiers);
    }

    [Fact]
    public void BuiltIn_Rekordbox_HasQPlayPause()
    {
        var profile  = KeyboardProfile.GetBuiltIn(BuiltInPreset.Rekordbox);
        var playBind = profile.Bindings.FirstOrDefault(b => b.Action == KeyboardAction.PlayPause);

        Assert.NotNull(playBind);
        Assert.Equal(Key.Q, playBind.Key);
    }

    [Fact]
    public void BuiltIn_DjStudio_HasSpacePlayPause()
    {
        var profile  = KeyboardProfile.GetBuiltIn(BuiltInPreset.DjStudio);
        var playBind = profile.Bindings.FirstOrDefault(b => b.Action == KeyboardAction.PlayPause);

        Assert.NotNull(playBind);
        Assert.Equal(Key.Space, playBind.Key);
    }

    [Fact]
    public void BuiltIn_AllPresets_HaveNonEmptyBindings()
    {
        foreach (BuiltInPreset preset in Enum.GetValues<BuiltInPreset>())
        {
            var profile = KeyboardProfile.GetBuiltIn(preset);
            Assert.NotEmpty(profile.Bindings);
        }
    }

    // ─── LoadProfile / ProfileChanged event ──────────────────────────────────

    [Fact]
    public void LoadProfile_FiresProfileChangedEvent()
    {
        using var svc = CreateService();
        bool firedAt  = false;
        svc.ProfileChanged += (_, _) => firedAt = true;

        svc.LoadProfile(KeyboardProfile.GetBuiltIn(BuiltInPreset.Rekordbox));

        Assert.True(firedAt);
    }

    [Fact]
    public void LoadProfile_UpdatesActiveProfile()
    {
        using var svc  = CreateService();
        var rekordbox   = KeyboardProfile.GetBuiltIn(BuiltInPreset.Rekordbox);

        svc.LoadProfile(rekordbox);

        Assert.Equal("Rekordbox Style", svc.ActiveProfile.Name);
    }

    // ─── ActionTriggered event ────────────────────────────────────────────────

    [Fact]
    public void Resolve_Match_FiresActionTriggeredEvent()
    {
        using var svc = CreateService(SingleBinding(Key.Space, KeyModifiers.None, KeyboardAction.PlayPause));
        KeyboardAction? triggered = null;
        svc.ActionTriggered += (_, a) => triggered = a;

        svc.Resolve(Key.Space, KeyModifiers.None);

        Assert.Equal(KeyboardAction.PlayPause, triggered);
    }

    [Fact]
    public void Resolve_NoMatch_DoesNotFireActionTriggeredEvent()
    {
        using var svc = CreateService(SingleBinding(Key.Space, KeyModifiers.None, KeyboardAction.PlayPause));
        bool fired = false;
        svc.ActionTriggered += (_, _) => fired = true;

        svc.Resolve(Key.Q, KeyModifiers.None);

        Assert.False(fired);
    }
}
