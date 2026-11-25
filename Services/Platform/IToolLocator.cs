using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SLSKDONET.Services.Platform;

/// <summary>Native external tools ORBIT invokes as child processes (#453).</summary>
public enum ExternalTool
{
    Ffmpeg,
    Essentia,
    Fpcalc,
    Curl,
}

/// <summary>
/// Resolves native tool binaries (FFmpeg, Essentia, fpcalc, curl) across
/// <c>win-x64</c>, <c>osx-x64</c>, <c>osx-arm64</c>, and <c>linux-x64</c> (#453).
/// </summary>
public interface IToolLocator
{
    /// <summary>Normalized platform/CPU identifier: <c>win-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>, or <c>linux-x64</c>.</summary>
    string CurrentRid { get; }

    /// <summary>
    /// Returns the absolute path to <paramref name="tool"/> if found (bundled per-RID, bundled flat,
    /// ORBIT-managed install, Homebrew/system bin, or PATH), or null when not installed.
    /// </summary>
    string? Locate(ExternalTool tool);

    /// <summary>Convenience wrapper returning <c>Locate(tool) is not null</c>.</summary>
    bool IsAvailable(ExternalTool tool);

    /// <summary>Returns the ordered candidate paths checked for <paramref name="tool"/> (diagnostics and tests).</summary>
    IReadOnlyList<string> GetCandidatePaths(ExternalTool tool);
}

/// <summary>
/// Default cross-platform implementation of <see cref="IToolLocator"/> (#453).
/// Checks per-RID bundled folders first (<c>Tools/{rid}/</c>), then legacy flat folders,
/// ORBIT-managed installs, standard OS package-manager directories (including Homebrew
/// <c>/opt/homebrew/bin</c> on Apple Silicon, which Finder-launched GUI apps omit from <c>PATH</c>),
/// and finally <c>PATH</c>.
/// </summary>
public sealed class DefaultToolLocator : IToolLocator
{
    private readonly string _baseDirectory;
    private readonly string _rid;
    private readonly bool _isWindows;
    private readonly bool _isMacOS;
    private readonly string _pathEnv;
    private readonly string _systemDirectory;
    private readonly string? _managedFfmpegPath;
    private readonly Func<string, bool> _fileExists;

    public DefaultToolLocator(
        string? baseDirectory = null,
        string? rid = null,
        bool? isWindows = null,
        bool? isMacOS = null,
        string? pathEnv = null,
        string? systemDirectory = null,
        string? managedFfmpegPath = null,
        Func<string, bool>? fileExists = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _isMacOS = isMacOS ?? OperatingSystem.IsMacOS();
        _rid = rid ?? DetectRid(_isWindows, _isMacOS, RuntimeInformation.ProcessArchitecture);
        _pathEnv = pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        _systemDirectory = systemDirectory ?? Environment.SystemDirectory;
        _managedFfmpegPath = managedFfmpegPath ?? FfmpegInstallerService.ManagedFfmpegPath;
        _fileExists = fileExists ?? File.Exists;
    }

    public string CurrentRid => _rid;

    internal static string DetectRid(bool isWindows, bool isMacOS, Architecture arch)
    {
        var cpu = arch == Architecture.Arm64 ? "arm64" : "x64";
        if (isWindows) return $"win-{cpu}";
        if (isMacOS) return $"osx-{cpu}";
        return $"linux-{cpu}";
    }

    public string? Locate(ExternalTool tool)
        => GetCandidatePaths(tool).FirstOrDefault(_fileExists);

    public bool IsAvailable(ExternalTool tool) => Locate(tool) is not null;

    public IReadOnlyList<string> GetCandidatePaths(ExternalTool tool)
    {
        var names = ExecutableNames(tool, _isWindows);
        var candidates = new List<string>();

        // 1. Per-RID bundled directory (e.g. Tools/osx-arm64/ffmpeg, Tools/win-x64/essentia_streaming_extractor_music.exe)
        foreach (var name in names)
            candidates.Add(Path.Combine(_baseDirectory, "Tools", _rid, name));

        // 2. Bundled directly alongside the executable
        foreach (var name in names)
            candidates.Add(Path.Combine(_baseDirectory, name));

        // 3. Flat Tools/ and tool-specific legacy subdirectories
        foreach (var name in names)
            candidates.Add(Path.Combine(_baseDirectory, "Tools", name));

        if (tool == ExternalTool.Ffmpeg)
        {
            foreach (var name in names)
                candidates.Add(Path.Combine(_baseDirectory, "Tools", "ffmpeg", name));

            if (!string.IsNullOrWhiteSpace(_managedFfmpegPath))
                candidates.Add(_managedFfmpegPath);
        }
        else if (tool == ExternalTool.Essentia)
        {
            foreach (var name in names)
                candidates.Add(Path.Combine(_baseDirectory, "Tools", "essentia", name));
        }
        else if (tool == ExternalTool.Curl && _isWindows && !string.IsNullOrWhiteSpace(_systemDirectory))
        {
            // Windows 10 1803+ ships curl.exe in System32
            candidates.Add(Path.Combine(_systemDirectory, "curl.exe"));
        }

        // 4. System PATH
        if (!string.IsNullOrWhiteSpace(_pathEnv))
        {
            var sep = _isWindows ? ';' : ':';
            foreach (var rawDir in _pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = rawDir.Trim();
                if (dir.Length == 0) continue;
                foreach (var name in names)
                {
                    try { candidates.Add(Path.Combine(dir, name)); }
                    catch { /* intentional: ignore malformed PATH entries */ }
                }
            }
        }

        // 5. Well-known Homebrew / POSIX install prefixes (GUI apps on macOS don't inherit shell PATH)
        if (_isMacOS)
        {
            var brewPrefixes = _rid == "osx-arm64"
                ? new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }
                : new[] { "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin" };
            foreach (var prefix in brewPrefixes)
                foreach (var name in names)
                    candidates.Add(Path.Combine(prefix, name));
        }
        else if (!_isWindows)
        {
            foreach (var prefix in new[] { "/usr/local/bin", "/usr/bin" })
                foreach (var name in names)
                    candidates.Add(Path.Combine(prefix, name));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    internal static string[] ExecutableNames(ExternalTool tool, bool isWindows) => (tool, isWindows) switch
    {
        (ExternalTool.Ffmpeg, true) => ["ffmpeg.exe"],
        (ExternalTool.Ffmpeg, false) => ["ffmpeg"],
        (ExternalTool.Essentia, true) => ["essentia_streaming_extractor_music.exe", "streaming_extractor_music.exe"],
        (ExternalTool.Essentia, false) => ["essentia_streaming_extractor_music", "streaming_extractor_music"],
        (ExternalTool.Fpcalc, true) => ["fpcalc.exe"],
        (ExternalTool.Fpcalc, false) => ["fpcalc"],
        (ExternalTool.Curl, true) => ["curl.exe"],
        (ExternalTool.Curl, false) => ["curl"],
        _ => Array.Empty<string>(),
    };
}

/// <summary>Process-wide accessor for static tool resolution call sites (#453).</summary>
public static class ToolLocator
{
    private static IToolLocator? _current;

    public static IToolLocator Current => _current ??= new DefaultToolLocator();

    public static void SetCurrent(IToolLocator locator)
        => _current = locator ?? throw new ArgumentNullException(nameof(locator));
}
