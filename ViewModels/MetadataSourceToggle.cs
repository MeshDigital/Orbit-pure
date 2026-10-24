using SLSKDONET.Services.Enrichment;

namespace SLSKDONET.ViewModels;

/// <summary>What a metadata source's switch shows, given the user's choice and what the source has.</summary>
/// <param name="SwitchOn">The switch reads ON only when the source will actually be asked.</param>
/// <param name="CanToggle">False for always-on sources and for sources still waiting for a key/tool/connection.</param>
/// <param name="IsWaiting">The user wants it (or has not decided) but it cannot run yet.</param>
/// <param name="WaitingText">"Waiting for a token" etc. while <paramref name="IsWaiting"/>.</param>
public readonly record struct MetadataSourceToggleState(bool SwitchOn, bool CanToggle, bool IsWaiting, string? WaitingText);

/// <summary>
/// One rule for the setup wizard's Metadata step and Settings → Metadata sources. Discogs and
/// AcoustID used to show their switch ON next to "Needs a token", which read as if they worked.
/// Now a source that cannot run yet shows its switch OFF and disabled with "Waiting for …" in a
/// warning style next to the key field; the user's choice is kept underneath, so the switch comes
/// on by itself once the key is there (or Spotify is connected).
/// </summary>
public static class MetadataSourceToggle
{
    public static bool IsWaiting(ProviderAvailability availability) =>
        availability is ProviderAvailability.NeedsKey or ProviderAvailability.NeedsTool or ProviderAvailability.NotConnected;

    public static MetadataSourceToggleState Compute(string providerName, bool userWantsIt, bool isAlwaysOn, ProviderAvailability availability)
    {
        if (isAlwaysOn) return new(true, false, false, null);
        if (!IsWaiting(availability)) return new(userWantsIt, true, false, null);
        return new(false, false, true, WaitingText(providerName, availability));
    }

    public static string WaitingText(string providerName, ProviderAvailability availability) => availability switch
    {
        ProviderAvailability.NeedsKey => providerName == "acoustid" ? "Waiting for an API key" : "Waiting for a token",
        ProviderAvailability.NeedsTool => "Waiting for fpcalc (Chromaprint)",
        ProviderAvailability.NotConnected => "Waiting for Spotify · connect it first",
        _ => string.Empty,
    };
}
