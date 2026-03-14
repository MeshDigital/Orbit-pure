using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

public class RelativePositionConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3) return 0.0;

        try
        {
            double timestamp = System.Convert.ToDouble(values[0]);
            double duration = System.Convert.ToDouble(values[1]);
            double width = System.Convert.ToDouble(values[2]);

            if (duration <= 0) return 0.0;

            double ratio = timestamp / duration;
            return ratio * width;
        }
        catch
        {
            return 0.0;
        }
    }

    public static readonly RelativePositionConverter Instance = new();
}
