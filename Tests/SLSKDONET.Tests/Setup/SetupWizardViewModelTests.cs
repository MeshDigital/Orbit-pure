using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

public class SetupWizardViewModelTests
{
    private static async Task<SetupWizardViewModel> OpenAsync(SetupSandbox sandbox)
    {
        var wizard = sandbox.CreateWizard();
        await wizard.InitializeAsync();
        return wizard;
    }

    [Fact]
    public async Task StartsOnWelcome_AndWalksEveryStepInOrder()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        Assert.True(wizard.IsWelcome);
        Assert.False(wizard.ShowFooterNavigation);

        await wizard.NextAsync();
        Assert.Same(wizard.Soulseek, wizard.CurrentStep);
        Assert.Equal("Step 2 of 6", wizard.StepPositionText); // same numbering as the header
        Assert.True(wizard.Indicators[0].IsDone);
        Assert.True(wizard.Indicators[1].IsCurrent);

        await wizard.SkipStepAsync();           // Soulseek
        await wizard.SkipStepAsync();           // Folders
        await wizard.SkipStepAsync();           // Connections
        await wizard.SkipStepAsync();           // Audio
        Assert.True(wizard.IsDone);
        Assert.Equal(4, wizard.Done.Summary.Count);
        Assert.All(wizard.Done.Summary, s => Assert.False(s.IsConfigured));
    }

    [Fact]
    public async Task Back_KeepsEntries()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        wizard.Soulseek.Username = "typed-name";
        wizard.Soulseek.Password = "typed-pass";
        await wizard.SkipStepAsync();
        wizard.Folders.DownloadDirectory = sandbox.Dir("dl");

        wizard.Back();
        Assert.Same(wizard.Soulseek, wizard.CurrentStep);
        Assert.Equal("typed-name", wizard.Soulseek.Username);
        Assert.Equal("typed-pass", wizard.Soulseek.Password);
        await wizard.SkipStepAsync();
        Assert.Equal(sandbox.Dir("dl"), wizard.Folders.DownloadDirectory);
    }

    [Fact]
    public async Task SkipSetup_MarksCompleted_ShowsReopenToast_AndCloses()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        SetupWizardOutcome? outcome = null;
        wizard.Closed += (_, o) => outcome = o;

        wizard.RequestSkipSetupCommand.Execute(null);
        Assert.True(wizard.IsSkipConfirmVisible);
        wizard.ConfirmSkipSetupCommand.Execute(null);

        Assert.Equal(SetupWizardOutcome.Skipped, outcome);
        Assert.True(sandbox.State.IsSetupCompleted);
        Assert.Contains(sandbox.Notifications.Shown, n => n.Message.Contains("Settings"));
        Assert.False(File.Exists(sandbox.ConfigPath)); // skipping writes no folder/audio settings
    }

    [Fact]
    public async Task EscapeTogglesSkipPrompt_EnterKeepsSettingUp()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        wizard.EscapeCommand.Execute(null);
        Assert.True(wizard.IsSkipConfirmVisible);
        wizard.EnterCommand.Execute(null);
        await Task.Yield();
        Assert.False(wizard.IsSkipConfirmVisible);
        Assert.False(wizard.IsClosed);
        Assert.True(wizard.IsWelcome); // Enter on the prompt doesn't also advance
    }

    [Fact]
    public async Task Soulseek_NextWithoutTest_RunsTheTest_AndStaysOnFailure()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Soulseek.Login = (_, _) => SoulseekLoginResultMapper.FromReason("login rejected: INVALIDPASS");
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        wizard.Soulseek.Username = "taken";
        wizard.Soulseek.Password = "wrong";

        await wizard.NextAsync();

        Assert.Same(wizard.Soulseek, wizard.CurrentStep);
        Assert.Equal(1, sandbox.Soulseek.TestCalls);
        Assert.True(wizard.Soulseek.ResultIsError);
        Assert.Empty(sandbox.Soulseek.Saved);
    }

    [Fact]
    public async Task Soulseek_TestedAccount_IsSavedOnlyOnNext()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        wizard.Soulseek.Username = "newdj";
        wizard.Soulseek.Password = "pw";
        wizard.Soulseek.RememberAndAutoConnect = false;

        await wizard.Soulseek.TestAsync();
        Assert.True(wizard.Soulseek.ResultIsSuccess);
        Assert.Empty(sandbox.Soulseek.Saved);

        await wizard.NextAsync();
        Assert.Same(wizard.Folders, wizard.CurrentStep);
        Assert.Equal(1, sandbox.Soulseek.TestCalls); // no second test
        var saved = Assert.Single(sandbox.Soulseek.Saved);
        Assert.Equal(new SoulseekAccountChoice("newdj", "pw", false), saved);
        Assert.True(wizard.Soulseek.IsConfigured);
    }

    [Fact]
    public async Task Soulseek_EmptyUsername_NeedsInputOrSkip()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        await wizard.NextAsync();
        Assert.Same(wizard.Soulseek, wizard.CurrentStep);
        Assert.True(wizard.Soulseek.HasError);
        Assert.Equal(0, sandbox.Soulseek.TestCalls);
    }

    [Fact]
    public async Task Folders_ConfigWrittenOnlyOnConfirm_AndMissingFoldersCreated()
    {
        using var sandbox = new SetupSandbox();
        var library = sandbox.Dir("Collection");
        Directory.CreateDirectory(library);
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        await wizard.SkipStepAsync();

        var downloads = Path.Combine(sandbox.Root, "Music", "ORBIT");
        wizard.Folders.DownloadDirectory = downloads;
        Assert.Equal(FolderCheckState.WillBeCreated, wizard.Folders.DownloadCheck.State);
        Assert.True(wizard.Folders.AddLibraryFolder(library));
        wizard.Folders.ShareOnSoulseek = false;

        // Edited but not confirmed: nothing on disk, config untouched.
        Assert.False(File.Exists(sandbox.ConfigPath));
        Assert.False(Directory.Exists(downloads));
        Assert.Empty(sandbox.LibraryFolders.Added);

        await wizard.NextAsync();

        Assert.Same(wizard.Connections, wizard.CurrentStep);
        Assert.True(Directory.Exists(downloads));
        Assert.True(Directory.Exists(Path.Combine(downloads, "ORBIT Edits")));
        var loaded = new ConfigManager(sandbox.ConfigPath).Load();
        Assert.Equal(downloads, loaded.DownloadDirectory);
        Assert.False(loaded.EnableLibrarySharing);
        Assert.Null(loaded.EditLabOutputDirectory); // follows the download folder
        var added = Assert.Single(sandbox.LibraryFolders.Added);
        Assert.Equal(library, added.Path);
        Assert.True(added.Watch);
        Assert.Equal(1, wizard.Folders.AddedLibraryFolderCount);
    }

    [Fact]
    public async Task Folders_UnwritableDownloadFolder_BlocksNext()
    {
        using var sandbox = new SetupSandbox();
        var wizard = sandbox.CreateWizard(new SetupFolderService(null, canWrite: _ => false, freeBytes: _ => null));
        await wizard.InitializeAsync();
        await wizard.NextAsync();
        await wizard.SkipStepAsync();
        wizard.Folders.DownloadDirectory = sandbox.Root;

        await wizard.NextAsync();

        Assert.Same(wizard.Folders, wizard.CurrentStep);
        Assert.True(wizard.Folders.HasError);
        Assert.False(File.Exists(sandbox.ConfigPath));
    }

    [Fact]
    public async Task Folders_CustomEditLabFolder_IsSaved()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        await wizard.SkipStepAsync();
        wizard.Folders.DownloadDirectory = sandbox.Dir("dl");
        sandbox.Picker.Folders.Enqueue(sandbox.Dir("renders"));
        wizard.Folders.BrowseEditLabCommand.Execute(null);
        await Task.Yield();
        Assert.False(wizard.Folders.EditLabUsesDefault);

        await wizard.NextAsync();

        Assert.Equal(sandbox.Dir("renders"), new ConfigManager(sandbox.ConfigPath).Load().EditLabOutputDirectory);
        Assert.True(Directory.Exists(sandbox.Dir("renders")));
    }

    [Fact]
    public async Task Audio_SavesDeviceAndAutoAnalysis_OnConfirm()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        await wizard.SkipStepAsync();
        await wizard.SkipStepAsync();
        await wizard.SkipStepAsync();
        Assert.Same(wizard.Audio, wizard.CurrentStep);
        Assert.True(wizard.Audio.FfmpegIsOk);
        Assert.Contains("Headphones", wizard.Audio.Devices);

        wizard.Audio.OutputDevice = "Headphones";
        wizard.Audio.AnalyzeNewDownloads = false;
        Assert.False(File.Exists(sandbox.ConfigPath));
        await wizard.NextAsync();

        Assert.True(wizard.IsDone);
        var loaded = new ConfigManager(sandbox.ConfigPath).Load();
        Assert.Equal("Headphones", loaded.AudioOutputDeviceName);
        Assert.False(loaded.AnalyzeNewDownloads);
    }

    [Fact]
    public async Task Audio_MissingFfmpeg_OffersFix()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Audio.FfmpegAvailable = false;
        var wizard = await OpenAsync(sandbox);
        Assert.True(wizard.Audio.FfmpegIsMissing);
        wizard.Audio.GetFfmpegCommand.Execute(null);
        Assert.Equal(1, sandbox.Audio.FfmpegPageOpens);
    }

    [Fact]
    public async Task Done_OffersScanForAddedFolders_AndFinishMarksCompleted()
    {
        using var sandbox = new SetupSandbox();
        var library = sandbox.Dir("Collection");
        Directory.CreateDirectory(library);
        var wizard = await OpenAsync(sandbox);
        await wizard.NextAsync();
        await wizard.SkipStepAsync();
        wizard.Folders.DownloadDirectory = sandbox.Dir("dl");
        wizard.Folders.AddLibraryFolder(library);
        await wizard.NextAsync();
        wizard.Connections.ArmDelay = TimeSpan.Zero; // pressed straight away; see ConnectionsStepTests
        wizard.Connections.ConnectSpotifyCommand.Execute(null);
        await Task.Yield();
        await wizard.NextAsync();
        await wizard.NextAsync();

        Assert.True(wizard.IsDone);
        Assert.True(wizard.Done.CanScanNow);
        wizard.Done.ScanNowCommand.Execute(null);
        Assert.Equal(1, sandbox.LibraryFolders.Scans);
        Assert.False(wizard.Done.CanScanNow);
        Assert.Contains(wizard.Done.Summary, s => s.Title == "Spotify" && s.IsConfigured);
        Assert.Contains(wizard.Done.Summary, s => s.Title == "Folders" && s.IsConfigured);

        SetupWizardOutcome? outcome = null;
        wizard.Closed += (_, o) => outcome = o;
        wizard.FinishAndTourCommand.Execute(null);
        Assert.Equal(SetupWizardOutcome.FinishedStartTour, outcome);
        Assert.True(sandbox.State.IsSetupCompleted);
        Assert.Empty(sandbox.Notifications.Shown.Where(n => n.Title == "Setup skipped"));
    }

    [Fact]
    public async Task Rerun_ShowsCurrentValues()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Config.DownloadDirectory = sandbox.Dir("existing-downloads");
        sandbox.Config.EnableLibrarySharing = false;
        sandbox.Config.AnalyzeNewDownloads = false;
        sandbox.Soulseek.Current = new SoulseekAccountSnapshot("owner", true, IsConnected: true);
        sandbox.LibraryFolders.Existing.Add(new LibraryFolderSetupEntry(sandbox.Dir("old-lib"), true));

        var wizard = await OpenAsync(sandbox);

        Assert.Equal(sandbox.Dir("existing-downloads"), wizard.Folders.DownloadDirectory);
        Assert.False(wizard.Folders.ShareOnSoulseek);
        Assert.False(wizard.Audio.AnalyzeNewDownloads);
        Assert.Equal("owner", wizard.Soulseek.Username);
        Assert.True(wizard.Soulseek.ResultIsSuccess); // already connected
        var existing = Assert.Single(wizard.Folders.LibraryFolders);
        Assert.True(existing.IsExisting);
        Assert.False(existing.RemoveCommand.CanExecute(null));

        // Unchanged connected account: Next saves without a new sign-in.
        await wizard.NextAsync();
        await wizard.NextAsync();
        Assert.Equal(0, sandbox.Soulseek.TestCalls);
        Assert.Single(sandbox.Soulseek.Saved);
    }

    [Fact]
    public async Task FreshInstall_SuggestsMusicOrbitFolder()
    {
        using var sandbox = new SetupSandbox();
        var wizard = await OpenAsync(sandbox);
        Assert.Equal(FoldersStepViewModel.SuggestedDownloadDirectory, wizard.Folders.DownloadDirectory);
    }

    [Fact]
    public void Shell_ReopensFromSettingsEvent_AndStartsTourAfterFinish()
    {
        using var sandbox = new SetupSandbox();
        var bus = new EventBusService();
        var closedCalls = 0;
        var shell = new SetupShellViewModel(() => sandbox.CreateWizard(), sandbox.State, NullLogger<SetupShellViewModel>.Instance, bus,
            onWizardClosed: () => closedCalls++, postToUi: a => a());
        var tourRequests = 0;
        shell.Tour.StartRequested += (_, _) => tourRequests++;

        bus.Publish(new OpenSetupWizardRequestedEvent()); // what Settings → "Run setup again" publishes
        Assert.True(shell.IsWizardOpen);
        var first = shell.Wizard!;
        bus.Publish(new OpenSetupWizardRequestedEvent());
        Assert.Same(first, shell.Wizard); // no second wizard on top

        first.FinishAndTourCommand.Execute(null);
        Assert.False(shell.IsWizardOpen);
        Assert.Equal(1, closedCalls);
        Assert.Equal(1, tourRequests);

        bus.Publish(new StartTourRequestedEvent()); // Settings → "Take the tour"
        Assert.Equal(2, tourRequests);
        shell.Dispose();
    }
}
