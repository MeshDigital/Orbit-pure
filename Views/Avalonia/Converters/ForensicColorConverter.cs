using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Models;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    /// <summary>
    /// Converts ForensicEntryType to color for display in Forensic Inspector panel.
    /// </summary>
    public class ForensicColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is ForensicEntryType entryType)
            {
                return entryType switch
                {
                    ForensicEntryType.Section => new SolidColorBrush(Color.Parse("#4EC9B0")),     // Cyan
                    ForensicEntryType.Bullet => new SolidColorBrush(Color.Parse("#CCCCCC")),     // Light Gray
                    ForensicEntryType.Warning => new SolidColorBrush(Color.Parse("#FFB300")),    // Orange
                    ForensicEntryType.Success => new SolidColorBrush(Color.Parse("#22DD22")),    // Green
                    ForensicEntryType.Detail => new SolidColorBrush(Color.Parse("#888888")),     // Dark Gray
                    ForensicEntryType.Verdict => new SolidColorBrush(Color.Parse("#FF4081")),    // Pink
                    _ => new SolidColorBrush(Color.Parse("#AAAAAA"))                              // Default Gray
                };
            }

            return new SolidColorBrush(Color.Parse("#AAAAAA"));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
