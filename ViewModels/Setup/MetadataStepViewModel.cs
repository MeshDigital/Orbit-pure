using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Setup;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>A source row on the Metadata step: a toggle plus one "gives · needs · limit" line.</summary>
public sealed class SetupMetadataSourceItem : SetupObservable
{
    private bool _isEnabled;
    private string _statusText = string.Empty;
    private string _liveText = string.Empty;
    private bool _isReady;
    private bool _needsSomething;

    public SetupMetadataSourceItem(SetupMetadataSource source)
    {
        Name = source.Name;
        DisplayName = source.DisplayName;
        Summary = source.Summary;
        IsAlwaysOn = source.IsAlwaysOn;
        IsExperimental = source.IsExperimental;
        _isEnabled = source.IsEnabled || source.IsAlwaysOn;
        Availability = source.Availability;
        RefreshStatus();
    }

    public string Name { get; }
    public string DisplayName { get; }
    public string Summary { get; }
    public bool IsAlwaysOn { get; }
    public bool CanToggle => !IsAlwaysOn;
    public bool IsExperimental { get; }
    public ProviderAvailability Availability { get; internal set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (IsAlwaysOn) return;
            if (SetProperty(ref _isEnabled, value)) RefreshStatus();
        }
    }

    /// <summary>"Ready", "Needs a token", "Connected", "Off".</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsReady { get => _isReady; private set => SetProperty(ref _isReady, value); }
    public bool NeedsSomething { get => _needsSomething; private set => SetProperty(ref _needsSomething, value); }

    /// <summary>While a run goes: "312 matched", "Paused for its rate limit · resumes 14:02:10".</summary>
    public string LiveText { get => _liveText; private set { if (SetProperty(ref _liveText, value)) OnPropertyChanged(nameof(HasLiveText)); } }
    public bool HasLiveText => !string.IsNullOrEmpty(LiveText);

    internal void RefreshStatus()
    {
        IsReady = IsEnabled && Availability == ProviderAvailability.Ready;
        NeedsSomething = IsEnabled && Availability is ProviderAvailability.NeedsKey or ProviderAvailability.NeedsTool or ProviderAvailability.NotConnected;
        StatusText = !IsEnabled ? "Off" : Availability switch
        {
            ProviderAvailability.NeedsKey => Name == "acoustid" ? "Needs an API key" : "Needs a token",
            ProviderAvailability.NeedsTool => "Needs fpcalc",
            ProviderAvailability.NotConnected => "Not connected",
            ProviderAvailability.Blocked => "Switched off this session",
            _ => Name == "spotify" ? "Connected" : "Ready",
        };
    }

    internal void ApplyLive(ProviderStatusSnapshot? s, bool runActive)
    {
        if (s == null || !runActive || !IsEnabled) { LiveText = IsEnabled ? string.Empty : "Disabled"; return; }
        LiveText = s.Availability switch
        {
            ProviderAvailability.Disabled => "Disabled",
            ProviderAvailability.NeedsKey or ProviderAvailability.NeedsTool or ProviderAvailability.NotConnected => "Skipped · " + StatusText.ToLowerInvariant(),
            ProviderAvailability.Blocked => "Switched off · " + (s.LastError ?? "access denied"),
            _ when s.IsCircuitOpen && s.OpenUntilUtc is { } until => $"Paused for its rate limit · resumes {until.ToLocalTime():HH:mm:ss}",
            _ when s.Requests == 0 => "Waiting",
            _ => $"{CountFormat.N(s.Matched)} matched",
        };
    }
}

/// <summary>
/// Wizard step "Metadata & artwork" (after Connections). Per source a toggle and one line on
/// what it gives, needs and its limit; Discogs token and AcoustID key; "Enrich automatically
/// after imports". Saved on Next like every other step, through the same store as Settings →
/// Metadata sources. "Start now" (once the library has tracks, e.g. while the Folders step's
/// import runs) saves first, then starts the run and shows live progress inline with
/// Pause / Resume. Leaving the step never stops the run: it belongs to the orchestrator.
/// </summary>
public sealed class MetadataStepViewModel : SetupStepViewModel, IDisposable
{
    public const string DiscogsTokenUrl = "https://www.discogs.com/settings/developers";
    public const string AcoustIdKeyUrl = "https://acoustid.org/new-application";
    public const string ChromaprintUrl = "https://acoustid.org/chromaprint";

