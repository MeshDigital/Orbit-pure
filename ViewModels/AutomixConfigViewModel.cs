using System;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Playlist;

namespace SLSKDONET.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Preview item for first-5-transitions live preview panel
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Displayable representation of a single automix transition for the preview grid.</summary>
public sealed class TransitionPreviewItem
{
    public string TrackA           { get; init; } = "";
    public string TrackB           { get; init; } = "";
    public string CamelotA         { get; init; } = "";
    public string CamelotB         { get; init; } = "";
    public double BpmA             { get; init; }
    public double BpmB             { get; init; }
    public string CompatibilityLabel { get; init; } = "–";
    /// <summary>Hex color reflecting transition quality: green = Perfect, yellow = Compatible, red = Risky.</summary>
    public string CompatibilityColor { get; init; } = "#AAAAAA";
}

// ─────────────────────────────────────────────────────────────────────────────
// AutomixConfigViewModel — Task 3.4 / #77
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// ViewModel backing the Automix Configuration dialog (<see cref="Views.Avalonia.Dialogs.AutomixConfigDialog"/>).
///
/// Exposes:
/// - BPM range sliders (MinBpm / MaxBpm)
/// - Key strictness toggle (MatchKey)
/// - Max energy jump slider (MaxEnergyJump, 1–9 on the 1-10 scale)
/// - Energy profile selector (EnergyCurve: None / Rising / Wave / Peak)
/// - Harmonic / Tempo / Energy optimizer weight sliders
/// - Live preview of up to 5 transitions (rebuilt on any constraint change)
/// - Save / Cancel / Reset-to-defaults commands
///
/// Call <see cref="LoadFrom(AppConfig)"/> to hydrate from persisted settings
/// and <see cref="SaveTo(AppConfig)"/> to flush confirmed values back.
/// </summary>
public sealed class AutomixConfigViewModel : ReactiveObject
{
    // ── BPM Range ─────────────────────────────────────────────────────────

    private double _minBpm = 100;
    public double MinBpm
    {
        get => _minBpm;
        set
        {
            value = Math.Clamp(value, 40, MaxBpm - 1);
            this.RaiseAndSetIfChanged(ref _minBpm, value);
            RebuildPreview();
        }
    }

    private double _maxBpm = 160;
    public double MaxBpm
    {
        get => _maxBpm;
        set
        {
            value = Math.Clamp(value, MinBpm + 1, 250);
            this.RaiseAndSetIfChanged(ref _maxBpm, value);
            RebuildPreview();
        }
    }

    // ── Key Strictness ────────────────────────────────────────────────────

    private bool _matchKey = true;
    /// <summary>When true, only compatible Camelot key pairs are allowed.</summary>
    public bool MatchKey
    {
        get => _matchKey;
        set
        {
            this.RaiseAndSetIfChanged(ref _matchKey, value);
            RebuildPreview();
        }
    }

    // ── Energy Variance ───────────────────────────────────────────────────

