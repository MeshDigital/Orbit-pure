using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SLSKDONET.Views.Avalonia.Converters;

public class MathConverter : IValueConverter
{
    public enum MathOperation
    {
        Add,
        Subtract,
        Multiply,
        Divide
    }

    private readonly MathOperation _operation;

    public MathConverter(MathOperation operation)
    {
        _operation = operation;
    }

    public static readonly MathConverter Add = new(MathOperation.Add);
    public static readonly MathConverter Subtract = new(MathOperation.Subtract);
    public static readonly MathConverter Multiply = new(MathOperation.Multiply);
    public static readonly MathConverter Divide = new(MathOperation.Divide);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            double v = System.Convert.ToDouble(value);
            double p = System.Convert.ToDouble(parameter);

            return _operation switch
            {
                MathOperation.Add => v + p,
                MathOperation.Subtract => v - p,
                MathOperation.Multiply => v * p,
                MathOperation.Divide => v / p,
                _ => value
            };
        }
        catch
        {
            return value;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
