using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Unit tests for UserCollectionViewModel — tree building, filtering, sorting, and statistics.
/// </summary>
public class UserCollectionViewModelTests
{
    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static UserCollectionViewModel BuildSut(
        IEnumerable<Track>? sharesResult = null,
        Exception? sharesException = null)
    {
        var soulseekMock = new Mock<ISoulseekAdapter>();

        if (sharesException != null)
        {
            soulseekMock
                .Setup(s => s.GetUserSharesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(sharesException);
        }
        else
        {
            soulseekMock
                .Setup(s => s.GetUserSharesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(sharesResult ?? Enumerable.Empty<Track>());
        }

        // DownloadManager is a heavy concrete class; pass null for tests that don't exercise queueing.
        return new UserCollectionViewModel(
            NullLogger<UserCollectionViewModel>.Instance,
            soulseekMock.Object,
            null!);
    }

    private static Track MakeTrack(string filename, string? artist = null, string? title = null,
        string? album = null, string? username = "peer", string? format = null, int bitrate = 320,
        int? sampleRate = null)
    {
        return new Track
        {
            Filename = filename,
            Artist = artist,
            Title = title,
            Album = album,
            Username = username,
            Format = format,
            Bitrate = bitrate,
            SampleRate = sampleRate
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // LoadUserAsync — edge cases
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadUserAsync_EmptyUsername_SetsStatusTextAndSkipsLoad()
    {
        var sut = BuildSut();

        await sut.LoadUserAsync(string.Empty);

        Assert.Equal("Missing username.", sut.StatusText);
        Assert.False(sut.IsLoading);
        Assert.Empty(sut.RootNodes);
    }

    [Fact]
    public async Task LoadUserAsync_WhitespaceUsername_SetsStatusTextAndSkipsLoad()
    {
        var sut = BuildSut();

        await sut.LoadUserAsync("   ");

        Assert.Equal("Missing username.", sut.StatusText);
        Assert.False(sut.IsLoading);
    }

    [Fact]
    public async Task LoadUserAsync_ValidUsername_SetsCurrentUsername()
    {
        var sut = BuildSut();

        await sut.LoadUserAsync("testUser");

        Assert.Equal("testUser", sut.CurrentUsername);
    }

    [Fact]
    public async Task LoadUserAsync_NoShares_SetsNoSharesStatus()
    {
        var sut = BuildSut(sharesResult: Enumerable.Empty<Track>());

        await sut.LoadUserAsync("emptyUser");

        Assert.Contains("emptyUser", sut.StatusText);
        Assert.Empty(sut.RootNodes);
        Assert.False(sut.IsLoading);
    }

    [Fact]
    public async Task LoadUserAsync_WithShares_BuildsTreeAndSetsStatus()
    {
        var shares = new[]
        {
            MakeTrack(@"Music\Rock\track1.flac", format: "flac"),
            MakeTrack(@"Music\Pop\track2.mp3", format: "mp3")
        };

        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("richUser");

        Assert.NotEmpty(sut.RootNodes);
        Assert.False(sut.IsLoading);
    }

    [Fact]
    public async Task LoadUserAsync_ExceptionFromAdapter_SetsErrorStatusAndClearsLoadingFlag()
    {
        var ex = new InvalidOperationException("network failure");
        var sut = BuildSut(sharesException: ex);

        await sut.LoadUserAsync("badUser");

        Assert.Contains("Failed to load", sut.StatusText);
        Assert.Contains("network failure", sut.StatusText);
        Assert.False(sut.IsLoading);
    }

    [Fact]
    public async Task LoadUserAsync_SecondCall_ClearsPreviousTreeAndReloads()
    {
        var firstShares = new[] { MakeTrack(@"Folder\track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: firstShares);

        await sut.LoadUserAsync("user1");
        Assert.NotEmpty(sut.RootNodes);

        // second load with same mock returning same data
        await sut.LoadUserAsync("user2");

        // After reloading the previous tree should have been cleared and rebuilt
        Assert.Equal("user2", sut.CurrentUsername);
    }

    // ─────────────────────────────────────────────────────────────────
    // Tree Building
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildTree_SingleLevelFile_AppearsAsRootFileNode()
    {
        var shares = new[] { MakeTrack("track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);

        await sut.LoadUserAsync("peer");

        Assert.Single(sut.RootNodes);
        Assert.IsType<UserCollectionFileNodeViewModel>(sut.RootNodes[0]);
    }

    [Fact]
    public async Task BuildTree_TwoLevelPath_CreatesRootFolderWithChildFile()
    {
        var shares = new[] { MakeTrack(@"Music\track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);

        await sut.LoadUserAsync("peer");

        var rootFolder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal("Music", rootFolder.Name);
        Assert.Single(rootFolder.ChildNodes);
        Assert.IsType<UserCollectionFileNodeViewModel>(rootFolder.ChildNodes[0]);
    }

    [Fact]
    public async Task BuildTree_ThreeLevelPath_CreatesNestedFolderHierarchy()
    {
        var shares = new[] { MakeTrack(@"Music\Rock\track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);

        await sut.LoadUserAsync("peer");

        var root = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal("Music", root.Name);

        var sub = Assert.IsType<UserCollectionFolderNodeViewModel>(root.ChildNodes[0]);
        Assert.Equal("Rock", sub.Name);

        Assert.IsType<UserCollectionFileNodeViewModel>(sub.ChildNodes[0]);
    }

    [Fact]
    public async Task BuildTree_ForwardSlashPathSeparator_IsHandledCorrectly()
    {
        var shares = new[] { MakeTrack("Music/Rock/track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);

        await sut.LoadUserAsync("peer");

        var root = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal("Music", root.Name);
    }

    [Fact]
    public async Task BuildTree_SameFolderMultipleTracks_MergesUnderOneFolderNode()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track1.flac", format: "flac"),
            MakeTrack(@"Folder\track2.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);

        await sut.LoadUserAsync("peer");

        // Both tracks share the same folder — only one root folder node
        Assert.Single(sut.RootNodes);
        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal(2, folder.ChildNodes.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Filtering — ShowMusicOnly
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowMusicOnly_FiltersNonMusicFiles()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track.flac", format: "flac"),
            MakeTrack(@"Folder\image.jpg", format: "jpg")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.ShowMusicOnly = true;

        // Only the flac file should remain visible
        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Single(folder.ChildNodes);
        var file = Assert.IsType<UserCollectionFileNodeViewModel>(folder.ChildNodes[0]);
        Assert.Equal("FLAC", file.Extension);
    }

    [Fact]
    public async Task ShowMusicOnly_AllFilesAreMusic_NoneFiltered()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track1.flac", format: "flac"),
            MakeTrack(@"Folder\track2.mp3", format: "mp3")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.ShowMusicOnly = true;

        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal(2, folder.ChildNodes.Count);
    }

    [Fact]
    public async Task ShowMusicOnly_TogglingOff_RestoresAllFiles()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track.flac", format: "flac"),
            MakeTrack(@"Folder\image.jpg", format: "jpg")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.ShowMusicOnly = true;
        sut.ShowMusicOnly = false;

        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal(2, folder.ChildNodes.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Filtering — FilterText
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FilterText_ByFilename_IncludesOnlyMatchingFiles()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\beethoven_sonata.flac", format: "flac"),
            MakeTrack(@"Folder\mozart_symphony.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.FilterText = "beethoven";

        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Single(folder.ChildNodes);
        Assert.Contains("beethoven", ((UserCollectionFileNodeViewModel)folder.ChildNodes[0]).Name,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilterText_ByArtist_MatchesOnArtistField()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track1.flac", artist: "Beethoven", format: "flac"),
            MakeTrack(@"Folder\track2.flac", artist: "Mozart", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.FilterText = "mozart";

        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Single(folder.ChildNodes);
    }

    [Fact]
    public async Task FilterText_CaseInsensitive_Matches()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\BEETHOVEN.flac", format: "flac"),
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.FilterText = "beethoven";

        Assert.NotEmpty(sut.RootNodes);
    }

    [Fact]
    public async Task FilterText_NoMatch_ClearsTree()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.FilterText = "zzz_no_match_zzz";

        Assert.Empty(sut.RootNodes);
        Assert.Equal("No items match the current browser filters.", sut.StatusText);
    }

    [Fact]
    public async Task FilterText_Cleared_RestoresFullTree()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track1.flac", format: "flac"),
            MakeTrack(@"Folder\track2.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.FilterText = "zzz_no_match_zzz";
        Assert.Empty(sut.RootNodes);

        sut.FilterText = string.Empty;

        var folder = Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
        Assert.Equal(2, folder.ChildNodes.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Statistics
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TotalFiles_CountsAllFileNodes()
    {
        var shares = new[]
        {
            MakeTrack(@"A\track1.flac", format: "flac"),
            MakeTrack(@"A\track2.mp3", format: "mp3"),
            MakeTrack(@"B\image.jpg", format: "jpg")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        Assert.Equal(3, sut.TotalFiles);
    }

    [Fact]
    public async Task MusicFileCount_CountsOnlyMusicExtensions()
    {
        var shares = new[]
        {
            MakeTrack(@"Folder\track1.flac", format: "flac"),
            MakeTrack(@"Folder\track2.mp3", format: "mp3"),
            MakeTrack(@"Folder\image.jpg", format: "jpg"),
            MakeTrack(@"Folder\doc.pdf", format: "pdf")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        Assert.Equal(2, sut.MusicFileCount);
    }

    [Fact]
    public async Task SuspiciousLosslessCount_ZeroInCurrentImplementation()
    {
        // IsSuspiciousLossless is always false in the current implementation.
        var shares = new[]
        {
            MakeTrack(@"Folder\track.flac", format: "flac", bitrate: 100)
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        Assert.Equal(0, sut.SuspiciousLosslessCount);
        Assert.False(sut.HasSuspiciousLossless);
    }

    [Fact]
    public async Task HasVisibleNodes_TrueWhenTreeHasContent()
    {
        var shares = new[] { MakeTrack(@"Folder\track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);

        Assert.False(sut.HasVisibleNodes);

        await sut.LoadUserAsync("peer");

        Assert.True(sut.HasVisibleNodes);
    }

    // ─────────────────────────────────────────────────────────────────
    // FilterSummary and HasActiveFilter
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FilterSummary_NoFiltersActive_ShowsDefaultMessage()
    {
        var sut = BuildSut();

        Assert.Equal("Showing all visible shares", sut.FilterSummary);
    }

    [Fact]
    public void FilterSummary_MusicOnlyEnabled_IncludesMusicOnly()
    {
        var sut = BuildSut();
        sut.ShowMusicOnly = true;

        Assert.Contains("music only", sut.FilterSummary);
    }

    [Fact]
    public void FilterSummary_FilterTextSet_IncludesFilterText()
    {
        var sut = BuildSut();
        sut.FilterText = "beethoven";

        Assert.Contains("filter: beethoven", sut.FilterSummary);
    }

    [Fact]
    public void FilterSummary_NonDefaultSortOption_IncludesSortLabel()
    {
        var sut = BuildSut();
        sut.SelectedSortOption = "Bitrate";

        Assert.Contains("sort: bitrate", sut.FilterSummary);
    }

    [Fact]
    public void FilterSummary_MultipleFilters_CombinesWithBullet()
    {
        var sut = BuildSut();
        sut.ShowMusicOnly = true;
        sut.FilterText = "jazz";

        Assert.Contains("•", sut.FilterSummary);
        Assert.Contains("music only", sut.FilterSummary);
        Assert.Contains("filter: jazz", sut.FilterSummary);
    }

    [Fact]
    public void HasActiveFilter_NoFilters_ReturnsFalse()
    {
        var sut = BuildSut();

        Assert.False(sut.HasActiveFilter);
    }

    [Fact]
    public void HasActiveFilter_ShowMusicOnly_ReturnsTrue()
    {
        var sut = BuildSut();
        sut.ShowMusicOnly = true;

        Assert.True(sut.HasActiveFilter);
    }

    [Fact]
    public void HasActiveFilter_NonDefaultSort_ReturnsTrue()
    {
        var sut = BuildSut();
        sut.SelectedSortOption = "Format";

        Assert.True(sut.HasActiveFilter);
    }

    [Fact]
    public void HasActiveFilter_DefaultSortName_ReturnsFalse()
    {
        var sut = BuildSut();
        sut.SelectedSortOption = "Name";

        Assert.False(sut.HasActiveFilter);
    }

    // ─────────────────────────────────────────────────────────────────
    // SelectedNode and derived properties
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectedNode_Null_DefaultsToNoSelectionText()
    {
        var sut = BuildSut();

        Assert.Equal("No selection", sut.SelectedNodeTitle);
        Assert.False(sut.HasSelectedNodeWarning);
    }

    [Fact]
    public async Task SelectedNode_FolderNode_ReflectsInDerivedProperties()
    {
        var shares = new[]
        {
            MakeTrack(@"Music\track.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        var folder = sut.RootNodes.OfType<UserCollectionFolderNodeViewModel>().First();
        sut.SelectedNode = folder;

        Assert.Equal("Music", sut.SelectedNodeTitle);
    }

    [Fact]
    public async Task SelectedNode_FileNode_ReflectsFilenameAsTitle()
    {
        var shares = new[] { MakeTrack("track.flac", format: "flac") };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        var file = sut.RootNodes.OfType<UserCollectionFileNodeViewModel>().First();
        sut.SelectedNode = file;

        Assert.Equal("track.flac", sut.SelectedNodeTitle);
    }

    // ─────────────────────────────────────────────────────────────────
    // Sorting
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SortOptions_ContainsExpectedValues()
    {
        var sut = BuildSut();

        Assert.Contains("Name", sut.SortOptions);
        Assert.Contains("Format", sut.SortOptions);
        Assert.Contains("Bitrate", sut.SortOptions);
    }

    [Fact]
    public async Task SelectedSortOption_Bitrate_OrdersFilesByBitrateDescending()
    {
        // Files at the root level (no subfolder) so they appear directly in RootNodes for easy inspection
        var shares = new[]
        {
            MakeTrack("low.flac", format: "flac", bitrate: 128),
            MakeTrack("high.flac", format: "flac", bitrate: 980),
            MakeTrack("mid.flac", format: "flac", bitrate: 320)
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.SelectedSortOption = "Bitrate";

        var files = sut.RootNodes.OfType<UserCollectionFileNodeViewModel>().ToList();
        // Highest bitrate should come first
        Assert.True(files[0].Bitrate >= files[1].Bitrate);
        Assert.True(files[1].Bitrate >= files[2].Bitrate);
    }

    [Fact]
    public async Task SelectedSortOption_Format_OrdersFilesByExtension()
    {
        var shares = new[]
        {
            MakeTrack("track.mp3", format: "mp3", bitrate: 320),
            MakeTrack("track.flac", format: "flac", bitrate: 980),
            MakeTrack("track.wav", format: "wav", bitrate: 1000)
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        sut.SelectedSortOption = "Format";

        var files = sut.RootNodes.OfType<UserCollectionFileNodeViewModel>().ToList();
        // Should be sorted alphabetically by extension (FLAC, MP3, WAV)
        var extensions = files.Select(f => f.Extension).ToList();
        var expectedOrder = extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(expectedOrder, extensions);
    }

    [Fact]
    public async Task SelectedSortOption_FoldersAppearBeforeFiles()
    {
        var shares = new[]
        {
            MakeTrack("root_track.mp3", format: "mp3"),
            MakeTrack(@"SubFolder\nested.flac", format: "flac")
        };
        var sut = BuildSut(sharesResult: shares);
        await sut.LoadUserAsync("peer");

        // Default Name sort — folders should precede files
        Assert.IsType<UserCollectionFolderNodeViewModel>(sut.RootNodes[0]);
    }

    [Fact]
    public void SelectedSortOption_NullOrWhitespace_DefaultsToName()
    {
        var sut = BuildSut();

        sut.SelectedSortOption = null!;

        Assert.Equal("Name", sut.SelectedSortOption);
    }

    // ─────────────────────────────────────────────────────────────────
    // UserCollectionFolderNodeViewModel — node properties
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FolderNode_Icon_IsFolder()
    {
        var folder = new UserCollectionFolderNodeViewModel("TestFolder");

        Assert.Equal("📁", folder.Icon);
    }

    [Fact]
    public void FolderNode_SecondaryText_NoMusicFiles_ShowsFolderLabel()
    {
        var folder = new UserCollectionFolderNodeViewModel("Empty");

        Assert.Equal("Folder", folder.SecondaryText);
    }

    [Fact]
    public void FolderNode_SecondaryText_WithMusicFiles_ShowsTrackCount()
    {
        var folder = new UserCollectionFolderNodeViewModel("WithTracks");
        var track = MakeTrack("song.flac", format: "flac");
        folder.ChildNodes.Add(new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true));

        Assert.Contains("1 track(s)", folder.SecondaryText);
    }

    [Fact]
    public void FolderNode_EnumerateFiles_RecursesIntoChildren()
    {
        var parent = new UserCollectionFolderNodeViewModel("Parent");
        var child = new UserCollectionFolderNodeViewModel("Child");
        var track = MakeTrack("song.flac", format: "flac");
        child.ChildNodes.Add(new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true));
        parent.ChildNodes.Add(child);

        Assert.Single(parent.EnumerateFiles());
    }

    // ─────────────────────────────────────────────────────────────────
    // UserCollectionFileNodeViewModel — node properties
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FileNode_MusicFile_IconIsNote()
    {
        var track = MakeTrack("song.flac", format: "flac");
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Equal("🎵", node.Icon);
    }

    [Fact]
    public void FileNode_NonMusicFile_IconIsDocument()
    {
        var track = MakeTrack("image.jpg", format: "jpg");
        var node = new UserCollectionFileNodeViewModel(track, "image.jpg", isMusicFile: false);

        Assert.Equal("📄", node.Icon);
    }

    [Fact]
    public void FileNode_Extension_UppercasesFormatField()
    {
        var track = MakeTrack("song.flac", format: "flac");
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Equal("FLAC", node.Extension);
    }

    [Fact]
    public void FileNode_Extension_FallsBackToFilenameExtension()
    {
        var track = MakeTrack("song.flac"); // no Format set
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Equal("FLAC", node.Extension);
    }

    [Fact]
    public void FileNode_Bitrate_ReflectsTrackBitrate()
    {
        var track = MakeTrack("song.flac", format: "flac", bitrate: 980);
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Equal(980, node.Bitrate);
    }

    [Fact]
    public void FileNode_SecondaryText_WithBitrate_IncludesBitrateKbps()
    {
        var track = MakeTrack("song.flac", format: "flac", bitrate: 980);
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Contains("980 kbps", node.SecondaryText);
    }

    [Fact]
    public void FileNode_SecondaryText_WithSampleRate_IncludesSampleRateKhz()
    {
        var track = MakeTrack("song.flac", format: "flac", sampleRate: 44100);
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        var expectedSampleRate = $"{track.SampleRate!.Value / 1000.0:0.0} kHz";
        Assert.Contains(expectedSampleRate, node.SecondaryText);
    }

    [Fact]
    public void FileNode_EnumerateFiles_MusicFile_YieldsSelf()
    {
        var track = MakeTrack("song.flac", format: "flac");
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Single(node.EnumerateFiles());
    }

    [Fact]
    public void FileNode_EnumerateFiles_NonMusicFile_YieldsNothing()
    {
        var track = MakeTrack("image.jpg", format: "jpg");
        var node = new UserCollectionFileNodeViewModel(track, "image.jpg", isMusicFile: false);

        Assert.Empty(node.EnumerateFiles());
    }

    [Fact]
    public void FileNode_EnumerateAllFiles_AlwaysYieldsSelf()
    {
        var track = MakeTrack("image.jpg", format: "jpg");
        var node = new UserCollectionFileNodeViewModel(track, "image.jpg", isMusicFile: false);

        Assert.Single(node.EnumerateAllFiles());
    }

    [Fact]
    public void FileNode_QueueableFileCount_MusicFile_IsOne()
    {
        var track = MakeTrack("song.flac", format: "flac");
        var node = new UserCollectionFileNodeViewModel(track, "song.flac", isMusicFile: true);

        Assert.Equal(1, node.QueueableFileCount);
    }

    [Fact]
    public void FileNode_QueueableFileCount_NonMusicFile_IsZero()
    {
        var track = MakeTrack("image.jpg", format: "jpg");
        var node = new UserCollectionFileNodeViewModel(track, "image.jpg", isMusicFile: false);

        Assert.Equal(0, node.QueueableFileCount);
    }
}
