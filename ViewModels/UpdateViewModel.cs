using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Update;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Auto-update surface: channel selection (Stable/Beta/Any), check-for-updates, and a one-click
/// download-and-run of the newest installer.
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
    public ReactiveCommand<Unit, Unit> DownloadUpdateCommand { get; }

    public UpdateViewModel(GitHubUpdateService updater)
    {
        _updater = updater;
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync);
        DownloadUpdateCommand = ReactiveCommand.CreateFromTask(DownloadUpdateAsync);
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

    private async Task DownloadUpdateAsync()
    {
        if (LatestUpdate is null)
            return;

        var installerUrl = LatestUpdate.InstallerUrl;
        if (installerUrl is null)
        {
            // No attached installer → open the release page instead.
            Process.Start(new ProcessStartInfo(LatestUpdate.HtmlUrl) { UseShellExecute = true });
            return;
        }

        StatusText = "Downloading update…";
        try
        {
            var fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
            var installerPath = Path.Combine(Path.GetTempPath(), fileName);

            using var http = new HttpClient();
            await using (var src = await http.GetStreamAsync(installerUrl).ConfigureAwait(true))
            await using (var dst = File.Create(installerPath))
                await src.CopyToAsync(dst).ConfigureAwait(true);

            StatusText = $"Launching installer ({fileName})…";
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "Download failed: " + ex.Message;
        }
    }
}
