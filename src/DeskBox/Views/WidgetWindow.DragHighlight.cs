// Copyright (c) DeskBox. All rights reserved.

using System.Numerics;
using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing the breathing drag-highlight logic for WidgetWindow.
/// When files are dragged over the widget (but not yet dropped), a subtle
/// accent-colored border pulses around the widget edge to indicate it can
/// accept the drop.
/// </summary>
public sealed partial class WidgetWindow
{
    private ScalarKeyFrameAnimation? _dragHighlightAnimation;
    private bool _isDragHighlightActive;

    /// <summary>
    /// Starts the breathing border animation.  Safe to call multiple times;
    /// subsequent calls are no-ops while the animation is already running.
    /// </summary>
    private void StartDragHighlight()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(StartDragHighlight);
            return;
        }

        if (_isDragHighlightActive || DragHighlightBorder is null)
        {
            return;
        }

        _isDragHighlightActive = true;

        // Use the current accent color for the border brush.
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        DragHighlightBorder.BorderBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xD8, accentColor.R, accentColor.G, accentColor.B));

        // Sync corner radius with the window's current corner preference.
        DragHighlightBorder.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());

        DragHighlightBorder.Visibility = Visibility.Visible;

        // Build the looping breathing animation on the border's visual.
        var visual = ElementCompositionPreview.GetElementVisual(DragHighlightBorder);
        if (visual is null)
        {
            return;
        }

        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");

        _dragHighlightAnimation = compositor.CreateScalarKeyFrameAnimation();
        _dragHighlightAnimation.Duration = TimeSpan.FromMilliseconds(1500);
        _dragHighlightAnimation.IterationBehavior = AnimationIterationBehavior.Forever;

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0.0f),
            new Vector2(0.58f, 1.0f));

        // Breathing: dim → bright → dim
        _dragHighlightAnimation.InsertKeyFrame(0.0f, 0.25f);
        _dragHighlightAnimation.InsertKeyFrame(0.5f, 0.90f, easing);
        _dragHighlightAnimation.InsertKeyFrame(1.0f, 0.25f, easing);

        visual.StartAnimation("Opacity", _dragHighlightAnimation);
    }

    /// <summary>
    /// Stops the breathing border animation and hides the border.
    /// </summary>
    private void StopDragHighlight()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(StopDragHighlight);
            return;
        }

        if (!_isDragHighlightActive || DragHighlightBorder is null)
        {
            return;
        }

        _isDragHighlightActive = false;

        var visual = ElementCompositionPreview.GetElementVisual(DragHighlightBorder);
        visual?.StopAnimation("Opacity");

        DragHighlightBorder.Visibility = Visibility.Collapsed;
        DragHighlightBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
        _dragHighlightAnimation = null;
    }
}
