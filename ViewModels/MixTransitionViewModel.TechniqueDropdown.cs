using System;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using SLSKDONET.Services.Transitions;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One entry in the unified Technique dropdown (Epic #517 / #531): built-in DJ techniques
/// (Auto, Cut, Crossfade, Echo-out, Filter sweep, EQ swap, Loop), plus any legacy user templates
/// from <c>TransitionTemplates</c> under a divider when non-empty.
/// </summary>
public sealed record TechniqueOption(
    string Key,
    string Label,
    bool IsEnabled = true,
    bool IsDivider = false,
    UserTransitionPreset? UserPreset = null)
{
    public override string ToString() => Label;
}

public partial class MixTransitionViewModel
{
    private bool _syncingTechnique;
    private TechniqueOption? _selectedTechniqueOption;

    /// <summary>
    /// Unified Technique dropdown items (Epic #517 / #531). Replaces the preset chip catalogue.
    /// </summary>
    public ObservableCollection<TechniqueOption> TechniqueOptions { get; } = new();

    public TechniqueOption? SelectedTechniqueOption
    {
        get => _selectedTechniqueOption;
        set
        {
            if (value is null || value.IsDivider || !value.IsEnabled) return;
            if (ReferenceEquals(_selectedTechniqueOption, value)) return;

            this.RaiseAndSetIfChanged(ref _selectedTechniqueOption, value);
            if (_syncingTechnique) return;

            _syncingTechnique = true;
            try
            {
                if (value.UserPreset is not null)
                {
                    SelectedUserPreset = value.UserPreset;
                }
                else
                {
                    ApplyBuiltInTechnique(value.Key);
                }
            }
            finally
            {
                _syncingTechnique = false;
            }
        }
    }

    private void InitTechniqueDropdown()
    {
        RebuildTechniqueOptions();
        UserPresets.CollectionChanged += (_, _) => RebuildTechniqueOptions();
        PropertyChanged += (_, e) =>
        {
            if (_syncingTechnique) return;
            if (e.PropertyName is nameof(SelectedPreset) or nameof(SelectedUserPreset) or nameof(CustomLoopEnabled))
            {
                SyncSelectedTechniqueFromState();
            }
        };
    }

    internal void RebuildTechniqueOptions()
    {
        string autoLabel = BuildAutoTechniqueLabel();
        var items = new System.Collections.Generic.List<TechniqueOption>
        {
            new("Auto", autoLabel),
            new("Cut", "Cut"),
            new("Crossfade", "Crossfade"),
            new("EchoOut", "Echo-out"),
            new("FilterSweep", "Filter sweep"),
            new("EqSwap", "EQ swap"),
            new("Loop", "Loop"),
        };

        if (UserPresets.Count > 0)
        {
            items.Add(new("Divider", "─── Saved templates ───", IsEnabled: false, IsDivider: true));
            foreach (var preset in UserPresets)
            {
                items.Add(new($"Template:{preset.Id}", preset.Name, IsEnabled: true, IsDivider: false, UserPreset: preset));
            }
        }

        _syncingTechnique = true;
        try
        {
            TechniqueOptions.Clear();
            foreach (var item in items)
            {
                TechniqueOptions.Add(item);
            }
            SyncSelectedTechniqueFromState();
        }
        finally
        {
            _syncingTechnique = false;
        }
    }

    private string BuildAutoTechniqueLabel()
    {
        if (SelectedPreset is "Rolling" or "Drop Sync" or "Relaxed" or "1-Bar Drop Swap")
            return "Auto (EQ swap)";
        if (SelectedPreset is "EchoOut" or "Melt")
            return "Auto (echo-out)";
        return "Auto (phrase-aligned)";
    }

    private void ApplyBuiltInTechnique(string key)
    {
        switch (key)
        {
            case "Auto":
                CustomLoopEnabled = false;
                SelectedPreset = "Auto";
                break;
            case "Cut":
                CustomLoopEnabled = false;
                SelectedPreset = "Cut";
                break;
            case "Crossfade":
                CustomLoopEnabled = false;
                SelectedPreset = "Fade";
                break;
            case "EchoOut":
                CustomLoopEnabled = false;
                SelectedPreset = "EchoOut";
                break;
            case "FilterSweep":
                CustomLoopEnabled = false;
                SelectedPreset = "Rise";
                break;
            case "EqSwap":
                CustomLoopEnabled = false;
                SelectedPreset = "Blend";
                break;
            case "Loop":
                SelectedPreset = "Custom";
                CustomLoopEnabled = true;
                break;
        }
    }

    private void SyncSelectedTechniqueFromState()
    {
        TechniqueOption? match = null;
        if (SelectedUserPreset is { } up)
        {
            match = TechniqueOptions.FirstOrDefault(o => o.UserPreset?.Id == up.Id);
        }

        if (match is null)
        {
            string key = MapPresetToTechniqueKey(SelectedPreset, CustomLoopEnabled == true);
            match = TechniqueOptions.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal))
                    ?? TechniqueOptions.FirstOrDefault();
        }

        _syncingTechnique = true;
        try
        {
            this.RaiseAndSetIfChanged(ref _selectedTechniqueOption, match, nameof(SelectedTechniqueOption));
        }
        finally
        {
            _syncingTechnique = false;
        }
    }

    internal static string MapPresetToTechniqueKey(string? presetName, bool loopEnabled)
    {
        if (loopEnabled) return "Loop";
        return presetName switch
        {
            "Cut" => "Cut",
            "Fade" or "Crossfade" => "Crossfade",
            "EchoOut" or "Melt" => "EchoOut",
            "Rise" or "FilterSweep" => "FilterSweep",
            "Blend" or "EqSwap" => "EqSwap",
            "Loop" or "DoubleDrop" => "Loop",
            _ => "Auto",
        };
    }
}
