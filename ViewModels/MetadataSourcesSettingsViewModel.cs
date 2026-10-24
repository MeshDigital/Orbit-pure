using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Enrichment.Providers;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>One provider row in Settings → Connection → Metadata sources.</summary>
public sealed class MetadataSourceRowViewModel : ReactiveObject
{
    private readonly MetadataSourcesSettingsViewModel _owner;
    private bool _isEnabled;
    private string _statusText = string.Empty;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _switchOn;
    private bool _canToggle;
    private bool _isWaiting;

    public MetadataSourceRowViewModel(MetadataSourcesSettingsViewModel owner, IMetadataProvider provider, bool isEnabled)
    {
        _owner = owner;
        Provider = provider;
        _isEnabled = isEnabled;
        MoveUpCommand = ReactiveCommand.Create(() => _owner.Move(this, -1), this.WhenAnyValue(x => x.CanMoveUp));
        MoveDownCommand = ReactiveCommand.Create(() => _owner.Move(this, +1), this.WhenAnyValue(x => x.CanMoveDown));
    }

    public IMetadataProvider Provider { get; }
    public string Name => Provider.Name;
    public string DisplayName => Provider.DisplayName;
    public string Description => Provider.Description;
    public bool IsExperimental => Provider.Name == BeatportProvider.ProviderName;
    public bool IsAlwaysOn => Provider.Name == MetadataMerger.TagsSource;

    /// <summary>False for file tags and while the source waits for a key / fpcalc / Spotify.</summary>
    public bool CanToggle { get => _canToggle; private set => this.RaiseAndSetIfChanged(ref _canToggle, value); }
    public bool IsWaiting { get => _isWaiting; private set => this.RaiseAndSetIfChanged(ref _isWaiting, value); }

    /// <summary>
    /// What the switch shows: ON only when the source will actually be asked (see
    /// <see cref="MetadataSourceToggle"/>). Flipping it changes <see cref="IsEnabled"/>, the saved choice.
    /// </summary>
    public bool SwitchOn
    {
        get => _switchOn;
        set
        {
            if (!CanToggle) { this.RaisePropertyChanged(); return; } // snap the control back
            IsEnabled = value;
        }
    }

    /// <summary>The user's saved choice (kept while the source waits for its key).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (IsAlwaysOn || _isEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _isEnabled, value);
            _owner.SaveProviders();
            RefreshStatus();
        }
    }

    /// <summary>Takes the value from config.ini without saving it again (after the setup wizard wrote it).</summary>
    internal void LoadEnabled(bool value)
    {
        if (IsAlwaysOn) return;
        this.RaiseAndSetIfChanged(ref _isEnabled, value, nameof(IsEnabled));
        RefreshStatus();
    }

    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public bool CanMoveUp { get => _canMoveUp; set => this.RaiseAndSetIfChanged(ref _canMoveUp, value); }
    public bool CanMoveDown { get => _canMoveDown; set => this.RaiseAndSetIfChanged(ref _canMoveDown, value); }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }

    public void RefreshStatus()
    {
        var availability = Provider.GetAvailability();
        var state = MetadataSourceToggle.Compute(Name, IsEnabled, IsAlwaysOn, availability);
        CanToggle = state.CanToggle;
        IsWaiting = state.IsWaiting;
        _switchOn = state.SwitchOn;
        this.RaisePropertyChanged(nameof(SwitchOn));
        StatusText = state.IsWaiting
            ? availability switch
            {
                ProviderAvailability.NeedsKey => state.WaitingText + " · paste it below",
                ProviderAvailability.NeedsTool => state.WaitingText + " · see below",
                ProviderAvailability.NotConnected => "Waiting for Spotify · connect it above",
                _ => state.WaitingText!,
            }
            : !IsEnabled && !IsAlwaysOn ? "Off" : availability switch
            {
                ProviderAvailability.Blocked => "Switched off this session after repeated access errors",
                _ => "Ready",
            };
    }
}

