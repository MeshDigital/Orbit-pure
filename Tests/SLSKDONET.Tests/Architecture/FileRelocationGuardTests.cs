using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// #344 (LF2): every library-track file move goes through <c>FileRelocationService</c> — the single
/// move/rename path with validation, rollback, journal and undo. Any other <c>File.Move</c> in the
/// library domain is a regression that would skip those guarantees.
/// </summary>
public class FileRelocationGuardTests
{
    // Non-track files: atomic temp→final writes for config/view state, not library audio files.
    private static readonly string[] Allowlisted =
    {
        "ColumnConfigurationService.cs",          // column layout JSON
        "LibraryTrackListViewStateStore.cs",      // view state JSON
    };

    [Fact]
    public void NoFileMove_OnLibraryTracks_OutsideFileRelocationService()
    {
        var libraryDir = Path.Combine(RepoGuard.Root, "Services", "Library");

        var offenders = RepoGuard.EnumerateFiles(libraryDir, "*.cs", skipTestData: true)
            .Where(f => !RepoGuard.IsLegacy(f))
            .Where(f => !f.EndsWith("FileRelocationService.cs", StringComparison.Ordinal))
            .Where(f => !Allowlisted.Any(a => f.EndsWith(a, StringComparison.Ordinal)))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => x.line.Contains("File.Move(", StringComparison.Ordinal))
            .Select(x => $"{RepoGuard.Rel(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "File.Move on library tracks must go through FileRelocationService; found:\n  " +
            string.Join("\n  ", offenders));
    }
}
