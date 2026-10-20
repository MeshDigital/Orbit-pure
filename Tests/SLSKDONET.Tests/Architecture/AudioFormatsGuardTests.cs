using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Supported-format lists used to be duplicated across the importer, watcher, share index,
/// tagger, path resolver, Serato export, quality tiers and search ranking, each with a different
/// set: the importer's had no AIFF, so a real Rekordbox library imported without its .aif/.aiff
/// files. Services/Audio/AudioFormats.cs is now the only list; this guard fails when a new
/// hard-coded extension list appears anywhere else in production code.
/// </summary>
public class AudioFormatsGuardTests
{
    private static readonly string[] SourceDirs =
        { "Services", "ViewModels", "Views", "Models", "Utils", "Data", "Database", "Engine", "Configuration" };

    private const string Formats = "mp3|flac|wav|wave|aiff?|aifc|m4a|aac|alac|ogg|oga|opus|wma|ape|wv";

    /// <summary>Three or more dotted extension literals on one line (".mp3", ".flac", ".wav"): an extension list.</summary>
    private static readonly Regex DottedList = new($"(\"\\.(?:{Formats})\"[^\"\\n]*){{3,}}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Three or more bare format tokens on one line ("flac", "wav", "aiff").</summary>
    private static readonly Regex BareList = new($"(\"(?:{Formats})\"[^\"\\n]*){{3,}}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Files whose bare-token lists are user preferences or UI choices, not "which formats ORBIT
    /// supports" (that question is answered only by AudioFormats). Keep this list short.
    /// </summary>
    private static readonly Dictionary<string, string> PreferenceFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Configuration/AppConfig.cs"] = "default preferred / auto-download formats (user settings)",
        ["Configuration/ConfigManager.cs"] = "same defaults when config.ini has none",
        ["Services/AutoDownload/MatchScorer.cs"] = "auto-download allowed-format policy defaults",
        ["ViewModels/Downloads/DownloadCenterViewModel.cs"] = "recognises the user's format preset",
        ["ViewModels/SettingsViewModel.cs"] = "recognises the user's format preset",
        ["ViewModels/SearchFilterViewModel.cs"] = "the search format chips",
        ["ViewModels/SearchQueryTokenParser.cs"] = "query words that toggle the search format chips",
        ["ViewModels/SearchViewModel.cs"] = "formats requested from the network for the active chips",
        ["ViewModels/Workstation/ExportDialogViewModel.cs"] = "render output formats",
        ["ViewModels/Downloads/UnifiedTrackViewModel.cs"] = "badge text per format",
        ["Services/LibraryFolderScannerService.cs"] = "per-format quality score",
    };

    [Fact]
    public void NoHardCodedAudioExtensionLists_OutsideAudioFormats()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var rel = RepoGuard.Rel(file);
            if (rel.Equals("Services/Audio/AudioFormats.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (DottedList.IsMatch(code) || (BareList.IsMatch(code) && !PreferenceFiles.ContainsKey(rel)))
                    offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Hard-coded audio format lists found. Use SLSKDONET.Services.Audio.AudioFormats " +
            "(IsImportable / IsLossless / LocalAudioExtensions / RekordboxKind…) instead:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PreferenceAllowlist_HasNoStaleEntries()
    {
        foreach (var rel in PreferenceFiles.Keys)
            Assert.True(File.Exists(Path.Combine(RepoGuard.Root, rel)), $"Allowlisted file no longer exists: {rel}");
    }

    private static IEnumerable<string> SourceFiles() =>
        SourceDirs.Select(d => Path.Combine(RepoGuard.Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => RepoGuard.EnumerateFiles(d, "*.cs", skipTestData: true));

    private static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*")) return string.Empty;
        var idx = line.IndexOf(" //", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }
}
