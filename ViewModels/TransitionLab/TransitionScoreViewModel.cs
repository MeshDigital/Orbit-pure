using ReactiveUI;

namespace SLSKDONET.ViewModels.TransitionLab;

/// <summary>
/// Slice K6: surfaces the transition's theoretical scores, the DJ's interactive rating, and the
/// verified toggle. Scores are populated by the Transition Lab from the track pair.
/// </summary>
public sealed class TransitionScoreViewModel : ReactiveObject
{
    private double _harmonicScore;
    public double HarmonicScore { get => _harmonicScore; set => this.RaiseAndSetIfChanged(ref _harmonicScore, value); }

    private double _bpmScore;
    public double BpmScore { get => _bpmScore; set => this.RaiseAndSetIfChanged(ref _bpmScore, value); }

    private double _energyScore;
    public double EnergyScore { get => _energyScore; set => this.RaiseAndSetIfChanged(ref _energyScore, value); }

    private double _vocalScore;
    public double VocalScore { get => _vocalScore; set => this.RaiseAndSetIfChanged(ref _vocalScore, value); }

    private double _crowdFitScore;
    public double CrowdFitScore { get => _crowdFitScore; set => this.RaiseAndSetIfChanged(ref _crowdFitScore, value); }

    private double? _userRating;
    public double? UserRating { get => _userRating; set => this.RaiseAndSetIfChanged(ref _userRating, value); }

    private bool _verified;
    public bool Verified { get => _verified; set => this.RaiseAndSetIfChanged(ref _verified, value); }
}
