using System;
using System.Text.RegularExpressions;
using System.Windows.Input;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>
/// Reusable "Use your own Spotify app" setup ViewModel shared by Settings → Connection and the
/// Setup Wizard Connections step (Epic #508 / #514 SP3). No duplicated copy or validation logic.
/// </summary>
public sealed class OwnSpotifyAppViewModel : SetupObservable
{
    private static readonly Regex ClientIdRegex = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    private readonly Func<string> _readClientId;
    private readonly Action<string> _writeClientId;
    private readonly Action _openDashboard;
    private readonly Action _copyRedirectUri;

    private string _clientIdInput;
    private string? _validationError;

    public const string PremiumRequirementText =
        "The Spotify account that creates the app needs an active Premium subscription (Spotify rule since Feb 2026). People who only sign in to it don't need Premium.";

    public OwnSpotifyAppViewModel(
        Func<string> readClientId,
        Action<string> writeClientId,
        string redirectUri,
        Action openDashboard,
        Action copyRedirectUri)
    {
        _readClientId = readClientId;
        _writeClientId = writeClientId;
        RedirectUri = redirectUri;
        _openDashboard = openDashboard;
        _copyRedirectUri = copyRedirectUri;

        _clientIdInput = IsBuiltIn(readClientId()) ? string.Empty : (readClientId() ?? string.Empty);

        OpenDashboardCommand = new RelayCommand(_openDashboard);
        CopyRedirectUriCommand = new RelayCommand(_copyRedirectUri);
        UseBuiltInTesterAppCommand = new RelayCommand(EnableBuiltInTesterApp);
        ClearClientIdCommand = new RelayCommand(ClearClientId);
    }

    public string RedirectUri { get; }
    public string PremiumRequirementNote => PremiumRequirementText;

    public ICommand OpenDashboardCommand { get; }
    public ICommand CopyRedirectUriCommand { get; }
    public ICommand UseBuiltInTesterAppCommand { get; }
    public ICommand ClearClientIdCommand { get; }

    public string ClientIdInput
    {
        get => _clientIdInput;
        set
        {
            var next = value ?? string.Empty;
            if (!SetProperty(ref _clientIdInput, next)) return;

            var trimmed = next.Trim();
            var error = ValidateClientId(trimmed);
            ValidationError = error;
            if (error == null)
            {
                _writeClientId(trimmed);
                RaiseModeChanged();
            }
        }
    }

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
                OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    public SpotifyClientIdKind CurrentKind => SpotifyClientIdMigrationService.Classify(_readClientId());
    public bool IsNoneConfigured => CurrentKind == SpotifyClientIdKind.None;
    public bool IsUsingOwnApp => CurrentKind == SpotifyClientIdKind.CustomOwnApp;
    public bool IsUsingTesterApp => CurrentKind == SpotifyClientIdKind.BuiltInTester;

    public string ModeLabel => CurrentKind switch
    {
        SpotifyClientIdKind.CustomOwnApp => "Using your own Spotify app",
        SpotifyClientIdKind.BuiltInTester => "Using ORBIT tester app",
        _ => "No Spotify app configured (public links work without sign-in)"
    };

    /// <summary>
    /// Validates a Spotify Client ID: empty is valid (clears configuration); otherwise must be
    /// exactly 32 hexadecimal characters.
    /// </summary>
    public static string? ValidateClientId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ClientIdRegex.IsMatch(value.Trim())
            ? null
            : "A Spotify Client ID is 32 hexadecimal characters (0–9, a–f). Copy it from your app's Settings page on developer.spotify.com.";
    }

    public void RefreshFromConfig()
    {
        var raw = _readClientId();
        _clientIdInput = IsBuiltIn(raw) ? string.Empty : (raw ?? string.Empty);
        ValidationError = null;
        OnPropertyChanged(nameof(ClientIdInput));
        RaiseModeChanged();
    }

    private void EnableBuiltInTesterApp()
    {
        ValidationError = null;
        _clientIdInput = string.Empty;
        OnPropertyChanged(nameof(ClientIdInput));
        _writeClientId(SpotifyAuthService.DefaultClientId);
        RaiseModeChanged();
    }

    private void ClearClientId()
    {
        ValidationError = null;
        _clientIdInput = string.Empty;
        OnPropertyChanged(nameof(ClientIdInput));
        _writeClientId(string.Empty);
        RaiseModeChanged();
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(CurrentKind));
        OnPropertyChanged(nameof(IsNoneConfigured));
        OnPropertyChanged(nameof(IsUsingOwnApp));
        OnPropertyChanged(nameof(IsUsingTesterApp));
        OnPropertyChanged(nameof(ModeLabel));
    }

    private static bool IsBuiltIn(string? id) =>
        SpotifyClientIdMigrationService.Classify(id) == SpotifyClientIdKind.BuiltInTester;
}