    private readonly ISetupEnrichmentGateway _gateway;
    private readonly ILogger _logger;
    private readonly Action<Action> _post;
    private readonly AsyncRelayCommand _startCommand;
    private readonly RelayCommand _pauseCommand;
    private readonly RelayCommand _resumeCommand;

    private bool _autoRun = true;
    private string _discogsToken = string.Empty;
    private string _acoustIdKey = string.Empty;
    private int _trackCount;
    private bool _isStarting;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isSlowedForAnalysis;
    private bool _startedHere;
    private int _done, _total, _enriched;
    private string? _startMessage;
    private int _countRefreshPending;

    public MetadataStepViewModel(ISetupEnrichmentGateway gateway, ILogger logger, Action<Action>? postToUi = null)
    {
        _gateway = gateway;
        _logger = logger;
        _post = postToUi ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _startCommand = new AsyncRelayCommand(StartNowAsync, () => CanStart);
        _pauseCommand = new RelayCommand(() => _gateway.Pause(), () => IsRunning && !IsPaused);
        _resumeCommand = new RelayCommand(() => _gateway.Resume(), () => IsPaused);
        StartNowCommand = _startCommand;
        PauseCommand = _pauseCommand;
        ResumeCommand = _resumeCommand;
        GetDiscogsTokenCommand = new RelayCommand(() => _gateway.OpenUrl(DiscogsTokenUrl));
        GetAcoustIdKeyCommand = new RelayCommand(() => _gateway.OpenUrl(AcoustIdKeyUrl));
        GetFpcalcCommand = new RelayCommand(() => _gateway.OpenUrl(ChromaprintUrl));

        _gateway.ProgressChanged += OnProgressChanged;
        _gateway.LibraryChanged += OnLibraryChanged;
    }

    public override string Title => "Metadata and artwork";
    public override string ShortTitle => "Metadata";
    public override string Subtitle => "Fill in cover art, album, label, year and genre from your files and free online sources. Your edits and analysed BPM/key are never overwritten.";
    public override bool CanSkip => true;
    public override string SkipNote => "Nothing changes; the defaults stay (file tags, Deezer, MusicBrainz). Settings → Connection → Metadata sources.";

    public ObservableCollection<SetupMetadataSourceItem> Sources { get; } = new();

    public ICommand StartNowCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand GetDiscogsTokenCommand { get; }
    public ICommand GetAcoustIdKeyCommand { get; }
    public ICommand GetFpcalcCommand { get; }

    public SetupMetadataSourceItem? Discogs => Sources.FirstOrDefault(s => s.Name == "discogs");
    public SetupMetadataSourceItem? AcoustId => Sources.FirstOrDefault(s => s.Name == "acoustid");

    public bool AutoRun { get => _autoRun; set => SetProperty(ref _autoRun, value); }
    public string DiscogsToken { get => _discogsToken; set => SetProperty(ref _discogsToken, value ?? string.Empty); }
    public string AcoustIdKey { get => _acoustIdKey; set => SetProperty(ref _acoustIdKey, value ?? string.Empty); }
    public bool HasDiscogsToken => _gateway.HasDiscogsToken;
    public bool HasAcoustIdKey => _gateway.HasAcoustIdKey;
    public bool HasFpcalc => _gateway.HasFpcalc;
    public bool FpcalcMissing => !HasFpcalc;
    public string DiscogsWatermark => HasDiscogsToken ? "Saved · paste a new token to replace" : "Discogs personal access token";
    public string AcoustIdWatermark => HasAcoustIdKey ? "Saved · paste a new key to replace" : "AcoustID application API key";

