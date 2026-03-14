using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters
{
    public static class StringConverters
    {
        public static readonly IValueConverter ToUpper =
            new FuncValueConverter<string?, string?>(x => x?.ToUpperInvariant());
            
        public static readonly IValueConverter ToLower =
            new FuncValueConverter<string?, string?>(x => x?.ToLowerInvariant());
            
        public static readonly IValueConverter IsNotNullOrEmpty =
            new FuncValueConverter<string?, bool>(x => !string.IsNullOrEmpty(x));
    }
}
