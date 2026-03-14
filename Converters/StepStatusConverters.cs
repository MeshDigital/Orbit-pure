using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLSKDONET.Converters;

/// <summary>
/// Converts StepStatus to appropriate foreground brush for Command Center UI.
/// Active = Bright White, Complete = Dimmed Green, Pending = Ghosted, Error = Red
/// </summary>
public class StepStatusToBrushConverter : IValueConverter
{
    // DJ-grade color palette
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#FFFFFF"));
    private static readonly SolidColorBrush CompleteBrush = new(Color.Parse("#32CD32")); // LimeGreen
    private static readonly SolidColorBrush PendingBrush = new(Color.Parse("#555555"));
    private static readonly SolidColorBrush ErrorBrush = new(Color.Parse("#FF4444"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ViewModels.StepStatus status)
        {
            return status switch
            {
                ViewModels.StepStatus.Active => ActiveBrush,
                ViewModels.StepStatus.Complete => CompleteBrush,
                ViewModels.StepStatus.Error => ErrorBrush,
                _ => PendingBrush
            };
        }
        return PendingBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts StepStatus to font weight for Command Center UI.
/// Active = Bold, others = Normal
/// </summary>
public class StepStatusToWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ViewModels.StepStatus status)
        {
            return status == ViewModels.StepStatus.Active 
                ? Avalonia.Media.FontWeight.Bold 
                : Avalonia.Media.FontWeight.Normal;
        }
        return Avalonia.Media.FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts StepStatus to opacity for Command Center UI.
/// Active = 1.0, Complete = 0.7, Pending = 0.4
/// </summary>
public class StepStatusToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ViewModels.StepStatus status)
        {
            return status switch
            {
                ViewModels.StepStatus.Active => 1.0,
                ViewModels.StepStatus.Complete => 0.75,
                ViewModels.StepStatus.Error => 1.0,
                _ => 0.45
            };
        }
        return 0.45;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
