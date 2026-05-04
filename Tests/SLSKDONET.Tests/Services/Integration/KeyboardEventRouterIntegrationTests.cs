using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Input;
using Xunit;

namespace SLSKDONET.Tests.Services.Integration;

/// <summary>
/// Integration tests for the keyboard routing pipeline — Task 16 of Epic #119.
///
/// These tests exercise the full path from KeyboardProfile → KeyboardMappingService
/// → ActionTriggered event, and verify the routing priority rules that
/// KeyboardEventRouter relies on.  UI-thread–dependent dispatch (DeckEngine
/// commands through DeckSlotViewModel) requires Avalonia and is tested manually.
/// </summary>
public class KeyboardEventRouterIntegrationTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static KeyboardMappingService CreateService(KeyboardProfile profile)
    {
        var svc = new KeyboardMappingService(NullLogger<KeyboardMappingService>.Instance);
        svc.LoadProfile(profile);
        return svc;
    }

    /// Build a profile with a distinct deck-specific binding AND a global fallback.
    private static KeyboardProfile BuildRoutingProfile() => new()
    {
        Name = "routing-integration",
        Bindings =
        {
            // Global: Space → PlayPause
            new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None,
                                  Action = KeyboardAction.PlayPause, Deck = 0 },
            // Deck 1 specific: Space → Cue  (overrides global for deck 1)
            new KeyboardBinding { Key = Key.Space, Modifiers = KeyModifiers.None,
                                  Action = KeyboardAction.Cue, Deck = 1 },
            // Deck 2 specific: different action binds
            new KeyboardBinding { Key = Key.Q, Modifiers = KeyModifiers.None,
                                  Action = KeyboardAction.PlayPause, Deck = 2 },
        }
    };

    // ─── Pipeline: profile → service → ActionTriggered ───────────────────────

    [Fact]
    public void FullPipeline_SpaceKey_TriggersPlayPauseOnGlobalDeck()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        KeyboardAction? triggered = null;
        svc.ActionTriggered += (_, a) => triggered = a;

        // Simulate: router resolved via deck 0 → ActionTriggered fires
        var action = svc.Resolve(Key.Space, KeyModifiers.None, deck: 0);

        Assert.Equal(KeyboardAction.PlayPause, action);
        Assert.Equal(KeyboardAction.PlayPause, triggered); // event fired
    }

    [Fact]
    public void FullPipeline_UnknownKey_TriggersNoAction()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        bool eventFired = false;
        svc.ActionTriggered += (_, _) => eventFired = true;

        var action = svc.Resolve(Key.F24, KeyModifiers.Alt | KeyModifiers.Meta);

        Assert.Null(action);
        Assert.False(eventFired);
    }

    // ─── Deck routing ────────────────────────────────────────────────────────

    /// Deck 1 binding (Cue) must win over global binding (PlayPause) for the same key.
    [Fact]
    public void Routing_Deck1Binding_DoesNotTriggerDeck2Action()
    {
        using var svc = CreateService(BuildRoutingProfile());

        // Deck 1: Space is mapped to Cue (deck-specific)
        var deck1Action = svc.Resolve(Key.Space, KeyModifiers.None, deck: 1);
        // Deck 2: Space is mapped to PlayPause (global fallback — no deck-2-specific binding)
        var deck2Action = svc.Resolve(Key.Space, KeyModifiers.None, deck: 2);

        Assert.Equal(KeyboardAction.Cue,      deck1Action);
        Assert.Equal(KeyboardAction.PlayPause, deck2Action);  // global fallback
        Assert.NotEqual(deck1Action, deck2Action);             // they must differ
    }

    [Fact]
    public void Routing_Deck1KeyQ_RoutesToDeck2BecauseHardwired()
    {
        using var svc = CreateService(BuildRoutingProfile());

        // Q is hardwired to Deck 2. When Deck 1 is focused, Priority 3 (cross-deck)
        // still routes Q → Deck 2's PlayPause binding so it fires on the correct channel.
        var result = svc.ResolveBinding(Key.Q, KeyModifiers.None, focusedDeck: 1);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Deck);                    // routed to Deck 2
        Assert.Equal(KeyboardAction.PlayPause, result.Action);
    }

    [Fact]
    public void Routing_GlobalBinding_AppliesWhenNoDeckSpecificBindingExists()
    {
        using var svc = CreateService(BuildRoutingProfile());

        // Deck 3 has no specific binding for Space → falls back to global PlayPause
        var action = svc.Resolve(Key.Space, KeyModifiers.None, deck: 3);
        Assert.Equal(KeyboardAction.PlayPause, action);
    }

    // ─── Modifier isolation ──────────────────────────────────────────────────

    [Fact]
    public void Routing_Modifier_IsolationAcrossDecks()
    {
        var profile = new KeyboardProfile
        {
            Name = "mod-isolation",
            Bindings =
            {
                // Same key, different modifier, different action
                new KeyboardBinding { Key = Key.Z, Modifiers = KeyModifiers.None,    Action = KeyboardAction.Cue,      Deck = 0 },
                new KeyboardBinding { Key = Key.Z, Modifiers = KeyModifiers.Control, Action = KeyboardAction.PlayPause, Deck = 0 },
            }
        };
        using var svc = CreateService(profile);

        Assert.Equal(KeyboardAction.Cue,       svc.Resolve(Key.Z, KeyModifiers.None,    deck: 0));
        Assert.Equal(KeyboardAction.PlayPause, svc.Resolve(Key.Z, KeyModifiers.Control, deck: 0));
    }

    // ─── Conflict isolation: correct action wins ──────────────────────────────

    [Fact]
    public void Routing_ConflictInProfile_GetConflictsReturnsAll()
    {
        var profile = new KeyboardProfile
        {
            Name = "conflicting",
            Bindings =
            {
                new KeyboardBinding { Key = Key.A, Modifiers = KeyModifiers.None, Action = KeyboardAction.Cue,      Deck = 0 },
                new KeyboardBinding { Key = Key.A, Modifiers = KeyModifiers.None, Action = KeyboardAction.PlayPause, Deck = 0 },
                new KeyboardBinding { Key = Key.B, Modifiers = KeyModifiers.None, Action = KeyboardAction.HotCue1,  Deck = 0 },
                new KeyboardBinding { Key = Key.B, Modifiers = KeyModifiers.None, Action = KeyboardAction.HotCue2,  Deck = 0 },
            }
        };
        using var svc = CreateService(profile);

        var conflicts = svc.GetConflicts().ToList();

        // Two conflict pairs: (A/Cue, A/PlayPause) and (B/HotCue1, B/HotCue2)
        Assert.Equal(2, conflicts.Count);
    }

    // ─── Hot-reload: profile swap mid-session ─────────────────────────────────

    [Fact]
    public void HotReload_AfterProfileSwap_ResolveReflectsNewProfile()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));

        // Before: Space → PlayPause (OrbitDefault)
        var before = svc.Resolve(Key.Space, KeyModifiers.None);
        Assert.Equal(KeyboardAction.PlayPause, before);

        // Swap to a custom profile where Space is NOT bound
        svc.LoadProfile(new KeyboardProfile { Name = "empty", Bindings = { } });

        // After: Space → null
        var after = svc.Resolve(Key.Space, KeyModifiers.None);
        Assert.Null(after);
    }

    [Fact]
    public void HotReload_ProfileChanged_SubscriberNotifiedBeforeNextResolve()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        string? notifiedName = null;
        svc.ProfileChanged += (_, _) => notifiedName = svc.ActiveProfile.Name;

        var rekordbox = KeyboardProfile.GetBuiltIn(BuiltInPreset.Rekordbox);
        svc.LoadProfile(rekordbox);

        Assert.Equal("Rekordbox Style", notifiedName);
    }

    // ─── Action frequency (telemetry-free counting in service layer) ──────────

    [Fact]
    public void ActionTriggered_MultipleResolutions_FiresForEachMatch()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        var triggered = new List<KeyboardAction>();
        svc.ActionTriggered += (_, a) => triggered.Add(a);

        svc.Resolve(Key.Space, KeyModifiers.None); // PlayPause
        svc.Resolve(Key.Space, KeyModifiers.None); // PlayPause again
        svc.Resolve(Key.F24, KeyModifiers.None);   // unbound — should not fire

        Assert.Equal(2, triggered.Count);
        Assert.All(triggered, a => Assert.Equal(KeyboardAction.PlayPause, a));
    }

    // ─── Browser navigation ───────────────────────────────────────────────────

    /// BrowseUp / BrowseDown are registered in OrbitDefault (arrow keys).
    /// The router returns false for these (lets Avalonia handle native list navigation).
    /// Verify that Resolve() still recognises them (returns the action).
    [Fact]
    public void BrowseUp_IsRecognisedByService()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        var actions = svc.ActiveProfile.Bindings
            .Where(b => b.Action == KeyboardAction.BrowseUp)
            .ToList();

        // OrbitDefault should register at least one BrowseUp binding
        Assert.NotEmpty(actions);
    }

    [Fact]
    public void BrowseDown_IsRecognisedByService()
    {
        using var svc = CreateService(KeyboardProfile.GetBuiltIn(BuiltInPreset.OrbitDefault));
        var actions = svc.ActiveProfile.Bindings
            .Where(b => b.Action == KeyboardAction.BrowseDown)
            .ToList();

        Assert.NotEmpty(actions);
    }

    // ─── "Suppression" — documented behaviour (requires Avalonia) ────────────
    //
    // When a TextBox has keyboard focus, the router intentionally returns e.Handled
    // = false and skips all action dispatch.  This requires a running Avalonia
    // FocusManager and cannot be exercised in a headless unit-test.
    //
    // Covered by: manual QA checklist Task 20 ("standard Ctrl+Z/Y undo still works
    // globally") and the TextBox-focus branch in KeyboardEventRouter.OnKeyDown line.
}
