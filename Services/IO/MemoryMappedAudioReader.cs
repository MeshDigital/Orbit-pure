using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.IO;

/// <summary>
/// Reads raw audio file bytes, switching to a <see cref="MemoryMappedFile"/> for
/// files larger than <see cref="MmfThresholdBytes"/> (default 100 MB) to avoid
/// large heap allocations and reduce peak RAM when decoding large FLAC/WAV sources.
///
/// For files under the threshold a pooled <see cref="FileStream"/> read is used —
/// same semantics, lower overhead for typical library sizes.
/// </summary>
public sealed class MemoryMappedAudioReader
{
    /// <summary>Files larger than this value are read via MemoryMappedFile.</summary>
    public long MmfThresholdBytes { get; }

    public MemoryMappedAudioReader(long mmfThresholdBytes = 100 * 1024 * 1024)
    {
        if (mmfThresholdBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(mmfThresholdBytes), "Threshold must be positive.");
        MmfThresholdBytes = mmfThresholdBytes;
    }

    /// <summary>
    /// Returns a <see cref="Stream"/> over the file contents.
    /// The caller is responsible for disposing the returned stream.
    /// For large files the stream is backed by a <see cref="MemoryMappedViewStream"/>;
    /// for small files it is a <see cref="FileStream"/>.
    /// </summary>
    public Stream OpenRead(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("Audio file not found.", filePath);

        if (info.Length > MmfThresholdBytes)
        {
            // Large file — use MMF for sequential read without buffering the full file in RAM.
            var mmf = MemoryMappedFile.CreateFromFile(
                filePath,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.Read);
            return new MmfOwningViewStream(mmf, info.Length);
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
    }

    /// <summary>
    /// Reads the entire file into a byte array.
    /// Uses <see cref="MemoryMappedFile"/> for large files to avoid double-buffering.
    /// </summary>
    public async Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = OpenRead(filePath);
        var buffer = new byte[stream.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (chunk == 0) break;
            read += chunk;
        }
        return buffer;
    }

    // ── inner type ────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="MemoryMappedViewStream"/> wrapper that also disposes the owning
    /// <see cref="MemoryMappedFile"/> when the stream is closed, and reports the
    /// exact file length rather than the page-aligned view capacity.
    /// </summary>
    private sealed class MmfOwningViewStream : Stream
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewStream _inner;
        private readonly long _fileLength;

        public MmfOwningViewStream(MemoryMappedFile mmf, long fileLength)
        {
            _mmf = mmf;
            _fileLength = fileLength;
            _inner = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        // Return the actual file length, not the page-aligned MMF view capacity.
        public override long Length => _fileLength;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _mmf.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
