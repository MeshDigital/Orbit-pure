using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// A single row in a chat message thread — shared between 1:1 conversations and chat rooms
/// (<see cref="UserProfileViewModel"/>'s Chat tab and <see cref="RoomViewModel"/>), rendered by
/// the shared MessageThreadView control.
///
/// Detects image-attachment offers (see <see cref="ChatAttachmentService"/>) embedded in the
/// plain-text message — Soulseek chat has no native attachment support, so an "offer" is just a
/// specially-formatted string both ORBIT clients recognize. Loading is manual (tap to load), not
/// automatic, so scrolling through old history with many images doesn't hammer possibly-offline
/// peers with redundant download attempts.
/// </summary>
public sealed class ChatMessageViewModel : ReactiveObject
{
    private readonly ChatAttachmentService? _attachments;
    private readonly string _remoteUsername;
    private readonly string _imageVirtualPath = string.Empty;

    public string SenderUsername { get; }
    public string Message { get; }
    public DateTime TimestampUtc { get; }
    public bool IsOutgoing { get; }

    public bool IsImageOffer { get; }
    public string ImageFileName { get; } = string.Empty;
    public long ImageSizeBytes { get; }

    public string ImageSizeDisplay => ImageSizeBytes >= 1024 * 1024
        ? $"{ImageSizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{Math.Max(1, ImageSizeBytes / 1024)} KB";

    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        private set => this.RaiseAndSetIfChanged(ref _image, value);
    }

    private bool _isLoadingImage;
    public bool IsLoadingImage
    {
        get => _isLoadingImage;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingImage, value);
    }

    private string? _imageError;
    public string? ImageError
    {
        get => _imageError;
        private set => this.RaiseAndSetIfChanged(ref _imageError, value);
    }

    public ReactiveCommand<Unit, Unit> LoadImageCommand { get; }

    /// <param name="remoteUsername">
    /// The peer to download the image FROM when this is an incoming offer — the profile's
    /// username for 1:1 chat, or whoever posted it for room chat. Irrelevant for outgoing offers
    /// (we already hold the file we sent), but always required since a message can't know in
    /// advance whether it carries an image offer.
    /// </param>
    public ChatMessageViewModel(string senderUsername, string message, DateTime timestampUtc, bool isOutgoing, ChatAttachmentService? attachments = null, string? remoteUsername = null)
    {
        SenderUsername = senderUsername;
        Message = message;
        TimestampUtc = timestampUtc;
        IsOutgoing = isOutgoing;
        _attachments = attachments;
        _remoteUsername = remoteUsername ?? senderUsername;

        if (ChatAttachmentService.TryParseImageOffer(message, out var size, out var virtualPath, out var fileName))
        {
            IsImageOffer = true;
            ImageSizeBytes = size;
            _imageVirtualPath = virtualPath;
            ImageFileName = fileName;
        }

        var canLoad = this.WhenAnyValue(x => x.IsLoadingImage, x => x.Image, (loading, image) => !loading && image == null);
        LoadImageCommand = ReactiveCommand.CreateFromTask(LoadImageAsync, canLoad);
    }

    private async Task LoadImageAsync()
    {
        if (!IsImageOffer || _attachments == null || Image != null || IsLoadingImage)
            return;

        IsLoadingImage = true;
        ImageError = null;
        try
        {
            var localPath = await _attachments.ResolveLocalImagePathAsync(_remoteUsername, _imageVirtualPath, ImageFileName, IsOutgoing).ConfigureAwait(true);
            await using var stream = File.OpenRead(localPath);
            Image = new Bitmap(stream);
        }
        catch (Exception)
        {
            ImageError = "Couldn't load image";
        }
        finally
        {
            IsLoadingImage = false;
        }
    }
}
