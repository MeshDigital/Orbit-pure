using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.Models;
using System;
using SLSKDONET.Utils;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Energy / mood / vocal summary for a <see cref="SonicProfileData"/>. Analysis output, so it is
/// shown as neutral bars and percentages (no colour-coded "vibe" fills).
/// </summary>
public partial class SonicProfileControl : UserControl
{
    public static readonly StyledProperty<SonicProfileData?> ProfileProperty =
        AvaloniaProperty.Register<SonicProfileControl, SonicProfileData?>(nameof(Profile));

    public SonicProfileData? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    // Display properties (computed from Profile)

    public static readonly DirectProperty<SonicProfileControl, double> EnergyValueProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, double>(nameof(EnergyValue), o => o.EnergyValue);

    private double _energyValue;
    /// <summary>Energy, 0..1.</summary>
    public double EnergyValue
    {
        get => _energyValue;
        private set => SetAndRaise(EnergyValueProperty, ref _energyValue, value);
    }

    public static readonly DirectProperty<SonicProfileControl, string> EnergyDisplayProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(EnergyDisplay), o => o.EnergyDisplay);

    private string _energyDisplay = "—";
    public string EnergyDisplay
    {
        get => _energyDisplay;
        private set => SetAndRaise(EnergyDisplayProperty, ref _energyDisplay, value);
    }

    public static readonly DirectProperty<SonicProfileControl, double> ValenceValueProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, double>(nameof(ValenceValue), o => o.ValenceValue);

    private double _valenceValue;
    /// <summary>Valence (melancholic 0 .. euphoric 1).</summary>
    public double ValenceValue
    {
        get => _valenceValue;
        private set => SetAndRaise(ValenceValueProperty, ref _valenceValue, value);
    }

    public static readonly DirectProperty<SonicProfileControl, string> MoodDisplayProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(MoodDisplay), o => o.MoodDisplay);

    private string _moodDisplay = "—";
    public string MoodDisplay
    {
        get => _moodDisplay;
        private set => SetAndRaise(MoodDisplayProperty, ref _moodDisplay, value);
    }

    public static readonly DirectProperty<SonicProfileControl, string> VocalTextProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(VocalText), o => o.VocalText);

    private string _vocalText = "";
    public string VocalText
    {
        get => _vocalText;
        private set => SetAndRaise(VocalTextProperty, ref _vocalText, value);
    }

    public static readonly DirectProperty<SonicProfileControl, bool> HasVocalDataProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, bool>(nameof(HasVocalData), o => o.HasVocalData);

    private bool _hasVocalData;
    public bool HasVocalData
    {
        get => _hasVocalData;
        private set => SetAndRaise(HasVocalDataProperty, ref _hasVocalData, value);
    }

    static SonicProfileControl()
    {
        // Registered once for the type (it used to be re-added in every instance constructor).
        ProfileProperty.Changed.AddClassHandler<SonicProfileControl>((control, _) => control.UpdateVisuals());
    }

    public SonicProfileControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdateVisuals()
    {
        if (Profile == null)
        {
            EnergyValue = 0;
            ValenceValue = 0;
            EnergyDisplay = "—";
            MoodDisplay = "Unknown";
            HasVocalData = false;
            return;
        }

        EnergyValue = Math.Clamp(Profile.Energy, 0, 1);
        EnergyDisplay = NumFormat.Ui($"{EnergyValue:P0}");

        ValenceValue = Math.Clamp(Profile.Valence, 0, 1);
        if (Profile.Valence > 0.7) MoodDisplay = "Happy";
        else if (Profile.Valence < 0.3) MoodDisplay = "Sad";
        else MoodDisplay = "Neutral";

        HasVocalData = Profile.Instrumentalness > 0 || Profile.IsInstrumental;
        VocalText = Profile.IsInstrumental ? "Instrumental" : "Vocal";
    }
}
