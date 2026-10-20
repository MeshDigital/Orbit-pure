using System;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

/// <summary>
/// The owner saw Spotify sign-in start by itself on the Connections step (browser tab opened,
/// "Finish signing in in your browser window") after only pressing Next on Folders. Sign-in may
/// only start from a deliberate press of Connect, never from arriving on the page or from the
/// input that advanced to it.
/// </summary>
public class ConnectionsStepTests
{
    private static async Task<(SetupSandbox Sandbox, SetupWizardViewModel Wizard)> ToConnectionsAsync(bool useEnter)
    {
        var sandbox = new SetupSandbox();
        var wizard = sandbox.CreateWizard();
        await wizard.InitializeAsync();
        await Advance(wizard, useEnter);          // Welcome → Soulseek
        await wizard.SkipStepAsync();             // Soulseek → Folders
        wizard.Folders.DownloadDirectory = sandbox.Dir("dl");
        await Advance(wizard, useEnter);          // Folders → Connections
        Assert.Same(wizard.Connections, wizard.CurrentStep);
        return (sandbox, wizard);
    }

    private static async Task Advance(SetupWizardViewModel wizard, bool useEnter)
    {
        if (useEnter)
        {
            wizard.EnterCommand.Execute(null);
            await Task.Yield();
            for (int i = 0; i < 50 && wizard.IsBusy; i++) await Task.Delay(10);
        }
        else
        {
            await wizard.NextAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivingOnConnections_NeverStartsSpotifySignIn(bool useEnter)
    {
        var (sandbox, wizard) = await ToConnectionsAsync(useEnter);
        using (sandbox)
        {
            Assert.Equal(0, sandbox.Spotify.ConnectCalls);
            Assert.False(wizard.Connections.IsConnecting);
            Assert.False(wizard.Connections.HasStatusMessage);
            Assert.True(wizard.Connections.IsSpotifyDisconnected);
        }
    }

    [Fact]
    public async Task ConnectPress_CarriedOverFromThePreviousPage_IsIgnored()
    {
        var (sandbox, wizard) = await ToConnectionsAsync(useEnter: false);
        using (sandbox)
        {
            // The click / Enter that pressed Next lands on Connect a moment after the page appears.
            wizard.Connections.ConnectSpotifyCommand.Execute(null);
            await Task.Yield();
            Assert.Equal(0, sandbox.Spotify.ConnectCalls);
            Assert.False(wizard.Connections.HasStatusMessage);
        }
    }

    [Fact]
    public async Task DeliberateConnectPress_StartsSignInOnce()
    {
        var (sandbox, wizard) = await ToConnectionsAsync(useEnter: false);
        using (sandbox)
        {
            var later = DateTime.UtcNow.AddSeconds(2);
            wizard.Connections.UtcNow = () => later;  // the user read the page, then pressed Connect

            await wizard.Connections.ConnectAsync();

            Assert.Equal(1, sandbox.Spotify.ConnectCalls);
            Assert.True(wizard.Connections.IsSpotifyConnected);
        }
    }

    [Fact]
    public async Task FooterStepCounter_MatchesTheHeaderNumbering()
    {
        using var sandbox = new SetupSandbox();
        var wizard = sandbox.CreateWizard();
        await wizard.InitializeAsync();
        Assert.Equal(string.Empty, wizard.StepPositionText); // Welcome has its own footer

        await wizard.NextAsync();
        while (!wizard.IsDone)
        {
            var current = wizard.Indicators.Single(i => i.IsCurrent);
            Assert.Equal($"Step {current.Number} of {wizard.Indicators.Count}", wizard.StepPositionText);
            if (wizard.CurrentStep == wizard.Folders) Assert.Equal("Step 3 of 6", wizard.StepPositionText);
            if (wizard.CurrentStep == wizard.Connections) Assert.Equal("Step 4 of 6", wizard.StepPositionText);
            await wizard.SkipStepAsync();
        }
    }
}
