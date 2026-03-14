# Login System Implementation - Backup Documentation

## Date: 2025-12-12

## Overview
This document serves as a backup of the complete login system implementation that was debugged and fixed during Phase 9.

---

## Critical Files & Their Roles

### 1. Configuration Files

#### `Configuration/AppConfig.cs`
**Purpose**: Defines default configuration values

**Key Settings** (Lines 22-24):
```csharp
// Soulseek Network Settings (matches Soulseek.NET library defaults)
public string SoulseekServer { get; set; } = "server.slsknet.org"; 
public int SoulseekPort { get; set; } = 2242;
```

**Important**: These values MUST match Soulseek.NET defaults. Previous values `208.76.170.59:2240` caused connection failures.

#### `Configuration/ConfigManager.cs`
**Purpose**: Loads and saves configuration from/to ini files

**Default Path**: `%APPDATA%\SLSKDONET\config.ini`

**Critical Fallback Values** (Lines 59, 82):
```csharp
// Line 59 - Port default
SoulseekPort = int.TryParse(config["Soulseek:Port"], out var sPort) ? sPort : 2242,

// Line 82 - Server default
if (string.IsNullOrEmpty(_config.SoulseekServer)) 
    _config.SoulseekServer = "server.slsknet.org";
```

**Save Method** (Lines 95-137):
- Saves credentials only when `RememberPassword = true`
- Encrypts password using `ProtectedDataService`
- Creates ini file with sections: [Soulseek], [Spotify], [Download]

---

### 2. Connection Layer

#### `Services/SoulseekAdapter.cs`
**Purpose**: Wraps the Soulseek.NET client library

**ConnectAsync Method** (Lines 30-67):
```csharp
public async Task ConnectAsync(string? password = null, CancellationToken ct = default)
{
    try
    {
        _client = new SoulseekClient();
        _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...", 
            _config.Username, _config.SoulseekServer, _config.SoulseekPort);
        
        await _client.ConnectAsync(
            _config.SoulseekServer ?? "server.slsknet.org", 
            _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort, 
            _config.Username, 
            password, 
            ct);
        
        // Subscribe to state changes
        _client.StateChanged += (sender, args) =>
        {
            _logger.LogInformation("Soulseek state changed: {State} (was {PreviousState})", 
                args.State, args.PreviousState);
            EventBus.OnNext(("state_changed", new { 
                state = args.State.ToString(), 
                previousState = args.PreviousState.ToString() 
            }));
        };
        
        _logger.LogInformation("Successfully connected to Soulseek as {Username}", _config.Username);
        EventBus.OnNext(("connection_status", new { status = "connected", username = _config.Username }));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to connect to Soulseek: {Message}", ex.Message);
        throw;
    }
}
```

**Key Features**:
- StateChanged event subscription for reactive UI updates
- EventBus notifications for state changes
- Proper error logging

---

### 3. ViewModel Layer

#### `Views/MainViewModel.cs`

**Fields** (Lines 28-30):
```csharp
private readonly AppConfig _config;
private readonly ConfigManager _configManager;
private readonly SoulseekAdapter _soulseek;
```

**Properties**:
```csharp
// Line 308
public bool IsLoginOverlayVisible => !_isConnected;

// Lines 293-306
public bool IsConnected
{
    get => _isConnected;
    private set
    {
        if (SetProperty(ref _isConnected, value))
        {
            OnPropertyChanged(nameof(IsLoginOverlayVisible));
            OnPropertyChanged(nameof(ConnectionStatus));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
```

**State Change Handler** (Lines 276-295):
```csharp
private void HandleStateChange(string? state)
{
    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
    {
        _logger.LogInformation("Handling state change: {State}", state);
        
        if (state == "Connected" || state == "LoggedIn")
        {
            IsConnected = true;
            StatusText = $"Connected as {Username}";
        }
        else if (state == "Disconnected")
        {
            IsConnected = false;
            StatusText = "Disconnected";
        }
        else if (state == "Connecting")
        {
            StatusText = "Connecting...";
        }
    });
}
```

