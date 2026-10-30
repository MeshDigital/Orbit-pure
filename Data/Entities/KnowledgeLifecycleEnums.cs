namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the knowledge-graph transition vocabulary. Broader than the DSP
/// <c>SLSKDONET.Models.Timeline.TransitionType</c> (which only covers the executable crossfade/EQ/
/// echo primitives) — this classifies the <em>intent</em> of a transition.
/// </summary>
public enum KnowledgeTransitionType
{
    Blend = 0,
    Cut = 1,
    EchoOut = 2,
    Backspin = 3,
    DropSwap = 4,
    DoubleDrop = 5,
    Mashup = 6,
    AcapellaOverlay = 7,
    StemMix = 8,
    GenreJump = 9,
    EnergyReset = 10,
}

/// <summary>Confidence tier derived from the volume and type of evidence (see §2.1).</summary>
public enum KnowledgeConfidenceLevel
{
    Unknown = 0,
    Experimental = 1,
    Likely = 2,
    Confirmed = 3,
}

/// <summary>Where a piece of transition evidence came from (source credibility in §6.1).</summary>
public enum TransitionEvidenceSource
{
    PhraseMatch = 0,       // algorithmic: phrase/energy alignment
    HarmonyDetection = 1,  // algorithmic: key/camelot distance
    BpmCompatibility = 2,  // algorithmic: tempo fit
    ManualRating = 3,      // explicit DJ rating in Transition Lab
    LivePerformance = 4,   // real-world: actually played live
    CrowdReaction = 5,     // real-world: audience-validated
    MashupSuccess = 6,     // real-world: acapella/double-drop that landed
}

/// <summary>Structural role of a slot in a SetProject (role belongs to the slot, not the track).</summary>
public enum SetSlotRole
{
    Opening = 0,
    Builder = 1,
    Atmosphere = 2,
    Peak = 3,
    Reset = 4,
    Closer = 5,
    Wildcard = 6,
}

/// <summary>How a planned slot turned out in the live <c>PerformanceSession</c>.</summary>
public enum PerformanceOutcome
{
    Planned = 0,    // played roughly as planned
    Audible = 1,    // improvised deviation from the plan
    Skipped = 2,    // planned but not played
    Replaced = 3,   // planned slot filled by a different track
}
