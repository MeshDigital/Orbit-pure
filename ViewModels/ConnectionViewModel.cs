using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Views; // For AsyncRelayCommand and RelayCommand

using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class ConnectionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<ConnectionViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;
    private readonly SpotifyAuthService _spotifyAuthService;
    private IDisposable? _stateChangedSubscription;
    private IDisposable? _connectionStatusSubscription;
    private EventHandler<bool>? _spotifyAuthHandler;

    // Connection State
    private string _username = "";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    private bool _isSpotifyConnected;
    public bool IsSpotifyConnected
    {
        get => _isSpotifyConnected;
        set => SetProperty(ref _isSpotifyConnected, value);
    }

    private string _statusText = "Disconnected";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _isInitializing = false; // Used for "Connecting..." spinner on button
    public bool IsInitializing
    {
        get => _isInitializing;
        set => SetProperty(ref _isInitializing, value);
    }

    private int _reconnectRetryCount = 0; // Exponential backoff counter
    private bool _manualDisconnectRequested;
    private int _reconnectLoopActive;
    private DateTime _reconnectCooldownUntilUtc = DateTime.MinValue;

    // Login Overlay State
    private bool _isLoginOverlayVisible;
    public bool IsLoginOverlayVisible
    {
        get => _isLoginOverlayVisible;
        set => SetProperty(ref _isLoginOverlayVisible, value);
    }

    private bool _rememberPassword;
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => SetProperty(ref _rememberPassword, value);
    }

    private bool _autoConnectEnabled;
    public bool AutoConnectEnabled
    {
        get => _autoConnectEnabled;
        set => SetProperty(ref _autoConnectEnabled, value);
    }

    // Commands
    public ICommand LoginCommand { get; }
    public ICommand ShowLoginCommand { get; }
    public ICommand DismissLoginCommand { get; }
    public ICommand DisconnectCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionViewModel(
        ILogger<ConnectionViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        SpotifyAuthService spotifyAuthService,
        IEventBus eventBus)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _spotifyAuthService = spotifyAuthService;

        // Initialize state from config
        Username = _config.Username ?? "";
        RememberPassword = _config.RememberPassword;
        AutoConnectEnabled = _config.AutoConnectEnabled;
        
        // Show login overlay if not auto-connecting or if credentials missing
        IsLoginOverlayVisible = !_config.AutoConnectEnabled || string.IsNullOrEmpty(_config.Username);

        LoginCommand = new AsyncRelayCommand<string>(LoginAsync);
        ShowLoginCommand = new RelayCommand(ShowLogin);
        DismissLoginCommand = new RelayCommand(DismissLogin);
        DisconnectCommand = new RelayCommand(Disconnect);

        // Subscribe to Soulseek state changes
        // Subscribe to Soulseek state changes
        _stateChangedSubscription = eventBus.GetEvent<SoulseekStateChangedEvent>().Subscribe(evt =>
        {
            try
            {
                bool wasConnected = IsConnected;
                if (evt.IsConnected)
                {
                    HandleStateChange("Connected");
                }
                else
                {
                    HandleStateChange(evt.State);
                    
                    // Auto-reconnect logic with exponential backoff (Phase 13.5)
                    // Only auto-reconnect if permitted and not currently connecting
                    if (wasConnected && AutoConnectEnabled && !IsInitializing && !_manualDisconnectRequested)
                    {
                        StartAutoReconnectLoop();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle state change event: {State}", evt.State);
            }
        });

        _connectionStatusSubscription = eventBus.GetEvent<SoulseekConnectionStatusEvent>().Subscribe(evt =>
        {
            if (!string.Equals(evt.Status, "kicked", StringComparison.OrdinalIgnoreCase))
                return;

            _reconnectCooldownUntilUtc = DateTime.UtcNow.AddSeconds(60);
            _logger.LogWarning("Received 'kicked' status. Auto-reconnect cooldown active until {CooldownUtc}.", _reconnectCooldownUntilUtc);
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                StatusText = "Disconnected by server. Waiting 60s before reconnect...";
            });
        });

        // Initialize Auto-Connect
        if (AutoConnectEnabled)
        {
             // We use Post to ensure this happens after constructor finishes
             Dispatcher.UIThread.Post(async () => await AttemptAutoConnect());
        }
        else if (RememberPassword)
        {
        }
        else if (RememberPassword)
        {
             Dispatcher.UIThread.Post(async () => await LoadUsernameOnly());
        }

        // Subscribe to Spotify auth changes
        _spotifyAuthHandler = (s, isAuthenticated) => 
        {
            Dispatcher.UIThread.Post(() => IsSpotifyConnected = isAuthenticated);
        };
        _spotifyAuthService.AuthenticationChanged += _spotifyAuthHandler;
        
        // Initialize Spotify status (use Post to avoid blocking constructor)
        Dispatcher.UIThread.Post(() => IsSpotifyConnected = _spotifyAuthService.IsAuthenticated);
    }

    private async Task AttemptAutoConnect()
    {
        var creds = await _credentialService.LoadCredentialsAsync();
        if (!string.IsNullOrEmpty(creds.Password))
        {
            if (!string.IsNullOrEmpty(creds.Username))
                Username = creds.Username;

            await LoginAsync(creds.Password);
        }
        else
        {
            IsLoginOverlayVisible = true;
        }
    }

    private async Task LoadUsernameOnly()
    {
        var creds = await _credentialService.LoadCredentialsAsync();
        if (!string.IsNullOrEmpty(creds.Username))
            Username = creds.Username;
    }

    public async Task LoginAsync(string? password)
    {
         if (string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "Please enter a username";
            return;
        }

        // If password provided in UI, use it. Otherwise try to use stored password if remembering is enabled.
        string? passwordToUse = password;

        if (string.IsNullOrEmpty(passwordToUse) && RememberPassword)
        {
             var creds = await _credentialService.LoadCredentialsAsync();
             if (!string.IsNullOrEmpty(creds.Password))
             {
                 passwordToUse = creds.Password;
             }
        }

        if (string.IsNullOrEmpty(passwordToUse))
        {
             StatusText = "Please enter a password";
             return;
        }

        IsInitializing = true;
        StatusText = "Connecting...";

        try
        {
            // Update config
            _config.Username = Username;
            _config.RememberPassword = RememberPassword;
            _config.AutoConnectEnabled = AutoConnectEnabled;

            await _soulseek.ConnectAsync(passwordToUse);
            
            // Check immediate status in case event is delayed
            if (_soulseek.IsConnected)
            {
                 HandleStateChange("Connected");
            }

            if (_soulseek.IsConnected)
            {
                Dispatcher.UIThread.Post(() => 
                {
                    IsLoginOverlayVisible = false;
                    IsInitializing = false; 
                });
                
                // Persistence
                _configManager.Save(_config);
                
                if (RememberPassword)
                {
                    await _credentialService.SaveCredentialsAsync(Username, passwordToUse);
                }
                else
                {
                    await _credentialService.DeleteCredentialsAsync();
                }
            }
            else
            {
                // Likely waiting for event, but if not connected instantly, just hide overlay and let event handle it?
                // Or keep overlay? Better to hide and let status text show "Connecting..." or error.
                // Keeping overlay hidden matching old logic optimization
                 Dispatcher.UIThread.Post(() => IsLoginOverlayVisible = false); 
                 _configManager.Save(_config);
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            Dispatcher.UIThread.Post(() => StatusText = $"Login error: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsInitializing = false);
        }
    }

    private void ShowLogin()
    {
        IsLoginOverlayVisible = true;
    }

    private void DismissLogin()
    {
        IsLoginOverlayVisible = false;
    }

    public void Disconnect()
    {
        _manualDisconnectRequested = true;
        _soulseek.Disconnect();
        IsConnected = false;
        StatusText = "Disconnected";

        // Do not force the login overlay here. Users can reopen it explicitly,
        // and transient reconnect cycles should never look like credential loss.
    }

    public void Shutdown()
    {
        AutoConnectEnabled = false; // Prevent auto-reconnect
        Disconnect();
    }

    private void HandleStateChange(string state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (state)
            {
                case "Connected":
                    IsConnected = true;
                    StatusText = $"Connected as {Username}";
                    IsLoginOverlayVisible = false;
                    _manualDisconnectRequested = false;
                    _reconnectRetryCount = 0; // Reset retry counter on successful connection
                    break;
                case "Disconnected":
                    IsConnected = false;
                    StatusText = "Disconnected";
                    // Keep the current overlay state. Only explicit login actions or
                    // missing startup credentials should surface the login prompt.
                    break;
                default:
                    StatusText = state;
                    break;
            }
        });
    }

    private void StartAutoReconnectLoop()
    {
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!IsConnected && AutoConnectEnabled && !_manualDisconnectRequested)
                {
                    var now = DateTime.UtcNow;
                    if (_reconnectCooldownUntilUtc > now)
                    {
                        var cooldownDelay = _reconnectCooldownUntilUtc - now;
                        _logger.LogInformation("Reconnect cooldown active. Waiting {Delay}s before next reconnect attempt.", Math.Ceiling(cooldownDelay.TotalSeconds));
                        await Task.Delay(cooldownDelay);
                    }

                    _reconnectRetryCount++;
                    var delayMs = CalculateReconnectDelay(_reconnectRetryCount);
                    _logger.LogInformation("Soulseek connection lost. Auto-reconnect attempt #{Attempt} in {Delay}s...", _reconnectRetryCount, delayMs / 1000);

                    await Task.Delay(delayMs);

                    if (IsConnected || !AutoConnectEnabled || _manualDisconnectRequested)
                        break;

                    await AttemptAutoConnect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-reconnect loop encountered an error");
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectLoopActive, 0);
            }
        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Calculates exponential backoff delay for reconnection attempts.
    /// Delays: 5s, 15s, 60s, then caps at 60s to prevent excessive waiting.
    /// </summary>
    private int CalculateReconnectDelay(int attemptNumber)
    {
        // Exponential backoff: 5s * 3^(attempt-1), capped at 60s
        var delaySeconds = 5 * Math.Pow(3, attemptNumber - 1);
        return Math.Min((int)delaySeconds, 60) * 1000; // Convert to milliseconds
    }

    public void Dispose()
    {
        _stateChangedSubscription?.Dispose();
        _connectionStatusSubscription?.Dispose();
        if (_spotifyAuthHandler != null)
        {
            _spotifyAuthService.AuthenticationChanged -= _spotifyAuthHandler;
        }
    }
}
