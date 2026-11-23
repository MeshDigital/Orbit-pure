using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.Genre;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Settings;

/// <summary>
/// Settings → Library "Learn from my genre corrections" (GA11 #256): the real toggle, what the head learned from, its
/// cross-validated score, and a Retrain now action.
/// </summary>
public sealed class PersonalGenreSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PersonalGenreHeadService _service;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ILogger<PersonalGenreSettingsViewModel> _logger;
    private readonly Action<Action> _ui;
    private string? _lastError;

    public PersonalGenreSettingsViewModel(PersonalGenreHeadService service, AppConfig config, ConfigManager configManager,
        ILogger<PersonalGenreSettingsViewModel> logger, Action<Action>? ui = null)
    {
        _service = service;
        _config = config;
        _configManager = configManager;
        _logger = logger;
        _ui = ui ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _service.StateChanged += OnStateChanged;
        RetrainCommand = new AsyncRelayCommand(RetrainAsync, () => LearnFromGenreCorrections && !_service.IsTraining);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool LearnFromGenreCorrections
    {
        get => _config.LearnFromGenreCorrections;
        set
        {
            if (_config.LearnFromGenreCorrections == value) return;
            _config.LearnFromGenreCorrections = value;
            try { _configManager.Save(_config); }
            catch (Exception ex) { _logger.LogError(ex, "[PersonalGenre] Couldn't save the setting"); }
            Raise();
            RaiseState();
        }
    }

    public ICommand RetrainCommand { get; }

    public bool IsTraining => _service.IsTraining;

    /// <summary>"Learned from 1,812 tracks (Drum and Bass 1,455 · House 264 · …). Improves genre on your tracks: 61 % → 66 % …".</summary>
    public string StatusText
    {
        get
        {
            if (!LearnFromGenreCorrections) return "Off: genre comes from the audio model and your tags only.";
            if (_service.IsTraining) return "Learning from your genres…";
            if (_lastError is not null) return _lastError;
            if (_service.Model is not { } model) return "Not trained yet. It learns in the background shortly after start and after you edit genres.";
            var counts = string.Join(" · ", model.LabelsPerClass.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{Label(kv.Key)} {SLSKDONET.Utils.CountFormat.N(kv.Value)}"));
            return $"Learned from {SLSKDONET.Utils.CountFormat.N(model.LabelCount)} tracks ({counts}). {model.Reason}";
        }
    }

    /// <summary>True when the head is blended into genre right now.</summary>
    public bool IsActive => _service.ActiveModel is not null;

    private static string Label(string bucket) =>
        Enum.TryParse<GenreBucket>(bucket, out var b) ? GenreTaxonomy.CanonicalDisplay(b, null) : bucket;

    private async Task RetrainAsync()
    {
        _lastError = null;
        try
        {
            await Task.Run(() => _service.TrainIfChangedAsync(force: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersonalGenre] Retrain failed");
            _lastError = $"Training failed: {ex.Message}";
        }
        RaiseState();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _ui(RaiseState);

    private void RaiseState()
    {
        Raise(nameof(StatusText));
        Raise(nameof(IsTraining));
        Raise(nameof(IsActive));
        (RetrainCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _service.StateChanged -= OnStateChanged;
}
