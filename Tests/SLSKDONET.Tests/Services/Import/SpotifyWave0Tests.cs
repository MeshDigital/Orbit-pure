using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Services.Setup;
using SLSKDONET.ViewModels.Setup;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Regression tests for Epic #508 Wave 0 (#509 SP0a, #510 SP0b, #511 SP0c).
/// </summary>
public sealed class SpotifyWave0Tests
{
    private static APIException ApiError(HttpStatusCode status, string body = "") =>
        new(new Response(new Dictionary<string, string>())
        {
            StatusCode = status,
            Body = body
        });

    [Fact]
    public void Connected_403_AndEmptyScraper_DoesNotOfferSignInOrRetry_AndNeverSaysFollowsIt()
    {
        var ex = ApiError(HttpStatusCode.Forbidden, "Forbidden");
        var translated = SpotifyImportErrors.Translate(
            ex,
            isAuthenticated: true,
            scraperOutcome: SpotifyScraperOutcome.Empty,
            input: "https://open.spotify.com/playlist/abc123");

        Assert.False(translated.RequiresSignIn);
        Assert.False(translated.CanRetry);
        Assert.DoesNotContain("follows it", translated.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("own", translated.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unauthorized_401_OffersSignIn_AndHidesRetry()
    {
        var ex = new APIUnauthorizedException(new Response(new Dictionary<string, string>())
        {
            StatusCode = HttpStatusCode.Unauthorized
        });

        var translated = SpotifyImportErrors.Translate(ex, isAuthenticated: true);

        Assert.True(translated.RequiresSignIn);
        Assert.False(translated.CanRetry);
    }

    [Fact]
    public void PersonalisedMixId_UsesMixCopy_WithoutSignInOrRetry()
    {
        var ex = ApiError(HttpStatusCode.NotFound, "Not found");
        var translated = SpotifyImportErrors.Translate(
            ex,
            isAuthenticated: true,
            scraperOutcome: SpotifyScraperOutcome.PersonalisedMix,
            input: "https://open.spotify.com/playlist/37i9dQZF1E35abcDEF");

        Assert.False(translated.RequiresSignIn);
        Assert.False(translated.CanRetry);
        Assert.Contains("Daily Mix", translated.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkError_KeepsRetryVisible()
    {
        var translated = SpotifyImportErrors.Translate(new HttpRequestException("offline"));

        Assert.False(translated.RequiresSignIn);
        Assert.True(translated.CanRetry);
    }

    [Fact]
    public void EnrichmentCircuitBreaker_Ignores403_AndOnlyTripsOn429()
    {
        SpotifyEnrichmentService.ResetCircuitBreaker();
        try
        {
            SpotifyEnrichmentService.RecordApiError(HttpStatusCode.Forbidden);
            Assert.False(SpotifyEnrichmentService.IsServiceDegraded);

            SpotifyEnrichmentService.RecordApiError(HttpStatusCode.NotFound);
            Assert.False(SpotifyEnrichmentService.IsServiceDegraded);

            SpotifyEnrichmentService.RecordApiError(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(5));
            Assert.True(SpotifyEnrichmentService.IsServiceDegraded);
        }
        finally
        {
            SpotifyEnrichmentService.ResetCircuitBreaker();
        }
    }

    [Fact]
    public async Task ConnectionsStep_NotAllowlisted_ShowsOwnAppGuidance_WithoutTryAgain()
    {
        var gateway = new StubSpotifyGateway(SpotifyConnectResult.NotAllowlisted());
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var vm = new ConnectionsStepViewModel(gateway, NullLogger.Instance)
        {
            UtcNow = () => now
        };
        vm.OnShown();
        now = now.AddSeconds(2);

        await vm.ConnectAsync();

        Assert.False(vm.IsSpotifyConnected);
        Assert.NotNull(vm.StatusMessage);
        Assert.DoesNotContain("Try again", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public playlists", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Premium", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionsStep_HandlesConnected_Cancelled_AndFailedOutcomes()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

        var connectedVm = new ConnectionsStepViewModel(new StubSpotifyGateway(SpotifyConnectResult.Succeeded()), NullLogger.Instance)
        {
            UtcNow = () => now
        };
        connectedVm.OnShown();
        now = now.AddSeconds(2);
        await connectedVm.ConnectAsync();
        Assert.True(connectedVm.IsSpotifyConnected);
        Assert.Null(connectedVm.StatusMessage);

        var cancelledVm = new ConnectionsStepViewModel(new StubSpotifyGateway(SpotifyConnectResult.Cancelled()), NullLogger.Instance)
        {
            UtcNow = () => now
        };
        cancelledVm.OnShown();
        now = now.AddSeconds(2);
        await cancelledVm.ConnectAsync();
        Assert.False(cancelledVm.IsSpotifyConnected);
        Assert.Contains("cancelled", cancelledVm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        var failedVm = new ConnectionsStepViewModel(new StubSpotifyGateway(SpotifyConnectResult.Failed("Port conflict")), NullLogger.Instance)
        {
            UtcNow = () => now
        };
        failedVm.OnShown();
        now = now.AddSeconds(2);
        await failedVm.ConnectAsync();
        Assert.False(failedVm.IsSpotifyConnected);
        Assert.Contains("Port conflict", failedVm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HomePage_HasNoDeadSpotifyRecommendationsPanel()
    {
        var root = FindRepoRoot();
        var homeXaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "HomePage.axaml"));
        Assert.DoesNotContain("SpotifyRecommendations", homeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsForYouTabActive", homeXaml, StringComparison.Ordinal);

        var wizardXaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "Setup", "SetupWizardView.axaml"));
        Assert.Contains("public Spotify playlist links", wizardXaml, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private sealed class StubSpotifyGateway : ISpotifySetupGateway
    {
        private readonly SpotifyConnectResult _result;

        public StubSpotifyGateway(SpotifyConnectResult result) => _result = result;

        public bool IsConnected { get; private set; }
        public event EventHandler<bool>? ConnectionChanged;

        public Task<SpotifyConnectResult> ConnectAsync()
        {
            IsConnected = _result.Connected;
            ConnectionChanged?.Invoke(this, IsConnected);
            return Task.FromResult(_result);
        }

        public void CancelConnect() { }
    }
}
