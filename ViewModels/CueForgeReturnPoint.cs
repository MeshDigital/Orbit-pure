using System;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Where a Cue Forge visit came from, so the page can offer "← Back to …" instead of a dead end.
/// Set by whoever opens a track in Cue Forge (Flow Builder's transition editor, the Library's
/// "Open in Cue Forge", the Mix sidepanel, Now Playing) right before navigating there.
/// </summary>
/// <param name="Label">Short button text, e.g. "Flow Builder · DnB 25 · transition 2 of 11".</param>
/// <param name="Return">Navigates back and restores the origin (playlist, transition, …).</param>
public sealed record CueForgeReturnPoint(string Label, Action Return);
