using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Services;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>Status dot fill: green=Running/Ready, yellow=Checking/Installing, red=else</summary>
public class AiStatusToDotBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiEngineStatus s ? s switch
        {
            AiEngineStatus.Running or AiEngineStatus.Ready
                => new SolidColorBrush(Color.Parse("#00C878")),
            AiEngineStatus.Checking or AiEngineStatus.Installing
                => new SolidColorBrush(Color.Parse("#DDB800")),
            _ => new SolidColorBrush(Color.Parse("#E03A3A"))
        } : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Card border color: green=Running, yellow=Installing, default=else</summary>
public class AiStatusToBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiEngineStatus s ? s switch
        {
            AiEngineStatus.Running    => new SolidColorBrush(Color.Parse("#00C878")),
            AiEngineStatus.Installing => new SolidColorBrush(Color.Parse("#DDB800")),
            AiEngineStatus.Ready      => new SolidColorBrush(Color.Parse("#2A5A8A")),
            _                         => new SolidColorBrush(Color.Parse("#2A3A4A"))
        } : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True when NotInstalled or Error → shows Install button</summary>
public class AiStatusToInstallVisConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiEngineStatus s && (s == AiEngineStatus.NotInstalled || s == AiEngineStatus.Error);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True when Ready → shows Start Server button</summary>
public class AiStatusToStartVisConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiEngineStatus s && s == AiEngineStatus.Ready;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True when Checking or Installing → shows spinner text</summary>
public class AiStatusToCheckingVisConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiEngineStatus s && (s == AiEngineStatus.Checking || s == AiEngineStatus.Installing);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
