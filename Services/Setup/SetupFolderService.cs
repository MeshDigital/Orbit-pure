using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Setup;

public enum FolderCheckState
{
    /// <summary>Nothing entered.</summary>
    Empty,
    /// <summary>Exists and is writable.</summary>
    Ready,
    /// <summary>Doesn't exist yet; will be created when the step is confirmed.</summary>
    WillBeCreated,
    /// <summary>Usable, but with a warning (low free space).</summary>
    Warning,
    /// <summary>Can't be used: invalid path, not writable, or can't be created.</summary>
    Invalid,
}

public sealed record FolderCheck(FolderCheckState State, string Message, long? FreeBytes)
{
    public bool IsUsable => State is FolderCheckState.Ready or FolderCheckState.WillBeCreated or FolderCheckState.Warning;
}

/// <summary>Folder validation for the setup wizard: checks without side effects, creates on confirm.</summary>
public interface ISetupFolderService
{
    /// <summary>Validates <paramref name="path"/> without creating or changing anything.</summary>
    FolderCheck Check(string? path);

    /// <summary>Creates the folder when it is missing. Returns false (with a reason) when that fails.</summary>
    bool TryEnsure(string path, out string? error);
}

public sealed class SetupFolderService : ISetupFolderService
{
    /// <summary>Below this a download folder gets a low-space warning.</summary>
    public const long LowSpaceBytes = 2L * 1024 * 1024 * 1024;

    private readonly ILogger<SetupFolderService>? _logger;
    private readonly Func<string, bool> _canWrite;
    private readonly Func<string, long?> _freeBytes;

    public SetupFolderService(ILogger<SetupFolderService>? logger = null)
        : this(logger, canWrite: null, freeBytes: null) { }

    /// <param name="canWrite">Writability probe for an existing directory (tests inject one).</param>
    /// <param name="freeBytes">Free-space probe (tests inject one).</param>
    public SetupFolderService(ILogger<SetupFolderService>? logger, Func<string, bool>? canWrite, Func<string, long?>? freeBytes)
    {
        _logger = logger;
        _canWrite = canWrite ?? ProbeWritable;
        _freeBytes = freeBytes ?? ProbeFreeBytes;
    }

    public FolderCheck Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FolderCheck(FolderCheckState.Empty, "Choose a folder.", null);

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path.Trim()))
                return new FolderCheck(FolderCheckState.Invalid, "Use a full path, for example C:\\Music\\ORBIT.", null);
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FolderCheck(FolderCheckState.Invalid, "That isn't a valid folder path.", null);
        }

        if (File.Exists(full))
            return new FolderCheck(FolderCheckState.Invalid, "That path is a file, not a folder.", null);

        var existing = Directory.Exists(full) ? full : NearestExistingAncestor(full);
        if (existing == null)
            return new FolderCheck(FolderCheckState.Invalid, "That drive isn't available.", null);

        if (!_canWrite(existing))
            return new FolderCheck(FolderCheckState.Invalid,
                existing == full ? "ORBIT can't write to this folder. Pick one you own." : "ORBIT can't create a folder here. Pick another location.",
                null);

        var free = _freeBytes(existing);
        if (free is { } bytes && bytes < LowSpaceBytes)
            return new FolderCheck(FolderCheckState.Warning, $"Only {FormatBytes(bytes)} free on this drive.", bytes);

        var freeText = free is { } f ? $" · {FormatBytes(f)} free" : string.Empty;
        return existing == full
            ? new FolderCheck(FolderCheckState.Ready, "Ready" + freeText, free)
            : new FolderCheck(FolderCheckState.WillBeCreated, "Will be created" + freeText, free);
    }

    public bool TryEnsure(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Setup: could not create folder {Path}", path);
            error = $"Couldn't create {path}: {ex.Message}";
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024d * 1024)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }

    private static string? NearestExistingAncestor(string full)
    {
        var dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir)) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private bool ProbeWritable(string directory)
    {
        var probe = Path.Combine(directory, $".orbit-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogInformation("Setup: {Directory} is not writable ({Error})", directory, ex.Message);
            return false;
        }
    }

    private long? ProbeFreeBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger?.LogDebug(ex, "Setup: free-space check failed for {Directory}", directory);
            return null;
        }
    }
}
