using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Update;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Auto-update surface: channel selection (Stable/Beta/Any), check-for-updates, and a download
/// action for the newest release newer than the current build.
/// </summary>
public sealed class UpdateViewModel : ReactiveObject
{
    private readonly GitHubUpdateService _updater;

    public IReadOnlyList<UpdateChannel> ChannelOptions { get; } = new[]
    {
        UpdateChannel.Stable, UpdateChannel.Beta, UpdateChannel.Any,
    };

    private UpdateChannel _selectedChannel = UpdateChannel.Stable;
    public UpdateChannel SelectedChannel
    {
        get => _selectedChannel;
        set => this.RaiseAndSetIfChanged(ref _selectedChannel, value);
    }

    private string _statusText = "Not checked yet";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private GitHubRelease? _latestUpdate;
    public GitHubRelease? LatestUpdate
    {
        get => _latestUpdate;
        private set
        {
            this.RaiseAndSetIfChanged(ref _latestUpdate, value);
            this.RaisePropertyChanged(nameof(UpdateAvailable));
        }
    }

    public bool UpdateAvailable => LatestUpdate is not null;

    public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDownloadCommand { get; }

    public UpdateViewModel(GitHubUpdateService updater)
    {
        _updater = updater;
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync);
        OpenDownloadCommand = ReactiveCommand.Create(OpenDownload);
    }

    private async Task CheckForUpdatesAsync()
    {
        StatusText = "Checking…";
        try
        {
            LatestUpdate = await _updater.CheckForUpdatesAsync(SelectedChannel).ConfigureAwait(true);
            StatusText = LatestUpdate is null
                ? "You're on the latest version."
                : $"Update available: {LatestUpdate.TagName}";
        }
        catch (Exception ex)
        {
            StatusText = "Check failed: " + ex.Message;
        }
    }

    private void OpenDownload()
    {
        if (LatestUpdate?.HtmlUrl is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
