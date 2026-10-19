using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services;

/// <summary>The four phases the import sheet shows, in order.</summary>
public enum LibraryImportPhase
{
    Scanning = 0,
    Reading = 1,
    Adding = 2,
    Done = 3,
}

/// <summary>What started an import. Only <see cref="Watch"/> imports are small background trickles.</summary>
public enum LibraryImportTrigger
{
    /// <summary>The user added or scanned a folder (import sheet).</summary>
    Manual,
    /// <summary>A watched folder saw new files appear.</summary>
    Watch,
    /// <summary>A maintenance path (Library Health rescan, legacy callers).</summary>
    Maintenance,
}

public enum LibraryImportIssueKind
{
    Unsupported,
    Unreadable,
    SaveFailed,
    FolderMissing,
    Failed,
}

/// <summary>One file (or folder) the import could not bring into the library, and why.</summary>
public sealed record LibraryImportIssue(LibraryImportIssueKind Kind, string Path, string Reason)
{
    public string FileName => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    public string KindLabel => Kind switch
    {
        LibraryImportIssueKind.Unsupported => "Unsupported",
        LibraryImportIssueKind.Unreadable => "Unreadable",
        LibraryImportIssueKind.SaveFailed => "Not saved",
        LibraryImportIssueKind.FolderMissing => "Folder missing",
        _ => "Error",
    };
}

public sealed class LibraryImportRequest
{
    /// <summary>Library Sources to import; null means every enabled one.</summary>
    public IReadOnlyList<Guid>? FolderIds { get; init; }

    /// <summary>Import only these files (a watch-folder event) instead of walking the folders.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Queue analysis for every added track as soon as the import ends (watch imports).</summary>
    public bool QueueAnalysis { get; init; }

    public LibraryImportTrigger Trigger { get; init; } = LibraryImportTrigger.Manual;

    /// <summary>Rows written per database transaction.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Minimum time between progress reports (phase changes always report).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>An immutable progress snapshot. Reported at most ~10 times a second.</summary>
public sealed record LibraryImportProgress(
    LibraryImportPhase Phase,
    int FilesFound,
    int Processed,
    int Total,
    string CurrentFile,
    int Added,
    int AlreadyInLibrary,
    int Duplicates,
    int Upgraded,
    int Unsupported,
    int Unreadable,
    int Failed)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Processed / Total, 0, 1);
}

public sealed class LibraryImportResult
{
    public LibraryImportTrigger Trigger { get; init; }
    public List<Guid> FolderIds { get; } = new();
    public List<string> FolderPaths { get; } = new();

    /// <summary>Supported audio files found on disk.</summary>
    public int FilesFound { get; set; }
    public int Added { get; set; }
    /// <summary>The same file was already in the library.</summary>
    public int AlreadyInLibrary { get; set; }
    /// <summary>A different file of a track already in the library (same artist/title identity).</summary>
    public int Duplicates { get; set; }
    /// <summary>Duplicates that replaced a lower-quality copy.</summary>
    public int Upgraded { get; set; }
    public int Unsupported { get; set; }
    public int Unreadable { get; set; }
    /// <summary>Files that were read but could not be written to the library.</summary>
    public int Failed { get; set; }

    public bool WasCancelled { get; set; }
    /// <summary>Set when the import stopped on an unexpected error.</summary>
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }

    public List<LibraryImportIssue> Issues { get; } = new();
    public List<Guid> AddedEntryIds { get; } = new();
    public List<string> AddedTrackHashes { get; } = new();

    /// <summary>Unsupported + unreadable + not saved.</summary>
    public int ProblemCount => Unsupported + Unreadable + Failed;

    /// <summary>True when the library's contents changed and views should refresh.</summary>
    public bool ChangedLibrary => Added > 0 || Upgraded > 0;
}

/// <summary>Which files the importer brings in, and which audio-looking files it reports as unsupported.</summary>
public static class LibraryAudioFormats
{
    public enum Kind { NotAudio, Supported, Unsupported }

    /// <summary>Formats ORBIT plays, tags and analyses.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg",
    };

    /// <summary>Audio formats found in music folders that ORBIT can't import yet; counted, not added.</summary>
    public static readonly IReadOnlySet<string> KnownUnsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".opus", ".wma", ".ape", ".aiff", ".aif", ".aifc", ".alac", ".wv", ".dsf", ".dff",
        ".mka", ".mpc", ".tta", ".m4b", ".caf", ".mp2", ".ac3", ".amr",
    };

    public static Kind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return Kind.NotAudio;
        if (Supported.Contains(ext)) return Kind.Supported;
        if (KnownUnsupported.Contains(ext)) return Kind.Unsupported;
        return Kind.NotAudio;
    }
}
