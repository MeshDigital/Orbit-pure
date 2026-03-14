using Avalonia.Media;
using ReactiveUI;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.ViewModels;

public class StyleFilterItem : ReactiveObject
{
    public StyleDefinitionEntity Style { get; }
    
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    
    public IBrush ColorBrush { get; }

    public StyleFilterItem(StyleDefinitionEntity style)
    {
        Style = style;
        // Parse hex color
        if (Color.TryParse(style.ColorHex, out var color))
        {
            ColorBrush = new SolidColorBrush(color);
        }
        else
        {
            ColorBrush = Brushes.Gray;
        }
    }
}
