using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>A library folder row on the Folders step.</summary>
public sealed class SetupLibraryFolderItem : SetupObservable
{
    private bool _watch;

    public SetupLibraryFolderItem(string path, bool watch, bool isExisting, Action<SetupLibraryFolderItem>? remove = null)
    {
        Path = path;
        _watch = watch;
        IsExisting = isExisting;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => !isExisting && remove != null);
    }

    /// <summary>Removes a not-yet-saved folder from the list (existing Library Sources are managed in Settings).</summary>
    public ICommand RemoveCommand { get; }

    public string Path { get; }

    /// <summary>Already a Library Source before this wizard run (shown, not removable here).</summary>
    public bool IsExisting { get; }
    public bool IsNew => !IsExisting;

    /// <summary>Watch-folder auto-import for this folder.</summary>
    public bool Watch
    {
        get => _watch;
        set => SetProperty(ref _watch, value);
    }
}

/// <summary>Step 3: download folder, library folders, Soulseek sharing and the Edit Lab output folder.</summary>
public sealed class FoldersStepViewModel : SetupStepViewModel
{
    public const string DefaultEditLabFolderName = "ORBIT Edits";

    private readonly ISetupSettingsGateway _settings;
    private readonly ILibraryFolderSetupGateway _libraryFolders;
    private readonly ISetupFolderService _folders;
    private readonly IFileInteractionService _picker;
    private readonly ILogger _logger;

    private string _downloadDirectory = string.Empty;
    private FolderCheck _downloadCheck = new(FolderCheckState.Empty, "Choose a folder.", null);
    private bool _shareOnSoulseek = true;
    private string? _extraSharedFolder;
    private FolderCheck? _extraShareCheck;
    private string? _customEditLabFolder;
    private FolderCheck _editLabCheck = new(FolderCheckState.Empty, string.Empty, null);
    private int _addedLibraryFolderCount;

    public FoldersStepViewModel(
        ISetupSettingsGateway settings,
        ILibraryFolderSetupGateway libraryFolders,
        ISetupFolderService folders,
        IFileInteractionService picker,
        ILogger logger)
    {
        _settings = settings;
        _libraryFolders = libraryFolders;
        _folders = folders;
        _picker = picker;
        _logger = logger;

        BrowseDownloadCommand = new AsyncRelayCommand(BrowseDownloadAsync);
        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new RelayCommand<SetupLibraryFolderItem>(item =>
        {
            if (item is { IsNew: true }) LibraryFolders.Remove(item);
            OnPropertyChanged(nameof(HasLibraryFolders));
        OnPropertyChanged(nameof(HasNewLibraryFolders));
        OnPropertyChanged(nameof(CanChooseImportNow));
        });
        BrowseExtraShareCommand = new AsyncRelayCommand(BrowseExtraShareAsync);
        ClearExtraShareCommand = new RelayCommand(() => ExtraSharedFolder = null);
        BrowseEditLabCommand = new AsyncRelayCommand(BrowseEditLabAsync);
        ResetEditLabCommand = new RelayCommand(() => CustomEditLabFolder = null);
    }

    public override string Title => "Choose your folders";
    public override string ShortTitle => "Folders";
    public override string Subtitle => "Where downloads land, which music ORBIT keeps in its library, and what you share.";
    public override bool IsRecommended => true;
    public override bool CanSkip => true;
    public override string SkipNote => $"Downloads then go to {AppConfigFallbackDisplay}. Change it any time in Settings → Downloads.";

    private static string AppConfigFallbackDisplay => SLSKDONET.Configuration.AppConfig.FallbackDownloadDirectory;

