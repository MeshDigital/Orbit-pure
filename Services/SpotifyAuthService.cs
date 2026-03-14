using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Utils;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace SLSKDONET.Services;

/// <summary>
/// Manages Spotify OAuth authentication using PKCE flow.
/// Handles authorization, token exchange, refresh, and secure storage.
/// </summary>
public class SpotifyAuthService
{
    private readonly ILogger<SpotifyAuthService> _logger;
    private readonly AppConfig _config;
    // private readonly LocalHttpServer _httpServer; // Retired
    private readonly ISecureTokenStorage _tokenStorage;

    private string? _currentCodeVerifier;
    private SpotifyClient? _authenticatedClient;
    private PKCETokenResponse? _currentTokenResponse;
    private DateTime _tokenExpiresAt;
    private DateTime _lastTokenRefreshTime = DateTime.MinValue; // Track last refresh for throttling
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private Task? _refreshTask;

    public event EventHandler<bool>? AuthenticationChanged;

    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (_isAuthenticated != value)
            {
                _isAuthenticated = value;
                AuthenticationChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Checks if a TCP port is available for listening.
    /// </summary>
    private bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    public SpotifyAuthService(
        ILogger<SpotifyAuthService> logger,
        AppConfig config,
        ISecureTokenStorage tokenStorage)
    {
        _logger = logger;
        _config = config;
        _tokenStorage = tokenStorage;
        
        // Initial check is now handled by consumers (e.g. SettingsViewModel) explicitly
        // to avoid race conditions with file storage during startup.
    }

    /// <summary>
    /// Proactively verifies Spotify connection on app startup.
    /// This method VALIDATES the stored token by making an actual API call,
    /// rather than just checking if a token file exists.
    /// 
    /// This is the fix for the "zombie token" bug where the app would show
    /// "Connected" after restart but the token was actually invalid.
    /// </summary>
    public async Task VerifyConnectionAsync()
    {
        try
        {
            _logger.LogInformation("Starting proactive Spotify connection verification...");

            // Load token from secure storage
            var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogInformation("No stored refresh token found - user is not authenticated");
                IsAuthenticated = false;
                return;
            }

            _logger.LogInformation("Stored refresh token found, attempting to validate...");

            // Try to refresh the token to verify it's still valid with Spotify
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // Increased timeout for robustness
            var verifyTask = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Attempting to refresh access token for verification...");
                    await RefreshAccessTokenAsync();
                    
                    // If refresh succeeded, we're authenticated
                    IsAuthenticated = _authenticatedClient != null;
                    
                    if (IsAuthenticated)
                    {
                        var user = await _authenticatedClient!.UserProfile.Current();
                        _logger.LogInformation("✓ Token verification succeeded - Authenticated as: {User}", user.DisplayName);
                    }
                    else
                    {
                        _logger.LogWarning("✗ Token verification failed - RefreshAccessTokenAsync completed but client is null");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "✗ Token verification failed during refresh - clearing invalid token. Message: {Msg}", ex.Message);
                    IsAuthenticated = false; 
                    // Don't re-throw; we want to degrade gracefully
                }
            }, cts.Token);

            await verifyTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Token verification timed out after 5 seconds - assuming not authenticated");
            IsAuthenticated = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Spotify connection verification");
            IsAuthenticated = false;
        }
    }

    /// <summary>
    /// Checks if the user is currently authenticated with Spotify.
    /// Does not trigger a refresh if the current token is still valid.
    /// </summary>
    public async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var checkTask = Task.Run(async () =>
            {
                // 1. Check if we have a valid client and non-expired token
                if (_authenticatedClient != null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-1))
                {
                    return true;
                }

                // 2. Check if we have a stored refresh token
                var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
                if (string.IsNullOrEmpty(refreshToken))
                    return false;

                // 3. Try to refresh the token to verify it's still valid
                try
                {
                    await RefreshAccessTokenAsync();
                    return _authenticatedClient != null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IsAuthenticatedAsync check failed during refresh");
                    return false;
                }
            }, cts.Token);

            return await checkTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IsAuthenticatedAsync timed out after 3 seconds");
            return false;
        }
    }

    /// <summary>
    /// Starts the OAuth authorization flow.
    /// Opens the browser for user consent and waits for the callback.
    /// Uses EmbedIOAuthServer for robust local handling.
    /// </summary>
    public async Task<bool> StartAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.SpotifyClientId))
            throw new InvalidOperationException("Spotify Client ID must be configured in Settings");

        // Parse configured port/URI
        var uri = new Uri(_config.SpotifyRedirectUri);
        var port = uri.Port;
        
        // Check if port is available, try fallbacks if not
        var availablePort = port;
        if (!IsPortAvailable(port))
        {
            _logger.LogWarning("Port {Port} is in use (likely stale auth session). Trying fallback ports...", port);
            
            // Try ports 5001-5005 as fallbacks
            for (int fallbackPort = port + 1; fallbackPort <= port + 5; fallbackPort++)
            {
                if (IsPortAvailable(fallbackPort))
                {
                    availablePort = fallbackPort;
                    _logger.LogInformation("Using fallback port {Port}", availablePort);
                    break;
                }
            }
            
            if (availablePort == port)
            {
                throw new InvalidOperationException($"Port {port} is in use and no fallback ports (5001-5005) are available. Please close any stale auth browser tabs and try again.");
            }
        }
        
        // Update URI with available port
        uri = new UriBuilder(uri) { Port = availablePort }.Uri;
        
        // Setup robust EmbedIO server
        var server = new EmbedIOAuthServer(uri, availablePort);
        
        try
        {
            await server.Start();
            _logger.LogInformation("EmbedIO Auth Server started on port {Port}", availablePort);

            // Generate PKCE
            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            _currentCodeVerifier = verifier;

            // Bridge Event-Driven Server to Linear Task
            var tcs = new TaskCompletionSource<string>();
            
            // Handle successful code reception
            server.AuthorizationCodeReceived += async (sender, response) =>
            {
                await server.Stop();
                tcs.TrySetResult(response.Code);
            };

            // Handle errors
            server.ErrorReceived += async (sender, error, description) =>
            {
                await server.Stop();
                tcs.TrySetException(new Exception($"OAuth Error: {error} - {description}"));
            };

            // Build Auth Request
            var loginRequest = new LoginRequest(
                server.BaseUri, 
                _config.SpotifyClientId, 
                LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = challenge,
                Scope = new[]
                {
                    Scopes.UserReadPrivate,
                    Scopes.UserReadEmail,
                    Scopes.PlaylistReadPrivate,
                    Scopes.PlaylistReadCollaborative,
                    Scopes.UserLibraryRead,
                    Scopes.UserTopRead,
                    Scopes.UserFollowRead
                }
            };

            var authUrl = loginRequest.ToUri();
            _logger.LogInformation("Opening browser: {Url}", authUrl);
            OpenBrowser(authUrl.ToString());

            // Wait for callback with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            // Race: Task vs Cancellation
            using var reg = timeoutCts.Token.Register(async () => 
            {
                await server.Stop();
                tcs.TrySetCanceled();
            });

            var code = await tcs.Task;
            
            // Exchange
            if (!string.IsNullOrEmpty(code))
            {
                 await ExchangeCodeForTokensAsync(code);
                 return true;
            }
            
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Spotify authorization timed out or cancelled");
            throw new TimeoutException("Spotify authorization timed out. Please try again.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Spotify authorization failed");
             throw; 
        }
        finally
        {
             // Double check cleanup
             await server.Stop();
             server.Dispose();
        }
    }

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// </summary>
    private async Task ExchangeCodeForTokensAsync(string authCode)
    {
        if (string.IsNullOrEmpty(_currentCodeVerifier))
            throw new InvalidOperationException("Code verifier not set. Call StartAuthorizationAsync first.");

        if (string.IsNullOrEmpty(_config.SpotifyClientId))
            throw new InvalidOperationException("Spotify Client ID is not configured.");

        var tokenRequest = new PKCETokenRequest(_config.SpotifyClientId, authCode, new Uri(_config.SpotifyRedirectUri), _currentCodeVerifier);

        var config = SpotifyClientConfig.CreateDefault();
        var oauthClient = new OAuthClient(config);

        // Add 30-second timeout to token exchange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tokenResponse = await oauthClient.RequestToken(tokenRequest).WaitAsync(cts.Token);

        _currentTokenResponse = tokenResponse;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        // Store refresh token securely
        if (_config.SpotifyRememberAuth && !string.IsNullOrEmpty(tokenResponse.RefreshToken))
        {
            await _tokenStorage.SaveRefreshTokenAsync(tokenResponse.RefreshToken);
            _logger.LogInformation("Refresh token stored securely");
        }

        // Save the successful callback port to config for deterministic RedirectUri
        // This ensures future auth attempts use the same port that worked
        _config.SpotifyCallbackPort = new Uri(_config.SpotifyRedirectUri).Port;
        _logger.LogInformation("Saved Spotify callback port {Port} for future auth attempts", _config.SpotifyCallbackPort);

        // Create authenticated client with PKCEAuthenticator for immediate auto-refresh support
        // This ensures the very first client instance can handle long-running imports/enrichment
        var authenticator = new PKCEAuthenticator(_config.SpotifyClientId, tokenResponse);
        var configWithAuth = SpotifyClientConfig
            .CreateDefault()
            .WithAuthenticator(authenticator)
            .WithRetryHandler(new SimpleRetryHandler() 
            { 
                RetryAfter = TimeSpan.FromSeconds(1), 
                RetryTimes = 3
            });
            
        _authenticatedClient = new SpotifyClient(configWithAuth);
        IsAuthenticated = true;

        _logger.LogInformation("Successfully exchanged authorization code for tokens and initialized client with PKCE auto-refresh");
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token.
    /// Thread-safe: concurrent calls will wait for the same refresh operation.
    /// </summary>
    public async Task RefreshAccessTokenAsync(bool force = false)
    {
        Task? taskToWait = null;

        if (!await _authLock.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            _logger.LogError("Deadlock detected: Timed out waiting for AuthLock.");
            throw new TimeoutException("Timed out waiting for Spotify Authentication lock.");
        }
        try
        {
            // If a refresh is already in progress, capture the task and wait for it outside the lock
            if (_refreshTask != null)
            {
                taskToWait = _refreshTask;
            }
            else
            {
                // Start a new refresh task
                _refreshTask = RefreshAccessTokenInternalAsync(force);
                taskToWait = _refreshTask;
            }
        }
        finally
        {
            _authLock.Release();
        }

        // Wait for the task outside the lock to allow other callers to check status
        if (taskToWait != null)
        {
            try 
            {
                await taskToWait;
            }
            finally 
            {
                // Only the first caller clears the task, but we use another lock or just check
                await _authLock.WaitAsync();
                if (_refreshTask == taskToWait)
                {
                    _refreshTask = null;
                }
                _authLock.Release();
            }
        }
    }

    private async Task RefreshAccessTokenInternalAsync(bool force = false)
    {
        // Throttle: Don't refresh if we just refreshed less than 5 minutes ago
        // This prevents "double refresh" glitch when both InitializeAsync and VerifyConnectionAsync trigger refreshes
        const int throttleMinutes = 5;
        var timeSinceLastRefresh = DateTime.UtcNow - _lastTokenRefreshTime;
        if (!force && timeSinceLastRefresh.TotalMinutes < throttleMinutes && _currentTokenResponse != null)
        {
            _logger.LogInformation("Token refresh throttled: last refresh was {Seconds} seconds ago", timeSinceLastRefresh.TotalSeconds);
            return;
        }

        var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("No refresh token available");
            IsAuthenticated = false;
            throw new InvalidOperationException("No refresh token available. Please sign in again.");
        }

        try
        {
            if (string.IsNullOrEmpty(_config.SpotifyClientId))
                throw new InvalidOperationException("Spotify Client ID is not configured.");

            var tokenRequest = new PKCETokenRefreshRequest(_config.SpotifyClientId, refreshToken);

            var oauthConfig = SpotifyClientConfig.CreateDefault();
            var oauthClient = new OAuthClient(oauthConfig);

            // Add 30-second timeout to token refresh
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tokenResponse = await oauthClient.RequestToken(tokenRequest).WaitAsync(cts.Token);

            _currentTokenResponse = tokenResponse;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            _lastTokenRefreshTime = DateTime.UtcNow; // Record refresh time for throttling

            // Update stored refresh token if a new one was provided
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                await _tokenStorage.SaveRefreshTokenAsync(tokenResponse.RefreshToken);
            }

            // Create authenticator for automatic token refresh
            var authenticator = new PKCEAuthenticator(_config.SpotifyClientId, tokenResponse);
            
            // Create client config with authenticator and retry handler
            var config = SpotifyClientConfig
                .CreateDefault()
                .WithAuthenticator(authenticator)
                .WithRetryHandler(new SimpleRetryHandler() 
                { 
                    RetryAfter = TimeSpan.FromSeconds(1), 
                    RetryTimes = 3
                }); // Automatic 429 handling
            
            _authenticatedClient = new SpotifyClient(config);
            IsAuthenticated = true;

            _logger.LogInformation("Successfully refreshed access token");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh access token. Error: {Message}", ex.Message);
            IsAuthenticated = false;
            
            // Explicitly clear the dead token so we don't loop forever trying to refresh it
            // ONLY if it's an OAuth error (e.g. invalid_grant)
            if (ex.Message.Contains("invalid_grant") || ex.Message.Contains("unauthorized"))
            {
                 _logger.LogWarning("Refresh token is invalid (revoked or expired). Clearing stored credentials.");
                 await _tokenStorage.DeleteRefreshTokenAsync();
            }

            throw new InvalidOperationException("Failed to refresh access token. Please sign in again.", ex);
        }
    }

    /// <summary>
    /// Gets an authenticated Spotify client, refreshing the token if necessary.
    /// </summary>
    /// <returns>Authenticated SpotifyClient</returns>
    public async Task<SpotifyClient> GetAuthenticatedClientAsync()
    {
        // Check if we need to refresh the token
        if (_authenticatedClient == null || DateTime.UtcNow >= _tokenExpiresAt.AddMinutes(-5))
        {
            _logger.LogInformation("Access token expired or about to expire, refreshing...");
            await RefreshAccessTokenAsync();
        }

        if (_authenticatedClient == null)
            throw new InvalidOperationException("Not authenticated. Please sign in first.");

        return _authenticatedClient;
    }

    /// <summary>
    /// Gets the valid access token string directly.
    /// Needed for SpotifyBatchClient.
    /// </summary>
    public async Task<string> GetAccessTokenAsync()
    {
         // Ensure we have a valid client first (triggers refresh if needed)
         await GetAuthenticatedClientAsync();
         
         if (_currentTokenResponse?.AccessToken == null)
             throw new InvalidOperationException("Not authenticated");
             
         return _currentTokenResponse.AccessToken;
    }

    /// <summary>
    /// Signs out the user and clears stored tokens.
    /// </summary>
    public async Task SignOutAsync()
    {
        await _tokenStorage.DeleteRefreshTokenAsync();
        _authenticatedClient = null;
        _currentTokenResponse = null;
        _currentCodeVerifier = null;
        IsAuthenticated = false;

        _logger.LogInformation("User signed out, tokens cleared");
    }

    /// <summary>
    /// Tests the connection by attempting to fetch the current user profile.
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
             var client = await GetAuthenticatedClientAsync();
             var user = await client.UserProfile.Current();
             return user != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Gets the current user's profile information.
    /// </summary>
    public async Task<PrivateUser> GetCurrentUserAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var userTask = Task.Run(async () =>
        {
            var client = await GetAuthenticatedClientAsync();
            return await client.UserProfile.Current();
        }, cts.Token);

        return await userTask;
    }

    /// <summary>
    /// Opens the default browser to the specified URL.
    /// </summary>
    private void OpenBrowser(string url)
    {
        try
        {
            // Cross-platform browser opening
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                _logger.LogWarning("Unsupported platform for opening browser");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open browser with default method. Attempting fallback...");
            
            try
            {
                // Fallback for Windows: explorer "url"
                if (OperatingSystem.IsWindows())
                {
                     // Escape the URL for cmd/explorer
                     url = url.Replace("&", "^&"); 
                     Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
            }
            catch (Exception fallbackEx)
            {
                 _logger.LogError(fallbackEx, "Fallback browser launch also failed");
                 throw new InvalidOperationException($"Failed to open browser. Please manually navigate to: {url}", ex);
            }
        }
    }

    /// <summary>
    /// Clears cached Spotify credentials (diagnostic method for testing auth issues).
    /// This is useful to confirm if the app has a "poisoned" token cache.
    /// </summary>
    public async Task ClearCachedCredentialsAsync()
    {
        try
        {
            _logger.LogInformation("Clearing cached Spotify credentials...");

            // Delete the stored refresh token
            await _tokenStorage.DeleteRefreshTokenAsync();

            // Clear in-memory auth state
            _authenticatedClient = null;
            _currentTokenResponse = null;
            _currentCodeVerifier = null;
            IsAuthenticated = false;
            _lastTokenRefreshTime = DateTime.MinValue;

            _logger.LogInformation("✓ Spotify credentials cleared. User must re-authenticate.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear Spotify credentials");
            throw;
        }
    }
}
