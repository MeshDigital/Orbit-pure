using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

public class MultiBoolToBlurConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // We expect all of the bools to be true to trigger blur in our current logic
        // (IsForensicLabVisible AND IntelligenceCenter.IsVisible)
        bool allTrue = values.All(x => x is bool b && b);
        
        if (allTrue)
        {
            // Use 15.0 for a significant blur in Forensic mode
            return new BlurEffect { Radius = 15.0 };
        }
        
        return null;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
