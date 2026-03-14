# Spotify OAuth 2.0 (PKCE) Integration

This document details the implementation of Spotify Authentication in QMUSICSLSK using the **Proof Key for Code Exchange (PKCE)** flow. This method is secure for desktop applications as it does not require storing a static Client Secret in the application binary (though we currently support both for flexibility).

## Architecture

The authentication flow is orchestrated by `SpotifyAuthService` and `LocalHttpServer`.

### Component Interaction

1.  **Trigger**: User clicks "Connect Spotify" in Settings.
2.  **PKCE Generation**: `PKCEHelper` generates a random `CodeVerifier` and a hashed `CodeChallenge`.
3.  **Browser Launch**: A system browser is opened to the Spotify Authorization URL with parameters:
    *   `client_id`: Configured in `AppConfig`
    *   `response_type`: `code`
    *   `redirect_uri`: `http://127.0.0.1:5000/callback`
    *   `code_challenge`: The generated challenge
    *   `code_challenge_method`: `S256`
    *   `scope`: `user-read-private`, `playlist-read-private`, etc.
4.  **Local Listener**: `LocalHttpServer` starts an `HttpListener` on **both**:
    *   `http://127.0.0.1:5000/callback/`
    *   `http://localhost:5000/callback/`
    *   *Note: Listening on both ensures compatibility with different browser behaviors and OS networking quirks.*
5.  **Callback**: After user approval, Spotify redirects to the `redirect_uri` with a `?code=AUTHORIZATION_CODE`.
6.  **Code Exchange**: `SpotifyAuthService` captures the code and exchanges it for an **Access Token** and **Refresh Token** using the `CodeVerifier`.
7.  **Storage**: Tokens are securely stored using OS-specific keychains (Dpapi on Windows) via `ISecureTokenStorage`.

## Configuration

To run the Spotify integration, you need a Client ID from the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).

### AppConfig Settings

*   **SpotifyClientId**: Your application's Client ID.
*   **SpotifyRedirectUri**: Must match exactly what is allowed in the Spotify Dashboard. Recommended: `http://127.0.0.1:5000/callback`.
*   **SpotifyCallbackPort**: Default `5000`.

### Developer Dashboard Setup

1.  Create an App in Spotify Developer Dashboard.
2.  Go to **Edit Settings**.
3.  Add `http://127.0.0.1:5000/callback` AND `http://localhost:5000/callback` to **Redirect URIs**.
4.  Save config.

## Troubleshooting

### "Page Not Found" Implementation Detail

We encountered an issue where some browsers/OS combinations favor `localhost` vs `127.0.0.1`.
The fix implemented in `LocalHttpServer.cs`:

```csharp
// 1. Ensure the prefix ends with a slash (required by HttpListener)
var listenerUri = redirectUri;
if (!listenerUri.EndsWith("/")) listenerUri += "/";

// 2. Add both localhost and 127.0.0.1 to be safe
_listener.Prefixes.Add(listenerUri);
if (listenerUri.Contains("localhost"))
    _listener.Prefixes.Add(listenerUri.Replace("localhost", "127.0.0.1"));
else
    _listener.Prefixes.Add(listenerUri.Replace("127.0.0.1", "localhost"));
```

This ensures the listener catches the callback regardless of name resolution.

### Access Denied (503 / 403)

If the app crashes or fails to bind the port, you may need to reserve the URL ACL:

```cmd
netsh http add urlacl url=http://127.0.0.1:5000/callback/ user=Everyone
```

## Security Considerations

*   **PKCE**: We use PKCE to prevent authorization code interception attacks.
*   **Token Storage**: Refresh tokens are never stored in plain text. We use `WindowsTokenStorage` (using `System.Security.Cryptography.ProtectedData`) to encrypt tokens at rest.