    public ICommand BrowseDownloadCommand { get; }
    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }
    public ICommand BrowseExtraShareCommand { get; }
    public ICommand ClearExtraShareCommand { get; }
    public ICommand BrowseEditLabCommand { get; }
    public ICommand ResetEditLabCommand { get; }

    public ObservableCollection<SetupLibraryFolderItem> LibraryFolders { get; } = new();
    public bool HasLibraryFolders => LibraryFolders.Count > 0;
    public bool HasNewLibraryFolders => LibraryFolders.Any(f => f.IsNew);

    private bool _importNow = true;
    private bool _importStarted;

    /// <summary>Start importing the new library folders when Next is pressed (background, default on).</summary>
    public bool ImportNow { get => _importNow; set => SetProperty(ref _importNow, value); }

    /// <summary>This wizard run already started the import (Done then doesn't offer "Scan now").</summary>
    public bool ImportStarted
    {
        get => _importStarted;
        private set
        {
            if (SetProperty(ref _importStarted, value)) OnPropertyChanged(nameof(CanChooseImportNow));
        }
    }

    public bool CanChooseImportNow => HasNewLibraryFolders && !ImportStarted;

    /// <summary>Library folders this run added (the Done page offers "Scan now" for them).</summary>
    public int AddedLibraryFolderCount
    {
        get => _addedLibraryFolderCount;
        private set => SetProperty(ref _addedLibraryFolderCount, value);
    }

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            if (SetProperty(ref _downloadDirectory, value ?? string.Empty))
            {
                DownloadCheck = _folders.Check(_downloadDirectory);
                OnPropertyChanged(nameof(EditLabFolderDisplay));
                RecheckEditLab();
                ClearError();
            }
        }
    }

    public FolderCheck DownloadCheck
    {
        get => _downloadCheck;
        private set
        {
            if (SetProperty(ref _downloadCheck, value))
            {
                OnPropertyChanged(nameof(DownloadCheckIsError));
                OnPropertyChanged(nameof(DownloadCheckIsWarning));
            }
        }
    }

    public bool DownloadCheckIsError => DownloadCheck.State is FolderCheckState.Invalid or FolderCheckState.Empty;
    public bool DownloadCheckIsWarning => DownloadCheck.State == FolderCheckState.Warning;

    public bool ShareOnSoulseek
    {
        get => _shareOnSoulseek;
        set => SetProperty(ref _shareOnSoulseek, value);
    }

    /// <summary>Optional extra shared folder, on top of the download folder and library folders.</summary>
    public string? ExtraSharedFolder
    {
        get => _extraSharedFolder;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (SetProperty(ref _extraSharedFolder, v))
            {
                ExtraShareCheck = v == null ? null : _folders.Check(v);
                OnPropertyChanged(nameof(HasExtraSharedFolder));
                ClearError();
            }
        }
    }

    public bool HasExtraSharedFolder => ExtraSharedFolder != null;

    public FolderCheck? ExtraShareCheck
    {
        get => _extraShareCheck;
        private set
        {
            if (SetProperty(ref _extraShareCheck, value))
                OnPropertyChanged(nameof(ExtraShareCheckIsError));
        }
    }

    public bool ExtraShareCheckIsError => ExtraShareCheck is { IsUsable: false };

    /// <summary>A custom Edit Lab output folder, or null to follow the download folder.</summary>
    public string? CustomEditLabFolder
    {
        get => _customEditLabFolder;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (SetProperty(ref _customEditLabFolder, v))
            {
                OnPropertyChanged(nameof(EditLabFolderDisplay));
                OnPropertyChanged(nameof(EditLabUsesDefault));
                RecheckEditLab();
                ClearError();
            }
        }
    }

    public bool EditLabUsesDefault => CustomEditLabFolder == null;

    public string EditLabFolderDisplay => CustomEditLabFolder ?? DefaultEditLabFolder;

    private string DefaultEditLabFolder =>
        string.IsNullOrWhiteSpace(DownloadDirectory) ? DefaultEditLabFolderName : Path.Combine(DownloadDirectory.Trim(), DefaultEditLabFolderName);

    public FolderCheck EditLabCheck
    {
        get => _editLabCheck;
        private set
        {
            if (SetProperty(ref _editLabCheck, value))
                OnPropertyChanged(nameof(EditLabCheckIsError));
        }
    }

    public bool EditLabCheckIsError => EditLabCheck.State == FolderCheckState.Invalid;

    /// <summary>The suggested download folder for a fresh install: Music\ORBIT.</summary>
    public static string SuggestedDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ORBIT");

    public override async Task InitializeAsync()
    {
        var current = _settings.ReadFolders();
        DownloadDirectory = _settings.ChosenDownloadDirectory ?? SuggestedDownloadDirectory;
        ShareOnSoulseek = current.ShareOnSoulseek;
        ExtraSharedFolder = current.ExtraSharedFolder;
        CustomEditLabFolder = current.EditLabOutputDirectory;

        try
        {
            var existing = await _libraryFolders.GetExistingAsync();
            LibraryFolders.Clear();
            foreach (var folder in existing.Where(f => !FirstRunDetector.SamePath(f.Path, DownloadDirectory)))
                LibraryFolders.Add(new SetupLibraryFolderItem(folder.Path, folder.Watch, isExisting: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: could not load existing library folders");
            ErrorMessage = "Your existing library folders couldn't be loaded. New folders can still be added.";
        }
        OnPropertyChanged(nameof(HasLibraryFolders));
        OnPropertyChanged(nameof(HasNewLibraryFolders));
        OnPropertyChanged(nameof(CanChooseImportNow));
    }

    /// <summary>Adds a library folder chosen in a picker (also used by tests).</summary>
    public bool AddLibraryFolder(string path)
    {
        ClearError();
        var check = _folders.Check(path);
        if (check.State is FolderCheckState.Invalid or FolderCheckState.Empty or FolderCheckState.WillBeCreated)
        {
            ErrorMessage = check.State == FolderCheckState.WillBeCreated
                ? $"{path} doesn't exist. Pick a folder that already holds music."
                : $"{path}: {check.Message}";
            return false;
        }
        if (LibraryFolders.Any(f => FirstRunDetector.SamePath(f.Path, path)))
        {
            ErrorMessage = "That folder is already in the list.";
            return false;
        }
        LibraryFolders.Add(new SetupLibraryFolderItem(path, watch: true, isExisting: false, remove: item => RemoveLibraryFolderCommand.Execute(item)));
        OnPropertyChanged(nameof(HasLibraryFolders));
        OnPropertyChanged(nameof(HasNewLibraryFolders));
        OnPropertyChanged(nameof(CanChooseImportNow));
        return true;
    }

    public override async Task<bool> CommitAsync()
    {
        ClearError();
        var download = DownloadDirectory.Trim();
        var downloadCheck = _folders.Check(download);
        DownloadCheck = downloadCheck;
        if (!downloadCheck.IsUsable)
        {
            ErrorMessage = "Download folder: " + downloadCheck.Message;
            return false;
        }

        var extra = ExtraSharedFolder?.Trim();
        if (extra != null)
        {
            var check = _folders.Check(extra);
            ExtraShareCheck = check;
            if (!check.IsUsable)
            {
                ErrorMessage = "Extra shared folder: " + check.Message;
                return false;
            }
        }

        var editLab = CustomEditLabFolder?.Trim();
        var editLabTarget = editLab ?? Path.Combine(download, DefaultEditLabFolderName);
        var editLabCheck = _folders.Check(editLabTarget);
        EditLabCheck = editLabCheck;
        if (!editLabCheck.IsUsable)
        {
            ErrorMessage = "Edit Lab output folder: " + editLabCheck.Message;
            return false;
        }

        // Create what's missing, only now that the step is confirmed.
        foreach (var folder in new[] { download, editLabTarget, extra })
        {
            if (folder == null) continue;
            if (!_folders.TryEnsure(folder, out var error))
            {
                ErrorMessage = error;
                return false;
            }
        }

        try
        {
            _settings.WriteFolders(new SetupFolderSettings(download, ShareOnSoulseek, extra, editLab));
            var confirmed = LibraryFolders.Select(f => new LibraryFolderSetupEntry(f.Path, f.Watch)).ToList();
            if (confirmed.Count > 0)
                AddedLibraryFolderCount += await _libraryFolders.AddAsync(confirmed);
            // "Import now": the import runs in the background (activity chip) while the wizard
            // carries on; rows show up in the Library as each batch lands.
            if (ImportNow && LibraryFolders.Any(f => f.IsNew) && !ImportStarted)
            {
                _libraryFolders.StartBackgroundScan();
                ImportStarted = true;
                _logger.LogInformation("Setup: library import started in the background from the Folders step");
            }
            DownloadCheck = _folders.Check(download);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: saving folders failed");
            ErrorMessage = "The folders couldn't be saved: " + ex.Message;
            return false;
        }
    }

    public override string SummaryTitle => "Folders";

    public override string SummaryDetail => Status switch
    {
        SetupStepStatus.Configured => $"Downloads to {DownloadDirectory.Trim()}"
                                      + (LibraryFolders.Count > 0 ? $" · {LibraryFolders.Count} library folder(s)" : string.Empty)
                                      + (ShareOnSoulseek ? " · sharing on" : " · sharing off"),
        SetupStepStatus.Skipped => "Skipped · downloads go to " + (_settings.ChosenDownloadDirectory ?? AppConfigFallbackDisplay),
        _ => "Not changed",
    };

    private void RecheckEditLab()
    {
        EditLabCheck = CustomEditLabFolder != null || !string.IsNullOrWhiteSpace(DownloadDirectory)
            ? _folders.Check(EditLabFolderDisplay)
            : new FolderCheck(FolderCheckState.Empty, string.Empty, null);
    }

    private async Task BrowseDownloadAsync()
    {
        var path = await _picker.OpenFolderDialogAsync("Choose the download folder");
        if (!string.IsNullOrEmpty(path)) DownloadDirectory = path;
    }

    private async Task AddLibraryFolderAsync()
    {
        var path = await _picker.OpenFolderDialogAsync("Add a music folder to the library");
        if (!string.IsNullOrEmpty(path)) AddLibraryFolder(path);
    }

    private async Task BrowseExtraShareAsync()
    {
        var path = await _picker.OpenFolderDialogAsync("Choose an extra folder to share");
        if (!string.IsNullOrEmpty(path)) ExtraSharedFolder = path;
    }

    private async Task BrowseEditLabAsync()
    {
        var path = await _picker.OpenFolderDialogAsync("Choose where Edit Lab saves renders");
        if (!string.IsNullOrEmpty(path)) CustomEditLabFolder = path;
    }
}
