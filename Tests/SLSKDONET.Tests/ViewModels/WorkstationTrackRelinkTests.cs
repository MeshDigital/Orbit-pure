using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Workstation;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public sealed class WorkstationTrackRelinkTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_Relink_{Guid.NewGuid():N}");

    public WorkstationTrackRelinkTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExistingFile_UpdatesSharedTrackIdentityAndReturnsSuccess()
    {
        var newPath = CreateAudioFile("moved.flac");
        var track = Track("stable-hash");
        var (library, files) = Dependencies(newPath);

        var result = await WorkstationTrackRelink.RelinkAsync(track, library.Object, files.Object);

        Assert.Equal(TrackRelinkOutcome.Success, result.Outcome);
        Assert.Equal(newPath, result.FilePath);
        library.Verify(service => service.UpdateTrackFilePathAsync("stable-hash", newPath), Times.Once);
    }

    [Fact]
    public async Task CancelledPicker_DoesNotChangeLibrary()
    {
        var track = Track("stable-hash");
        var (library, files) = Dependencies(null);

        var result = await WorkstationTrackRelink.RelinkAsync(track, library.Object, files.Object);

        Assert.Equal(TrackRelinkOutcome.Cancelled, result.Outcome);
        library.Verify(service => service.UpdateTrackFilePathAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SelectedPathThatDoesNotExist_DoesNotChangeLibrary()
    {
        var missingPath = Path.Combine(_tempDir, "gone.mp3");
        var track = Track("stable-hash");
        var (library, files) = Dependencies(missingPath);

        var result = await WorkstationTrackRelink.RelinkAsync(track, library.Object, files.Object);

        Assert.Equal(TrackRelinkOutcome.InvalidFile, result.Outcome);
        library.Verify(service => service.UpdateTrackFilePathAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MissingTrackHash_BlocksBeforePickerSoEmptyHashRowsCannotBeRelinkedTogether()
    {
        var track = Track(string.Empty);
        var (library, files) = Dependencies(CreateAudioFile("unused.mp3"));

        var result = await WorkstationTrackRelink.RelinkAsync(track, library.Object, files.Object);

        Assert.Equal(TrackRelinkOutcome.MissingIdentity, result.Outcome);
        files.VerifyNoOtherCalls();
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public void PersistenceStateUpdate_RelinksEveryPlaylistRowWithoutDowngradingReadyPreparation()
    {
        var entry = new LibraryEntryEntity
        {
            UniqueHash = "stable-hash",
            FilePath = "old.flac",
            AvailabilityState = TrackAvailabilityState.Ghost,
            CuePointsJson = "saved-cues",
        };
        var ghost = new PlaylistTrackEntity
        {
            TrackUniqueHash = "stable-hash",
            Status = TrackStatus.Missing,
            AvailabilityState = TrackAvailabilityState.Ghost,
            ResolvedFilePath = "old.flac",
            CuePointsJson = "saved-cues",
        };
        var ready = new PlaylistTrackEntity
        {
            TrackUniqueHash = "stable-hash",
            Status = TrackStatus.Downloaded,
            AvailabilityState = TrackAvailabilityState.Ready,
            ResolvedFilePath = "old.flac",
        };

        LibraryService.ApplyRelinkedFilePath(entry, new[] { ghost, ready }, "new.flac");

        Assert.Equal("new.flac", entry.FilePath);
        Assert.Equal(TrackAvailabilityState.LocalUnanalyzed, entry.AvailabilityState);
        Assert.Equal("saved-cues", entry.CuePointsJson);
        Assert.NotNull(entry.FilePathUpdatedAt);
        Assert.All(new[] { ghost, ready }, track =>
        {
            Assert.Equal("new.flac", track.ResolvedFilePath);
            Assert.Equal(TrackStatus.Downloaded, track.Status);
        });
        Assert.Equal(TrackAvailabilityState.LocalUnanalyzed, ghost.AvailabilityState);
        Assert.Equal(TrackAvailabilityState.Ready, ready.AvailabilityState);
        Assert.Equal("saved-cues", ghost.CuePointsJson);
    }

    [Fact]
    public void MissingStatusWithRetainedBrokenPath_IsOfferedLocateInsteadOfAcquire()
    {
        var track = Track("stable-hash");
        track.Status = TrackStatus.Missing;
        track.ResolvedFilePath = Path.Combine(_tempDir, "moved-away.mp3");

        var issue = WorkstationDeckViewModel.GetTrackEligibilityIssue(track);

        Assert.Equal(WorkstationTrackEligibilityIssue.MissingFile, issue);
    }

    private string CreateAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "fake audio");
        return path;
    }

    private static PlaylistTrack Track(string hash) => new()
    {
        Artist = "Artist",
        Title = "Title",
        TrackUniqueHash = hash,
        Status = TrackStatus.Downloaded,
        AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
    };

    private static (Mock<ILibraryService> Library, Mock<IFileInteractionService> Files) Dependencies(string? selectedPath)
    {
        var library = new Mock<ILibraryService>();
        library.Setup(service => service.UpdateTrackFilePathAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var files = new Mock<IFileInteractionService>();
        files.Setup(service => service.OpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<FileDialogFilter>?>()))
            .ReturnsAsync(selectedPath);
        return (library, files);
    }
}
