using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public static class WidgetSegmentedStyleHelper
{
    public static void Apply(Segmented segmented, string? style)
    {
        ArgumentNullException.ThrowIfNull(segmented);

        string normalizedStyle = SettingsService.NormalizeWidgetTabStyle(style);
        if (normalizedStyle == SettingsService.WidgetTabStyleButton)
        {
            segmented.ClearValue(FrameworkElement.StyleProperty);
            return;
        }

        if (Application.Current.Resources.TryGetValue("WidgetPivotSegmentedStyle", out object resource) &&
            resource is Style segmentedStyle)
        {
            segmented.Style = segmentedStyle;
        }
    }
}
