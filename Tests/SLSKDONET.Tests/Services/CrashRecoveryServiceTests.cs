using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for the Download-checkpoint race condition: CrashRecoveryService used to
/// unconditionally report Download checkpoints as "recovered" (fake success), which raced ahead
/// of and silently consumed the checkpoints that DownloadManager.InitAsync()'s own real recovery
/// scan (Issue #48) needs to see — because DownloadManager.StartAsync() is fired fire-and-forget
/// just before CrashRecoveryService.RecoverAsync() is awaited, and this class's Download path did
/// no DB work before completing, so it almost always won that race.
/// </summary>
public class CrashRecoveryServiceTests
{
    [Fact]
    public async Task ProcessCheckpointAsync_DownloadCheckpoint_ReturnsFalse_SoItStaysPendingForDownloadManager()
    {
        // Arrange: minimal, uninitialized dependencies — the Download branch under test does no
        // I/O and touches neither, so a real (but never-.InitAsync()'d) journal is safe here.
        var journal = new CrashRecoveryJournal(NullLogger<CrashRecoveryJournal>.Instance);
        var service = new CrashRecoveryService(
            NullLogger<CrashRecoveryService>.Instance,
            journal,
            safeWrite: null!);

        var checkpoint = new RecoveryCheckpoint
        {
            OperationType = OperationType.Download,
            TargetPath = "C:/fake/path/track.mp3.part",
            StateJson = "{}"
        };

        var method = typeof(CrashRecoveryService).GetMethod(
            "ProcessCheckpointAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var task = (Task<bool>)method!.Invoke(service, new object[] { checkpoint })!;
        var result = await task;

        // Assert: false means RecoverAsync's loop will NOT call CompleteCheckpointAsync on it,
        // leaving it pending for DownloadManager's own scan to actually resolve.
        Assert.False(result);
    }
}
