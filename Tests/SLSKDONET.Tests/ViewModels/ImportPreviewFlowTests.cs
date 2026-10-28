using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Import;
using SLSKDONET.Utils;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// End-to-end behaviour of the import preview without a UI, DB or network: row status,
/// destination choice, duplicate suggestion, the footer sentence and what the commit sends.
/// </summary>
public class ImportPreviewFlowTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    internal sealed class FakeQueue : IImportQueueGateway
    {
        public List<PlaylistJob> Committed { get; } = new();
        public HashSet<string> InFlightTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? FailWith { get; set; }

        public Task<ProjectQueueResult> QueueProjectAsync(PlaylistJob job)
        {
            if (FailWith != null) return Task.FromException<ProjectQueueResult>(FailWith);
            Committed.Add(job);
            return Task.FromResult(new ProjectQueueResult(job.OriginalTracks.Count, 1, 0, 0, 0, 0));
        }

        public bool IsInFlight(string? spotifyTrackId, string? artist, string? title)
            => title != null && InFlightTitles.Contains(title);
    }

    private sealed class Harness
    {
        public FakeQueue Queue { get; } = new();
        public List<LibraryEntry> Library { get; } = new();
        public List<PlaylistJob> Playlists { get; } = new();
        public Dictionary<Guid, List<PlaylistTrack>> PlaylistRows { get; } = new();
        public Mock<ILibraryService> LibraryService { get; } = new();

        public ImportPreviewViewModel Create()
        {
            LibraryService.Setup(l => l.LoadAllLibraryEntriesAsync()).ReturnsAsync(() => Library.ToList());
            LibraryService.Setup(l => l.LoadAllPlaylistJobsAsync()).ReturnsAsync(() => Playlists.ToList());
            LibraryService.Setup(l => l.LoadPlaylistTracksAsync(It.IsAny<Guid>()))
                .ReturnsAsync((Guid id) => PlaylistRows.TryGetValue(id, out var rows) ? rows.ToList() : new List<PlaylistTrack>());

            var vm = new ImportPreviewViewModel(
                NullLogger<ImportPreviewViewModel>.Instance,
                Queue,
                new Mock<INavigationService>().Object,
                LibraryService.Object);
            vm.RunOnUiThread = a => { a(); return Task.CompletedTask; };
            return vm;
        }

        public LibraryEntry Own(string artist, string title, string? hash = null)
        {
            var e = new LibraryEntry
            {
                Artist = artist,
                Title = title,
                UniqueHash = hash ?? TrackHashUtil.Compute(artist, title),
                FilePath = $@"C:\music\{artist} - {title}.flac"
            };
            Library.Add(e);
            return e;
        }

        public PlaylistJob Playlist(string title, string type = "Spotify", string? url = null, params PlaylistTrack[] rows)
        {
            var job = new PlaylistJob
            {
                Id = Guid.NewGuid(),
                SourceTitle = title,
                SourceType = type,
                SourceUrl = url,
                TotalTracks = rows.Length,
                CreatedAt = DateTime.UtcNow.AddDays(-Playlists.Count - 1)
            };
            Playlists.Add(job);
            PlaylistRows[job.Id] = rows.ToList();
            return job;
        }
    }

    private static SearchQuery Q(string artist, string title) => new() { Artist = artist, Title = title };

    private static PlaylistTrack Row(string artist, string title, TrackStatus status = TrackStatus.Downloaded, string? hash = null) => new()
    {
        Id = Guid.NewGuid(),
        Artist = artist,
        Title = title,
        Status = status,
        TrackUniqueHash = hash ?? TrackHashUtil.Compute(artist, title)
    };

    private static async Task<ImportPreviewViewModel> LoadAsync(Harness h, string title, IEnumerable<SearchQuery> queries,
        PlaylistJob? existing = null, string type = "Spotify")
    {
        var vm = h.Create();
        vm.InitializeStreamingPreview(type, type, Guid.NewGuid(), "https://open.spotify.com/playlist/abcdefghijklmnopqrstuv", existing);
        vm.SourceTitle = title;
        await vm.AddTracksToPreviewAsync(queries);
        vm.CompleteLoading();
        return vm;
    }

    private static SelectableTrack RowFor(ImportPreviewViewModel vm, string title) =>
        vm.ImportedTracks.First(r => r.Model.Title == title);

    // ── Row status & summary ───────────────────────────────────────────────────

    [Fact]
    public async Task Rows_AreLabelled_OwnedNewQueuedRepeatNoMatch_AndCounted()
    {
        var h = new Harness();
        h.Own("Bicep", "Glue");
        h.Queue.InFlightTitles.Add("Opal");

        var vm = await LoadAsync(h, "Night Drive", new[]
        {
            Q("Bicep", "Glue"),       // owned
            Q("Four Tet", "Baby"),    // new
            Q("Bicep", "Opal"),       // already downloading
            Q("Four Tet", "Baby"),    // repeat
            Q("Someone", "")          // no title
        });

        Assert.Equal(ImportRowStatus.Owned, RowFor(vm, "Glue").Status);
        Assert.Equal(ImportRowStatus.New, vm.ImportedTracks[1].Status);
        Assert.Equal(ImportRowStatus.Queued, RowFor(vm, "Opal").Status);
        Assert.Equal(ImportRowStatus.Repeat, vm.ImportedTracks[3].Status);
        Assert.Equal(ImportRowStatus.NoMatch, vm.ImportedTracks[4].Status);

        Assert.Equal(5, vm.TotalCount);
        Assert.Equal(1, vm.OwnedCount);
        Assert.Equal(1, vm.NewCount);
        Assert.Equal(1, vm.QueuedCount);
        Assert.Equal(2, vm.NoMatchCount);

        // Repeats and unsearchable rows start unchecked; everything else is included.
        Assert.False(vm.ImportedTracks[3].IsSelected);
        Assert.False(vm.ImportedTracks[4].IsSelected);
        Assert.Equal(3, vm.EffectiveSelectedCount);
        Assert.Equal("Add 3 tracks to new playlist 'Night Drive' · 1 already owned, 1 will download, 1 already downloading",
            vm.CommitSummary);
        Assert.Equal("Import 3 tracks", vm.CommitButtonLabel);
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task OwnedTracks_AreIncludedByDefault_SoThePlaylistIsComplete()
    {
        // The old default ("select missing") left owned songs out of the new playlist entirely.
        var h = new Harness();
        h.Own("A", "One");
        h.Own("B", "Two");
        var vm = await LoadAsync(h, "Mix", new[] { Q("A", "One"), Q("B", "Two"), Q("C", "Three") });

        Assert.All(vm.ImportedTracks, r => Assert.True(r.IsSelected));
        Assert.True(vm.ShowAllOwnedNotice == false); // one track still needs downloading
    }

    [Fact]
    public async Task AllOwned_ShowsNotice_AndStillAllowsCreatingThePlaylist()
    {
        var h = new Harness();
        h.Own("A", "One");
        var vm = await LoadAsync(h, "Mix", new[] { Q("A", "One") });

        Assert.True(vm.ShowAllOwnedNotice);
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Filter_ShowsOnlyMatchingRows_AndAllRestoresThem()
    {
        var h = new Harness();
        h.Own("A", "One");
        var vm = await LoadAsync(h, "Mix", new[] { Q("A", "One"), Q("B", "Two"), Q("C", "Three") });

        vm.IsFilterOwned = true;
        Assert.Single(vm.VisibleTracks);
        Assert.Equal("One", vm.VisibleTracks[0].Model.Title);

        vm.IsFilterAll = true;
        Assert.Equal(3, vm.VisibleTracks.Count);
    }

    [Fact]
    public async Task SelectMissing_ChecksOnlyTracksThatNeedDownloading()
    {
        var h = new Harness();
        h.Own("A", "One");
        var vm = await LoadAsync(h, "Mix", new[] { Q("A", "One"), Q("B", "Two") });

        vm.SelectMissingCommand.Execute(null);

        Assert.False(RowFor(vm, "One").IsSelected);
        Assert.True(RowFor(vm, "Two").IsSelected);
        Assert.Equal(1, vm.EffectiveSelectedCount);
    }

    [Fact]
    public async Task EditingARow_RematchesItAgainstTheLibrary()
    {
        var h = new Harness();
        h.Own("Bicep", "Glue");
        var vm = await LoadAsync(h, "Mix", new[] { Q("Bicep", "Atlas") });
        var row = vm.ImportedTracks[0];
        Assert.NotEqual(ImportRowStatus.Owned, row.Status);

        row.Title = "Glue";
        await Task.Delay(50); // re-match runs async (library index task already completed)

        Assert.Equal(ImportRowStatus.Owned, row.Status);
    }

    [Fact]
    public void DurationDisplay_UsesSecondsOrSpotifyMilliseconds()
    {
        Assert.Equal("3:05", new SelectableTrack(new Track { Length = 185 }).DurationDisplay);
        Assert.Equal("4:00", new SelectableTrack(new Track { CanonicalDuration = 240_000 }).DurationDisplay);
        Assert.Equal("–", new SelectableTrack(new Track()).DurationDisplay);
    }

    // ── Destination: existing playlist ──────────────────────────────────────────

    [Fact]
    public async Task AddToExisting_MarksTracksAlreadyThere_AndRetriesFailedOnes()
    {
        var h = new Harness();
        var target = h.Playlist("Techno", "User", null,
            Row("A", "One"),
            Row("B", "Two", TrackStatus.Failed));
        var vm = await LoadAsync(h, "Pasted", new[] { Q("A", "One"), Q("B", "Two"), Q("C", "Three") }, type: "Pasted Tracklist");

        vm.IsMergeMode = true;
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));
        Assert.Equal("Choose a playlist to add to", vm.CommitBlockedReason);

        vm.SelectedMergeTarget = vm.MergeTargets.Single(t => t.Job.Id == target.Id);

        Assert.Equal(ImportRowStatus.InPlaylist, RowFor(vm, "One").Status);
        Assert.False(RowFor(vm, "One").IsSelected);
        Assert.Equal(ImportRowStatus.Retry, RowFor(vm, "Two").Status);
        Assert.True(RowFor(vm, "Two").IsSelected);
        Assert.Equal(ImportRowStatus.New, RowFor(vm, "Three").Status);
        Assert.Equal("Add 2 to 'Techno'", vm.CommitButtonLabel);
        Assert.EndsWith("· 1 already there", vm.CommitSummary);
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddToExisting_RecognisesRowsStoredUnderTheLibraryFilesHash()
    {
        // QueueProject links owned tracks under the library file's own hash. Matching on the
        // incoming hash alone missed those, so every re-import appended them again.
        var h = new Harness();
        var owned = h.Own("Bicep feat. Clara", "Glue (Original Mix)", hash: "library-file-hash");
        h.Library[0].SpotifyTrackId = "sp1";
        var target = h.Playlist("Night Drive", "Spotify", null, Row("Bicep feat. Clara", "Glue (Original Mix)", hash: owned.UniqueHash));

        var vm = h.Create();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "https://open.spotify.com/playlist/x", target);
        await vm.AddTracksToPreviewAsync(new[] { new SearchQuery { Artist = "Bicep", Title = "Glue", SpotifyTrackId = "sp1" } });
        vm.CompleteLoading();

        Assert.Equal(ImportRowStatus.InPlaylist, vm.ImportedTracks[0].Status);
    }

    [Fact]
    public async Task AddToExisting_WhenEverythingIsAlreadyThere_ExplainsWhyItIsDisabled()
    {
        var h = new Harness();
        var target = h.Playlist("Techno", "User", null, Row("A", "One"));
        var vm = await LoadAsync(h, "Pasted", new[] { Q("A", "One") });

        vm.IsMergeMode = true;
        vm.SelectedMergeTarget = vm.MergeTargets.Single(t => t.Job.Id == target.Id);

        Assert.False(vm.AddToLibraryCommand.CanExecute(null));
        Assert.Equal("Everything is already in 'Techno'", vm.CommitBlockedReason);
    }

    [Fact]
    public async Task TargetSearch_FiltersThePicker_ButKeepsTheSelectedPlaylist()
    {
        var h = new Harness();
        var techno = h.Playlist("Techno", "User");
        h.Playlist("House", "User");
        h.Playlist("Ambient", "User");
        var vm = await LoadAsync(h, "Pasted", new[] { Q("A", "One") });

        vm.IsMergeMode = true;
        vm.SelectedMergeTarget = vm.MergeTargets.Single(t => t.Job.Id == techno.Id);
        vm.TargetSearchText = "amb";

        Assert.Equal(2, vm.FilteredMergeTargets.Count);
        Assert.Contains(vm.FilteredMergeTargets, t => t.Title == "Ambient");
        Assert.Contains(vm.FilteredMergeTargets, t => t.Title == "Techno");
        Assert.Same(techno, vm.SelectedMergeTarget!.Job);
    }

    // ── Duplicate detection / re-import ──────────────────────────────────────

    [Fact]
    public async Task ReImportOfSameSource_DefaultsToUpdating_AndCountsWhatIsNew()
    {
        var h = new Harness();
        var earlier = h.Playlist("Night Drive", "Spotify", "https://open.spotify.com/playlist/abcdefghijklmnopqrstuv",
            Row("A", "One"), Row("B", "Two"));
        var vm = await LoadAsync(h, "Night Drive", new[] { Q("A", "One"), Q("B", "Two"), Q("C", "Three") }, existing: earlier);

        Assert.True(vm.IsMergeMode);
        Assert.True(vm.IsUpdatingSameSource);
        Assert.True(vm.ShowSuggestion);
        Assert.Equal(ImportSuggestionReason.SameSource, vm.SuggestionReason);
        Assert.Equal(1, vm.SuggestionNewCount);
        Assert.Equal("Update 'Night Drive' (+1)", vm.CommitButtonLabel);

        vm.AcceptSuggestionCommand.Execute(null);
        Assert.False(vm.ShowSuggestion);
        Assert.True(vm.IsMergeMode);
    }

    [Fact]
    public async Task ReImport_ImportAsNew_CreatesASeparatePlaylistWithAFreshId()
    {
        var h = new Harness();
        var earlier = h.Playlist("Night Drive", "Spotify", "https://open.spotify.com/playlist/abc");
        var vm = h.Create();
        // The orchestrator passes the deterministic id; here it equals the earlier playlist's id.
        vm.InitializeStreamingPreview("Spotify", "Spotify", earlier.Id, "https://open.spotify.com/playlist/abc", earlier);
        await vm.AddTracksToPreviewAsync(new[] { Q("A", "One") });
        vm.CompleteLoading();

        vm.ImportAsNewCommand.Execute(null);
        Assert.False(vm.IsMergeMode);
        Assert.Equal(ImportRowStatus.New, vm.ImportedTracks[0].Status);

        await ((SLSKDONET.Views.AsyncRelayCommand)vm.AddToLibraryCommand).ExecuteAsync(null);

        var job = Assert.Single(h.Queue.Committed);
        Assert.NotEqual(earlier.Id, job.Id);
        Assert.Equal("Spotify", job.SourceTitle); // name defaults to the source title
    }

    [Fact]
    public async Task SameNamedPlaylist_IsSuggested_AfterLoading()
    {
        var h = new Harness();
        var existing = h.Playlist("Night Drive", "User", null, Row("A", "One"));
        var vm = await LoadAsync(h, "Night Drive", new[] { Q("A", "One"), Q("B", "Two") });

        Assert.Equal(ImportSuggestionReason.SameName, vm.SuggestionReason);
        Assert.True(vm.ShowSuggestion);
        Assert.Same(existing, vm.SuggestedTarget!.Job);
        Assert.Equal(1, vm.SuggestionNewCount);
        Assert.False(vm.IsMergeMode); // a name match only suggests; it doesn't switch for you
    }

    [Fact]
    public async Task HighOverlap_IsSuggested_AfterLoading()
    {
        var h = new Harness();
        var existing = h.Playlist("Older copy", "Spotify", null, Row("A", "One"), Row("B", "Two"), Row("C", "Three"));
        var vm = await LoadAsync(h, "Brand new name", new[] { Q("A", "One"), Q("B", "Two"), Q("C", "Three"), Q("D", "Four") });

        Assert.Equal(ImportSuggestionReason.HighOverlap, vm.SuggestionReason);
        Assert.Same(existing, vm.SuggestedTarget!.Job);
    }

    [Fact]
    public async Task NameCollision_IsFlagged_AndCanSwitchToThatPlaylist()
    {
        var h = new Harness();
        var techno = h.Playlist("Techno", "User");
        var vm = await LoadAsync(h, "Pasted", new[] { Q("A", "One") });

        vm.PlaylistName = "techno";
        Assert.True(vm.ShowNameCollision);

        vm.UseCollidingPlaylistCommand.Execute(null);
        Assert.True(vm.IsMergeMode);
        Assert.Same(techno, vm.SelectedMergeTarget!.Job);
    }

    [Fact]
    public async Task PlaylistName_FollowsTheSourceTitle_UntilTheUserEditsIt()
    {
        var h = new Harness();
        var vm = h.Create();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "u");
        Assert.Equal("Spotify", vm.PlaylistName);

        vm.SourceTitle = "Night Drive";
        Assert.Equal("Night Drive", vm.PlaylistName);

        vm.PlaylistName = "My Night Drive";
        vm.SourceTitle = "Something else";
        Assert.Equal("My Night Drive", vm.PlaylistName);

        vm.PlaylistName = "   ";
        await vm.AddTracksToPreviewAsync(new[] { Q("A", "One") });
        vm.CompleteLoading();
        Assert.Equal("Name the new playlist", vm.CommitBlockedReason);
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitToExisting_KeepsThePlaylistsOwnNameTypeAndLink()
    {
        // Regression: merging used to stamp the incoming source's title/type/URL onto the target,
        // renaming "Techno" to "Pasted Tracklist (…)".
        var h = new Harness();
        var techno = h.Playlist("Techno", "User", "https://example.com/original");
        var vm = await LoadAsync(h, "Pasted Tracklist (2026-10-17)", new[] { Q("A", "One") }, type: "Pasted Tracklist");

        vm.IsMergeMode = true;
        vm.SelectedMergeTarget = vm.MergeTargets.Single(t => t.Job.Id == techno.Id);
        await ((SLSKDONET.Views.AsyncRelayCommand)vm.AddToLibraryCommand).ExecuteAsync(null);

        var job = Assert.Single(h.Queue.Committed);
        Assert.Equal(techno.Id, job.Id);
        Assert.Equal("Techno", job.SourceTitle);
        Assert.Equal("User", job.SourceType);
        Assert.Equal("https://example.com/original", job.SourceUrl);
    }

    [Fact]
    public async Task Commit_SendsOnlyIncludedRows_ReportsTheResult_AndIsIdempotent()
    {
        var h = new Harness();
        var vm = await LoadAsync(h, "Night Drive", new[] { Q("A", "One"), Q("B", "Two"), Q("B", "Two") });
        RowFor(vm, "One").IsSelected = false;

        PlaylistJob? raised = null;
        vm.AddedToLibrary += (_, j) => raised = j;

        var command = (SLSKDONET.Views.AsyncRelayCommand)vm.AddToLibraryCommand;
        await command.ExecuteAsync(null);
        await command.ExecuteAsync(null); // a second click must not import again

        var job = Assert.Single(h.Queue.Committed);
        Assert.Same(job, raised);
        Assert.Single(job.OriginalTracks); // the repeat and the unchecked row are not sent
        Assert.Equal("Two", job.OriginalTracks[0].Title);
        Assert.Equal("Night Drive", job.SourceTitle);
        Assert.Contains("added to 'Night Drive'", vm.LastCommitSummary);
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));
        Assert.Equal("Already imported", vm.CommitBlockedReason);
    }

    [Fact]
    public async Task CommitFailure_StaysOnThePage_ShowsTheError_AndCanBeRetried()
    {
        var h = new Harness();
        h.Queue.FailWith = new InvalidOperationException("database is locked");
        var vm = await LoadAsync(h, "Night Drive", new[] { Q("A", "One") });

        var raised = false;
        vm.AddedToLibrary += (_, _) => raised = true;
        await ((SLSKDONET.Views.AsyncRelayCommand)vm.AddToLibraryCommand).ExecuteAsync(null);

        Assert.False(raised);
        Assert.True(vm.HasCommitError);
        Assert.Contains("database is locked", vm.CommitError);
        Assert.True(vm.AddToLibraryCommand.CanExecute(null)); // not marked as committed
    }

    [Fact]
    public void CommitSummary_ForAnUpdateThatChangedNothing_SaysSo()
    {
        var text = ImportPreviewViewModel.BuildCommitSummary(
            new PlaylistJob { SourceTitle = "Night Drive" }, new ProjectQueueResult(0, 0, 0, 0, 5, 0));
        Assert.Equal("Nothing new to add to 'Night Drive'. Every track was already there.", text);
    }

    // ── Load / error states ───────────────────────────────────────────────────

    [Fact]
    public void ErrorBeforeAnyTracks_ShowsTheErrorState_AndBlocksTheCommit()
    {
        var h = new Harness();
        var vm = h.Create();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "u");
        Assert.True(vm.ShowLoadingState);

        vm.ShowError("Spotify sign-in expired.", requiresSignIn: true);

        Assert.True(vm.ShowErrorState);
        Assert.False(vm.ShowLoadingState);
        Assert.True(vm.ErrorRequiresSignIn);
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));

        var retried = false;
        vm.RetryRequested += (_, _) => retried = true;
        vm.RetryCommand.Execute(null);
        Assert.True(retried);
    }

    [Fact]
    public async Task ErrorAfterSomeTracks_ShowsABanner_AndKeepsTheRows()
    {
        var h = new Harness();
        var vm = h.Create();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "u");
        vm.ReportExpectedTotal(300);
        await vm.AddTracksToPreviewAsync(new[] { Q("A", "One") });
        Assert.Equal("Loading tracks… 1 of 300", vm.LoadProgressText);

        vm.ShowError("Spotify stopped responding after 1 of 300 tracks.");

        Assert.True(vm.ShowErrorBanner);
        Assert.False(vm.ShowErrorState);
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public void EmptySource_ShowsTheEmptyState()
    {
        var h = new Harness();
        var vm = h.Create();
        vm.InitializeStreamingPreview("CSV", "CSV", Guid.NewGuid(), @"C:\x.csv");
        vm.CompleteLoading();

        Assert.True(vm.ShowEmptyState);
        Assert.Equal("Nothing to import", vm.CommitBlockedReason);
    }

    [Fact]
    public async Task Cancel_RaisesCancelled_AndDropsBatchesThatArriveLater()
    {
        var h = new Harness();
        var vm = h.Create();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "u");
        var cancelled = false;
        vm.Cancelled += (_, _) => cancelled = true;

        vm.CancelCommand.Execute(null);
        await vm.AddTracksToPreviewAsync(new[] { Q("A", "One") });

        Assert.True(cancelled);
        Assert.Empty(vm.ImportedTracks);
    }
}
