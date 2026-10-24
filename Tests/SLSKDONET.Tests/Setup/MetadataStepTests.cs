using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Setup;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

internal sealed class FakeEnrichmentGateway : ISetupEnrichmentGateway
{
    public List<SetupMetadataSource> Sources { get; } = new()
    {
        new("tags", "File tags", "tags line", true, true, false, ProviderAvailability.Ready),
        new("deezer", "Deezer", "deezer line", true, false, false, ProviderAvailability.Ready),
        new("musicbrainz", "MusicBrainz", "mb line", true, false, false, ProviderAvailability.Ready),
        new("discogs", "Discogs", "discogs line", true, false, false, ProviderAvailability.NeedsKey),
        new("spotify", "Spotify", "spotify line", true, false, false, ProviderAvailability.NotConnected),
        new("beatport", "Beatport", "beatport line", false, false, true, ProviderAvailability.Ready),
    };

    public int Tracks { get; set; }
    public List<SetupEnrichmentChoice> Saved { get; } = new();
    public int Starts { get; private set; }
    public int Pauses { get; private set; }
    public int Resumes { get; private set; }
    public List<string> OpenedUrls { get; } = new();
    public EnrichmentProgress Progress { get; set; } = EnrichmentProgress.Idle;

    public IReadOnlyList<SetupMetadataSource> GetSources() => Sources.ToList();
    public bool AutoRun { get; set; } = true;
    public bool HasDiscogsToken { get; set; }
    public bool HasAcoustIdKey => false;
    public bool HasFpcalc => false;

    public string? Save(SetupEnrichmentChoice choice)
    {
        Saved.Add(choice);
        AutoRun = choice.AutoRun;
        for (var i = 0; i < Sources.Count; i++)
            Sources[i] = Sources[i] with { IsEnabled = Sources[i].IsAlwaysOn || choice.EnabledSources.Contains(Sources[i].Name) };
        if (choice.DiscogsToken != null)
        {
            HasDiscogsToken = true;
            var i = Sources.FindIndex(s => s.Name == "discogs");
            Sources[i] = Sources[i] with { Availability = ProviderAvailability.Ready };
        }
        return null;
    }

    public Task<int> CountLibraryTracksAsync() => Task.FromResult(Tracks);

    public Task<int> StartAsync()
    {
        Starts++;
        Progress = new EnrichmentProgress(true, false, false, Tracks, 0, 0, Tracks, Array.Empty<ProviderStatusSnapshot>());
        return Task.FromResult(Tracks);
    }

    public void Pause() => Pauses++;
    public void Resume() => Resumes++;
    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public EnrichmentProgress CurrentProgress() => Progress;

    public event EventHandler<EnrichmentProgress>? ProgressChanged;
    public event EventHandler? LibraryChanged;

    public void RaiseProgress(EnrichmentProgress p) { Progress = p; ProgressChanged?.Invoke(this, p); }
    public void RaiseLibraryChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);
}

public class MetadataStepTests
{
    private static async Task<(SetupSandbox Sandbox, SetupWizardViewModel Wizard, FakeEnrichmentGateway Gateway)> OpenAsync(int tracks = 0)
    {
        var sandbox = new SetupSandbox();
        var gateway = new FakeEnrichmentGateway { Tracks = tracks };
        var wizard = new SetupWizardViewModel(sandbox.State, sandbox.Soulseek, sandbox.SettingsGateway(), sandbox.LibraryFolders,
            new SetupFolderService(null, canWrite: _ => true, freeBytes: _ => 100L * 1024 * 1024 * 1024),
            sandbox.Picker, sandbox.Spotify, sandbox.Audio, NullLogger<SetupWizardViewModel>.Instance, sandbox.Notifications,
            gateway, activity: null, postToUi: a => a());
        await wizard.InitializeAsync();
        return (sandbox, wizard, gateway);
    }

    private static async Task GoToMetadataAsync(SetupWizardViewModel wizard)
    {
        await wizard.NextAsync();          // Welcome
        await wizard.SkipStepAsync();      // Soulseek
        await wizard.SkipStepAsync();      // Folders
        await wizard.SkipStepAsync();      // Connections
        Assert.Same(wizard.Metadata, wizard.CurrentStep);
    }

