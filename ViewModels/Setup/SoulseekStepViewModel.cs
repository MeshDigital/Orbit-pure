using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

public sealed class WelcomeStepViewModel : SetupStepViewModel
{
    public override string Title => "Welcome to ORBIT";
    public override string ShortTitle => "Welcome";
    public override string Subtitle => "A prep assistant for DJs: find, organise, analyse and prepare tracks.";
    public override bool AppearsInSummary => false;
}

/// <summary>Step 2: Soulseek account. "Test connection" signs in for real through the app's connection lifecycle.</summary>
public sealed class SoulseekStepViewModel : SetupStepViewModel, IDisposable
{
    private readonly ISoulseekSetupGateway _gateway;
    private readonly ILogger _logger;
    private readonly AsyncRelayCommand _testCommand;
    private CancellationTokenSource? _testCts;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _rememberAndAutoConnect = true;
    private bool _isTesting;
    private SoulseekLoginResult? _lastResult;
    private string? _verifiedUsername;
    private string? _verifiedPassword;
    private string? _savedUsername;

    public SoulseekStepViewModel(ISoulseekSetupGateway gateway, ILogger logger)
    {
        _gateway = gateway;
        _logger = logger;
        _testCommand = new AsyncRelayCommand(() => TestAsync(), () => !IsTesting);
        TestConnectionCommand = _testCommand;
    }

    public override string Title => "Sign in to Soulseek";
    public override string ShortTitle => "Soulseek";
    public override string Subtitle => "ORBIT searches and downloads through the Soulseek network.";
    public override bool IsRecommended => true;
    public override bool CanSkip => true;
    public override string SkipNote => "Search and downloads need a Soulseek account. You can sign in later from the status dot in the top bar.";

    public ICommand TestConnectionCommand { get; }

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value ?? string.Empty))
                OnInputChanged();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value ?? string.Empty))
                OnInputChanged();
        }
    }

    public bool RememberAndAutoConnect
    {
        get => _rememberAndAutoConnect;
        set => SetProperty(ref _rememberAndAutoConnect, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
                _testCommand.RaiseCanExecuteChanged();
        }
    }

    public SoulseekLoginResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(ResultIsSuccess));
                OnPropertyChanged(nameof(ResultIsError));
                OnPropertyChanged(nameof(ResultTitle));
                OnPropertyChanged(nameof(ResultMessage));
            }
        }
    }

    public bool HasResult => LastResult != null;
    public bool ResultIsSuccess => LastResult?.IsSuccess == true;
    public bool ResultIsError => LastResult is { IsSuccess: false };
    public string ResultTitle => LastResult?.Title ?? string.Empty;
    public string ResultMessage => LastResult?.Message ?? string.Empty;

    /// <summary>ORBIT is already signed in with the saved account (re-running setup).</summary>
    public bool IsAlreadyConnected { get; private set; }

    /// <summary>A second ORBIT on a preview profile: signing in with the real account would kick the real session.</summary>
    public bool IsPreviewProfile => SLSKDONET.Data.OrbitPaths.IsProfileOverridden;

    public override Task InitializeAsync()
    {
        var current = _gateway.GetCurrent();
        _savedUsername = string.IsNullOrWhiteSpace(current.Username) ? null : current.Username;
        _username = current.Username;
        _rememberAndAutoConnect = current.RememberAndAutoConnect;
        IsAlreadyConnected = current.IsConnected && _savedUsername != null;
        if (IsAlreadyConnected)
        {
            _verifiedUsername = _savedUsername;
            LastResult = SoulseekLoginResultMapper.AlreadyConnected(_savedUsername!);
        }
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(RememberAndAutoConnect));
        OnPropertyChanged(nameof(IsAlreadyConnected));
        return Task.CompletedTask;
    }

    public async Task<SoulseekLoginResult> TestAsync()
    {
        ClearError();
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        LastResult = null;
        try
        {
            var result = await _gateway.TestLoginAsync(Username, Password, _testCts.Token);
            LastResult = result;
            if (result.IsSuccess)
            {
                _verifiedUsername = Username.Trim();
                _verifiedPassword = Password;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            LastResult = null;
            return new SoulseekLoginResult(SoulseekLoginOutcome.Failed, "Cancelled", "The connection test was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: Soulseek connection test failed unexpectedly");
            var failed = SoulseekLoginResultMapper.FromException(ex);
            LastResult = failed;
            return failed;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>The account is confirmed: tested now, or unchanged from the connected saved one.</summary>
    private bool IsVerified =>
        _verifiedUsername != null
        && string.Equals(_verifiedUsername, Username.Trim(), StringComparison.Ordinal)
        && (_verifiedPassword == null || _verifiedPassword == Password || Password.Length == 0);

    public override async Task<bool> CommitAsync()
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Enter a username and password, or skip this step.";
            return false;
        }

        // Next without a successful test runs the test first: nothing is saved for an account
        // that didn't sign in.
        if (!IsVerified)
        {
            var result = await TestAsync();
            if (!result.IsSuccess) return false;
        }

        try
        {
            var password = Password.Length > 0 ? Password : _verifiedPassword ?? string.Empty;
            await _gateway.SaveAccountAsync(new SoulseekAccountChoice(Username.Trim(), password, RememberAndAutoConnect));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: saving the Soulseek account failed");
            ErrorMessage = "Signed in, but the account couldn't be saved: " + ex.Message;
            return false;
        }
    }

    public override string SummaryTitle => "Soulseek account";

    public override string SummaryDetail => Status switch
    {
        SetupStepStatus.Configured => $"Signed in as {Username.Trim()}" + (RememberAndAutoConnect ? " · connects on startup" : string.Empty),
        SetupStepStatus.Skipped => "Skipped · search and downloads need an account",
        _ => _savedUsername != null ? $"Unchanged ({_savedUsername})" : "Not set up",
    };

    private void OnInputChanged()
    {
        // A different name or password needs a fresh test; keep the old result visible only while it still applies.
        if (LastResult != null && !IsVerified && !IsTesting)
            LastResult = null;
        ClearError();
    }

    public void Dispose()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
    }
}
