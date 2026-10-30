using System;
using ReactiveUI;

namespace SLSKDONET.ViewModels.TransitionLab;

/// <summary>
/// Slice K6: the Transition Lab surface — a template editor, a score/rating panel, and the outgoing
/// / incoming track pair. Audition / save commands are added in later slices once the preview player
/// and persistence are wired.
/// </summary>
public sealed class TransitionLabViewModel : ReactiveObject
{
    public TransitionTemplateViewModel Template { get; } = new();
    public TransitionScoreViewModel Score { get; } = new();

    private string _sourceTrackHash = string.Empty;
    public string SourceTrackHash { get => _sourceTrackHash; set => this.RaiseAndSetIfChanged(ref _sourceTrackHash, value); }

    private string _targetTrackHash = string.Empty;
    public string TargetTrackHash { get => _targetTrackHash; set => this.RaiseAndSetIfChanged(ref _targetTrackHash, value); }

    public void SetTrackPair(string sourceTrackHash, string targetTrackHash)
    {
        SourceTrackHash = sourceTrackHash;
        TargetTrackHash = targetTrackHash;
    }
}
