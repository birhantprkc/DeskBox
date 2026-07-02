using CommunityToolkit.WinUI.Controls;

namespace DeskBox.Services;

public static class WidgetSegmentedLayoutHelper
{
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
        }
    }
}
