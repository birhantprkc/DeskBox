using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public static class WidgetSegmentedLayoutHelper
{
    public static void ApplyNaturalItemWidths(Segmented segmented)
    {
        if (segmented.Items.Count == 0)
        {
            return;
        }

        foreach (var item in segmented.Items.OfType<SegmentedItem>())
        {
            item.Width = double.NaN;
            item.MaxWidth = double.PositiveInfinity;
            item.MinWidth = 0;
            item.ClearValue(Microsoft.UI.Xaml.Controls.Control.PaddingProperty);
            item.ClearValue(FrameworkElement.MinHeightProperty);
        }
    }

    public static void ApplyEqualItemWidths(Segmented segmented)
    {
        if (segmented.ActualWidth <= 0 || segmented.Items.Count == 0)
        {
            return;
        }

        double itemWidth = Math.Max(0, Math.Floor(segmented.ActualWidth / segmented.Items.Count));
        foreach (var item in segmented.Items.OfType<SegmentedItem>())
        {
            item.Width = itemWidth;
            item.MaxWidth = itemWidth;
            item.MinWidth = 0;
            item.Padding = new Thickness(4, 1, 4, 2);
            item.MinHeight = Math.Max(24, segmented.MinHeight - 3);
        }
    }
}
