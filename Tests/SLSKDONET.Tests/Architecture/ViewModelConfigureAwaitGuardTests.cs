using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// ViewModels must not <c>ConfigureAwait(false)</c>: the continuation then runs on the thread pool,
/// and the next bound-property set or <c>ObservableCollection</c> change is a cross-thread UI update
/// (Avalonia throws or silently drops it). SQLite's async calls usually complete synchronously, which
/// hides the bug until one doesn't. Services may use it freely; a VM that really needs pool work
/// should push it into a service or <c>Task.Run</c> and await the result on the UI context.
/// <para>
/// RATCHET: files below predate this guard. Their count may only go down; lower it as they're
/// fixed, and delete the entry at zero. Never add a file or raise a number.
/// </para>
/// </summary>
public class ViewModelConfigureAwaitGuardTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ViewModels/FlowBuilderViewModel.cs"] = 10,
        ["ViewModels/PlayerViewModel.cs"] = 9,
        ["ViewModels/Library/ProjectListViewModel.cs"] = 7,
        ["ViewModels/LibraryDoubleInspectorViewModel.cs"] = 2,
        ["ViewModels/SimilarTracksViewModel.cs"] = 2,
        ["ViewModels/Library/VirtualizedTrackCollection.cs"] = 2,
        ["ViewModels/UserProfileViewModel.cs"] = 2,
        ["ViewModels/Workstation/WorkstationViewModel.cs"] = 2,
        ["ViewModels/PlaylistIntelligenceViewModel.cs"] = 2,
        ["ViewModels/LibraryViewModel.cs"] = 2,
        ["ViewModels/PlaylistTrackViewModel.cs"] = 1,
        ["ViewModels/Downloads/DownloadGroupViewModel.cs"] = 1,
        ["ViewModels/Downloads/UnifiedTrackViewModel.cs"] = 1,
        ["ViewModels/Library/TrackListViewModel.State.cs"] = 1,
        ["ViewModels/LibraryViewModel.Events.cs"] = 1,
        ["ViewModels/PlaylistTrackViewModel.Inspector.cs"] = 1,
        ["ViewModels/EnrichmentViewModel.cs"] = 1,
    };

    private static readonly Regex ConfigureAwaitFalse = new(@"\.ConfigureAwait\(\s*false\s*\)", RegexOptions.Compiled);

    [Fact]
    public void ViewModels_DoNotAddConfigureAwaitFalse()
    {
        var root = RepoGuard.Root;
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "ViewModels"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var count = ConfigureAwaitFalse.Matches(File.ReadAllText(file)).Count;
            var allowed = Baseline.TryGetValue(relative, out var max) ? max : 0;
            if (count > allowed)
                offenders.Add($"{relative}: {count} ConfigureAwait(false) (allowed {allowed})");
        }

        Assert.True(offenders.Count == 0,
            "ViewModels must resume on the UI context — drop ConfigureAwait(false):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Baseline_HasNoStaleEntries()
    {
        var root = RepoGuard.Root;
        var stale = Baseline
            .Where(kv =>
            {
                var path = Path.Combine(root, kv.Key);
                return !File.Exists(path) || ConfigureAwaitFalse.Matches(File.ReadAllText(path)).Count < kv.Value;
            })
            .Select(kv => $"{kv.Key} (baseline {kv.Value})")
            .ToList();

        Assert.True(stale.Count == 0,
            "Lower or remove these ratchet entries — the files improved:\n" + string.Join("\n", stale));
    }
}