    [Fact]
    public async Task Step_ComesAfterConnections_AndTheCountsFollowIt()
    {
        var (sandbox, wizard, _) = await OpenAsync();
        using var _s = sandbox;
        Assert.Equal(7, wizard.Steps.Count);
        Assert.Equal(new[] { "Welcome", "Soulseek", "Folders", "Connections", "Metadata", "Audio", "Done" }, wizard.Steps.Select(s => s.ShortTitle));
        await GoToMetadataAsync(wizard);
        Assert.Equal("Step 5 of 7", wizard.StepPositionText);
    }

    [Fact]
    public async Task Toggles_AndKeys_AreSavedOnNext_NotBefore()
    {
        var (sandbox, wizard, gateway) = await OpenAsync();
        using var _s = sandbox;
        await GoToMetadataAsync(wizard);
        var step = wizard.Metadata!;

        Assert.False(step.Sources.Single(s => s.Name == "tags").CanToggle);       // always on
        Assert.False(step.Sources.Single(s => s.Name == "beatport").IsEnabled);   // experimental, off
        // No token yet: the switch reads OFF and is disabled, the choice is kept underneath.
        Assert.Equal("Waiting for a token", step.Discogs!.StatusText);
        Assert.True(step.Discogs.IsWaiting);
        Assert.False(step.Discogs.SwitchOn);
        Assert.False(step.Discogs.CanToggle);
        Assert.True(step.Discogs.IsEnabled);
        Assert.True(step.Discogs.HasKeyField);
        var spotify = step.Sources.Single(s => s.Name == "spotify");
        Assert.Equal("Waiting for Spotify · connect it first", spotify.StatusText);
        Assert.False(spotify.SwitchOn);

        step.Sources.Single(s => s.Name == "musicbrainz").IsEnabled = false;
        step.Sources.Single(s => s.Name == "tags").IsEnabled = false;          // ignored
        step.AutoRun = false;
        step.DiscogsToken = "abc123";
        // Typing the token (here or in the row's own field) already turns the switch on.
        Assert.Equal("abc123", step.Discogs.KeyText);
        Assert.True(step.Discogs.SwitchOn);
        Assert.True(step.Discogs.CanToggle);
        Assert.Equal("Saved when you continue", step.Discogs.StatusText);
        Assert.Empty(gateway.Saved);

        await wizard.NextAsync();

        var saved = Assert.Single(gateway.Saved);
        Assert.DoesNotContain("musicbrainz", saved.EnabledSources);
        Assert.Contains("tags", saved.EnabledSources);
        Assert.Contains("deezer", saved.EnabledSources);
        Assert.False(saved.AutoRun);
        Assert.Equal("abc123", saved.DiscogsToken);
        Assert.Equal(string.Empty, step.DiscogsToken);                          // never kept in memory after saving
        Assert.Equal("Ready", step.Discogs!.StatusText);
        Assert.Same(wizard.Audio, wizard.CurrentStep);

        step.GetDiscogsTokenCommand.Execute(null);
        Assert.Contains(MetadataStepViewModel.DiscogsTokenUrl, gateway.OpenedUrls);
    }

    [Fact]
    public async Task StartNow_IsOnlyAvailableWithTracks_AndFollowsARunningImport()
    {
        var (sandbox, wizard, gateway) = await OpenAsync(tracks: 0);
        using var _s = sandbox;
        await GoToMetadataAsync(wizard);
        var step = wizard.Metadata!;
        Assert.False(step.CanStart);
        Assert.False(step.StartNowCommand.CanExecute(null));

        gateway.Tracks = 500;            // an import batch landed
        gateway.RaiseLibraryChanged();
        var sw = Stopwatch.StartNew();
        while (!step.CanStart && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);

        Assert.True(step.CanStart);
        Assert.True(step.StartNowCommand.CanExecute(null));
        Assert.Equal(500, step.TrackCount);
    }

