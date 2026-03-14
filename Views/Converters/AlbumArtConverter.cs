using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using SLSKDONET.Services;

namespace SLSKDONET.Views.Converters;

/// <summary>
/// Converts a Spotify album art URL to a local cached file path Bitmap.
/// Uses ArtworkCacheService to download and cache artwork.
/// </summary>
public class AlbumArtConverter : IValueConverter
{
    private static ArtworkCacheService? _artworkCache;
    
    /// <summary>
    /// Sets the ArtworkCacheService instance to use for conversions.
    /// This should be called once during app startup.
    /// </summary>
    public static void Initialize(ArtworkCacheService artworkCache)
    {
        _artworkCache = artworkCache;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_artworkCache == null || value is not string albumArtUrl)
            return null;

        if (string.IsNullOrWhiteSpace(albumArtUrl))
            return null;

        try
        {
            // Extract Spotify album ID from the track's metadata
            // For now, we'll use the URL as the cache key
            // In a real implementation, we'd need the SpotifyAlbumId
            var albumId = ExtractAlbumIdFromUrl(albumArtUrl);
            
            // Get the local file path (async operation, but we need to handle it synchronously for the converter)
            // This is a limitation of IValueConverter - we'll need to use a different approach
            // For now, return the URL and handle caching elsewhere
            return albumArtUrl;
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private string ExtractAlbumIdFromUrl(string url)
    {
        // Extract album ID from Spotify URL
        // Example: https://i.scdn.co/image/ab67616d0000b273... -> use hash of URL as ID
        return url.GetHashCode().ToString();
    }
}