/// <summary>
/// Settings → Connection → "Metadata sources": provider toggles and order, the Discogs token and
/// AcoustID key (stored in the DPAPI <see cref="MetadataCredentialStore"/>, not config.ini), the
/// fpcalc state and the background auto-run switch.
/// </summary>
public sealed class MetadataSourcesSettingsViewModel : ReactiveObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly MetadataCredentialStore _keys;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MetadataSourcesSettingsViewModel> _logger;
    private readonly AcoustIdProvider? _acoustId;
    private string _discogsToken = string.Empty;
    private string _acoustIdKey = string.Empty;
    private string _keyStatus = string.Empty;

    public MetadataSourcesSettingsViewModel(
        AppConfig config,
        ConfigManager configManager,
        MetadataEnrichmentOrchestrator orchestrator,
        MetadataCredentialStore keys,
        IEventBus eventBus,
        ILogger<MetadataSourcesSettingsViewModel> logger)
    {
        _config = config;
        _configManager = configManager;
        _keys = keys;
        _eventBus = eventBus;
        _logger = logger;
        _acoustId = orchestrator.Providers.OfType<AcoustIdProvider>().FirstOrDefault();

        var settings = EnrichmentSettings.From(config);
        var ordered = settings.Order
            .Select(n => orchestrator.Providers.FirstOrDefault(p => p.Name == n))
            .Where(p => p != null)
            .Concat(orchestrator.Providers.Where(p => !settings.Order.Contains(p.Name)))
            .Distinct()
            .ToList();
        foreach (var p in ordered)
            Sources.Add(new MetadataSourceRowViewModel(this, p!, p!.Name == MetadataMerger.TagsSource || settings.IsEnabled(p.Name)));
        UpdateMoveState();
        foreach (var row in Sources) row.RefreshStatus();

        SaveDiscogsTokenCommand = ReactiveCommand.Create(() => SaveKey(MetadataCredentialStore.DiscogsToken, DiscogsToken, "Discogs token"));
        SaveAcoustIdKeyCommand = ReactiveCommand.Create(() => SaveKey(MetadataCredentialStore.AcoustIdKey, AcoustIdKey, "AcoustID key"));

        // A provider switched off mid-run (repeated 403) or Spotify (dis)connecting shows up here.
        _progressSubscription = eventBus.GetEvent<EnrichmentProgressEvent>()
            .Sample(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { foreach (var row in Sources) row.RefreshStatus(); });
    }

    private readonly IDisposable _progressSubscription;

    public void Dispose() => _progressSubscription.Dispose();

    /// <summary>The setup wizard's Metadata step writes the same [Enrichment] keys; show them here.</summary>
    public void RefreshFromConfig()
    {
        var settings = EnrichmentSettings.From(_config);
        foreach (var row in Sources) row.LoadEnabled(settings.IsEnabled(row.Name));
        this.RaisePropertyChanged(nameof(AutoRun));
        this.RaisePropertyChanged(nameof(HasDiscogsToken));
        this.RaisePropertyChanged(nameof(HasAcoustIdKey));
        this.RaisePropertyChanged(nameof(DiscogsTokenWatermark));
        this.RaisePropertyChanged(nameof(AcoustIdKeyWatermark));
    }

    public ObservableCollection<MetadataSourceRowViewModel> Sources { get; } = new();

    public bool AutoRun
    {
        get => _config.EnrichmentAutoRun;
        set
        {
            if (_config.EnrichmentAutoRun == value) return;
            _config.EnrichmentAutoRun = value;
            this.RaisePropertyChanged();
            Persist();
        }
    }

    /// <summary>Typed token; never pre-filled with the stored one (only "saved" is shown).</summary>
    public string DiscogsToken { get => _discogsToken; set => this.RaiseAndSetIfChanged(ref _discogsToken, value); }
    public string AcoustIdKey { get => _acoustIdKey; set => this.RaiseAndSetIfChanged(ref _acoustIdKey, value); }
    public bool HasDiscogsToken => _keys.Has(MetadataCredentialStore.DiscogsToken);
    public bool HasAcoustIdKey => _keys.Has(MetadataCredentialStore.AcoustIdKey);
    public string DiscogsTokenWatermark => HasDiscogsToken ? "Saved · paste a new token to replace" : "Personal access token";
    public string AcoustIdKeyWatermark => HasAcoustIdKey ? "Saved · paste a new key to replace" : "Application API key";
    public string KeyStatus { get => _keyStatus; private set => this.RaiseAndSetIfChanged(ref _keyStatus, value); }
    public bool HasKeyStatus => !string.IsNullOrEmpty(KeyStatus);
    public bool IsSecureStorageAvailable => _keys.IsSecureStorageAvailable;

    public string FpcalcStatus => _acoustId?.FpcalcPath is { } path
        ? "fpcalc found: " + path
        : "fpcalc not found. Install Chromaprint and put fpcalc.exe next to ORBIT.exe, in a Tools folder there, or on PATH.";
    public bool HasFpcalc => _acoustId?.FpcalcPath != null;

    public ReactiveCommand<Unit, Unit> SaveDiscogsTokenCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAcoustIdKeyCommand { get; }

    internal void Move(MetadataSourceRowViewModel row, int delta)
    {
        var index = Sources.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Sources.Count) return;
        Sources.Move(index, target);
        UpdateMoveState();
        SaveProviders();
    }

    private void UpdateMoveState()
    {
        for (int i = 0; i < Sources.Count; i++)
        {
            Sources[i].CanMoveUp = i > 0;
            Sources[i].CanMoveDown = i < Sources.Count - 1;
        }
    }

    internal void SaveProviders()
    {
        _config.EnrichmentProviderOrder = string.Join(",", Sources.Select(s => s.Name));
        _config.EnrichmentEnabledProviders = string.Join(",", Sources.Where(s => s.IsEnabled).Select(s => s.Name));
        Persist();
    }

    private void SaveKey(string name, string value, string label)
    {
        var clearing = string.IsNullOrWhiteSpace(value);
        if (_keys.Set(name, value))
        {
            KeyStatus = clearing ? $"{label} removed." : $"{label} saved (encrypted for your Windows account).";
            if (name == MetadataCredentialStore.DiscogsToken) DiscogsToken = string.Empty;
            else AcoustIdKey = string.Empty;
        }
        else
        {
            KeyStatus = _keys.LastError ?? $"{label} could not be saved.";
            _eventBus.Publish(new ToastRequestedEvent("Metadata sources", KeyStatus, NotificationType.Error, null));
        }
        this.RaisePropertyChanged(nameof(HasKeyStatus));
        this.RaisePropertyChanged(nameof(HasDiscogsToken));
        this.RaisePropertyChanged(nameof(HasAcoustIdKey));
        this.RaisePropertyChanged(nameof(DiscogsTokenWatermark));
        this.RaisePropertyChanged(nameof(AcoustIdKeyWatermark));
        foreach (var row in Sources) row.RefreshStatus();
    }

    private void Persist()
    {
        try
        {
            _configManager.Save(_config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Enrichment] Could not save metadata source settings");
            _eventBus.Publish(new ToastRequestedEvent("Settings not saved", "Could not write the settings file: " + ex.Message, NotificationType.Error, null));
        }
    }
}
