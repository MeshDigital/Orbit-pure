using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Events;
using SLSKDONET.Services;
using SLSKDONET.Services.Rekordbox;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Settings → Advanced → Rekordbox library write: the opt-in switch for writing ORBIT's cues straight
/// into Rekordbox's master.db, plus the backups ORBIT takes before every write.
/// </summary>
public partial class SettingsViewModel
{
    private readonly RekordboxDbWriteService? _rekordboxWriter;
    private ICommand? _turnOnRekordboxDbWriteCommand;
    private ICommand? _turnOffRekordboxDbWriteCommand;
    private ICommand? _openRekordboxBackupsCommand;
    private AsyncRelayCommand? _restoreRekordboxBackupCommand;

    /// <summary>The write service exists and Rekordbox's library and the bridge were found.</summary>
    public bool HasRekordboxDbWrite => _rekordboxWriter is not null;

    public bool IsRekordboxDbWriteEnabled => _config.EnableRekordboxDbWrite;

    public string RekordboxDbWriteStatus
    {
        get
        {
            if (_rekordboxWriter is null) return "Not available in this build.";
            if (!_rekordboxWriter.IsAvailable) return "No Rekordbox 6/7 library found on this computer.";
            var latest = _rekordboxWriter.ListBackups().FirstOrDefault();
            string library = $"Library: {_rekordboxWriter.DatabasePath}";
            return latest is null
                ? $"{library} · no ORBIT backups yet"
                : $"{library} · latest ORBIT backup {latest.CreatedLocal:yyyy-MM-dd HH:mm}";
        }
    }

    public bool HasRekordboxBackups => _rekordboxWriter?.ListBackups().Count > 0;

    public ICommand TurnOnRekordboxDbWriteCommand => _turnOnRekordboxDbWriteCommand ??= new AsyncRelayCommand(TurnOnRekordboxDbWriteAsync);

    public ICommand TurnOffRekordboxDbWriteCommand => _turnOffRekordboxDbWriteCommand ??= new RelayCommand(() => SetRekordboxDbWrite(false));

    public ICommand OpenRekordboxBackupsCommand => _openRekordboxBackupsCommand ??= new RelayCommand(OpenRekordboxBackups);

    public ICommand RestoreRekordboxBackupCommand => _restoreRekordboxBackupCommand ??= new AsyncRelayCommand(RestoreLatestRekordboxBackupAsync);

    private async Task TurnOnRekordboxDbWriteAsync()
    {
        if (_rekordboxWriter is null || _dialogService is null) return;
        bool confirmed = await _dialogService.ConfirmAsync(
            "Write cues straight into Rekordbox?",
            "This lets ORBIT write your cues directly into Rekordbox's own library database (master.db), " +
            "so they appear in Rekordbox without importing an XML.\n\n" +
            "Pioneer doesn't support other apps writing to that database. A mistake there can damage your whole Rekordbox library " +
            "(collection, playlists, cues, history).\n\n" +
            "• Always back up your Rekordbox library yourself before using tools like this: in Rekordbox, File → Library → Backup Library.\n" +
            "• ORBIT also copies master.db to its own backup folder before every write, and can restore it from here.\n" +
            "• Rekordbox must be closed while ORBIT writes.\n" +
            "• Tracks that already have cues in Rekordbox are left alone unless you choose to replace them.\n\n" +
            "Turn it on only if you have your own backup.",
            "I have a backup, turn it on", "Cancel");
        if (confirmed) SetRekordboxDbWrite(true);
    }

    private void SetRekordboxDbWrite(bool enabled)
    {
        if (_config.EnableRekordboxDbWrite == enabled) return;
        _config.EnableRekordboxDbWrite = enabled;
        SaveSettings();
        _logger.LogInformation("Rekordbox library write turned {State}", enabled ? "on" : "off");
        OnPropertyChanged(nameof(IsRekordboxDbWriteEnabled));
        OnPropertyChanged(nameof(RekordboxDbWriteStatus));
        _eventBus.Publish(new RekordboxDbWriteSettingChangedEvent(enabled));
    }

    private void OpenRekordboxBackups()
    {
        if (_rekordboxWriter is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(_rekordboxWriter.BackupRoot);
            if (!Services.Platform.PlatformShell.Current.OpenFolder(_rekordboxWriter.BackupRoot))
                _eventBus.Publish(new ToastRequestedEvent("Couldn't open the folder", _rekordboxWriter.BackupRoot, NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't open the Rekordbox backup folder");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't open the folder", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task RestoreLatestRekordboxBackupAsync()
    {
        if (_rekordboxWriter is null || _dialogService is null) return;
        var latest = _rekordboxWriter.ListBackups().FirstOrDefault();
        if (latest is null)
        {
            await _dialogService.ShowAlertAsync("No backups yet", "ORBIT makes a backup before each write to Rekordbox. There isn't one yet.");
            return;
        }

        bool confirmed = await _dialogService.ConfirmAsync(
            "Restore Rekordbox's library?",
            $"This puts back the copy of Rekordbox's library ORBIT made on {latest.CreatedLocal:yyyy-MM-dd HH:mm}.\n\n" +
            "Everything changed in Rekordbox since then (cues, playlists, new tracks, play history) is undone. " +
            "ORBIT saves the current state first, so this restore can be undone too.\n\nRekordbox must be closed.",
            "Restore", "Cancel");
        if (!confirmed) return;

        var (success, message) = await _rekordboxWriter.RestoreAsync(latest);
        _eventBus.Publish(new ToastRequestedEvent(success ? "Rekordbox library restored" : "Restore failed", message,
            success ? NotificationType.Success : NotificationType.Error, TimeSpan.FromSeconds(8)));
        OnPropertyChanged(nameof(RekordboxDbWriteStatus));
        OnPropertyChanged(nameof(HasRekordboxBackups));
    }
}
