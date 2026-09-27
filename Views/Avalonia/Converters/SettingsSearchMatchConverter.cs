using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Drives the Settings page's search box: bind a section's IsVisible to
    /// {Binding SettingsSearchText} with a ConverterParameter of that section's own keyword
    /// string (its header + description text). Visible whenever the search box is empty or the
    /// keyword string contains the typed text, case-insensitively.
    /// </summary>
    public class SettingsSearchMatchConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var searchText = value as string;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            var keywords = parameter as string ?? string.Empty;
            return keywords.Contains(searchText, StringComparison.OrdinalIgnoreCase);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
