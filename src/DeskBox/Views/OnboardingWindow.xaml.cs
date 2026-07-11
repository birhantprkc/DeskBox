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

public sealed partial class OnboardingWindow : Window
{
    private const int DesiredWindowWidth = 1040;
    private const int DesiredWindowHeight = 740;
    private const int MinWindowWidth = 660;
    private const int MinWindowHeight = 540;
    private const int WindowWorkAreaMargin = 96;
    private const int CompactLayoutThreshold = 880;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private sealed record IntroMarkTarget(double TranslateX, double TranslateY, double Scale);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;

    private Storyboard? _introStoryboard;
    private Storyboard? _brandLogoShineStoryboard;
    private Storyboard? _stepTransitionStoryboard;
    private Storyboard? _keycapPulseStoryboard;
    private System.Threading.CancellationTokenSource? _hotkeyDemoCts;
    private int _introGeneration;
    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isSubclassInstalled;
    private bool _isAnimating;
    private bool _isRecordingHotkey;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;

    // Accent color preset list
    private static readonly string[] PresetAccentColors = { "#0078D4", "#E81123", "#107C10", "#5D2E9B", "#FF8C00", "#0099BC" };

    public OnboardingWindow(SettingsService settingsService, LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _windowSubclassProc = WindowSubclassProc;
        InitializeComponent();
        _localizationService.LanguageChanged += OnLanguageChanged;

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        Title = localizationService.T("Onboarding.WindowTitle");
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        ResizeAndCenterForDisplay(windowId);
        InstallMinimumSizeHook();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = false;
        }

