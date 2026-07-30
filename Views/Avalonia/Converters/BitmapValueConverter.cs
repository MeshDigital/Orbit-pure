using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SLSKDONET.Views.Avalonia.Converters;

public class BitmapValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    return new Bitmap(path);
                }
            }
            catch
            {
                // Ignore errors, return null (fallback to placeholder)
            }
        }

        if (value is byte[] { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch
            {
                // Ignore errors (e.g. malformed/unsupported image bytes), return null
            }
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
