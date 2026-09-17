using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Services.IO;

namespace SLSKDONET.Tests.Helpers;

/// <summary>
/// Test double for <see cref="IFileWriteService"/> — mirrors the real <c>SafeWriteService</c>'s
/// write-temp/verify/atomic-swap contract without its dependency on <c>CrashRecoveryJournal</c>
/// (which hardcodes the user's real AppData library.db, unsafe to touch from a test). Shared
/// across test suites that exercise services now requiring atomic writes (e.g.
/// <c>PlaylistExportService</c>, <c>MetadataTaggerService</c>) instead of a raw
/// <c>File.Save</c>/<c>File.Copy</c> that can leave a target file corrupted if interrupted.
/// </summary>
public sealed class FakeFileWriteService : IFileWriteService
{
    public async Task<bool> WriteAtomicAsync(
        string targetPath, Func<string, Task> writeAction, Func<string, Task<bool>>? verifyAction = null,
        CancellationToken cancellationToken = default)
    {
        var tempPath = targetPath + ".tmp";
        try
        {
            await writeAction(tempPath);
            if (verifyAction != null && !await verifyAction(tempPath))
            {
                File.Delete(tempPath);
                return false;
            }
            File.Copy(tempPath, targetPath, overwrite: true);
            File.Delete(tempPath);
            return true;
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
    }

    public Task<bool> WriteAllBytesAtomicAsync(string targetPath, byte[] data, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(targetPath, async tempPath => await File.WriteAllBytesAsync(tempPath, data, cancellationToken), cancellationToken: cancellationToken);

    public async Task<bool> CopyFileAtomicAsync(string sourcePath, string targetPath, bool preserveTimestamps = true, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) return false;
        var expectedLength = new FileInfo(sourcePath).Length;
        return await WriteAtomicAsync(
            targetPath,
            tempPath => { File.Copy(sourcePath, tempPath, overwrite: true); return Task.CompletedTask; },
            tempPath => Task.FromResult(new FileInfo(tempPath).Length == expectedLength),
            cancellationToken);
    }

    public Task<bool> MoveAtomicAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
