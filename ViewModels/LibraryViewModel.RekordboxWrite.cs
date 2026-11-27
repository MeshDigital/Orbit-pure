using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.Rekordbox;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// "Write cues to Rekordbox library…" on a playlist and on a track selection — only shown once the
/// user opted in (Settings → Advanced). Preview first, ask before replacing Rekordbox's own cues,
/// confirm with the backup note, then write and report what was verified.
/// </summary>
public partial class LibraryViewModel
{
    private ICommand? _writeCuesToRekordboxCommand;
    private ICommand? _batchWriteCuesToRekordboxCommand;
    private bool _isWritingToRekordbox;

    public bool IsRekordboxDbWriteEnabled => _appConfig.EnableRekordboxDbWrite;

    public ICommand WriteCuesToRekordboxCommand => _writeCuesToRekordboxCommand ??= new AsyncRelayCommand<object>(async param =>
    {
        if (param is not PlaylistJob project) return;
        var tracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id)).ToList();
        await WriteCuesToRekordboxAsync(tracks, $"'{project.SourceTitle}'");
    });

    public ICommand BatchWriteCuesToRekordboxCommand => _batchWriteCuesToRekordboxCommand ??= new AsyncRelayCommand(async () =>
    {
        var tracks = Tracks.SelectedTracks.Select(t => t.Model).ToList();
        if (tracks.Count > 0) await WriteCuesToRekordboxAsync(tracks, $"{tracks.Count} selected track(s)");
    });

    private void OnRekordboxDbWriteSettingChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsRekordboxDbWriteEnabled)));

    private async Task WriteCuesToRekordboxAsync(IReadOnlyList<PlaylistTrack> tracks, string what)
    {
        var writer = _serviceProvider?.GetService<RekordboxDbWriteService>();
        if (writer is null || !writer.IsEnabled || _isWritingToRekordbox) return;
        _isWritingToRekordbox = true;
        try
        {
            IsLoading = true;
            var plan = await writer.BuildPlanAsync(tracks);
            if (plan.Count == 0)
            {
                _notificationService.Show("Nothing to write", $"None of {what} have ORBIT cues yet.", NotificationType.Information);
                return;
            }

            var mode = RekordboxWriteMode.FillEmpty;
            var preview = await writer.PreviewAsync(plan, mode);
            if (!preview.Success)
            {
                _notificationService.Show("Rekordbox write unavailable", preview.Message, NotificationType.Error);
                return;
            }

            if (preview.SkippedHasCues.Count > 0)
            {
                bool replace = await _dialogService.ConfirmAsync(
                    "Some tracks already have Rekordbox cues",
                    $"{preview.SkippedHasCues.Count} of these tracks already have cues in Rekordbox — your own work there.\n\n" +
                    Sample(preview.SkippedHasCues) + "\n\n" +
                    "Keep them (ORBIT only writes the tracks without cues), or delete them and write ORBIT's cues instead?",
                    "Replace with ORBIT cues", "Keep Rekordbox cues");
                if (replace)
                {
                    mode = RekordboxWriteMode.Replace;
                    preview = await writer.PreviewAsync(plan, mode);
                    if (!preview.Success)
                    {
                        _notificationService.Show("Rekordbox write unavailable", preview.Message, NotificationType.Error);
                        return;
                    }
                }
            }

            string notInCollection = preview.NotInRekordbox.Count > 0
                ? $"\n\n{preview.NotInRekordbox.Count} track(s) aren't in your Rekordbox collection, so they're skipped. Add them in Rekordbox first, or use the XML export for them.\n{Sample(preview.NotInRekordbox)}"
                : "";
            if (preview.TracksWritten == 0)
            {
                await _dialogService.ShowAlertAsync("Nothing to write", preview.Message + notInCollection);
                return;
            }

            bool go = await _dialogService.ConfirmAsync(
                "Write cues into Rekordbox's library?",
                preview.Message + notInCollection + "\n\n" +
                "Rekordbox must be closed. ORBIT first copies Rekordbox's library to its backup folder " +
                "(restore it from Settings → Advanced → Rekordbox library write).\n\n" +
                "This writes directly into Rekordbox's database, which Pioneer doesn't support. Keep your own Rekordbox backup " +
                "(File → Library → Backup Library) until you've checked the cues in Rekordbox.",
                "Back up and write", "Cancel");
            if (!go) return;

            var result = await writer.WriteAsync(plan, mode);
            _notificationService.Show(
                result.Success ? "Cues written to Rekordbox" : "Rekordbox write failed",
                result.Success
                    ? $"{result.Message} Verified {result.Verified} track(s). Open Rekordbox to see them."
                    : result.Message,
                result.Success ? NotificationType.Success : NotificationType.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing cues to Rekordbox failed");
            _notificationService.Show("Rekordbox write failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
            _isWritingToRekordbox = false;
        }
    }

    private static string Sample(IReadOnlyList<string> paths) =>
        string.Join("\n", paths.Take(4).Select(p => "• " + System.IO.Path.GetFileName(p)))
        + (paths.Count > 4 ? $"\n• …and {paths.Count - 4} more" : "");
}
