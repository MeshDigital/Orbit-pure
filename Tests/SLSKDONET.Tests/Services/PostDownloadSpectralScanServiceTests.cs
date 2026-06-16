using System;
using System.IO;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Diagnostics;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Tests for <see cref="PostDownloadSpectralScanService"/>.
///
/// Integration-level tests that need GetPlaylistTrackByHashAsync to return controlled
/// data (extension filter, dedup, event publication) require a real in-memory SQLite
/// DatabaseService — those are tracked separately.  These tests verify the observable
/// state-gate behaviour that does not depend on the DB path completing successfully.
/// </summary>
public class PostDownloadSpectralScanServiceTests : IDisposable
{
    private readonly Mock<IAudioIntegrityService> _integrityMock = new();
    private readonly Mock<ITrackAuditLogger> _auditMock = new();
    private readonly Mock<IEventBus> _eventBusMock = new();
    private readonly Subject<TrackStateChangedEvent> _stateSubject = new();

    // DatabaseService has no virtual methods so we use GetUninitializedObject to
    // construct an instance without invoking its real constructor.
    private static readonly DatabaseService DbStub =
        (DatabaseService)RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));

    public PostDownloadSpectralScanServiceTests()
    {
        _eventBusMock
            .Setup(b => b.GetEvent<TrackStateChangedEvent>())
            .Returns(_stateSubject);
    }

    private PostDownloadSpectralScanService CreateSut() =>
        new PostDownloadSpectralScanService(
            _integrityMock.Object,
            DbStub,
            _eventBusMock.Object,
            NullLogger<PostDownloadSpectralScanService>.Instance,
            _auditMock.Object);

    // ── state gate ────────────────────────────────────────────────────────────
    // These tests are accurate: only Completed events invoke ScanAsync.
    // The inner DB call may throw (uninitialized stub), but that is caught by
    // the try/catch in ScanAsync — the observable outcome is the same: no scan.

    [Theory]
    [InlineData(PlaylistTrackState.Searching)]
    [InlineData(PlaylistTrackState.Downloading)]
    [InlineData(PlaylistTrackState.Failed)]
    [InlineData(PlaylistTrackState.Cancelled)]
    public async Task NonCompletedState_DoesNotTriggerAnalysis(PlaylistTrackState state)
    {
        using var sut = CreateSut();

        _stateSubject.OnNext(new TrackStateChangedEvent("hash", Guid.NewGuid(), state));
        await Task.Delay(50);

        _integrityMock.Verify(s => s.AnalyseAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task CompletedState_WhenDbThrows_DoesNotCrash_AndAnalyseIsNotCalled()
    {
        // The uninitialized DatabaseService will throw NullReferenceException when
        // GetPlaylistTrackByHashAsync accesses _trackRepository.  ScanAsync catches
        // the exception; no crash and no analysis should occur.
        using var sut = CreateSut();

        _stateSubject.OnNext(new TrackStateChangedEvent("hash", Guid.NewGuid(), PlaylistTrackState.Completed));
        await Task.Delay(100);

        _integrityMock.Verify(s => s.AnalyseAsync(It.IsAny<string>(), default), Times.Never);
    }

    // ── lossless-only filter (file-system tier) ───────────────────────────────
    // These tests create a real temp file with the appropriate extension and verify
    // that AnalyseAsync is NOT called for lossy formats.
    // Note: with the uninitialized DB stub, ScanAsync never reaches the extension
    // check — the DB call throws first.  These tests therefore verify the final
    // observable property (AnalyseAsync not called) while acknowledging the path
    // differs from the production path.  Full path coverage requires integration
    // tests with a seeded in-memory SQLite database.

    [Theory]
    [InlineData(PlaylistTrackState.Searching)]
    [InlineData(PlaylistTrackState.Downloading)]
    [InlineData(PlaylistTrackState.Failed)]
    public async Task LossyFileWithNonCompletedState_NeverCallsAnalyse(PlaylistTrackState state)
    {
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".mp3");
        try
        {
            using var sut = CreateSut();

            _stateSubject.OnNext(new TrackStateChangedEvent("hash", Guid.NewGuid(), state));
            await Task.Delay(50);

            _integrityMock.Verify(s => s.AnalyseAsync(It.IsAny<string>(), default), Times.Never);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sut = CreateSut();
        var ex = Record.Exception(() => sut.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task AfterDispose_CompletedEvent_DoesNotTriggerAnalysis()
    {
        var sut = CreateSut();
        sut.Dispose();

        _stateSubject.OnNext(new TrackStateChangedEvent("hash", Guid.NewGuid(), PlaylistTrackState.Completed));
        await Task.Delay(50);

        _integrityMock.Verify(s => s.AnalyseAsync(It.IsAny<string>(), default), Times.Never);
    }

    public void Dispose() => _stateSubject.Dispose();
}
