using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Sends/receives images over Soulseek chat. The Soulseek protocol's chat messages are plain
/// strings only — there is no attachment support at the wire level. This builds images-in-chat
/// on top of that constraint using two pieces the app already has: chat messages (to carry a
/// structured "offer" string) and the file-transfer pipeline built for sharing (to actually move
/// the bytes, peer-to-peer, once the recipient's client requests them).
///
/// Privacy: attachment files live in their own folder, deliberately never added to
/// <see cref="ShareIndexService"/> (which backs Browse/Search) — an image sent to one peer must
/// never appear in your general share listing for anyone else. Authorization is a simple
/// per-file, per-recipient-username grant, checked in <see cref="SoulseekAdapter"/>'s
/// EnqueueDownload resolver.
/// </summary>
public sealed class ChatAttachmentService
{
    private const string ImageOfferPrefix = "ORBIT_IMG_OFFER|";
    private const long MaxImageBytes = 15 * 1024 * 1024; // 15 MB safety cap — this rides the same P2P transfer as downloads, no need to allow huge files
    private static readonly string[] AllowedImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>Pass as the recipient to <see cref="PrepareOutgoingImage"/> for a room post — a room has no single recipient, so anyone may request it (see <see cref="TryAuthorize"/>).</summary>
    public const string AnyoneRecipient = "*";

    public static string AttachmentsDirectory { get; } = Path.Combine(
        SLSKDONET.Data.OrbitPaths.RoamingRoot, "ORBIT-Pure", "ChatAttachments");

    // IServiceProvider (not ISoulseekAdapter directly) deliberately breaks a DI cycle:
    // SoulseekAdapter needs this service (to authorize incoming attachment requests) and this
    // service needs SoulseekAdapter (to actually download an image someone sent us) — resolving
    // the adapter lazily, only when actually downloading, avoids a circular constructor dependency.
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChatAttachmentService> _logger;

    // Unbounded otherwise — every outgoing/incoming chat image sent or received in a session adds
    // an entry that was never removed. Cap both and evict the oldest batch once the cap is hit.
    private const int MaxTrackedAttachments = 500;
    private const int EvictionBatchSize = 50;