    [Fact]
    public async Task StartNow_SavesFirst_ShowsLiveProgress_AndLeavingTheStepKeepsItRunning()
    {
        var (sandbox, wizard, gateway) = await OpenAsync(tracks: 120);
        using var _s = sandbox;
        await GoToMetadataAsync(wizard);
        var step = wizard.Metadata!;
        step.Sources.Single(s => s.Name == "deezer").IsEnabled = false;

        await step.StartNowAsync();

        Assert.Equal(1, gateway.Starts);
        Assert.DoesNotContain("deezer", Assert.Single(gateway.Saved).EnabledSources); // the run uses the toggles
        Assert.True(step.ShowProgress);
        Assert.True(step.IsRunning);
        Assert.False(step.CanStart);

        var snapshots = new[]
        {
            new ProviderStatusSnapshot("musicbrainz", "MusicBrainz", ProviderAvailability.Ready, true, DateTime.UtcNow.AddSeconds(30), 40, 30, 1, null),
            new ProviderStatusSnapshot("deezer", "Deezer", ProviderAvailability.Disabled, false, null, 0, 0, 0, null),
            new ProviderStatusSnapshot("tags", "File tags", ProviderAvailability.Ready, false, null, 120, 60, 0, null),
        };
        gateway.RaiseProgress(new EnrichmentProgress(true, false, false, 120, 30, 25, 90, snapshots));

        Assert.Equal(30, step.Done);
        Assert.Equal(120, step.Total);
        Assert.Equal(25, step.ProgressValue);
        Assert.Equal("30 / 120 · 25 enriched", step.ProgressText);
        Assert.StartsWith("Paused for its rate limit", step.Sources.Single(s => s.Name == "musicbrainz").LiveText);
        Assert.Equal("Disabled", step.Sources.Single(s => s.Name == "deezer").LiveText);
        Assert.Equal("60 matched", step.Sources.Single(s => s.Name == "tags").LiveText);

        step.PauseCommand.Execute(null);
        Assert.Equal(1, gateway.Pauses);
        gateway.RaiseProgress(new EnrichmentProgress(false, true, false, 120, 30, 25, 90, snapshots));
        Assert.True(step.ResumeCommand.CanExecute(null));
        step.ResumeCommand.Execute(null);
        Assert.Equal(1, gateway.Resumes);
        gateway.RaiseProgress(new EnrichmentProgress(true, false, false, 120, 31, 26, 89, snapshots));

        // Next saves again and moves on; nothing stops the run, not even closing the wizard.
        await wizard.NextAsync();
        Assert.Same(wizard.Audio, wizard.CurrentStep);
        wizard.Close(SetupWizardOutcome.Finished);
        wizard.Dispose();
        Assert.Equal(1, gateway.Pauses);
        Assert.Equal(1, gateway.Starts);
        Assert.True(gateway.Progress.IsRunning);
    }

    [Fact]
    public async Task FoldersImportNow_StartsTheImportInTheBackground_AndDoneDoesNotOfferASecondScan()
    {
        var (sandbox, wizard, gateway) = await OpenAsync(tracks: 0);
        using var _s = sandbox;
        var library = sandbox.Dir("music");
        System.IO.Directory.CreateDirectory(library);
        await wizard.NextAsync();
        await wizard.SkipStepAsync();
        wizard.Folders.DownloadDirectory = sandbox.Dir("dl");
        Assert.True(wizard.Folders.AddLibraryFolder(library));
        Assert.True(wizard.Folders.CanChooseImportNow);
        Assert.True(wizard.Folders.ImportNow);                  // default on
        await wizard.NextAsync();

        Assert.Equal(1, sandbox.LibraryFolders.BackgroundScans);
        Assert.Equal(0, sandbox.LibraryFolders.Scans);
        Assert.True(wizard.Folders.ImportStarted);

        // Back and Next again doesn't start a second import.
        wizard.Back();
        await wizard.NextAsync();
        Assert.Equal(1, sandbox.LibraryFolders.BackgroundScans);

        while (!wizard.IsDone)
            await wizard.SkipStepAsync();
        Assert.True(wizard.Done.ScanStarted);
        Assert.False(wizard.Done.CanScanNow);
        Assert.Same(wizard.Metadata, wizard.Done.Metadata);
    }
}
