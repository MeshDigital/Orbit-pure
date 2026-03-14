using System;
using System.Globalization;
using Avalonia.Data.Converters;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Converts PlayerDockLocation to Grid positioning values (Row, Column, Span, etc.)
/// </summary>
public class PlayerLocationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PlayerDockLocation location || parameter is not string param)
            return 0;

        return param switch
        {
            // Grid.Row: Bottom bar = row 2, Sidebar = row 1
            "Row" => location == PlayerDockLocation.BottomBar ? 2 : 1,
            
            // Grid.Column: Bottom bar = column 0, Sidebar = column 2
            "Column" => location == PlayerDockLocation.BottomBar ? 0 : 2,
            
            // Grid.ColumnSpan: Bottom bar spans all 3 columns
            "ColumnSpan" => location == PlayerDockLocation.BottomBar ? 3 : 1,
            
            // Height: Bottom bar = 80px, Sidebar = auto (NaN)
            "Height" => location == PlayerDockLocation.BottomBar ? 80.0 : double.NaN,
            
            // Width: Bottom bar = auto (NaN), Sidebar = 300px
            "Width" => location == PlayerDockLocation.BottomBar ? double.NaN : 300.0,
            
            _ => 0
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException("PlayerLocationConverter does not support two-way binding");
    }
}
