using ReactiveUI;
using SLSKDONET.Engine.Cueing;

namespace SLSKDONET.ViewModels;

/// <summary>Checkbox row for the Cue Lab profile comparison picker.</summary>
public sealed class CueGenerationProfileOption : ReactiveObject
{
    public CueGenerationProfileOption(CueGenerationProfile profile, bool isSelected)
    {
        Profile = profile;
        _isSelected = isSelected;
    }

    public CueGenerationProfile Profile { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
