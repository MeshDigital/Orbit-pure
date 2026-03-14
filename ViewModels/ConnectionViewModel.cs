using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
                    
                    // Auto-reconnect logic (Phase 13.5)
                    // Only auto-reconnect if permitted and not currently connecting
                    if (wasConnected && AutoConnectEnabled && !IsInitializing)
                    {
                        _logger.LogInformation("Soulseek connection lost. Auto-reconnect in 5s...");
                        Task.Run(async () => {
                            await Task.Delay(5000);
                            // Check AutoConnectEnabled again in case Disconnect/Shutdown disabled it during the delay
                            if (!IsConnected && AutoConnectEnabled)
                            {
                                await AttemptAutoConnect();
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle state change event: {State}", evt.State);
            }
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
        _soulseek.Disconnect();
        IsConnected = false;
        StatusText = "Disconnected";
        
        // Ensure we don't auto-reconnect if user manually disconnected
        // Logic handled in event subscription via state check, but we could add a flag if needed.
        // For now, explicit Disconnect sets state to Disconnected which event handler sees.
        // But the event handler also triggers auto-reconnect if it WAS connected.
        // We need to differentiate User Disconnect vs Network Drop.
        
        IsLoginOverlayVisible = true;
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
                    break;
                case "Disconnected":
                    IsConnected = false;
                    StatusText = "Disconnected";
                    break;
                default:
                    StatusText = state;
                    break;
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

    public void Dispose()
    {
        _stateChangedSubscription?.Dispose();
        if (_spotifyAuthHandler != null)
        {
            _spotifyAuthService.AuthenticationChanged -= _spotifyAuthHandler;
        }
    }
}
