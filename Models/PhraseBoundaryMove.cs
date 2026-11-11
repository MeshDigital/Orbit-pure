namespace SLSKDONET.Models;

/// <summary>
/// A phrase-strip drag in Cue Forge (#225): the boundary at the start of phrase <paramref name="PhraseIndex"/>
/// (≥ 1 — the boundary with the phrase before it) moved to <paramref name="Seconds"/>.
/// </summary>
public sealed record PhraseBoundaryMove(int PhraseIndex, double Seconds);
