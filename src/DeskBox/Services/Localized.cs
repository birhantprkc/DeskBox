using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

public static class Localized
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    public static readonly DependencyProperty ToolTipKeyProperty =
        DependencyProperty.RegisterAttached(
            "ToolTipKey",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    private static readonly List<WeakReference<DependencyObject>> s_targets = [];

    public static string? GetKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(KeyProperty);
    }

    public static void SetKey(DependencyObject obj, string? value)
    {
        obj.SetValue(KeyProperty, value);
    }

    public static string? GetToolTipKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(ToolTipKeyProperty);
    }

    public static void SetToolTipKey(DependencyObject obj, string? value)
    {
        obj.SetValue(ToolTipKeyProperty, value);
    }

    public static void RefreshAll(LocalizationService localizationService)
    {
        for (int index = s_targets.Count - 1; index >= 0; index--)
        {
            if (!s_targets[index].TryGetTarget(out var target))
            {
                s_targets.RemoveAt(index);
                continue;
            }

            Apply(target, localizationService);
        }
    }

    private static void OnLocalizationPropertyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is null)
        {
            return;
        }

        Track(target);
        if (App.Current?.LocalizationService is { } localizationService)
        {
            Apply(target, localizationService);
        }
    }

    private static void Track(DependencyObject target)
    {
        if (s_targets.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, target)))
        {
            return;
        }

        s_targets.Add(new WeakReference<DependencyObject>(target));
    }

    private static void Apply(DependencyObject target, LocalizationService localizationService)
    {
        string? key = GetKey(target);
        if (!string.IsNullOrWhiteSpace(key))
        {
            string text = localizationService.T(key);
            switch (target)
            {
                case TextBlock textBlock:
                    textBlock.Text = text;
                    break;
                case ContentControl contentControl:
                    contentControl.Content = text;
                    break;
            }
        }

        string? toolTipKey = GetToolTipKey(target);
        if (!string.IsNullOrWhiteSpace(toolTipKey) && target is UIElement element)
        {
            ToolTipService.SetToolTip(element, localizationService.T(toolTipKey));
        }
    }
}
