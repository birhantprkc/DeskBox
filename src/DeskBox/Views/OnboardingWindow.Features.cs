using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep4()
    {
        Step4TodoToggle.Toggled -= Step4Toggle_Toggled;
        Step4QuickCaptureToggle.Toggled -= Step4Toggle_Toggled;
        Step4MusicToggle.Toggled -= Step4Toggle_Toggled;
        Step4WeatherToggle.Toggled -= Step4Toggle_Toggled;

        Step4TodoToggle.IsOn = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo);
        Step4QuickCaptureToggle.IsOn = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture);
        Step4MusicToggle.IsOn = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music);
        Step4WeatherToggle.IsOn = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Weather);

        Step4TodoToggle.Toggled += Step4Toggle_Toggled;
        Step4QuickCaptureToggle.Toggled += Step4Toggle_Toggled;
        Step4MusicToggle.Toggled += Step4Toggle_Toggled;
        Step4WeatherToggle.Toggled += Step4Toggle_Toggled;

        UpdateFeatureCardHighlight(Step4TodoCard, Step4TodoToggle.IsOn);
        UpdateFeatureCardHighlight(Step4QuickCaptureCard, Step4QuickCaptureToggle.IsOn);
        UpdateFeatureCardHighlight(Step4MusicCard, Step4MusicToggle.IsOn);
        UpdateFeatureCardHighlight(Step4WeatherCard, Step4WeatherToggle.IsOn);
    }

    private void Step4Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        WidgetKind kind;
        Border card;
        if (toggle == Step4TodoToggle)
        {
            kind = WidgetKind.Todo;
            card = Step4TodoCard;
        }
        else if (toggle == Step4QuickCaptureToggle)
        {
            kind = WidgetKind.QuickCapture;
            card = Step4QuickCaptureCard;
        }
        else if (toggle == Step4MusicToggle)
        {
            kind = WidgetKind.Music;
            card = Step4MusicCard;
        }
        else if (toggle == Step4WeatherToggle)
        {
            kind = WidgetKind.Weather;
            card = Step4WeatherCard;
        }
        else
        {
            return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, toggle.IsOn);
        _settingsService.SaveDebounced();
        UpdateFeatureCardHighlight(card, toggle.IsOn);

        // Play a subtle scale bounce on the card
        if (toggle.IsOn)
        {
            try
            {
                var transform = GetElementTransform(card);
                var storyboard = new Storyboard();
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

                var scaleXUp = new DoubleAnimation
                {
                    From = 1,
                    To = 1.04,
                    Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleXUp, transform);
                Storyboard.SetTargetProperty(scaleXUp, "ScaleX");
                storyboard.Children.Add(scaleXUp);

                var scaleYUp = new DoubleAnimation
                {
                    From = 1,
                    To = 1.04,
                    Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleYUp, transform);
                Storyboard.SetTargetProperty(scaleYUp, "ScaleY");
                storyboard.Children.Add(scaleYUp);

                var scaleXDown = new DoubleAnimation
                {
                    From = 1.04,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                    BeginTime = TimeSpan.FromMilliseconds(160),
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleXDown, transform);
                Storyboard.SetTargetProperty(scaleXDown, "ScaleX");
                storyboard.Children.Add(scaleXDown);

                var scaleYDown = new DoubleAnimation
                {
                    From = 1.04,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                    BeginTime = TimeSpan.FromMilliseconds(160),
                    EasingFunction = easing
                };
                Storyboard.SetTarget(scaleYDown, transform);
                Storyboard.SetTargetProperty(scaleYDown, "ScaleY");
                storyboard.Children.Add(scaleYDown);

                storyboard.Begin();
            }
            catch { }
        }
    }

    private void UpdateFeatureCardHighlight(Border card, bool isOn)
    {
        if (isOn)
        {
            card.BorderBrush = AccentBrush();
            card.BorderThickness = new Thickness(1.5);
        }
        else
        {
            card.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            card.BorderThickness = new Thickness(1);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: Daily Use
    // ════════════════════════════════════════════════════════════
}
