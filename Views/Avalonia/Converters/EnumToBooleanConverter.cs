using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public class EnumToBooleanConverter : IValueConverter
    {
        public bool Negate { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Negate;
            
            // Robust check using string representation to handle type mismatches (int vs enum)
            bool isEqual = value?.ToString()?.Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ?? false;
            return Negate ? !isEqual : isEqual;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                return parameter;
            }
            return global::Avalonia.Data.BindingOperations.DoNothing;
        }
    }
}
