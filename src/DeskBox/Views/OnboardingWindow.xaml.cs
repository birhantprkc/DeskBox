using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow : Window
{
    private sealed record OnboardingStep(
        string Eyebrow,
        string Title,
        string Body,
        string[] Hints,
        string WidgetTitle,
        string WidgetGlyph);

    private static readonly OnboardingStep[] Steps =
    [
        new(
            "欢迎",
            "把桌面整理留给格子",
            "DeskBox 不接管桌面，只在 Windows 原生桌面上增加一层轻量整理能力。",
            ["桌面仍然是 Windows 桌面", "格子只是帮助你收纳、映射和快速访问"],
            "我的桌面",
            "\uE8B7"),
        new(
            "两种格子",
            "收纳和映射不一样",
            "收纳格子会把文件移动或复制到 DeskBox 的收纳路径；映射格子只是展示一个真实文件夹，文件仍在原位置。",
            ["收纳适合整理散落文件", "映射适合固定查看项目文件夹"],
            "收纳 / 映射",
            "\uE8A5"),
        new(
            "文件安全",
            "拖入、右键和删除都可控",
            "拖入收纳格子默认移动，也可以在设置里改成复制。删除格子时会明确提示，不会悄悄删除你的文件。",
            ["内容区右键可以添加文件", "映射格子可以随时更改映射路径"],
            "文件操作",
            "\uE7C3"),
        new(
            "托盘",
            "需要时把格子带到前面",
            "点击托盘图标可以临时把所有格子带到普通窗口前面；它不是始终置顶，其他应用仍然可以自然覆盖。",
            ["适合快速访问，不打断当前桌面习惯", "后续可在设置里再次查看这个引导"],
            "临时置顶",
            "\uE77B")
    ];

    private readonly SettingsService _settingsService;
    private readonly AppWindow _appWindow;
    private int _stepIndex;

    public OnboardingWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop
        {
            Kind = MicaKind.BaseAlt
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(860, 600));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - _appWindow.Size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - _appWindow.Size.Height) / 2)));

        RootGrid.Loaded += (_, _) =>
        {
            BuildProgressDots();
            RenderStep(animate: false);
        };
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0)
        {
            return;
        }

        _stepIndex--;
        RenderStep(animate: true);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < Steps.Length - 1)
        {
            _stepIndex++;
            RenderStep(animate: true);
            return;
        }

        await CompleteOnboardingAsync(openSettings: true);
    }

    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync(openSettings: false);
    }

    private async Task CompleteOnboardingAsync(bool openSettings)
    {
        _settingsService.Settings.HasCompletedOnboarding = true;
        await _settingsService.SaveAsync();
        Close();

        if (openSettings)
        {
            App.Current.ShowSettings();
        }
    }

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int index = 0; index < Steps.Length; index++)
        {
            ProgressDots.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Opacity = 0.42,
                Fill = (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"]
            });
        }
    }

    private void RenderStep(bool animate)
    {
        var step = Steps[_stepIndex];

        StepEyebrowText.Text = step.Eyebrow;
        StepTitleText.Text = step.Title;
        StepBodyText.Text = step.Body;
        DemoWidgetTitle.Text = step.WidgetTitle;
        DemoWidgetIcon.Glyph = step.WidgetGlyph;

        StepHintPanel.Children.Clear();
        foreach (string hint in step.Hints)
        {
            StepHintPanel.Children.Add(CreateHintRow(hint));
        }

        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == Steps.Length - 1 ? "开始使用" : "下一步";
        SkipButton.Visibility = _stepIndex == Steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressDots();

        if (animate)
        {
            PlayContentTransition();
        }

        PlayDemoAnimation();
    }

    private UIElement CreateHintRow(string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        return panel;
    }

    private void UpdateProgressDots()
    {
        for (int index = 0; index < ProgressDots.Children.Count; index++)
        {
            if (ProgressDots.Children[index] is not Ellipse dot)
            {
                continue;
            }

            bool active = index == _stepIndex;
            dot.Width = 8;
            dot.Height = 8;
            dot.Opacity = active ? 1 : 0.42;
            dot.Fill = active
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"];
        }
    }

    private void PlayContentTransition()
    {
        var storyboard = new Storyboard();
        AddOpacityAnimation(storyboard, StepTitleText, from: 0, to: 1, milliseconds: 220);
        AddOpacityAnimation(storyboard, StepBodyText, from: 0, to: 1, milliseconds: 240);
        AddOpacityAnimation(storyboard, DemoWidget, from: 0.72, to: 1, milliseconds: 260);
        storyboard.Begin();
    }

    private void PlayDemoAnimation()
    {
        ResetDemoVisuals();

        var storyboard = new Storyboard();
        switch (_stepIndex)
        {
            case 0:
                PlayWelcomeDemo(storyboard);
                break;
            case 1:
                PlayTypeDemo(storyboard);
                break;
            case 2:
                PlayFileDemo(storyboard);
                break;
            case 3:
                PlayTrayDemo(storyboard);
                break;
        }

        storyboard.Begin();
    }

    private void ResetDemoVisuals()
    {
        DemoWindow.Opacity = 0.92;
        DemoWidget.Opacity = 1;
        DemoMovingFile.Opacity = 0;

        ResetTransform(DemoWidgetTransform);
        ResetTransform(DemoMovingFileTransform);
        ResetFileVisual(DemoFileOne);
        ResetFileVisual(DemoFileTwo);
        ResetFileVisual(DemoFileThree);
    }

    private void PlayWelcomeDemo(Storyboard storyboard)
    {
        DemoWindow.Opacity = 0.68;
        DemoWidget.Opacity = 0.36;
        DemoWidgetTransform.TranslateX = 24;
        DemoWidgetTransform.TranslateY = 30;
        DemoWidgetTransform.ScaleX = 0.94;
        DemoWidgetTransform.ScaleY = 0.94;

        AddDoubleAnimation(storyboard, DemoWindow, "Opacity", 0.9, 360);
        AddOpacityAnimation(storyboard, DemoWidget, from: 0.36, to: 1, milliseconds: 540);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "TranslateX", 0, 540);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "TranslateY", 0, 540);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleX", 1, 540);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleY", 1, 540);
    }

    private void PlayTypeDemo(Storyboard storyboard)
    {
        var fileOneTransform = EnsureTransform(DemoFileOne);
        var fileTwoTransform = EnsureTransform(DemoFileTwo);

        DemoFileOne.Opacity = 0.48;
        DemoFileTwo.Opacity = 0.48;
        DemoFileThree.Opacity = 0.26;
        fileOneTransform.TranslateX = -8;
        fileTwoTransform.TranslateX = 8;
        fileOneTransform.ScaleX = 0.86;
        fileOneTransform.ScaleY = 0.86;
        fileTwoTransform.ScaleX = 0.86;
        fileTwoTransform.ScaleY = 0.86;

        AddOpacityAnimation(storyboard, DemoFileOne, from: 0.48, to: 1, milliseconds: 360);
        AddOpacityAnimation(storyboard, DemoFileTwo, from: 0.48, to: 1, milliseconds: 360);
        AddDoubleAnimation(storyboard, DemoFileThree, "Opacity", 0.34, 240);
        AddDoubleAnimation(storyboard, fileOneTransform, "TranslateX", 0, 360);
        AddDoubleAnimation(storyboard, fileTwoTransform, "TranslateX", 0, 360);
        AddDoubleAnimation(storyboard, fileOneTransform, "ScaleX", 1, 360);
        AddDoubleAnimation(storyboard, fileOneTransform, "ScaleY", 1, 360);
        AddDoubleAnimation(storyboard, fileTwoTransform, "ScaleX", 1, 360);
        AddDoubleAnimation(storyboard, fileTwoTransform, "ScaleY", 1, 360);
    }

    private void PlayFileDemo(Storyboard storyboard)
    {
        DemoWindow.Opacity = 0.78;
        DemoMovingFile.Opacity = 1;
        DemoMovingFileTransform.TranslateX = -8;
        DemoMovingFileTransform.TranslateY = 8;
        DemoMovingFileTransform.ScaleX = 1;
        DemoMovingFileTransform.ScaleY = 1;
        DemoWidgetTransform.ScaleX = 0.985;
        DemoWidgetTransform.ScaleY = 0.985;

        AddDoubleAnimation(storyboard, DemoMovingFileTransform, "TranslateX", 174, 680);
        AddDoubleAnimation(storyboard, DemoMovingFileTransform, "TranslateY", -74, 680);
        AddDoubleAnimation(storyboard, DemoMovingFileTransform, "ScaleX", 0.72, 680);
        AddDoubleAnimation(storyboard, DemoMovingFileTransform, "ScaleY", 0.72, 680);
        AddOpacityAnimation(storyboard, DemoMovingFile, from: 1, to: 0.16, milliseconds: 680);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleX", 1.018, 360);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleY", 1.018, 360);
    }

    private void PlayTrayDemo(Storyboard storyboard)
    {
        DemoWindow.Opacity = 1;
        DemoWidget.Opacity = 0.58;
        DemoWidgetTransform.TranslateY = 24;
        DemoWidgetTransform.ScaleX = 0.97;
        DemoWidgetTransform.ScaleY = 0.97;

        AddDoubleAnimation(storyboard, DemoWindow, "Opacity", 0.3, 320);
        AddOpacityAnimation(storyboard, DemoWidget, from: 0.58, to: 1, milliseconds: 520);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "TranslateY", -12, 520);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleX", 1.04, 520);
        AddDoubleAnimation(storyboard, DemoWidgetTransform, "ScaleY", 1.04, 520);
    }

    private static void ResetFileVisual(FrameworkElement element)
    {
        element.Opacity = 1;
        ResetTransform(EnsureTransform(element));
    }

    private static CompositeTransform EnsureTransform(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        if (element.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        transform = new CompositeTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void ResetTransform(CompositeTransform transform)
    {
        transform.TranslateX = 0;
        transform.TranslateY = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
        transform.Rotation = 0;
        transform.SkewX = 0;
        transform.SkewY = 0;
    }

    private static void AddOpacityAnimation(
        Storyboard storyboard,
        DependencyObject target,
        double from,
        double to,
        int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double to,
        int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