**LoginAsync Method** (Lines 584-647):
```csharp
private async Task LoginAsync(string? password)
{
    if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(password))
    {
        StatusText = "Username and password required";
        return;
    }

    try
    {
        // Update config
        _config.Username = Username;
        _config.RememberPassword = RememberPassword;

        // Save password if "Remember Me" is checked
        if (RememberPassword)
        {
            _config.Password = _protectedDataService.Protect(password);
        }
        else
        {
            _config.Password = null;
        }
        
        StatusText = "Connecting...";
        await _soulseek.ConnectAsync(password);
        IsConnected = true;
        StatusText = $"Connected as {Username}";
        
        // CRITICAL: Save config to persist credentials
        _configManager.Save(_config);
        
        _logger.LogInformation("Login successful");
    }
    catch (Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        
        // Specific error messages
        if (message.Contains("invalid username or password") || 
            message.Contains("authentication failed") ||
            message.Contains("login failed"))
        {
            StatusText = "❌ Invalid username or password";
        }
        else if (message.Contains("timeout") || message.Contains("timed out"))
        {
            StatusText = "⏱️ Connection timeout - check your network";
        }
        else if (message.Contains("refused") || message.Contains("connection refused"))
        {
            StatusText = "🚫 Connection refused - server may be down";
        }
        else if (message.Contains("network") || message.Contains("unreachable"))
        {
            StatusText = "🌐 Network error - check your internet connection";
        }
        else
        {
            StatusText = $"Login failed: {ex.Message}";
        }
        
        _logger.LogError(ex, "Login failed");
    }
}
```

**Auto-Login on Startup** (Lines 242-271):
```csharp
public void OnViewLoaded()
{
    _logger.LogInformation("OnViewLoaded called");
    
    // Attempt auto-login if credentials are saved
    if (!string.IsNullOrEmpty(_config.Username) && 
        !string.IsNullOrEmpty(_config.Password) &&
        _config.RememberPassword)
    {
        _logger.LogInformation("Auto-login: credentials found, attempting...");
        _ = AutoLoginAsync();
    }
    
    _ = LoadLibraryAsync();
}

private async Task AutoLoginAsync()
{
    try
    {
        var decryptedPassword = _protectedDataService.Unprotect(_config.Password);
        Username = _config.Username;
        await LoginAsync(decryptedPassword);
        _logger.LogInformation("Auto-login successful");
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Auto-login failed, showing login screen");
        // Login overlay will remain visible since IsConnected is still false
    }
}
```

**Event Bus Subscription** (Lines 215-231):
```csharp
// Subscribe to Soulseek state changes
_soulseek.EventBus.Subscribe(evt =>
{
    if (evt.eventType == "state_changed")
    {
        try
        {
            dynamic data = evt.data;
            string state = data.state;
            HandleStateChange(state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle state change event");
        }
    }
});
```

---

### 4. UI Layer

#### `Views/MainWindow.xaml`
**Login Overlay** (Lines 90-163):
- Visible when `IsLoginOverlayVisible = true`
- Centered modal overlay with dark background
- Contains username/password inputs and "Remember Me" checkbox

**Visibility Binding** (Lines 96-105):
```xml
<Grid.Style>
    <Style TargetType="Grid">
        <Setter Property="Visibility" Value="Collapsed"/>
        <Style.Triggers>
            <DataTrigger Binding="{Binding IsLoginOverlayVisible, FallbackValue=True}" Value="True">
                <Setter Property="Visibility" Value="Visible"/>
            </DataTrigger>
        </Style.Triggers>
    </Style>
</Grid.Style>
```

