using Avalonia.Data.Converters;
using System;
using System.Globalization;
using Avalonia;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class RepeatModeIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string repeatMode)
            {
                return repeatMode switch
                {
                    "None" => "🔁",
                    "One" => "🔂",
                    "All" => "🔁",
                    _ => "🔁"
                };
            }
            return "🔁";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EqualityConverter : IValueConverter, IMultiValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value?.Equals(parameter) ?? false;
        }

        public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values == null || values.Count < 2) return false;

            // 1. Evaluate Equality
            bool isEqual = Equals(values[0], values[1]);

            // 2. Return Result based on count
            // Case A: Just equality check (2 inputs) -> return bool
            if (values.Count == 2) return isEqual;

            // Case B: Conditional return (3 inputs) -> if equal return [2], else unset
            if (values.Count == 3) return isEqual ? values[2] : AvaloniaProperty.UnsetValue;


            // Case C: Ternary (4 inputs) -> if equal return [2], else return [3]
            if (values.Count >= 4) return isEqual ? values[2] : values[3];

            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class VuHeightConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is float vu && parameter is string paramStr && double.TryParse(paramStr, out var maxHeight))
            {
                // Simple linear scaling for now. In real DJ gear it's usually logarithmic.
                return Math.Clamp(vu * maxHeight, 0, maxHeight);
            }
            return 0.0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