    private int _maxEnergyJump = 3;
    /// <summary>Maximum energy jump (1–9) between consecutive tracks.</summary>
    public int MaxEnergyJump
    {
        get => _maxEnergyJump;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxEnergyJump, Math.Clamp(value, 1, 9));
            RebuildPreview();
        }
    }

    private int _maxTracks = 20;
    public int MaxTracks
    {
        get => _maxTracks;
        set => this.RaiseAndSetIfChanged(ref _maxTracks, Math.Clamp(value, 2, 200));
    }

    // ── Energy Profile ────────────────────────────────────────────────────

    private string _energyCurve = "Wave";
    /// <summary>"None" | "Rising" | "Wave" | "Peak"</summary>
    public string EnergyCurve
    {
        get => _energyCurve;
        set
        {
            this.RaiseAndSetIfChanged(ref _energyCurve, value);
            RebuildPreview();
        }
    }

    // ── Optimizer Weights ─────────────────────────────────────────────────

    private double _harmonicWeight = 3.0;
    public double HarmonicWeight
    {
        get => _harmonicWeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _harmonicWeight, Math.Clamp(value, 0.1, 10.0));
            RebuildPreview();
        }
    }

    private double _tempoWeight = 1.0;
    public double TempoWeight
    {
        get => _tempoWeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _tempoWeight, Math.Clamp(value, 0.1, 10.0));
            RebuildPreview();
        }
    }

    private double _energyWeight = 0.5;
    public double EnergyWeight
    {
        get => _energyWeight;
        set
        {
            this.RaiseAndSetIfChanged(ref _energyWeight, Math.Clamp(value, 0.0, 10.0));
            RebuildPreview();
        }
    }

    // ── Live Preview ──────────────────────────────────────────────────────

    /// <summary>
    /// Up to 5 example transitions shown live as the user adjusts sliders.
    /// Populated from <see cref="RebuildPreview"/>.
    /// </summary>
    public ObservableCollection<TransitionPreviewItem> PreviewTransitions { get; } = new();

    // ── Dialog state ──────────────────────────────────────────────────────

    private bool _wasSaved;
    /// <summary>True after <see cref="SaveCommand"/> was executed; the dialog host should close on true.</summary>
    public bool WasSaved => _wasSaved;

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand          { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand        { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public AutomixConfigViewModel()
    {
        SaveCommand = ReactiveCommand.Create(() =>
        {
            _wasSaved = true;
            this.RaisePropertyChanged(nameof(WasSaved));
        });

        CancelCommand = ReactiveCommand.Create(() =>
        {
            _wasSaved = false;
        });

        ResetToDefaultsCommand = ReactiveCommand.Create(ResetToDefaults);

        RebuildPreview();
    }

    // ── AppConfig round-trip ──────────────────────────────────────────────

    /// <summary>Hydrate slider values from persisted <see cref="AppConfig"/> settings.</summary>
    public void LoadFrom(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _minBpm        = config.AutomixMinBpm;
        _maxBpm        = config.AutomixMaxBpm;
        _matchKey      = config.AutomixMatchKey;
        _maxEnergyJump = config.AutomixMaxEnergyJump;
        _maxTracks     = config.AutomixMaxTracks;
        _energyCurve   = config.AutomixEnergyCurve;
        _harmonicWeight = config.AutomixHarmonicWeight;
        _tempoWeight    = config.AutomixTempoWeight;
        _energyWeight   = config.AutomixEnergyWeight;

        this.RaisePropertyChanged(nameof(MinBpm));
        this.RaisePropertyChanged(nameof(MaxBpm));
        this.RaisePropertyChanged(nameof(MatchKey));
        this.RaisePropertyChanged(nameof(MaxEnergyJump));
        this.RaisePropertyChanged(nameof(MaxTracks));
        this.RaisePropertyChanged(nameof(EnergyCurve));
        this.RaisePropertyChanged(nameof(HarmonicWeight));
        this.RaisePropertyChanged(nameof(TempoWeight));
        this.RaisePropertyChanged(nameof(EnergyWeight));

        RebuildPreview();
    }

    /// <summary>Flush confirmed values back into <paramref name="config"/>.</summary>
    public void SaveTo(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.AutomixMinBpm         = MinBpm;
        config.AutomixMaxBpm         = MaxBpm;
        config.AutomixMatchKey       = MatchKey;
        config.AutomixMaxEnergyJump  = MaxEnergyJump;
        config.AutomixMaxTracks      = MaxTracks;
        config.AutomixEnergyCurve    = EnergyCurve;
        config.AutomixHarmonicWeight = HarmonicWeight;
        config.AutomixTempoWeight    = TempoWeight;
        config.AutomixEnergyWeight   = EnergyWeight;
    }

    /// <summary>Build an <see cref="AutomixConstraints"/> instance from current settings.</summary>
    public AutomixConstraints ToConstraints() => new()
    {
        MinBpm        = MinBpm,
        MaxBpm        = MaxBpm,
        MaxTracks     = MaxTracks,
        MatchKey      = MatchKey,
        MaxEnergyJump = MaxEnergyJump,
        EnergyCurve   = EnergyCurve,
        HarmonicWeight = HarmonicWeight,
        TempoWeight    = TempoWeight,
        EnergyWeight   = EnergyWeight,
    };

    /// <summary>Build a <see cref="PlaylistOptimizerOptions"/> instance from current settings.</summary>
    public PlaylistOptimizerOptions ToOptimizerOptions()
    {
        var curve = EnergyCurve switch
        {
            "Rising" => EnergyCurvePattern.Rising,
            "Wave"   => EnergyCurvePattern.Wave,
            "Peak"   => EnergyCurvePattern.Peak,
            _        => EnergyCurvePattern.None,
        };

        return new PlaylistOptimizerOptions
        {
            HarmonicWeight = HarmonicWeight,
            TempoWeight    = TempoWeight,
            EnergyWeight   = EnergyWeight,
            MaxBpmJump     = MaxBpm - MinBpm,
            EnergyCurve    = curve,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ResetToDefaults()
    {
        MinBpm        = 100;
        MaxBpm        = 160;
        MatchKey      = true;
        MaxEnergyJump = 3;
        MaxTracks     = 20;
        EnergyCurve   = "Wave";
        HarmonicWeight = 3.0;
        TempoWeight    = 1.0;
        EnergyWeight   = 0.5;
    }

    /// <summary>
    /// Reconstructs the preview from the current settings.
    /// When no real playlist is loaded, shows parameterised example transitions so the
    /// user can see how the harmony/BPM/energy constraints interact.
    /// </summary>
    private void RebuildPreview()
    {
        PreviewTransitions.Clear();

        // Example transitions — illustrate current settings using well-known Camelot pairs.
        var examples = new[]
        {
            ("Track A", "Track B", "8A", "8A",   MinBpm,         MinBpm,         "Perfect",    "#44FF88"),
            ("Track B", "Track C", "8A", "9A",   MinBpm,         MinBpm + 5,     "Compatible", "#FFD700"),
            ("Track C", "Track D", "9A", "10A",  MinBpm + 5,     MinBpm + 10,    MatchKey ? "Compatible" : "Risky", MatchKey ? "#FFD700" : "#FF4444"),
            ("Track D", "Track E", "10A","10B",  MinBpm + 10,    MaxBpm - 5,     MatchKey ? "Risky" : "Any",        MatchKey ? "#FF4444" : "#AAAAAA"),
            ("Track E", "Track F", "10B","11B",  MaxBpm - 5,     MaxBpm,         "Compatible", "#FFD700"),
        };

        foreach (var (a, b, ca, cb, bpmA, bpmB, label, color) in examples)
        {
            PreviewTransitions.Add(new TransitionPreviewItem
            {
                TrackA            = a,
                TrackB            = b,
                CamelotA          = ca,
                CamelotB          = cb,
                BpmA              = Math.Round(bpmA, 1),
                BpmB              = Math.Round(bpmB, 1),
                CompatibilityLabel = label,
                CompatibilityColor = color,
            });
        }
    }
}
