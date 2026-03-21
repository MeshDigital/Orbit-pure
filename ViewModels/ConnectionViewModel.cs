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
    private readonly IConnectionLifecycleService _lifecycle;
    private IDisposable? _lifecycleChangedSubscription;
    private EventHandler<bool>? _spotifyAuthHandler;
    private string? _pendingSavePassword;

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

    // Reconnect state is now owned by ConnectionLifecycleService.
    // These local flags exist only for UI suppression (overlay / status text).

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
        set
        {
            if (SetProperty(ref _autoConnectEnabled, value))
                _lifecycle.AutoReconnectEnabled = value;
        }
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
        IEventBus eventBus,
        IConnectionLifecycleService lifecycle)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _spotifyAuthService = spotifyAuthService;
        _lifecycle = lifecycle;

        // Initialize state from config
        Username = _config.Username ?? "";
        RememberPassword = _config.RememberPassword;
        AutoConnectEnabled = _config.AutoConnectEnabled;
        // Sync auto-reconnect policy with the lifecycle service
        _lifecycle.AutoReconnectEnabled = AutoConnectEnabled;
        
        // Show login overlay if not auto-connecting or if credentials missing
        IsLoginOverlayVisible = !_config.AutoConnectEnabled || string.IsNullOrEmpty(_config.Username);

        LoginCommand = new AsyncRelayCommand<string>(LoginAsync);
        ShowLoginCommand = new RelayCommand(ShowLogin);
        DismissLoginCommand = new RelayCommand(DismissLogin);
        DisconnectCommand = new RelayCommand(Disconnect);

        // Subscribe to lifecycle state changes — lifecycle service owns reconnect loop and state machine
        _lifecycleChangedSubscription = eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(evt =>
        {
            try
            {
                HandleLifecycleChange(evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle lifecycle state change event: {State}", evt.Current);
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
            _configManager.Save(_config);
            _pendingSavePassword = passwordToUse;

            await _lifecycle.RequestConnectAsync(passwordToUse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            _pendingSavePassword = null;
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
        _lifecycle.NotifyManualDisconnect();
        IsConnected = false;
        StatusText = "Disconnected";
        _ = _lifecycle.RequestDisconnectAsync("manual disconnect");
        // Do not force the login overlay here; transient reconnect cycles should
        // never look like credential loss.
    }

    public void Shutdown()
    {
        AutoConnectEnabled = false; // Prevent auto-reconnect
        _lifecycle.AutoReconnectEnabled = false;
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

    /// <summary>
    /// Translates <see cref="ConnectionLifecycleStateChangedEvent"/> from the lifecycle
    /// service into UI state (IsConnected, StatusText, IsInitializing).
    /// Auto-reconnect is fully owned by <see cref="IConnectionLifecycleService"/>.
    /// </summary>
    private void HandleLifecycleChange(ConnectionLifecycleStateChangedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            static string ToFriendlyFailureMessage(string reason)
            {
                if (reason.StartsWith("login rejected:", StringComparison.OrdinalIgnoreCase))
                    return $"Sign-in failed: {reason["login rejected:".Length..].Trim()}";

                if (reason.StartsWith("connect failed:", StringComparison.OrdinalIgnoreCase))
                    return $"Connection failed: {reason["connect failed:".Length..].Trim()}";

                return "Disconnected";
            }

            switch (evt.Current)
            {
                case "LoggedIn":
                    IsConnected = true;
                    IsInitializing = false;
                    StatusText = $"Connected as {Username}";
                    IsLoginOverlayVisible = false;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (RememberPassword)
                            {
                                if (!string.IsNullOrEmpty(_pendingSavePassword))
                                {
                                    await _credentialService.SaveCredentialsAsync(Username, _pendingSavePassword);
                                }
                            }
                            else
                            {
                                await _credentialService.DeleteCredentialsAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to persist Soulseek credentials after LoggedIn transition");
                        }
                        finally
                        {
                            _pendingSavePassword = null;
                        }
                    });
                    break;

                case "Connecting":
                    IsConnected = false;
                    IsInitializing = true;
                    StatusText = "Connecting...";
                    break;

                case "LoggingIn":
                    IsInitializing = true;
                    StatusText = "Logging in...";
                    break;

                case "CoolingDown":
                    IsConnected = false;
                    IsInitializing = false;
                    StatusText = "Disconnected by server — cooling down before reconnect...";
                    break;

                case "Disconnecting":
                    IsInitializing = false;
                    StatusText = "Disconnecting...";
                    break;

                case "Disconnected":
                    IsConnected = false;
                    IsInitializing = false;
                    StatusText = ToFriendlyFailureMessage(evt.Reason);
                    if (evt.Reason.StartsWith("login rejected:", StringComparison.OrdinalIgnoreCase)
                     || !AutoConnectEnabled)
                        IsLoginOverlayVisible = true;
                    break;
            }
        });
    }

    private void StartAutoReconnectLoop()
    {
        // Auto-reconnect is now owned by ConnectionLifecycleService.
        // This stub is kept to avoid build errors from any remaining call sites
        // and will be removed in a follow-up cleanup.
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
        _lifecycleChangedSubscription?.Dispose();
        if (_spotifyAuthHandler != null)
        {
            _spotifyAuthService.AuthenticationChanged -= _spotifyAuthHandler;
        }
    }
}
