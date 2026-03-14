using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using TagLib;

namespace SLSKDONET.Services.IO
{
    /// <summary>
    /// Provides file verification methods for SafeWrite operations.
    /// </summary>
    public static class FileVerificationHelper
    {
        /// <summary>
        /// Verifies file size matches expected minimum.
        /// </summary>
        public static Task<bool> VerifyFileSizeAsync(string filePath, long expectedMinSize)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                return Task.FromResult(fileInfo.Length >= expectedMinSize);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Verifies audio file can be opened and has valid metadata.
        /// Prevents corrupted files from being committed.
        /// </summary>
        public static async Task<bool> VerifyAudioFormatAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Use TagLib to verify file structure
                    using var file = TagLib.File.Create(filePath);
                    
                    // Check basic validity
                    if (file.Properties == null)
                        return false;

                    // Ensure duration is reasonable (not 0 or corrupted)
                    if (file.Properties.Duration <= TimeSpan.Zero)
                        return false;

                    // File opened successfully and has valid properties
                    return true;
                }
                catch (CorruptFileException)
                {
                    return false;
                }
                catch (UnsupportedFormatException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Calculates SHA256 checksum of file for integrity verification.
        /// </summary>
        public static async Task<string> CalculateChecksumAsync(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = System.IO.File.OpenRead(filePath);
            
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Verifies checksum matches expected value.
        /// </summary>
        public static async Task<bool> VerifyChecksumAsync(string filePath, string expectedChecksum)
        {
            try
            {
                var actualChecksum = await CalculateChecksumAsync(filePath);
                return string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