    private readonly ConcurrentDictionary<string, ChatAttachmentGrant> _outgoingGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string LocalPath, DateTime CreatedAtUtc)> _downloadedByVirtualPath = new(StringComparer.OrdinalIgnoreCase);

    public ChatAttachmentService(IServiceProvider serviceProvider, ILogger<ChatAttachmentService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public static bool TryParseImageOffer(string message, out long sizeBytes, out string virtualPath, out string fileName)
    {
        sizeBytes = 0;
        virtualPath = string.Empty;
        fileName = string.Empty;

        if (string.IsNullOrEmpty(message) || !message.StartsWith(ImageOfferPrefix, StringComparison.Ordinal))
            return false;

        var parts = message[ImageOfferPrefix.Length..].Split('|', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], out sizeBytes))
            return false;

        virtualPath = parts[1];
        fileName = parts[2];
        return !string.IsNullOrWhiteSpace(virtualPath) && !string.IsNullOrWhiteSpace(fileName);
    }

    /// <summary>
    /// Copies the picked file into the attachments store, grants the recipient download access,
    /// and returns the chat message payload to actually send (a plain string, like any other message).
    /// </summary>
    public string PrepareOutgoingImage(string localSourceFilePath, string recipientUsername)
    {
        var extension = Path.GetExtension(localSourceFilePath);
        if (!AllowedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{extension}' isn't a supported image type.");

        var sourceInfo = new FileInfo(localSourceFilePath);
        if (!sourceInfo.Exists)
            throw new FileNotFoundException("Image file not found.", localSourceFilePath);

        if (sourceInfo.Length > MaxImageBytes)
            throw new InvalidOperationException($"Image is too large ({sourceInfo.Length / 1024 / 1024} MB) — max {MaxImageBytes / 1024 / 1024} MB.");

        Directory.CreateDirectory(AttachmentsDirectory);
        var storedFileName = $"{Guid.NewGuid():N}{extension}";
        var storedPath = Path.Combine(AttachmentsDirectory, storedFileName);
        File.Copy(localSourceFilePath, storedPath, overwrite: false);

        // Privacy: never expose the real on-disk path (which contains the Windows username) to
        // peers. Use a virtual alias as the grant key and in the offer; the real path stays
        // inside the grant only.
        var virtualPath = $"ORBIT-Chat\\{storedFileName}";
        _outgoingGrants[virtualPath] = new ChatAttachmentGrant(storedPath, recipientUsername, DateTime.UtcNow);
        EvictOldestIfOverCap(_outgoingGrants, g => g.CreatedAtUtc);

        _logger.LogInformation("Prepared outgoing chat image {FileName} ({Size} bytes) for {Recipient}", Path.GetFileName(localSourceFilePath), sourceInfo.Length, recipientUsername);

        return $"{ImageOfferPrefix}{sourceInfo.Length}|{virtualPath}|{Path.GetFileName(localSourceFilePath)}";
    }

    /// <summary>
    /// Resolves an image offer to a local file path — either the copy we already hold (if we're
    /// the one who sent it) or a freshly peer-to-peer downloaded copy (if we received it).
    /// Incoming offers are validated before anything touches the Skia decoder: the peer-declared
    /// size and extension are checked, the transfer is capped, and the downloaded bytes are
    /// verified to be an actual image (magic bytes) — a file that fails any check is deleted.
    /// </summary>
    public async Task<string> ResolveLocalImagePathAsync(string peerUsername, string virtualPath, string fileName, bool isOutgoing, long sizeBytes, CancellationToken ct = default)
    {
        if (isOutgoing && _outgoingGrants.TryGetValue(virtualPath, out var grant) && File.Exists(grant.LocalPath))
            return grant.LocalPath;

        if (_downloadedByVirtualPath.TryGetValue(virtualPath, out var cached) && File.Exists(cached.LocalPath))
            return cached.LocalPath;

        // The peer controls both fields on the incoming path, so enforce them here (outgoing
        // offers are already validated in PrepareOutgoingImage).
        if (sizeBytes <= 0 || sizeBytes > MaxImageBytes)
        {
            _logger.LogWarning("Rejected incoming chat image offer of {Size} bytes (max {Max})", sizeBytes, MaxImageBytes);
            throw new InvalidOperationException($"Image is too large — max {MaxImageBytes / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(fileName);
        if (!AllowedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected incoming chat image with disallowed extension '{Extension}'", extension);
            throw new InvalidOperationException($"'{extension}' isn't a supported image type.");
        }

        Directory.CreateDirectory(AttachmentsDirectory);
        var localPath = Path.Combine(AttachmentsDirectory, $"recv_{Guid.NewGuid():N}{extension}");

        var adapter = _serviceProvider.GetRequiredService<ISoulseekAdapter>();
        var success = await adapter.DownloadAsync(peerUsername, virtualPath, localPath, size: sizeBytes, ct: ct).ConfigureAwait(false);

        if (!success || !File.Exists(localPath))
            throw new IOException($"Failed to download image from {peerUsername}.");

        // The peer can lie about the advertised size — enforce the cap on the bytes actually written.
        var downloadedInfo = new FileInfo(localPath);
        if (downloadedInfo.Length > MaxImageBytes)
        {
            DeleteRejectedFile(localPath);
            _logger.LogWarning("Incoming chat image exceeded the size cap after download ({Size} bytes)", downloadedInfo.Length);
            throw new InvalidOperationException($"Image is too large — max {MaxImageBytes / 1024 / 1024} MB.");
        }

        // Validate the content is a real image before any decoder sees it; delete it if not.
        if (!IsAllowedImageContent(localPath))
        {
            DeleteRejectedFile(localPath);
            _logger.LogWarning("Incoming chat image failed magic-byte validation: {Path}", localPath);
            throw new InvalidOperationException("File is not a valid image.");
        }

        _downloadedByVirtualPath[virtualPath] = (localPath, DateTime.UtcNow);
        EvictOldestIfOverCap(_downloadedByVirtualPath, v => v.CreatedAtUtc);
        return localPath;
    }

    /// <summary>Best-effort deletion of a rejected download; failures are logged, not thrown.</summary>
    private void DeleteRejectedFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete rejected chat image {Path}", path);
        }
    }

    /// <summary>Checks the file header (magic bytes) against the supported image formats.</summary>
    private static bool IsAllowedImageContent(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[12];
            var read = fs.Read(header);
            return read switch
            {
                // PNG: 89 50 4E 47 0D 0A 1A 0A
                >= 8 when header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G'
                    && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A => true,
                // JPEG: FF D8 FF
                >= 3 when header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF => true,
                // GIF: "GIF87a" or "GIF89a"
                >= 6 when header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
                    && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a' => true,
                // BMP: "BM"
                >= 2 when header[0] == (byte)'B' && header[1] == (byte)'M' => true,
                // WebP: "RIFF" .... "WEBP"
                >= 12 when header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                    && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P' => true,
                _ => false
            };
        }
        catch (Exception)
        {
            // Unreadable files are treated as invalid; the caller logs and deletes.
            return false;
        }
    }

    /// <summary>
    /// Called from <see cref="SoulseekAdapter"/>'s EnqueueDownload resolver. A 1:1 grant only
    /// authorizes the exact recipient it was sent to; a room grant (<see cref="AnyoneRecipient"/>,
    /// used because a room has no single recipient) authorizes any requester — once an image is
    /// posted to a room it's implicitly visible to whoever is in it, same as the message itself.
    /// </summary>
    public bool TryAuthorize(string virtualPath, string requestingUsername, out ShareIndexEntry? entry)
    {
        entry = null;

        if (!_outgoingGrants.TryGetValue(virtualPath, out var grant))
            return false;

        var isAuthorized = grant.AuthorizedUsername == AnyoneRecipient
            || string.Equals(grant.AuthorizedUsername, requestingUsername, StringComparison.OrdinalIgnoreCase);
        if (!isAuthorized)
            return false;

        if (!File.Exists(grant.LocalPath))
            return false;

        entry = new ShareIndexEntry(grant.LocalPath, new FileInfo(grant.LocalPath).Length);
        return true;
    }

    private static void EvictOldestIfOverCap<TValue>(ConcurrentDictionary<string, TValue> dict, Func<TValue, DateTime> createdAtSelector)
    {
        if (dict.Count <= MaxTrackedAttachments) return;

        var oldest = dict
            .OrderBy(kv => createdAtSelector(kv.Value))
            .Take(EvictionBatchSize)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
        {
            dict.TryRemove(key, out _);
        }
    }

    private sealed record ChatAttachmentGrant(string LocalPath, string AuthorizedUsername, DateTime CreatedAtUtc);
}
