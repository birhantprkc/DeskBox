using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DeskBox.Helpers;

internal static class DetailPageTransitionHelper
{
    private const int EnterDurationMs = 210;
    private const int ExitDurationMs = 150;
    private const float EnterOffsetY = 10f;
    private const float ExitOffsetY = 7f;

    public static void PlayEnter(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(EnterDurationMs);
        opacityAnimation.InsertKeyFrame(0f, 0.28f);
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);

        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(EnterDurationMs);
        offsetAnimation.InsertKeyFrame(0f, new Vector3(0, EnterOffsetY, 0));
        offsetAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);

        element.Opacity = 1;
        visual.Opacity = 1f;
        visual.Offset = Vector3.Zero;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Offset", offsetAnimation);
    }

    public static async Task PlayExitAsync(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f),
            new Vector2(0.6f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ExitDurationMs);
        opacityAnimation.InsertKeyFrame(0f, 1f);
        opacityAnimation.InsertKeyFrame(1f, 0f, easing);

        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(ExitDurationMs);
        offsetAnimation.InsertKeyFrame(0f, Vector3.Zero);
        offsetAnimation.InsertKeyFrame(1f, new Vector3(0, ExitOffsetY, 0), easing);

        visual.Opacity = 0f;
        visual.Offset = new Vector3(0, ExitOffsetY, 0);
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Offset", offsetAnimation);

        await Task.Delay(ExitDurationMs);
    }

    public static void Reset(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        element.Opacity = 1;
        visual.Opacity = 1f;
        visual.Offset = Vector3.Zero;
    }
}