#### `Views/MainWindow.xaml.cs`
**SignInButton_Click Handler** (Lines 128-179):
```csharp
private void SignInButton_Click(object sender, RoutedEventArgs e)
{
    try
    {
        var passwordBox = this.FindName("OverlayPasswordBox") as PasswordBox;
        if (passwordBox == null)
        {
            MessageBox.Show("Login form error - password box not found", "Error");
            return;
        }

        var password = passwordBox.Password;
        
        if (string.IsNullOrEmpty(_viewModel.Username))
        {
            _viewModel.StatusText = "Please enter a username";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            _viewModel.StatusText = "Please enter a password";
            return;
        }

        if (_viewModel.LoginCommand.CanExecute(password))
        {
            _viewModel.LoginCommand.Execute(password);
        }
        else
        {
            _viewModel.StatusText = "Cannot login at this time";
        }
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Login error: {ex.Message}", "Error");
    }
}
```

---

## Security Features

### Password Encryption
**Service**: `Services/ProtectedDataService.cs`
- Uses Windows DPAPI for encryption
- Passwords encrypted before saving to config
- Decrypted on auto-login

**Methods**:
```csharp
public string Protect(string plainText)
public string Unprotect(string encryptedText)
```

---

## Known Issues Fixed

1. **Connection Refused** (Fixed)
   - **Cause**: Wrong server address `208.76.170.59:2240`
   - **Solution**: Changed to `server.slsknet.org:2242`

2. **Missing Config Save** (Fixed)
   - **Cause**: LoginAsync didn't call `_configManager.Save()`
   - **Solution**: Added save call after successful connection

3. **Multiple Hardcoded Fallbacks** (Fixed)
   - **Locations**: AppConfig.cs, ConfigManager.cs (2 places)
   - **Solution**: Unified all defaults to match Soulseek.NET

4. **No State Monitoring** (Fixed)
   - **Cause**: Not listening to SoulseekClient.StateChanged
   - **Solution**: Added event subscription in SoulseekAdapter

5. **No Auto-Login** (Fixed)
   - **Cause**: Credentials saved but never used
   - **Solution**: Added AutoLoginAsync in OnViewLoaded

---

## Testing Checklist

- [x] Manual login works
- [x] "Remember Me" persists credentials
- [x] Auto-login works on restart
- [x] Login overlay shows/hides correctly
- [x] Specific error messages display
- [x] State changes update UI
- [ ] Reconnection after network drop (deferred to Phase 10)

---

## Dependencies

- **Soulseek.NET**: Core connection library
- **System.Reactive**: EventBus pattern
- **Microsoft.Extensions.Configuration**: INI file parsing
- **Windows DPAPI**: Password encryption

---

## Configuration File Format

**Location**: `%APPDATA%\SLSKDONET\config.ini`

**Example**:
```ini
[Soulseek]
Server = server.slsknet.org
Port = 2242
Username = your-username
Password = <encrypted-base64-string>
ListenPort = 49998
UseUPnP = False
ConnectTimeout = 60000
SearchTimeout = 6000
RememberPassword = True

[Spotify]
SpotifyClientId = 
SpotifyClientSecret = 
SpotifyUsePublicOnly = True

[Download]
Directory = C:\Users\...\Downloads
MaxConcurrent = 2
NameFormat = {artist} - {title}
CheckForDuplicates = True
...
```

---

## Future Improvements (Phase 10)

- [ ] Automatic reconnection with exponential backoff
- [ ] Connection health monitoring (ping/pong)
- [ ] Toast notifications for connection events
- [ ] Session persistence across network changes
- [ ] Multiple account support

---

## Emergency Rollback

If login breaks, check these files in order:

1. `Configuration/ConfigManager.cs` lines 59 & 82
2. `Configuration/AppConfig.cs` lines 22-24
3. Delete `%APPDATA%\SLSKDONET\config.ini` to force defaults
4. Check `Services/SoulseekAdapter.cs` line 44 fallback

**Critical Values**:
- Server: `server.slsknet.org` (NOT an IP address)
- Port: `2242` (NOT 2240)

---

*Backup created: 2025-12-12T01:44:00Z*
*All login functionality verified working*
