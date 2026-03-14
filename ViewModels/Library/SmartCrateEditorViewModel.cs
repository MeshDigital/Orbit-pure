using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.ViewModels.Library;

public class SmartCrateEditorViewModel : ReactiveObject
{
    private readonly SmartCrateService _smartCrateService;
    private readonly ILogger<SmartCrateEditorViewModel> _logger;
    private readonly SmartCrateDefinitionEntity? _existingEntity;

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    // Rules
    private string? _mood;
    public string? Mood
    {
        get => _mood;
        set => this.RaiseAndSetIfChanged(ref _mood, value);
    }
    
    private string? _subGenre;
    public string? SubGenre
    {
        get => _subGenre;
        set => this.RaiseAndSetIfChanged(ref _subGenre, value);
    }

    private double? _minBpm;
    public double? MinBpm
    {
        get => _minBpm;
        set => this.RaiseAndSetIfChanged(ref _minBpm, value);
    }

    private double? _maxBpm;
    public double? MaxBpm
    {
        get => _maxBpm;
        set => this.RaiseAndSetIfChanged(ref _maxBpm, value);
    }

    private double? _minEnergy;
    public double? MinEnergy
    {
        get => _minEnergy;
        set => this.RaiseAndSetIfChanged(ref _minEnergy, value);
    }

    private double? _maxEnergy;
    public double? MaxEnergy
    {
        get => _maxEnergy;
        set => this.RaiseAndSetIfChanged(ref _maxEnergy, value);
    }

    public ObservableCollection<string> AvailableVibes { get; } = new()
    {
         "Aggressive", "Chaotic", "Energetic", "Happy", 
         "Party", "Relaxed", "Sad", "Dark"
    };

    public ReactiveCommand<Unit, SmartCrateDefinitionEntity?> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public SmartCrateEditorViewModel(
        SmartCrateService smartCrateService,
        ILogger<SmartCrateEditorViewModel> logger,
        SmartCrateDefinitionEntity? existing = null)
    {
        _smartCrateService = smartCrateService;
        _logger = logger;
        _existingEntity = existing;

        if (existing != null)
        {
            Name = existing.Name;
            try
            {
                var rules = JsonSerializer.Deserialize<SmartCrateRules>(existing.RulesJson);
                if (rules != null)
                {
                    Mood = rules.Mood;
                    SubGenre = rules.SubGenre;
                    MinBpm = rules.MinBpm;
                    MaxBpm = rules.MaxBpm;
                    MinEnergy = rules.MinEnergy;
                    MaxEnergy = rules.MaxEnergy;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse existing crate rules");
            }
        }

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, 
            this.WhenAnyValue(x => x.Name, name => !string.IsNullOrWhiteSpace(name)));
            
        CancelCommand = ReactiveCommand.Create(() => { });
    }

    private async Task<SmartCrateDefinitionEntity?> SaveAsync()
    {
        try
        {
            var rules = new SmartCrateRules
            {
                Mood = string.IsNullOrWhiteSpace(Mood) ? null : Mood,
                SubGenre = string.IsNullOrWhiteSpace(SubGenre) ? null : SubGenre,
                MinBpm = MinBpm,
                MaxBpm = MaxBpm,
                MinEnergy = MinEnergy,
                MaxEnergy = MaxEnergy
            };

            if (_existingEntity != null)
            {
                // Update local model
                _existingEntity.Name = Name;
                _existingEntity.RulesJson = JsonSerializer.Serialize(rules);
                _existingEntity.UpdatedAt = DateTime.UtcNow;
                
                // Persist
                await _smartCrateService.UpdateCrateAsync(_existingEntity, rules);
                return _existingEntity;
            }
            else
            {
                // Create
                return await _smartCrateService.CreateCrateAsync(Name, rules);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save smart crate");
            return null;
        }
    }
}
