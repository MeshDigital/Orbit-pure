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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT-Pure", "ChatAttachments");

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

        var virtualPath = storedPath.Replace('/', '\\');
        _outgoingGrants[virtualPath] = new ChatAttachmentGrant(storedPath, recipientUsername, DateTime.UtcNow);
        EvictOldestIfOverCap(_outgoingGrants, g => g.CreatedAtUtc);

        _logger.LogInformation("Prepared outgoing chat image {FileName} ({Size} bytes) for {Recipient}", Path.GetFileName(localSourceFilePath), sourceInfo.Length, recipientUsername);

        return $"{ImageOfferPrefix}{sourceInfo.Length}|{virtualPath}|{Path.GetFileName(localSourceFilePath)}";
    }

    /// <summary>
    /// Resolves an image offer to a local file path — either the copy we already hold (if we're
    /// the one who sent it) or a freshly peer-to-peer downloaded copy (if we received it).
    /// </summary>
    public async Task<string> ResolveLocalImagePathAsync(string peerUsername, string virtualPath, string fileName, bool isOutgoing, CancellationToken ct = default)
    {
        if (isOutgoing && _outgoingGrants.TryGetValue(virtualPath, out var grant) && File.Exists(grant.LocalPath))
            return grant.LocalPath;

        if (_downloadedByVirtualPath.TryGetValue(virtualPath, out var cached) && File.Exists(cached.LocalPath))
            return cached.LocalPath;

        Directory.CreateDirectory(AttachmentsDirectory);
        var extension = Path.GetExtension(fileName);
        var localPath = Path.Combine(AttachmentsDirectory, $"recv_{Guid.NewGuid():N}{extension}");

        var adapter = _serviceProvider.GetRequiredService<ISoulseekAdapter>();
        var success = await adapter.DownloadAsync(peerUsername, virtualPath, localPath, ct: ct).ConfigureAwait(false);

        if (!success || !File.Exists(localPath))
            throw new IOException($"Failed to download image from {peerUsername}.");

        _downloadedByVirtualPath[virtualPath] = (localPath, DateTime.UtcNow);
        EvictOldestIfOverCap(_downloadedByVirtualPath, v => v.CreatedAtUtc);
        return localPath;
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
