using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services.Transitions;

namespace SLSKDONET.ViewModels;

// User transition presets (rebuild plan D5 / P3.5, #204): a DJ saves a tuned Custom blend under a
// name and picks it again from "My presets" in any transition editor (Flow Builder, Sets, sidebar).
public partial class MixTransitionViewModel
{
    private IUserTransitionPresetService? _userPresets;

    /// <summary>The DJ's saved Custom blends, by name.</summary>
    public ObservableCollection<UserTransitionPreset> UserPresets { get; } = new();

    public bool HasUserPresets => UserPresets.Count > 0;

    /// <summary>User presets are offered only when their store is available (it is in the app).</summary>
    public bool CanUseUserPresets => _userPresets is not null;

    private UserTransitionPreset? _selectedUserPreset;
    /// <summary>The user preset last applied; picking one applies it. Cleared when another preset is picked.</summary>
    public UserTransitionPreset? SelectedUserPreset
    {
        get => _selectedUserPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedUserPreset, value);
            this.RaisePropertyChanged(nameof(HasSelectedUserPreset));
            if (value is not null)
                ApplyUserPreset(value);
        }
    }

    public bool HasSelectedUserPreset => SelectedUserPreset is not null;

    private string _newUserPresetName = string.Empty;
    /// <summary>Name for "Save as preset" (an existing name is replaced).</summary>
    public string NewUserPresetName { get => _newUserPresetName; set => this.RaiseAndSetIfChanged(ref _newUserPresetName, value ?? string.Empty); }

    private bool _applyingUserPreset;

    private void InitUserPresets(IServiceProvider services)
    {
        _userPresets = services.GetService(typeof(IUserTransitionPresetService)) as IUserTransitionPresetService;
        UserPresets.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasUserPresets));
        // Picking a built-in preset (or tuning away from Custom) ends "this is my preset".
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedPreset) && !_applyingUserPreset && SelectedUserPreset is not null)
                SelectedUserPreset = null;
        };
        if (_userPresets is not null)
            _ = LoadUserPresetsAsync();
    }

    /// <summary>Reloads the user presets from the library database.</summary>
    public async Task LoadUserPresetsAsync()
    {
        if (_userPresets is null) return;
        try
        {
            var all = await _userPresets.GetAllAsync();
            UserPresets.Clear();
            foreach (var preset in all)
                UserPresets.Add(preset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixTransition] couldn't load user transition presets");
            StatusMessage = $"Couldn't load your presets: {ex.Message}";
        }
    }

    /// <summary>The editor's current Custom blend and length.</summary>
    public CustomBlendSettings CurrentCustomBlend() => new(
        DurationBars, CustomEchoDecayFactor, CustomFilterStartFrequency, CustomFilterEndFrequency,
        CustomWaveDuckDepth, CustomFilterSweepRising, CustomEqSwapLow, CustomEqSwapMid, CustomEqSwapHigh,
        CustomEqLowCrossoverHz, CustomEqHighCrossoverHz, CustomLoopEnabled, CustomLoopBars, CustomLoopRepeats);

    /// <summary>Switches to Custom and loads the preset's blend and length.</summary>
    public void ApplyUserPreset(UserTransitionPreset preset)
    {
        var s = preset.Settings;
        _applyingUserPreset = true;
        try
        {
            SelectedPreset = "Custom";
            CustomEchoDecayFactor = s.EchoDecayFactor;
            CustomFilterStartFrequency = s.FilterStartFrequency;
            CustomFilterEndFrequency = s.FilterEndFrequency;
            CustomWaveDuckDepth = s.WaveDuckDepth;
            CustomFilterSweepRising = s.FilterSweepRising;
            CustomEqSwapLow = s.EqSwapLow;
            CustomEqSwapMid = s.EqSwapMid;
            CustomEqSwapHigh = s.EqSwapHigh;
            CustomEqLowCrossoverHz = s.EqLowCrossoverHz;
            CustomEqHighCrossoverHz = s.EqHighCrossoverHz;
            CustomLoopEnabled = s.LoopEnabled;
            CustomLoopBars = s.LoopBars;
            CustomLoopRepeats = s.LoopRepeats;
            if (s.DurationBars > 0)
                DurationBars = s.DurationBars;
        }
        finally
        {
            _applyingUserPreset = false;
        }
        if (!ReferenceEquals(_selectedUserPreset, preset))
        {
            _selectedUserPreset = preset;
            this.RaisePropertyChanged(nameof(SelectedUserPreset));
            this.RaisePropertyChanged(nameof(HasSelectedUserPreset));
        }
        StatusMessage = $"Applied your preset \"{preset.Name}\"";
    }

    /// <summary>Saves the current Custom blend under <see cref="NewUserPresetName"/>.</summary>
    public async Task SaveUserPresetAsync()
    {
        if (_userPresets is null) return;
        var name = NewUserPresetName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Name the preset first";
            return;
        }
        if (!IsCustomMode)
        {
            StatusMessage = "Pick Custom and tune the blend, then save it as a preset";
            return;
        }
        try
        {
            var saved = await _userPresets.SaveAsync(name, CurrentCustomBlend());
            await LoadUserPresetsAsync();
            NewUserPresetName = string.Empty;
            _applyingUserPreset = true;
            try { SelectedUserPreset = UserPresets.FirstOrDefault(p => p.Id == saved.Id); }
            finally { _applyingUserPreset = false; }
            StatusMessage = $"Saved your preset \"{saved.Name}\"";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixTransition] couldn't save user preset {Name}", name);
            StatusMessage = $"Couldn't save the preset: {ex.Message}";
        }
    }

    /// <summary>Deletes the selected user preset (the current blend stays as it is).</summary>
    public async Task DeleteSelectedUserPresetAsync()
    {
        if (_userPresets is null || SelectedUserPreset is not { } preset) return;
        try
        {
            var deleted = await _userPresets.DeleteAsync(preset.Id);
            _selectedUserPreset = null;
            this.RaisePropertyChanged(nameof(SelectedUserPreset));
            this.RaisePropertyChanged(nameof(HasSelectedUserPreset));
            await LoadUserPresetsAsync();
            StatusMessage = deleted ? $"Deleted your preset \"{preset.Name}\"" : $"\"{preset.Name}\" was already deleted";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixTransition] couldn't delete user preset {Name}", preset.Name);
            StatusMessage = $"Couldn't delete the preset: {ex.Message}";
        }
    }
}
