using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SLSKDONET.Models;
using System;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class SonicProfileControl : UserControl
{
    public static readonly StyledProperty<SonicProfileData?> ProfileProperty =
        AvaloniaProperty.Register<SonicProfileControl, SonicProfileData?>(nameof(Profile));

    public SonicProfileData? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    // Display Properties (computed from Profile)
    public static readonly DirectProperty<SonicProfileControl, double> EnergyBarWidthProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, double>(nameof(EnergyBarWidth), o => o.EnergyBarWidth);

    private double _energyBarWidth;
    public double EnergyBarWidth
    {
        get => _energyBarWidth;
        private set => SetAndRaise(EnergyBarWidthProperty, ref _energyBarWidth, value);
    }
    
    public static readonly DirectProperty<SonicProfileControl, IBrush> EnergyColorProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, IBrush>(nameof(EnergyColor), o => o.EnergyColor);
        
    private IBrush _energyColor = Brushes.Gray;
    public IBrush EnergyColor
    {
        get => _energyColor;
        private set => SetAndRaise(EnergyColorProperty, ref _energyColor, value);
    }

    public static readonly DirectProperty<SonicProfileControl, string> EnergyDisplayProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(EnergyDisplay), o => o.EnergyDisplay);

    private string _energyDisplay = "—";
    public string EnergyDisplay
    {
        get => _energyDisplay;
        private set => SetAndRaise(EnergyDisplayProperty, ref _energyDisplay, value);
    }

    public static readonly DirectProperty<SonicProfileControl, double> ValencePositionProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, double>(nameof(ValencePosition), o => o.ValencePosition);

    private double _valencePosition;
    public double ValencePosition
    {
        get => _valencePosition;
        private set => SetAndRaise(ValencePositionProperty, ref _valencePosition, value);
    }
    
    public static readonly DirectProperty<SonicProfileControl, string> MoodDisplayProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(MoodDisplay), o => o.MoodDisplay);
        
    private string _moodDisplay = "—";
    public string MoodDisplay
    {
        get => _moodDisplay;
        private set => SetAndRaise(MoodDisplayProperty, ref _moodDisplay, value);
    }
    
    public static readonly DirectProperty<SonicProfileControl, string> VocalIconProperty =
        AvaloniaProperty.RegisterDirect<SonicProfileControl, string>(nameof(VocalIcon), o => o.VocalIcon);

    private string _vocalIcon = "";
    public string VocalIcon
    {
        get => _vocalIcon;
        private set => SetAndRaise(VocalIconProperty, ref _vocalIcon, value);
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

    public SonicProfileControl()
    {
        InitializeComponent();
        ProfileProperty.Changed.AddClassHandler<SonicProfileControl>(OnProfileChanged);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnProfileChanged(SonicProfileControl control, AvaloniaPropertyChangedEventArgs args)
    {
        control.UpdateVisuals();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (Profile == null)
        {
            EnergyBarWidth = 0;
            ValencePosition = 0;
            EnergyDisplay = "Unknown";
            MoodDisplay = "Unknown";
            HasVocalData = false;
            return;
        }

        // Energy
        double totalWidth = Bounds.Width > 0 ? Bounds.Width : 200; // Fallback
        EnergyBarWidth = Math.Clamp(Profile.Energy, 0, 1) * totalWidth;
        
        if (Profile.Energy > 0.8) EnergyColor = Brushes.OrangeRed;
        else if (Profile.Energy > 0.5) EnergyColor = Brushes.LimeGreen;
        else EnergyColor = Brushes.DeepSkyBlue;
        
        EnergyDisplay = $"{Profile.Energy:P0}";

        // Valence (Mood)
        // Position on slider (padding for icon)
        double sliderWidth = totalWidth - 30; // approx
        if (sliderWidth < 0) sliderWidth = 0;
        ValencePosition = Math.Clamp(Profile.Valence, 0, 1) * sliderWidth;
        
        if (Profile.Valence > 0.7) MoodDisplay = "Happy";
        else if (Profile.Valence < 0.3) MoodDisplay = "Sad";
        else MoodDisplay = "Neutral";

        // Vocals
        HasVocalData = Profile.Instrumentalness > 0 || Profile.IsInstrumental;
        if (Profile.IsInstrumental)
        {
            VocalIcon = "🚫🎤"; 
            VocalText = "Instrumental";
        }
        else
        {
            VocalIcon = "🎤";
            VocalText = "Vocal";
        }
    }
}
