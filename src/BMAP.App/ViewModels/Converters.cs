using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BMAP.App.ViewModels;

public static class Converters
{
    /// <summary>Green for the recommended row of the height analysis, white otherwise.</summary>
    public static readonly IValueConverter RecommendedBrush =
        new FuncValueConverter<bool, IBrush>(r => r ? new SolidColorBrush(Color.FromRgb(0x8F, 0xD4, 0x93)) : Brushes.White);
}