    /// <summary>Tracks in the library now (grows while an import runs).</summary>
    public int TrackCount
    {
        get => _trackCount;
        private set
        {
            if (SetProperty(ref _trackCount, value))
            {
                OnPropertyChanged(nameof(HasTracks));
                OnPropertyChanged(nameof(NoTracks));
                OnPropertyChanged(nameof(StartHint));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanRunAgain));
                RaiseCommands();
            }
        }
    }

    public bool HasTracks => TrackCount > 0;
    public bool NoTracks => !HasTracks;

    public bool CanStart => HasTracks && !IsRunning && !IsPaused && !_isStarting;

    /// <summary>A run started here has finished; offer another pass (new rows from the import).</summary>
    public bool CanRunAgain => _startedHere && CanStart;

    public string StartHint => HasTracks
        ? $"{CountFormat.N(TrackCount)} tracks in your library. Start now, or let it run after imports."
        : "No tracks yet. Import a folder on the Folders step, or start this later from the Done page or the Analysis page.";

    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseRunState(); } }
    public bool IsPaused { get => _isPaused; private set { if (SetProperty(ref _isPaused, value)) RaiseRunState(); } }
    public bool ShowProgress => IsRunning || IsPaused || _startedHere;
    public bool IsSlowedForAnalysis { get => _isSlowedForAnalysis; private set { if (SetProperty(ref _isSlowedForAnalysis, value)) OnPropertyChanged(nameof(RunStatusText)); } }
    public int Done { get => _done; private set => SetProperty(ref _done, value); }
    public int Total { get => _total; private set => SetProperty(ref _total, value); }
    public int Enriched { get => _enriched; private set => SetProperty(ref _enriched, value); }

    /// <summary>0–100 for the meter.</summary>
    public double ProgressValue => Total <= 0 ? 0 : Math.Clamp(100.0 * Done / Total, 0, 100);

    /// <summary>"312 / 3,267 · 280 enriched" (mono).</summary>
    public string ProgressText => $"{CountFormat.N(Done)} / {CountFormat.N(Math.Max(Total, Done))} · {CountFormat.N(Enriched)} enriched";

    public string RunStatusText => IsPaused ? "Paused. Resume any time; nothing is lost."
        : IsRunning ? IsSlowedForAnalysis ? "Running in the background, one track at a time while analysis runs." : "Running in the background. You can carry on; it keeps going."
        : _startedHere ? "Finished for now. New imports are enriched automatically when that is on." : string.Empty;

    /// <summary>Why Start couldn't run, or a key-save problem.</summary>
    public string? StartMessage { get => _startMessage; private set { if (SetProperty(ref _startMessage, value)) OnPropertyChanged(nameof(HasStartMessage)); } }
    public bool HasStartMessage => !string.IsNullOrEmpty(StartMessage);

    public override async Task InitializeAsync()
    {
        Sources.Clear();
        foreach (var s in _gateway.GetSources()) Sources.Add(new SetupMetadataSourceItem(s));
        AutoRun = _gateway.AutoRun;
        OnPropertyChanged(nameof(Discogs));
        OnPropertyChanged(nameof(AcoustId));
        Apply(_gateway.CurrentProgress());
        await RefreshTrackCountAsync();
    }

    public override void OnShown()
    {
        // Spotify may have been connected on the previous step.
        RefreshAvailability();
        _ = RefreshTrackCountAsync();
    }

    public override Task<bool> CommitAsync()
    {
        ClearError();
        try
        {
            var keyError = SaveChoice();
            if (keyError != null)
            {
                ErrorMessage = keyError;
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: saving metadata sources failed");
            ErrorMessage = "The metadata settings couldn't be saved: " + ex.Message;
            return Task.FromResult(false);
        }
    }

    /// <summary>Saves the toggles and keys (also before "Start now", so the run uses them).</summary>
    private string? SaveChoice()
    {
        var keyError = _gateway.Save(new SetupEnrichmentChoice(
            Sources.Where(s => s.IsEnabled).Select(s => s.Name).ToList(), AutoRun,
            string.IsNullOrWhiteSpace(DiscogsToken) ? null : DiscogsToken,
            string.IsNullOrWhiteSpace(AcoustIdKey) ? null : AcoustIdKey));
        if (keyError == null)
        {
            DiscogsToken = string.Empty;
            AcoustIdKey = string.Empty;
        }
        OnPropertyChanged(nameof(HasDiscogsToken));
        OnPropertyChanged(nameof(HasAcoustIdKey));
        OnPropertyChanged(nameof(DiscogsWatermark));
        OnPropertyChanged(nameof(AcoustIdWatermark));
        RefreshAvailability();
        return keyError;
    }

    public async Task StartNowAsync()
    {
        if (!CanStart) return;
        _isStarting = true;
        RaiseCommands();
        StartMessage = null;
        try
        {
            var keyError = SaveChoice();
            if (keyError != null) StartMessage = keyError + " Started without it.";
            _startedHere = true;
            var queued = await _gateway.StartAsync();
            if (queued == 0)
                StartMessage = "Every track already has metadata, or was looked up in the last 7 days.";
            Apply(_gateway.CurrentProgress());
            _logger.LogInformation("Setup: metadata enrichment started from the wizard ({Count} tracks)", queued);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: starting metadata enrichment failed");
            StartMessage = "Enrichment couldn't start: " + ex.Message;
        }
        finally
        {
            _isStarting = false;
            RaiseRunState();
        }
    }

    public override string SummaryTitle => "Metadata and artwork";

    public override string SummaryDetail
    {
        get
        {
            var on = Sources.Where(s => s.IsEnabled).Select(s => s.DisplayName).ToList();
            var sources = on.Count == 0 ? "file tags" : string.Join(", ", on);
            return Status switch
            {
                SetupStepStatus.Configured => sources + (AutoRun ? " · after every import" : " · manual only") + (IsRunning ? " · running now" : string.Empty),
                SetupStepStatus.Skipped => "Skipped · defaults kept" + (IsRunning ? " · running now" : string.Empty),
                _ => "Not changed",
            };
        }
    }

    private void RefreshAvailability()
    {
        try
        {
            var fresh = _gateway.GetSources().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Sources)
            {
                if (fresh.TryGetValue(row.Name, out var s)) row.Availability = s.Availability;
                row.RefreshStatus();
            }
            OnPropertyChanged(nameof(HasFpcalc));
            OnPropertyChanged(nameof(FpcalcMissing));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: refreshing metadata source availability failed");
        }
    }

    private async Task RefreshTrackCountAsync()
    {
        try
        {
            var count = await _gateway.CountLibraryTracksAsync();
            _post(() => TrackCount = count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: counting library tracks failed");
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // Import batches land every second or two; one count query at a time is plenty.
        if (Interlocked.Exchange(ref _countRefreshPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshTrackCountAsync(); }
            finally { Volatile.Write(ref _countRefreshPending, 0); }
        });
    }

    private void OnProgressChanged(object? sender, EnrichmentProgress progress) => _post(() => Apply(progress));

    internal void Apply(EnrichmentProgress p)
    {
        Done = p.Done;
        Total = Math.Max(p.Total, p.Done + p.Pending);
        Enriched = p.Enriched;
        IsSlowedForAnalysis = p.IsSlowedForAnalysis;
        IsPaused = p.IsPaused;
        IsRunning = p.IsRunning;
        var active = p.IsRunning || p.IsPaused;
        foreach (var row in Sources)
            row.ApplyLive(p.Providers.FirstOrDefault(s => s.Name == row.Name), active);
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RunStatusText));
    }

    private void RaiseRunState()
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRunAgain));
        OnPropertyChanged(nameof(RunStatusText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        _startCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _resumeCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _gateway.ProgressChanged -= OnProgressChanged;
        _gateway.LibraryChanged -= OnLibraryChanged;
    }
}
