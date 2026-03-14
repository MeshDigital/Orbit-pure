using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SLSKDONET.Services;

namespace SLSKDONET.Models;

/// <summary>
/// A lightweight proxy for lazy loading of artwork in virtualized lists.
/// The actual Bitmap is only fetched from the cache when the Image property is accessed by the binding engine.
/// </summary>
public class ArtworkProxy : INotifyPropertyChanged
{
    private readonly ArtworkCacheService _cacheService;
    private readonly string? _urlOrPath;
    private Bitmap? _image;
    private bool _isLoading;

    public ArtworkProxy(ArtworkCacheService cacheService, string? urlOrPath)
    {
        _cacheService = cacheService;
        _urlOrPath = urlOrPath;
    }

    public Bitmap? Image
    {
        get
        {
            if (_image == null && !_isLoading && !string.IsNullOrEmpty(_urlOrPath))
            {
                _isLoading = true;
                // Dispatch to background to avoid UI hitch on property access
                // But the binding expects a return value. Trigger load.
                _ = LoadAsync();
            }
            return _image;
        }
        private set
        {
            if (_image != value)
            {
                _image = value;
                OnPropertyChanged();
            }
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var bmp = await _cacheService.GetBitmapAsync(_urlOrPath);
            
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Image = bmp;
                _isLoading = false;
            });
        }
        catch
        {
             _isLoading = false;
             // On failure, Image remains null (placeholder will show)
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
