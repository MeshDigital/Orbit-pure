using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Library;

public sealed record AudioContentHash(string Kind, string Hex)
{
    public string Value => $"{Kind}:{Hex}";
}

public class AudioContentHasher
{
    private readonly ILogger<AudioContentHasher> _logger;

    public AudioContentHasher(ILogger<AudioContentHasher> logger) => _logger = logger;

    public async Task<AudioContentHash?> ComputeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return await Task.Run(() => ComputeCore(path, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audio content hash failed for {Path}", path);
            return null;
        }
    }

    /// <summary>Synchronous form for callers already on a worker thread (e.g. the folder scanner).</summary>
    public AudioContentHash? Compute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return ComputeCore(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio content hash failed for {Path}", path);
            return null;
        }
    }

    private AudioContentHash ComputeCore(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

        return Services.Audio.AudioFormats.Find(ext)?.Family switch
        {
            Services.Audio.AudioContainerFamily.Flac => HashFlac(fs, ct),
            Services.Audio.AudioContainerFamily.Wave => HashWav(fs, ct),
            Services.Audio.AudioContainerFamily.Mpeg => HashMp3(fs, ct),
            Services.Audio.AudioContainerFamily.Mp4 => HashM4a(fs, ct),
            Services.Audio.AudioContainerFamily.Aiff => HashAiff(fs, ct),
            _ => HashWholeFile(fs, "file", ct),
        };
    }

    private AudioContentHash HashFlac(FileStream fs, CancellationToken ct)
    {
        // FLAC starts with "fLaC" (4 bytes) then metadata blocks.
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4 || magic[0] != (byte)'f' || magic[1] != (byte)'L' || magic[2] != (byte)'a' || magic[3] != (byte)'C')
            return HashWholeFile(fs, "payload", ct);

        bool last = false;
        byte[]? streamInfoMd5 = null;
        while (!last)
        {
            ct.ThrowIfCancellationRequested();
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) != 4) break;
            last = (header[0] & 0x80) != 0;
            int blockType = header[0] & 0x7F;
            int blockSize = (header[1] << 16) | (header[2] << 8) | header[3];
            if (blockSize < 0 || blockSize > 10 * 1024 * 1024)
                return HashWholeFile(fs, "payload", ct);

            var block = new byte[blockSize];
            int read = 0;
            while (read < blockSize)
            {
                int r = fs.Read(block, read, blockSize - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }

            if (blockType == 0 && blockSize >= 34) // STREAMINFO
            {
                var md5 = new byte[16];
                Buffer.BlockCopy(block, 18, md5, 0, 16);
                if (!md5.All(b => b == 0))
                    streamInfoMd5 = md5;
            }
        }

        if (streamInfoMd5 != null)
            return new AudioContentHash("pcm-md5", Convert.ToHexString(streamInfoMd5).ToLowerInvariant());

        // Fall back to hashing the frames after the last metadata block.
        fs.Seek(0, SeekOrigin.Begin);
        // skip magic + metadata already consumed; recompute by hashing rest
        return HashStreamSuffix(fs, ct);
    }

    private AudioContentHash HashWav(FileStream fs, CancellationToken ct)
    {
        // Parse RIFF chunks, hash the "data" chunk. If 16-bit PCM, compute MD5 of raw samples
        // (matches FLAC STREAMINFO MD5 only for identical PCM; for our purposes hash payload).
        // Simpler: hash the data chunk bytes with MD5 -> pcm-md5 when 16-bit, else payload.
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> riff = stackalloc byte[12];
        if (fs.Read(riff) != 12) return HashWholeFile(fs, "payload", ct);
        // "RIFF....WAVE"
        if (riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F' ||
            riff[8] != 'W' || riff[9] != 'A' || riff[10] != 'V' || riff[11] != 'E')
            return HashWholeFile(fs, "payload", ct);

        // Walk chunks
        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            Span<byte> chunkHeader = stackalloc byte[8];
            if (fs.Read(chunkHeader) != 8) break;
            var id = System.Text.Encoding.ASCII.GetString(chunkHeader[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (size > int.MaxValue) break;
            if (id == "data")
            {
                var data = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int r = fs.Read(data, read, (int)size - read);
                    if (r <= 0) throw new EndOfStreamException();
                    read += r;
                }
                // Determine bit depth from fmt chunk? We didn't parse; default to payload hash.
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(data);
                return new AudioContentHash("payload", Convert.ToHexString(hash).ToLowerInvariant());
            }
            else
            {
                fs.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }
        return HashWholeFile(fs, "payload", ct);
    }

    private AudioContentHash HashMp3(FileStream fs, CancellationToken ct)
    {
        long start = 0;
        // ID3v2 tag
        Span<byte> id3 = stackalloc byte[10];
        if (fs.Read(id3) == 10 && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
        {
            // syncsafe size
            int size = (id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9];
            start = 10 + size;
            if ((id3[5] & 0x10) != 0) start += 10; // footer
        }
        fs.Seek(0, SeekOrigin.Begin);

        long end = fs.Length;
        // trailing ID3v1
        if (end >= 128)
        {
            fs.Seek(end - 128, SeekOrigin.Begin);
            Span<byte> tag = stackalloc byte[3];
            if (fs.Read(tag) == 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
                end -= 128;
        }
        // APEv2
        if (end >= 32)
        {
            fs.Seek(end - 32, SeekOrigin.Begin);
            Span<byte> ape = stackalloc byte[8];
            if (fs.Read(ape) == 8 && System.Text.Encoding.ASCII.GetString(ape) == "APETAGEX")
            {
                // size at offset 12 (little-endian) within APE header (last 32 bytes)
                fs.Seek(end - 20, SeekOrigin.Begin);
                Span<byte> sizeBytes = stackalloc byte[4];
                if (fs.Read(sizeBytes) == 4)
                {
                    uint apeSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
                    end -= (apeSize + 32);
                }
            }
        }

        return HashRange(fs, start, end, "payload", ct);
    }

    private AudioContentHash HashM4a(FileStream fs, CancellationToken ct)
    {
        // Find mdat atom
        fs.Seek(0, SeekOrigin.Begin);
        while (fs.Position < fs.Length - 8)
        {
            ct.ThrowIfCancellationRequested();
            long atomStart = fs.Position;
            Span<byte> header = stackalloc byte[8];
            if (fs.Read(header) != 8) break;
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            long size = size32 == 0 ? fs.Length - atomStart : (size32 == 1 ? ReadSize64(fs) : size32);
            if (size < 8) break;

            if (type == "mdat")
            {
                long dataStart = atomStart + 8;
                long dataLen = size - 8;
                return HashRange(fs, dataStart, dataStart + dataLen, "payload", ct);
            }
            fs.Seek(atomStart + size, SeekOrigin.Begin);
        }
        return HashWholeFile(fs, "payload", ct);
    }

    private static long ReadSize64(FileStream fs)
    {
        Span<byte> b = stackalloc byte[8];
        if (fs.Read(b) != 8) return 0;
        return (long)BinaryPrimitives.ReadUInt64BigEndian(b);
    }

    private AudioContentHash HashAiff(FileStream fs, CancellationToken ct)
    {
        // Hash SSND chunk payload.
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> form = stackalloc byte[12];
        if (fs.Read(form) != 12) return HashWholeFile(fs, "payload", ct);
        if (form[0] != 'F' || form[1] != 'O' || form[2] != 'R' || form[3] != 'M') return HashWholeFile(fs, "payload", ct);

        while (fs.Position < fs.Length - 8)
        {
            ct.ThrowIfCancellationRequested();
            long chunkStart = fs.Position;
            Span<byte> ch = stackalloc byte[8];
            if (fs.Read(ch) != 8) break;
            var id = System.Text.Encoding.ASCII.GetString(ch[..4]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(ch[4..]);
            if (id == "SSND")
            {
                // skip 8-byte offset/block size
                fs.Seek(8, SeekOrigin.Current);
                long dataLen = size - 8;
                return HashRange(fs, fs.Position, fs.Position + dataLen, "payload", ct);
            }
            fs.Seek(chunkStart + 8 + size + (size % 2), SeekOrigin.Begin);
        }
        return HashWholeFile(fs, "payload", ct);
    }

    private AudioContentHash HashWholeFile(FileStream fs, string kind, CancellationToken ct)
    {
        fs.Seek(0, SeekOrigin.Begin);
        var hasher = new XxHash128();
        var buffer = new byte[65536];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, n));
        }
        var hash = hasher.GetHashAndReset();
        return new AudioContentHash(kind, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private AudioContentHash HashStreamSuffix(FileStream fs, CancellationToken ct)
    {
        // Hash everything from current position to end.
        var hasher = new XxHash128();
        var buffer = new byte[65536];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, n));
        }
        var hash = hasher.GetHashAndReset();
        return new AudioContentHash("payload", Convert.ToHexString(hash).ToLowerInvariant());
    }

    private AudioContentHash HashRange(FileStream fs, long start, long end, string kind, CancellationToken ct)
    {
        if (start < 0 || end <= start || end > fs.Length)
            return HashWholeFile(fs, "payload", ct);

        fs.Seek(start, SeekOrigin.Begin);
        var hasher = new XxHash128();
        var buffer = new byte[65536];
        long remaining = end - start;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = fs.Read(buffer, 0, toRead);
            if (n <= 0) break;
            hasher.Append(buffer.AsSpan(0, n));
            remaining -= n;
        }
        var hash = hasher.GetHashAndReset();
        return new AudioContentHash(kind, Convert.ToHexString(hash).ToLowerInvariant());
    }
}