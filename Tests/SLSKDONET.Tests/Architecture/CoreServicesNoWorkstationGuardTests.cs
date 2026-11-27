using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Guard for #611: core Services/ and Engine/ code must not depend on Labs-only
/// <c>ViewModels.Workstation</c> types, so freezing or removing Workstation can never break
/// core playback, scoring, or Prep for Mix.
/// </summary>
public sealed class CoreServicesNoWorkstationGuardTests
{
    private static readonly HashSet<string> AllowedWorkstationAdapters = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dedicated Workstation deck keyboard shortcut dispatcher (not used by core playback/prep).
        Path.Combine("Services", "Input", "KeyboardEventRouter.cs"),
    };

    [Fact]
    public void ServicesAndEngine_DoNotReference_WorkstationViewModels()
    {
        var root = FindRepoRoot();
        var violations = new List<string>();

        foreach (var folder in new[] { "Services", "Engine" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (AllowedWorkstationAdapters.Contains(rel))
                    continue;

                foreach (var rawLine in File.ReadLines(file))
                {
                    var commentIdx = rawLine.IndexOf("//", StringComparison.Ordinal);
                    var code = commentIdx >= 0 ? rawLine[..commentIdx] : rawLine;
                    if (code.Contains("ViewModels.Workstation", StringComparison.Ordinal)
                        || code.Contains("WorkstationViewModel", StringComparison.Ordinal)
                        || code.Contains("WorkstationDeckViewModel", StringComparison.Ordinal))
                    {
                        violations.Add(rel);
                        break;
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Core Services/ and Engine/ must not reference ViewModels.Workstation (#611). Offending files:\n"
            + string.Join("\n", violations));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root.");
    }
}