        SizeChanged += (_, _) => ApplyResponsiveLayout();
        RootGrid.KeyDown += (_, e) => OnHotkeyKeyDown(e.Key);
        RootGrid.Loaded += (_, _) =>
        {
            _hasLoaded = true;
            ApplyResponsiveLayout();
            ApplyTitleBarButtonColors();
            BuildProgressDots();
            SetupStep(animate: false);
            StartBrandLogoShine();
            PlayIntroSequence();

            DispatcherQueue.TryEnqueue(async () =>
            {
                int introGeneration = _introGeneration;
                await Task.Delay(5200);
                if (introGeneration == _introGeneration &&
                    IntroOverlay.Visibility == Visibility.Visible &&
                    (StepContainer.Opacity <= 0.01 ||
                     FooterNav.Opacity <= 0.01))
                {
                    App.Log("[Onboarding] First paint fallback restored hidden main content.");
                    DismissIntro();
                }
            });
        };

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarButtonColors();
            PrepareIntroContent();
            SetupStep(animate: false);
        };

        Closed += (_, _) =>
        {
            _introGeneration++;
            _introStoryboard?.Stop();
            _brandLogoShineStoryboard?.Stop();
            _stepTransitionStoryboard?.Stop();
            _keycapPulseStoryboard?.Stop();
            _hotkeyDemoCts?.Cancel();
            _hotkeyDemoCts?.Dispose();
            _hotkeyDemoCts = null;
            IntroMarkHost.Children.Clear();
            RemoveMinimumSizeHook();
            _localizationService.LanguageChanged -= OnLanguageChanged;
        };
    }

    public void RestartIntro()
    {
        if (!_hasLoaded)
        {
            return;
        }

        _stepIndex = 0;
        SetupStep(animate: false);
        PlayIntroSequence();
    }

    // ════════════════════════════════════════════════════════════
    //  Window Setup
    // ════════════════════════════════════════════════════════════

    private void ResizeAndCenterForDisplay(Microsoft.UI.WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetCurrentDpiScale();
        int desiredWidth = ToPhysicalPixels(DesiredWindowWidth, scale);
        int desiredHeight = ToPhysicalPixels(DesiredWindowHeight, scale);
        int minWidth = ToPhysicalPixels(MinWindowWidth, scale);
        int minHeight = ToPhysicalPixels(MinWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        int width = Math.Clamp(desiredWidth, minWidth, Math.Max(minWidth, workArea.Width - workAreaMargin));
        int height = Math.Clamp(desiredHeight, minHeight, Math.Max(minHeight, workArea.Height - workAreaMargin));

        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private void ApplyResponsiveLayout()
    {
        double width = RootGrid.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        bool compact = width < CompactLayoutThreshold;
        RootGrid.Padding = compact ? new Thickness(28) : new Thickness(40);
        TitleBarHost.Margin = compact
            ? new Thickness(-28, -28, -28, 6)
            : new Thickness(-40, -40, -40, 6);
        IntroOverlay.Margin = compact ? new Thickness(-28) : new Thickness(-40);
        IntroOverlay.Padding = compact ? new Thickness(28) : new Thickness(40);
        FooterNav.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(0, 24, 0, 0);

        // Step 3: stack columns vertically in compact mode
        if (Step3Panel.ColumnDefinitions.Count > 0)
        {
            if (compact)
            {
                Step3Panel.ColumnSpacing = 0;
                Step3Panel.RowSpacing = 20;
                Step3Panel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                Step3Panel.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(Step3PreviewHost, 1);
                Grid.SetColumn(Step3PreviewHost, 0);
                Step3PreviewHost.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                Step3Panel.ColumnSpacing = 32;
                Step3Panel.RowSpacing = 0;
                Step3Panel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                Step3Panel.ColumnDefinitions[1].Width = GridLength.Auto;
                Grid.SetRow(Step3PreviewHost, 0);
                Grid.SetColumn(Step3PreviewHost, 1);
                Step3PreviewHost.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        ProgressDots.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    // ════════════════════════════════════════════════════════════
    //  Step Navigation
    // ════════════════════════════════════════════════════════════

    private static readonly int StepCount = 6;

    private FrameworkElement GetStepPanel(int index) => index switch
    {
        0 => Step1Panel,
        1 => Step2Panel,
        2 => Step3Panel,
        3 => Step4Panel,
        4 => Step5Panel,
        5 => Step6Panel,
        _ => Step1Panel
    };

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0 || _isAnimating)
        {
            return;
        }

        _ = NavigateToStepAsync(_stepIndex - 1, forward: false);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimating)
        {
            return;
        }

        if (_stepIndex < StepCount - 1)
        {
            await NavigateToStepAsync(_stepIndex + 1, forward: true);
            return;
        }

        await CompleteOnboardingAsync();
    }

    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync();
    }

    private async Task CompleteOnboardingAsync()
    {
        _settingsService.Settings.HasCompletedOnboarding = true;
        await _settingsService.SaveAsync();
        Close();
    }

    private async Task NavigateToStepAsync(int newStep, bool forward)
    {
        if (newStep == _stepIndex || _isAnimating)
        {
            return;
        }

        _isAnimating = true;
        StopStepAnimations();

        var currentPanel = GetStepPanel(_stepIndex);
        var newPanel = GetStepPanel(newStep);

        // Prepare new panel start state
        newPanel.Visibility = Visibility.Visible;
        double enterFromX = forward ? 40 : -40;
        SetElementTransform(newPanel, translateX: enterFromX, translateY: 0, scale: 0.99);
        newPanel.Opacity = 0;

        // Build transition storyboard
        _stepTransitionStoryboard?.Stop();
        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // ── Animate current panel out ──
        var curOpacityOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = easing
        };
        Storyboard.SetTarget(curOpacityOut, currentPanel);
        Storyboard.SetTargetProperty(curOpacityOut, "Opacity");
        storyboard.Children.Add(curOpacityOut);

        var curTransform = GetElementTransform(currentPanel);
        var curXOut = new DoubleAnimation
        {
            From = 0,
            To = forward ? -40 : 40,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = easing
        };
        Storyboard.SetTarget(curXOut, curTransform);
        Storyboard.SetTargetProperty(curXOut, "TranslateX");
        storyboard.Children.Add(curXOut);

        // ── Animate new panel in (delayed) ──
        int inDelay = 140;
        var newOpacityIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(340)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newOpacityIn, newPanel);
        Storyboard.SetTargetProperty(newOpacityIn, "Opacity");
        storyboard.Children.Add(newOpacityIn);

        var newTransform = GetElementTransform(newPanel);
        var newXIn = new DoubleAnimation
        {
            From = enterFromX,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newXIn, newTransform);
        Storyboard.SetTargetProperty(newXIn, "TranslateX");
        storyboard.Children.Add(newXIn);

        var newScaleInX = new DoubleAnimation
        {
            From = 0.99,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newScaleInX, newTransform);
        Storyboard.SetTargetProperty(newScaleInX, "ScaleX");
        storyboard.Children.Add(newScaleInX);

        var newScaleInY = new DoubleAnimation
        {
            From = 0.99,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newScaleInY, newTransform);
        Storyboard.SetTargetProperty(newScaleInY, "ScaleY");
        storyboard.Children.Add(newScaleInY);

        _stepTransitionStoryboard = storyboard;
        int previousStep = _stepIndex;
        _stepIndex = newStep;

        storyboard.Completed += (_, _) =>
        {
            currentPanel.Visibility = Visibility.Collapsed;
            SetElementTransform(currentPanel);
            currentPanel.Opacity = 1;
            _isAnimating = false;

            // Start step-specific ambient animations
            StartStepAmbientAnimation(newStep);
        };

        UpdateFooterState();
        SetupStep(animate: true);
        storyboard.Begin();
    }

    /// <summary>
    /// Sets up the current step's dynamic content and wires up events.
    /// Called on initial load (animate=false) and during transitions (animate=true).
    /// </summary>
    private void SetupStep(bool animate)
    {
        switch (_stepIndex)
        {
            case 0:
                // Step 1 has no dynamic content
                break;
            case 1:
                SetupStep2();
                break;
            case 2:
                SetupStep3();
                break;
            case 3:
                SetupStep4();
                break;
            case 4:
                SetupStep5();
                break;
            case 5:
                SetupStep6();
                break;
        }
    }

    private void StopStepAnimations()
    {
        _keycapPulseStoryboard?.Stop();
        _keycapPulseStoryboard = null;
        _hotkeyDemoCts?.Cancel();
        _hotkeyDemoCts?.Dispose();
        _hotkeyDemoCts = null;
    }

    /// <summary>
    /// Starts ambient (looping) animations specific to a step.
    /// </summary>
    private void StartStepAmbientAnimation(int step)
    {
        switch (step)
        {
            case 4:
                // Step 5: Start keycap pulse if hotkey is enabled
                if (Step5HotkeyToggle.IsOn)
                {
                    StartKeycapPulse();
                }
                break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Footer State
    // ════════════════════════════════════════════════════════════

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int index = 0; index < StepCount; index++)
        {
            ProgressDots.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Opacity = 0.42,
                Fill = SubtleDotBrush()
            });
        }
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
            dot.Fill = active ? AccentBrush() : SubtleDotBrush();
        }
    }

    private void UpdateFooterState()
    {
        BackButton.IsEnabled = _stepIndex > 0;
        SkipButton.Content = _localizationService.T("Onboarding.Skip");
        BackButton.Content = _localizationService.T("Onboarding.Back");
        NextButton.Content = _stepIndex == StepCount - 1
            ? _localizationService.T("Onboarding.Start")
            : _localizationService.T("Onboarding.Next");
        SkipButton.Visibility = _stepIndex == StepCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressDots();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 2: Storage & Quick Access
    // ════════════════════════════════════════════════════════════

    private void SetupStep2()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        Step2PathText.Text = path;

        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        Step2PinToggle.Toggled -= Step2PinToggle_Toggled;
        Step2PinToggle.IsOn = isPinned;
        Step2PinToggle.Toggled += Step2PinToggle_Toggled;
    }

    private void Step2ChangePath_Click(object sender, RoutedEventArgs e)
    {
        _ = ChangeStoragePathAsync();
    }

    private async Task ChangeStoragePathAsync()
    {
        string? folderPath = FolderPickerService.PickFolder(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        string currentPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0 && RootGrid.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = _localizationService.T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = _localizationService.T("Settings.Dialog.MigrateButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        currentPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
            }
            catch (Exception ex)
            {
                if (RootGrid.XamlRoot is not null)
                {
                    var errorDialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
                        CloseButtonText = _localizationService.T("Common.Ok"),
                        DefaultButton = ContentDialogButton.Close,
                        Content = new TextBlock
                        {
                            Text = _localizationService.Format("Settings.Dialog.MigrateFailedBody", ex.Message),
                            TextWrapping = TextWrapping.Wrap
                        }
                    };
                    await errorDialog.ShowAsync();
                }
                return;
            }
        }

        _settingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
        _settingsService.SaveDebounced();
        Step2PathText.Text = normalizedPath;
    }

    private async void Step2PinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        string storagePath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);

        if (toggle.IsOn)
        {
            var result = await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(storagePath);
            if (!result.Succeeded && RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = _localizationService.T("Onboarding.Step2.PinTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = _localizationService.T("Onboarding.Step2.PinDescription"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await dialog.ShowAsync();
            }
        }
        else
        {
            await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(storagePath);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 3: Appearance
    // ════════════════════════════════════════════════════════════

    private void SetupStep3()
    {
        BuildThemeSelector();
        BuildAccentSelector();
        BuildMaterialSelector();
        UpdateAppearancePreview();
    }

    private void BuildThemeSelector()
    {
        Step3ThemeSelector.Children.Clear();
        string[] themeKeys = { "System", "Light", "Dark" };
        string[] themeLabelKeys = { "Onboarding.Step3.ThemeSystem", "Onboarding.Step3.ThemeLight", "Onboarding.Step3.ThemeDark" };
        string currentTheme = _settingsService.Settings.Theme;

        for (int i = 0; i < themeKeys.Length; i++)
        {
            string key = themeKeys[i];
            var rb = new RadioButton
            {
                Content = _localizationService.T(themeLabelKeys[i]),
                IsChecked = string.Equals(currentTheme, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetTheme(capturedKey);
                }
                else
                {
                    _settingsService.Settings.Theme = capturedKey;
                    _settingsService.SaveDebounced();
                }
                UpdateAppearancePreview();
            };
            Step3ThemeSelector.Children.Add(rb);
        }
    }

    private void BuildAccentSelector()
    {
        Step3AccentSelector.Children.Clear();
        bool useSystemAccent = !string.Equals(_settingsService.Settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);

        var systemAccentRb = new RadioButton
        {
            Content = _localizationService.T("Onboarding.Step3.UseSystemAccent"),
            IsChecked = useSystemAccent,
            MinWidth = 0,
            Padding = new Thickness(10, 4, 10, 4)
        };
        systemAccentRb.Checked += (_, _) =>
        {
            if (App.Current.ThemeService is { } ts)
            {
                ts.SetAccentMode(ThemeService.AccentModeSystem);
            }
            else
            {
                _settingsService.Settings.AccentColorMode = ThemeService.AccentModeSystem;
                _settingsService.SaveDebounced();
            }
            UpdateAppearancePreview();
        };
        Step3AccentSelector.Children.Add(systemAccentRb);

        foreach (string colorHex in PresetAccentColors)
        {
            var color = ColorHelper.FromArgb(0xFF,
                Convert.ToByte(colorHex.Substring(1, 2), 16),
                Convert.ToByte(colorHex.Substring(3, 2), 16),
                Convert.ToByte(colorHex.Substring(5, 2), 16));

            var colorBtn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Content = null
            };
            string captured = colorHex;
            colorBtn.Click += (_, _) =>
            {
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetCustomAccentColor(color);
                }
                else
                {
                    _settingsService.Settings.AccentColorMode = ThemeService.AccentModeCustom;
                    _settingsService.Settings.CustomAccentColor = captured;
                    _settingsService.SaveDebounced();
                }
                systemAccentRb.IsChecked = false;
                UpdateAppearancePreview();
            };
            Step3AccentSelector.Children.Add(colorBtn);
        }
    }

    private void BuildMaterialSelector()
    {
        Step3MaterialSelector.Children.Clear();
        string[] materialKeys = { "Mica", "Acrylic", "Solid" };
        string[] materialLabelKeys = { "Onboarding.Step3.MaterialMica", "Onboarding.Step3.MaterialAcrylic", "Onboarding.Step3.MaterialSolid" };
        string currentMaterial = _settingsService.Settings.WidgetMaterialType;

        for (int i = 0; i < materialKeys.Length; i++)
        {
            string key = materialKeys[i];
            var rb = new RadioButton
            {
                Content = _localizationService.T(materialLabelKeys[i]),
                IsChecked = string.Equals(currentMaterial, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                _settingsService.Settings.WidgetMaterialType = capturedKey;
                _settingsService.SaveDebounced();
                if (App.Current.ThemeService is { } ts)
                {
                    ts.RefreshAppearance();
                }
                UpdateAppearancePreview();
            };
            Step3MaterialSelector.Children.Add(rb);
        }
    }

    private void UpdateAppearancePreview()
    {
        // Update preview background based on material
        string material = _settingsService.Settings.WidgetMaterialType;
        if (Step3PreviewBackground is SolidColorBrush brush)
        {
            brush.Color = material switch
            {
                "Acrylic" => IsDarkTheme()
                    ? ColorHelper.FromArgb(0xCC, 0x2C, 0x2C, 0x2C)
                    : ColorHelper.FromArgb(0xCC, 0xF4, 0xF4, 0xF4),
                "Solid" => IsDarkTheme()
                    ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xF8),
                _ => IsDarkTheme() // Mica
                    ? ColorHelper.FromArgb(0x80, 0x1C, 0x1C, 0x1C)
                    : ColorHelper.FromArgb(0x80, 0xFA, 0xFA, 0xFA)
            };
        }

        // Update accent-colored elements
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        if (Step3PreviewIcon?.Background is SolidColorBrush iconBrush)
        {
            iconBrush.Color = accentColor;
        }
        if (Step3PreviewItem1?.Background is SolidColorBrush itemBrush)
        {
            itemBrush.Color = accentColor;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: Feature Widgets
    // ════════════════════════════════════════════════════════════

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

    private void SetupStep5()
    {
        Step5HotkeyToggle.Toggled -= Step5HotkeyToggle_Toggled;
        Step5StartupToggle.Toggled -= Step5StartupToggle_Toggled;

        Step5HotkeyToggle.IsOn = _settingsService.Settings.GlobalHotkeyEnabled;
        Step5StartupToggle.IsOn = StartupService.IsEnabled();

        Step5HotkeyToggle.Toggled += Step5HotkeyToggle_Toggled;
        Step5StartupToggle.Toggled += Step5StartupToggle_Toggled;

        RefreshHotkeyChangeButton();
        Step5HotkeyChangeButton.IsEnabled = Step5HotkeyToggle.IsOn;

        if (Step5HotkeyToggle.IsOn && !_isAnimating)
        {
            StartKeycapPulse();
        }
    }

    private void RefreshHotkeyChangeButton()
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        Step5KeycapText.Text = hotkeyText;
        Step5HotkeyChangeButton.Content = hotkeyText;
    }

    private void Step5HotkeyChange_Click(object sender, RoutedEventArgs e)
    {
        BeginHotkeyRecording();
    }

    private void BeginHotkeyRecording()
    {
        _isRecordingHotkey = true;
        Step5HotkeyChangeButton.Content = _localizationService.T("Onboarding.Step5.HotkeyRecording");
        Step5HotkeyChangeButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        RefreshHotkeyChangeButton();
    }

    private async Task ApplyRecordedHotkeyAsync(GlobalHotkeyGesture gesture)
    {
        EndHotkeyRecording();
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.TryApplyGesture(gesture, out string? error))
        {
            if (RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await dialog.ShowAsync();
            }
        }

        RefreshHotkeyChangeButton();
    }

    private static HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        var modifiers = HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
        }
        return modifiers;
    }

    private static bool IsModifierKey(Windows.System.VirtualKey key)
    {
        return key is
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift;
    }

    private void OnHotkeyKeyDown(Windows.System.VirtualKey key)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var gesture = new GlobalHotkeyGesture(
            GetPressedHotkeyModifiers(),
            (int)key);
        _ = ApplyRecordedHotkeyAsync(gesture);
    }

    private void Step5HotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (App.Current.GlobalHotkeyService is { } globalHotkeyService)
        {
            globalHotkeyService.SetEnabled(toggle.IsOn);
        }
        else
        {
            _settingsService.Settings.GlobalHotkeyEnabled = toggle.IsOn;
            _settingsService.SaveDebounced();
        }
        Step5HotkeyChangeButton.IsEnabled = toggle.IsOn;

        if (toggle.IsOn)
        {
            StartKeycapPulse();
        }
        else
        {
            _keycapPulseStoryboard?.Stop();
            _keycapPulseStoryboard = null;
            SetElementTransform(Step5Keycap);
        }
    }

    private void Step5StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        StartupService.SetEnabled(toggle.IsOn);
        _settingsService.Settings.AutoStart = toggle.IsOn;
        _settingsService.SaveDebounced();
    }

    private void StartKeycapPulse()
    {
        _keycapPulseStoryboard?.Stop();

        var transform = GetElementTransform(Step5Keycap);
        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };

        var scaleUpX = new DoubleAnimation
        {
            From = 1,
            To = 1.06,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scaleUpX, transform);
        Storyboard.SetTargetProperty(scaleUpX, "ScaleX");
        storyboard.Children.Add(scaleUpX);

        var scaleUpY = new DoubleAnimation
        {
            From = 1,
            To = 1.06,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scaleUpY, transform);
        Storyboard.SetTargetProperty(scaleUpY, "ScaleY");
        storyboard.Children.Add(scaleUpY);

        _keycapPulseStoryboard = storyboard;
        storyboard.Begin();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 6: Ready Summary
    // ════════════════════════════════════════════════════════════

    private void SetupStep6()
    {
        // Storage summary
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        string pinStatus = isPinned
            ? _localizationService.T("Onboarding.Step6.SummaryPinned")
            : _localizationService.T("Onboarding.Step6.SummaryNotPinned");
        Step6StorageSummary.Text = $"{System.IO.Path.GetFileName(path)} · {pinStatus}";

        // Appearance summary
        string themeLabel = _settingsService.Settings.Theme switch
        {
            "Light" => _localizationService.T("Onboarding.Step3.ThemeLight"),
            "Dark" => _localizationService.T("Onboarding.Step3.ThemeDark"),
            _ => _localizationService.T("Onboarding.Step3.ThemeSystem")
        };
        string materialLabel = _settingsService.Settings.WidgetMaterialType switch
        {
            "Acrylic" => _localizationService.T("Onboarding.Step3.MaterialAcrylic"),
            "Solid" => _localizationService.T("Onboarding.Step3.MaterialSolid"),
            _ => _localizationService.T("Onboarding.Step3.MaterialMica")
        };
        Step6AppearanceSummary.Text = $"{themeLabel} · {materialLabel}";

        // Widgets summary
        var enabledWidgets = new List<string>();
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.TodoTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.QuickCaptureTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.MusicTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Weather))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.WeatherTitle"));
        }
        Step6WidgetsSummary.Text = enabledWidgets.Count > 0
            ? string.Join(" · ", enabledWidgets)
            : _localizationService.T("Onboarding.Step6.NoWidgets");

        // Daily use summary
        string hotkeySummary = _settingsService.Settings.GlobalHotkeyEnabled
            ? _localizationService.T("Onboarding.Step6.SummaryHotkeyOn")
            : _localizationService.T("Onboarding.Step6.SummaryHotkeyOff");
        string startupSummary = StartupService.IsEnabled()
            ? _localizationService.T("Onboarding.Step6.SummaryStartupOn")
            : _localizationService.T("Onboarding.Step6.SummaryStartupOff");
        Step6DailySummary.Text = $"{hotkeySummary} · {startupSummary}";
    }

    // ════════════════════════════════════════════════════════════
    //  Localization
    // ════════════════════════════════════════════════════════════

    private void OnLanguageChanged()
    {
        Title = _localizationService.T("Onboarding.WindowTitle");
        PrepareIntroContent();
        SetupStep(animate: false);
        UpdateFooterState();
        Localized.RefreshAll(_localizationService);
    }

    // ════════════════════════════════════════════════════════════
    //  Intro Sequence (preserved from original)
    // ════════════════════════════════════════════════════════════

    private void PrepareIntroContent()
    {
        IntroTitleText.Text = _localizationService.T("Onboarding.Intro.Title");
        IntroBodyText.Text = _localizationService.T("Onboarding.Intro.Body");
    }

    private async void PlayIntroSequence()
    {
        _introStoryboard?.Stop();
        int introGeneration = ++_introGeneration;
        PrepareIntroContent();

        StepContainer.IsHitTestVisible = false;
        FooterNav.IsHitTestVisible = false;
        StepContainer.Opacity = 0;
        FooterNav.Opacity = 0;
        BrandLogoHost.Opacity = 0;
        IntroOverlay.Opacity = 1;
        IntroOverlay.Visibility = Visibility.Visible;
        IntroMarkHost.Children.Clear();
        SetElementTransform(IntroOverlay);
        SetElementTransform(StepContainer, translateY: 8, scale: 0.995);
        SetElementTransform(FooterNav, translateY: 8);
        SetElementTransform(BrandLogoHost);

        var mark = CreateDeskBoxMark(size: 170, layerWidth: 108, layerHeight: 102, cornerRadius: 18, offsetX: 25, offsetY: 20);
        IntroMarkHost.Children.Add(mark);
        SetElementTransform(IntroMarkHost);

        var backLayer = mark.Children[0];
        var middleLayer = mark.Children[1];
        var frontLayer = mark.Children[2];

        SetElementOpacity(backLayer, 0);
        SetElementOpacity(middleLayer, 0);
        SetElementOpacity(frontLayer, 0);
        SetTransformValues(backLayer, translateX: -36, translateY: -18, scale: 0.9);
        SetTransformValues(middleLayer, translateX: -14, translateY: -8, scale: 0.93);
        SetTransformValues(frontLayer, translateX: 28, translateY: 18, scale: 0.95);

        IntroTitleText.Opacity = 0;
        IntroBodyText.Opacity = 0;
        SetElementTransform(IntroTitleText, translateY: 8, scale: 1);
        SetElementTransform(IntroBodyText, translateY: 8, scale: 1);

        try
        {
            var animationTask = RunIntroAnimationAsync(introGeneration, backLayer, middleLayer, frontLayer);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            if (await Task.WhenAny(animationTask, timeoutTask) == timeoutTask)
            {
                App.Log("[Onboarding] Intro animation timed out; showing main content fallback.");
            }
            else
            {
                await animationTask;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Intro animation failed; showing main content fallback. {ex}");
            if (introGeneration == _introGeneration)
            {
                DismissIntro();
                return;
            }
        }

        if (introGeneration == _introGeneration)
        {
            DismissIntro();
        }
    }

    private async Task RunIntroAnimationAsync(
        int introGeneration,
        UIElement backLayer,
        UIElement middleLayer,
        UIElement frontLayer)
    {
        await AnimateIntroLayerAsync(introGeneration, backLayer, -36, -18, 0, 0, 0.9, 1, 380);
        await Task.Delay(70);
        if (introGeneration != _introGeneration) return;
        await AnimateIntroLayerAsync(introGeneration, middleLayer, -14, -8, 0, 0, 0.93, 1, 340);
        await Task.Delay(70);
        if (introGeneration != _introGeneration) return;
        await AnimateIntroLayerAsync(introGeneration, frontLayer, 28, 18, 0, 0, 0.95, 1, 340);
        if (introGeneration != _introGeneration) return;

        await AnimateIntroElementAsync(introGeneration, IntroTitleText, 0, 1, 0, 0, 8, 0, 1, 1, 220);
        await AnimateIntroElementAsync(introGeneration, IntroBodyText, 0, 1, 0, 0, 8, 0, 1, 1, 220);
        await Task.Delay(520);
        if (introGeneration != _introGeneration) return;

        _ = AnimateIntroElementAsync(introGeneration, IntroTitleText, 1, 0, 0, 0, 0, -6, 1, 0.98, 180);
        _ = AnimateIntroElementAsync(introGeneration, IntroBodyText, 1, 0, 0, 0, 0, -6, 1, 0.98, 180);
        _ = AnimateIntroElementAsync(introGeneration, StepContainer, 0, 1, 0, 0, 8, 0, 0.995, 1, 360);
        _ = AnimateIntroElementAsync(introGeneration, FooterNav, 0, 1, 0, 0, 8, 0, 1, 1, 320);
        _ = AnimateIntroElementAsync(introGeneration, BrandLogoHost, 0, 1, 0, 0, 0, 0, 1, 1, 240);
        var target = GetIntroMarkTargetTransform();
        await AnimateIntroElementAsync(
            introGeneration,
            IntroMarkHost,
            1, 0.98, 0, target.TranslateX, 0, target.TranslateY, 1, target.Scale, 620);
        if (introGeneration != _introGeneration) return;

        await AnimateIntroElementAsync(introGeneration, IntroOverlay, 1, 0, 0, 0, 0, 0, 1, 1, 220);
    }

    private Task AnimateIntroLayerAsync(
        int introGeneration,
        UIElement element,
        double fromX, double fromY, double toX, double toY,
        double fromScale, double toScale, int milliseconds)
    {
        return AnimateIntroElementAsync(
            introGeneration, element,
            0, 1, fromX, toX, fromY, toY, fromScale, toScale, milliseconds);
    }

    private Task AnimateIntroElementAsync(
        int introGeneration,
        UIElement element,
        double fromOpacity, double toOpacity,
        double fromX, double toX,
        double fromY, double toY,
        double fromScale, double toScale,
        int milliseconds)
    {
        var transform = GetElementTransform(element);
        transform.TranslateX = fromX;
        transform.TranslateY = fromY;
        transform.ScaleX = fromScale;
        transform.ScaleY = fromScale;
        element.Opacity = fromOpacity;

        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        void AddAnim(double from, double to, string property, DependencyObject target)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            storyboard.Children.Add(anim);
        }

        AddAnim(fromOpacity, toOpacity, "Opacity", element);
        AddAnim(fromX, toX, "TranslateX", transform);
        AddAnim(fromY, toY, "TranslateY", transform);
        AddAnim(fromScale, toScale, "ScaleX", transform);
        AddAnim(fromScale, toScale, "ScaleY", transform);

        var tcs = new TaskCompletionSource<bool>();
        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            if (introGeneration == _introGeneration)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetCanceled();
            }
        }
        storyboard.Completed += OnCompleted;

        _introStoryboard = storyboard;
        storyboard.Begin();
        return tcs.Task;
    }

    private void DismissIntro()
    {
        IntroOverlay.Visibility = Visibility.Collapsed;
        IntroOverlay.Opacity = 0;
        StepContainer.Opacity = 1;
        FooterNav.Opacity = 1;
        BrandLogoHost.Opacity = 1;
        StepContainer.IsHitTestVisible = true;
        FooterNav.IsHitTestVisible = true;
        SetTransformValues(IntroMarkHost);
        SetTransformValues(StepContainer);
        SetTransformValues(FooterNav);
        SetTransformValues(BrandLogoHost);

        // Ensure all step panels have their children visible
        for (int i = 0; i < StepCount; i++)
        {
            ResetPanelChildren(GetStepPanel(i));
        }

        StartStepAmbientAnimation(_stepIndex);
    }

    /// <summary>
    /// Resets all children of a panel to fully visible state.
    /// </summary>
    private void ResetPanelChildren(FrameworkElement panel)
    {
        if (panel is Panel panelChildren)
        {
            foreach (var child in panelChildren.Children)
            {
                if (child is FrameworkElement fe)
                {
                    fe.Opacity = 1;
                    fe.Translation = System.Numerics.Vector3.Zero;
                }
            }
        }
    }

    private IntroMarkTarget GetIntroMarkTargetTransform()
    {
        try
        {
            double introWidth = IntroMarkHost.ActualWidth > 0 ? IntroMarkHost.ActualWidth : IntroMarkHost.Width;
            double introHeight = IntroMarkHost.ActualHeight > 0 ? IntroMarkHost.ActualHeight : IntroMarkHost.Height;
            double brandWidth = BrandLogoHost.ActualWidth > 0 ? BrandLogoHost.ActualWidth : BrandLogoHost.Width;
            double brandHeight = BrandLogoHost.ActualHeight > 0 ? BrandLogoHost.ActualHeight : BrandLogoHost.Height;
            var introCenter = IntroMarkHost
                .TransformToVisual(RootGrid)
                .TransformPoint(new Windows.Foundation.Point(introWidth / 2, introHeight / 2));
            var brandCenter = BrandLogoHost
                .TransformToVisual(RootGrid)
                .TransformPoint(new Windows.Foundation.Point(brandWidth / 2, brandHeight / 2));

            return new IntroMarkTarget(
                brandCenter.X - introCenter.X,
                brandCenter.Y - introCenter.Y,
                Math.Clamp(brandWidth / Math.Max(1, introWidth), 0.18, 0.28));
        }
        catch
        {
            return new IntroMarkTarget(-304, -172, 0.22);
        }
    }

    private void StartBrandLogoShine()
    {
        _brandLogoShineStoryboard?.Stop();
        BrandLogoShineTransform.TranslateX = -44;

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        var anim = new DoubleAnimation
        {
            From = -44,
            To = 66,
            Duration = new Duration(TimeSpan.FromMilliseconds(1450)),
            BeginTime = TimeSpan.FromMilliseconds(700),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, BrandLogoShineTransform);
        Storyboard.SetTargetProperty(anim, "TranslateX");
        storyboard.Children.Add(anim);

        _brandLogoShineStoryboard = storyboard;
        storyboard.Begin();
    }

    // ════════════════════════════════════════════════════════════
    //  DeskBox Mark (logo layers)
    // ════════════════════════════════════════════════════════════

    private Canvas CreateDeskBoxMark(
        double size = 130,
        double layerWidth = 82,
        double layerHeight = 78,
        double cornerRadius = 14,
        double offsetX = 18,
        double offsetY = 14)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size
        };

        canvas.Children.Add(CreateDeskBoxMarkLayer(0, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        canvas.Children.Add(CreateDeskBoxMarkLayer(1, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        canvas.Children.Add(CreateDeskBoxMarkLayer(2, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        return canvas;
    }

    private Border CreateDeskBoxMarkLayer(
        int index,
        double layerWidth,
        double layerHeight,
        double cornerRadius,
        double offsetX,
        double offsetY)
    {
        Color[] colors =
        [
            ColorHelper.FromArgb(0xFF, 0x0B, 0x64, 0xBF),
            ColorHelper.FromArgb(0xFF, 0x16, 0x91, 0xE8),
            ColorHelper.FromArgb(0xFF, 0x58, 0xAA, 0xFE)
        ];

        var layer = new Border
        {
            Width = layerWidth,
            Height = layerHeight,
            Background = BrushFromColor(colors[index]),
            BorderBrush = BrushFromColor(ColorHelper.FromArgb(0x42, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };

        layer.RenderTransform = new CompositeTransform
        {
            SkewY = -12
        };

        Canvas.SetLeft(layer, 14 + index * offsetX);
        Canvas.SetTop(layer, 14 + index * offsetY);
        return layer;
    }

    // ════════════════════════════════════════════════════════════
    //  Window Subclass (minimum size enforcement)
    // ════════════════════════════════════════════════════════════

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        const uint WmGetMinMaxInfo = 0x0024;
        const uint WmNcDestroy = 0x0082;

        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = GetCurrentDpiScale();
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, ToPhysicalPixels(MinWindowWidth, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, ToPhysicalPixels(MinWindowHeight, scale));
            System.Runtime.InteropServices.Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private double GetCurrentDpiScale()
    {
        return Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        return Math.Max(1, (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));
    }

    // ════════════════════════════════════════════════════════════
    //  Title Bar
    // ════════════════════════════════════════════════════════════

    private void ApplyTitleBarButtonColors()
    {
        bool isDark = IsDarkTheme();
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
    }

    // ════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════

    private bool IsDarkTheme()
    {
        return RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
    }

    private static SolidColorBrush BrushFromColor(Color color)
    {
        return new SolidColorBrush(color);
    }

    private SolidColorBrush AccentBrush()
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return BrushFromColor(accentColor);
    }

    private SolidColorBrush SubtleDotBrush()
    {
        return IsDarkTheme()
            ? BrushFromColor(ColorHelper.FromArgb(0xFF, 0x68, 0x72, 0x80))
            : BrushFromColor(ColorHelper.FromArgb(0xFF, 0xC6, 0xD0, 0xDE));
    }

    private static void SetElementOpacity(UIElement element, double opacity)
    {
        element.Opacity = opacity;
    }

    private static void SetElementTransform(
        UIElement element,
        double translateX = 0,
        double translateY = 0,
        double scale = 1)
    {
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        SetTransformValues(element, translateX, translateY, scale);
    }

    private static void SetTransformValues(
        UIElement element,
        double translateX = 0,
        double translateY = 0,
        double scale = 1)
    {
        var transform = GetElementTransform(element);
        transform.TranslateX = translateX;
        transform.TranslateY = translateY;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private static CompositeTransform GetElementTransform(UIElement element)
    {
        if (element.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        transform = new CompositeTransform();
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        element.RenderTransform = transform;
        return transform;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
