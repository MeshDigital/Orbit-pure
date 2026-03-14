using System;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.IO
{
    /// <summary>
    /// Provides atomic, crash-safe file write operations.
    /// Ensures that files are never left in a partially written state.
    /// </summary>
    public interface IFileWriteService
    {
        /// <summary>
        /// Atomically writes data to a file using the write → verify → rename pattern.
        /// </summary>
        /// <param name="targetPath">Final destination path</param>
        /// <param name="writeAction">Action that performs the actual write to the temp file</param>
        /// <param name="verifyAction">Optional verification before commit (e.g., file size check)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if write succeeded and was verified, false otherwise</returns>
        Task<bool> WriteAtomicAsync(
            string targetPath,
            Func<string, Task> writeAction,
            Func<string, Task<bool>>? verifyAction = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically writes a byte array to a file.
        /// </summary>
        Task<bool> WriteAllBytesAtomicAsync(
            string targetPath,
            byte[] data,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically copies a file while preserving metadata.
        /// </summary>
        Task<bool> CopyFileAtomicAsync(
            string sourcePath,
            string targetPath,
            bool preserveTimestamps = true,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically moves a file to a new location.
        /// Useful for finalizing downloads (.part -> .mp3).
        /// </summary>
        Task<bool> MoveAtomicAsync(
            string sourcePath,
            string targetPath,
            CancellationToken cancellationToken = default);
    }
}
