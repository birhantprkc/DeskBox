using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow : Window
{
    private enum ItemSurfaceState
    {
        Normal,
        Hover,
        Pressed,
        DropTarget
    }

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private const int WidgetShowAnimationMs = 280;
    private const int WidgetHideAnimationMs = 220;
    private const double WidgetShowOffsetY = 14.0;
    private const double WidgetShowStartOpacity = 0.72;
    private const double WidgetShowStartScale = 0.985;

    private readonly Microsoft.UI.WindowId _windowId;
    private readonly SettingsService _settingsService;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _hasMovedTitleBarDrag;
    private bool _isResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private Windows.Graphics.PointInt32 _initialWindowPos;
    private Windows.Graphics.SizeInt32 _initialWindowSize;
    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private DispatcherQueueTimer? _statusToastTimer;
    private bool _emptyStateUpdateQueued;
    private bool _deletePending;
    private string[] _cutClipboardPaths = [];
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private Border? _folderDropTarget;
    private bool _surfaceDragCompletionHandled;
    private int _lastSelectionAnchorIndex = -1;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private Flyout? _itemRenameFlyout;
    private MenuFlyout? _itemDeleteConfirmFlyout;
    private Flyout? _messageFlyout;
    private TextBox? _itemRenameTextBox;
    private WidgetItem? _itemRenameTarget;
    private MenuFlyout? _deleteWidgetFlyout;
    private FrameworkElement? _lastMoreFlyoutTarget;
    private Windows.Foundation.Point? _lastMoreFlyoutPosition;
    private bool _isDeleteWidgetFlyoutOpen;
    private bool _isInlineFlyoutOpen;
    private bool _isCommittingItemRename;
    private DateTime _lastTitleBarClickTimeUtc;
    private Win32Helper.POINT _lastTitleBarClickPoint;
    private bool _hasPendingTitleBarClick;
    private bool _isAtDesktopLayer;
    private bool _isHideAnimationRunning;
    private long _trayAnimationGeneration;
    private Storyboard? _trayTransitionStoryboard;

    public WidgetViewModel ViewModel { get; }

    private bool _isVisibleOnDesktop;
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public new void Activate()
    {
        base.Activate();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
    }

    public WidgetWindow(WidgetViewModel viewModel, SettingsService settingsService)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        TitleEditBox.PlaceholderText = "\u8f93\u5165\u7ec4\u4ef6\u540d\u79f0";
        ToolTipService.SetToolTip(AddButton, "\u65b0\u5efa");
        ToolTipService.SetToolTip(MoreButton, "\u66f4\u591a\u9009\u9879");
        ToolTipService.SetToolTip(CloseButton, "\u5220\u9664\u7ec4\u4ef6");

        _hWnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);

        ConfigureWindow();
        SetupEventHandlers();

        ViewModel.Items.CollectionChanged += (_, _) => QueueEmptyStateUpdate();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEmptyState();
        ApplyTitleBarLayout();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        _appWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;

        var config = ViewModel.Config;
        ApplyWindowBounds(
            (int)config.X,
            (int)config.Y,
            (int)config.Width,
            (int)config.Height,
            persist: false);

        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));

        ApplyWindowCornerPreference();

        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            var parent = VisualTreeHelper.GetParent(RootGrid) as FrameworkElement;
            while (parent is not null)
            {
                if (parent is Control control)
                {
                    control.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is Panel panel)
                {
                    panel.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is Border border)
                {
                    border.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is ContentPresenter presenter)
                {
                    presenter.Background = new SolidColorBrush(Colors.Transparent);
                }

                parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
            }

            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
        };

        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;

        Activated += WidgetWindow_Activated;

        _appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
            }
        };

        foreach (var child in ResizeGrid.Children)
        {
            if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                element.PointerMoved += ResizeBorder_PointerMoved;
                element.PointerReleased += ResizeBorder_PointerReleased;
            }
        }

        Closed += (_, _) =>
        {
            Visible = false;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            DisposeAcrylicController();
        };
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        Win32Helper.SetWindowToBottom(_hWnd);
    }

    public void RaiseTemporarilyFromTray()
    {
        _appWindow.Show();
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Win32Helper.BringWindowTemporarilyToFront(_hWnd);
        _isAtDesktopLayer = false;
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        PlayTrayRaiseAnimation();

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);
            if (Visible)
            {
                Win32Helper.BringWindowTemporarilyToFront(_hWnd);
            }
        });
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimation();
    }

    private void PlayTrayRaiseAnimation()
    {
        var animationGeneration = ++_trayAnimationGeneration;
        _trayTransitionStoryboard?.Stop();
        _trayTransitionStoryboard = null;
        _isHideAnimationRunning = false;
        var transform = EnsureTrayTransitionTransform();

        RootGrid.Opacity = WidgetShowStartOpacity;
        transform.TranslateY = WidgetShowOffsetY;
        transform.ScaleX = WidgetShowStartScale;
        transform.ScaleY = WidgetShowStartScale;
        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetShowAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnimation, RootGrid);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        storyboard.Children.Add(opacityAnimation);

        var translateYAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetShowAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(translateYAnimation, transform);
        Storyboard.SetTargetProperty(translateYAnimation, "TranslateY");
        storyboard.Children.Add(translateYAnimation);

        var scaleXAnimation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetShowAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleXAnimation, transform);
        Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");
        storyboard.Children.Add(scaleXAnimation);

        var scaleYAnimation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetShowAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleYAnimation, transform);
        Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");
        storyboard.Children.Add(scaleYAnimation);

        storyboard.Completed += (_, _) =>
        {
            if (animationGeneration == _trayAnimationGeneration)
            {
                RootGrid.Opacity = 1;
                transform.TranslateY = 0;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
                _trayTransitionStoryboard = null;
            }
        };
        _trayTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void PlayTrayHideAnimation(Action completed)
    {
        var animationGeneration = ++_trayAnimationGeneration;
        _trayTransitionStoryboard?.Stop();
        _trayTransitionStoryboard = null;
        var transform = EnsureTrayTransitionTransform();

        _isHideAnimationRunning = true;
        RootGrid.Opacity = 1;
        transform.TranslateY = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;

        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetHideAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opacityAnimation, RootGrid);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
        storyboard.Children.Add(opacityAnimation);

        var translateYAnimation = new DoubleAnimation
        {
            To = WidgetShowOffsetY,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetHideAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(translateYAnimation, transform);
        Storyboard.SetTargetProperty(translateYAnimation, "TranslateY");
        storyboard.Children.Add(translateYAnimation);

        var scaleXAnimation = new DoubleAnimation
        {
            To = WidgetShowStartScale,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetHideAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleXAnimation, transform);
        Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");
        storyboard.Children.Add(scaleXAnimation);

        var scaleYAnimation = new DoubleAnimation
        {
            To = WidgetShowStartScale,
            Duration = new Duration(TimeSpan.FromMilliseconds(WidgetHideAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleYAnimation, transform);
        Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");
        storyboard.Children.Add(scaleYAnimation);

        storyboard.Completed += (_, _) =>
        {
            if (animationGeneration == _trayAnimationGeneration && !Visible)
            {
                _isHideAnimationRunning = false;
                RootGrid.Opacity = 1;
                transform.TranslateY = 0;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
                _trayTransitionStoryboard = null;
                completed();
            }
        };
        _trayTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private CompositeTransform EnsureTrayTransitionTransform()
    {
        if (RootGrid.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        transform = new CompositeTransform();
        RootGrid.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        RootGrid.RenderTransform = transform;
        return transform;
    }

    public void RevealFromTray(bool autoRestore = true)
    {
        ElevateForInteraction();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        PlayTrayRaiseAnimation();

        if (!autoRestore)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1200);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer();
            }
        };
        timer.Start();
    }

    public void HideWindow()
    {
        if (!Visible || _isHideAnimationRunning)
        {
            return;
        }

        Visible = false;
        ViewModel.Config.IsVisible = false;
        _settingsService.SaveDebounced();
        PlayTrayHideAnimation(() =>
        {
            Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
            _appWindow.Hide();
        });
    }

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        ViewModel.ApplyAppearancePreview();
        ApplyBackdropPreference();
    }

    private void ElevateForInteraction()
    {
        _isAtDesktopLayer = false;
        Win32Helper.BringWindowToFront(_hWnd);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void WidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(ApplyBackdropPreference);

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing)
        {
            return;
        }

        _isAtDesktopLayer = false;
        PlayTrayRaiseAnimation();
    }

    private void RestoreDesktopLayer()
    {
        if (_isDragging ||
            _isResizing ||
            TitleEditBox.Visibility == Visibility.Visible ||
            _deletePending ||
            _isDeleteWidgetFlyoutOpen ||
            _isInlineFlyoutOpen)
        {
            return;
        }

        PushToBottom();
        ApplyBackdropPreference();
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist)
    {
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        ViewModel.UpdateBounds(x, y, width, height, persist: false);

        if (persist)
        {
            ViewModel.UpdateBounds(x, y, width, height, persist: true);
        }
    }

    private void OnSettingsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
        });
    }

    private void ApplyWindowCornerPreference()
    {
        int cornerPreference = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    private void ApplyBackdropPreference()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            int backdropType;
            if (ApplyAcrylicController(isDark, tintColor, surfaceOpacity))
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                SystemBackdrop ??= new DesktopAcrylicBackdrop();
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{_hWnd.ToInt64():X} useNativeBlur=true isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} systemBackdrop={(SystemBackdrop?.GetType().Name ?? "null")} " +
                $"acrylicController={_acrylicController is not null}");

            // Keep the window composition layer neutral and let the widget surface provide the tint.
            Win32Helper.DisableAccentPolicy(_hWnd);
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            SystemBackdrop = null;
            DisposeAcrylicController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private bool ApplyAcrylicController(bool isDark, Windows.UI.Color tintColor, double surfaceOpacity)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _backdropConfiguration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            SystemBackdrop = null;
            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        _acrylicController.TintOpacity = (float)(isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44));
        _acrylicController.LuminosityOpacity = (float)(isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86));
        return true;
    }

    private void DisposeAcrylicController()
    {
        if (_acrylicController is null)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _acrylicController = null;
        }
    }

    private Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var baseColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BuildAccentSurfaceColor(
            isDark,
            accentColor,
            baseColor,
            accentMix: isDark ? 0.08 : 0.16,
            overlayMix: isDark ? 0.04 : 0.08);
    }

    private void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;

        var backgroundColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);

        byte chromeAlpha = 0x18;

        var borderColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.75), 0, 255), 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), chromeAlpha);

        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);

        var iconForeground = Windows.UI.Color.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        var editorBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x1A, 0x1C, 0x21)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.14 : 0.08,
            overlayMix: isDark ? 0.12 : 0.08);

        var editorBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);

        var secondaryText = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.Background = new SolidColorBrush(backgroundColor);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        TitleIcon.Foreground = new SolidColorBrush(iconForeground);
        TitleEditBox.Background = new SolidColorBrush(editorBackground);
        TitleEditBox.BorderBrush = new SolidColorBrush(editorBorder);
        TitleEditBox.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
        EmptyStateTitleText.Foreground = new SolidColorBrush(isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        EmptyStateDescriptionText.Foreground = new SolidColorBrush(secondaryText);
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryText);
        SelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        StatusToastText.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);

        UpdateInteractiveSurfaces();
    }

    private static Windows.UI.Color BuildFrostedSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity)
    {
        double materialOpacity = isDark
            ? Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82)
            : Math.Clamp(surfaceOpacity * 0.78, 0.0, 0.78);

        return ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.18,
                overlayMix: isDark ? 0.15 : 0.04),
            materialOpacity);
    }

    private void UpdateInteractiveSurfaces()
    {
        foreach (var border in FindInteractiveSurfaceBorders(RootGrid))
        {
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void ApplyTitleBarLayout()
    {
        double listIcon = ViewModel.ListIconSize;
        double labelFont = ViewModel.ListLabelFontSize;

        double titleIconSize = Math.Clamp(Math.Round(listIcon * 0.54), 11, 18);
        TitleIcon.FontSize = titleIconSize;

        double titleTextSize = Math.Clamp(Math.Round(labelFont * 1.27), 12, 18);
        TitleText.FontSize = titleTextSize;
        TitleEditBox.FontSize = Math.Max(titleTextSize - 1, 11);

        double btnSize = Math.Clamp(titleIconSize + 14, 24, 34);
        AddButton.Width = btnSize;
        AddButton.Height = btnSize;
        MoreButton.Width = btnSize;
        MoreButton.Height = btnSize;
        CloseButton.Width = btnSize;
        CloseButton.Height = btnSize;

        double btnIconSize = Math.Clamp(titleIconSize - 3, 10, 15);
        AddButtonIcon.FontSize = btnIconSize;
        MoreButtonIcon.FontSize = btnIconSize;
        CloseButtonIcon.FontSize = btnIconSize;

        double rowHeight = Math.Clamp(titleIconSize + 32, 40, 54);
        RootGrid.RowDefinitions[0].Height = new GridLength(rowHeight);

        double padH = Math.Clamp(Math.Round(titleIconSize * 0.9), 10, 16);
        double padT = Math.Clamp(Math.Round(titleIconSize * 0.5), 4, 10);
        double padB = Math.Clamp(Math.Round(titleIconSize * 0.35), 3, 8);
        TitleBarGrid.Padding = new Thickness(padH, padT, padH - 2, padB);
    }

    private IEnumerable<Border> FindInteractiveSurfaceBorders(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "InteractiveSurface")
            {
                yield return border;
            }

            foreach (var nested in FindInteractiveSurfaceBorders(child))
            {
                yield return nested;
            }
        }
    }

    private void ApplyWidgetItemSurfaceState(Border border, ItemSurfaceState state)
    {
        if (ReferenceEquals(border, _folderDropTarget) && state != ItemSurfaceState.DropTarget)
        {
            state = ItemSurfaceState.DropTarget;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var item = border.DataContext as WidgetItem;
        bool isSelected = item?.IsSelected == true;
        bool isCut = item?.IsCut == true;

        var defaultBackground = ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        var selectedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x31, 0x36, 0x3E)
                    : ColorHelper.FromArgb(0xFF, 0xF1, 0xF6, 0xFC),
                accentMix: isDark ? 0.30 : 0.18,
                overlayMix: isDark ? 0.08 : 0.04),
            isDark ? (byte)0x62 : (byte)0x72);

        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2A, 0x2D, 0x33)
                    : ColorHelper.FromArgb(0xFF, 0xFB, 0xFB, 0xFC),
                accentMix: isDark ? 0.20 : 0.12,
                overlayMix: isDark ? 0.12 : 0.18),
            isDark ? (byte)0x3A : (byte)0x44);

        var pressedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2D, 0x30, 0x37)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xFA),
                accentMix: isDark ? 0.24 : 0.15,
                overlayMix: isDark ? 0.10 : 0.16),
            isDark ? (byte)0x48 : (byte)0x54);

        var selectedHoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            isDark ? (byte)0x78 : (byte)0x88);

        var dropTargetBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x35, 0x3D, 0x48)
                    : ColorHelper.FromArgb(0xFF, 0xE7, 0xF3, 0xFF),
                accentMix: isDark ? 0.42 : 0.30,
                overlayMix: isDark ? 0.06 : 0.04),
            isDark ? (byte)0x92 : (byte)0x9C);

        var backgroundColor = state switch
        {
            ItemSurfaceState.DropTarget => dropTargetBackground,
            ItemSurfaceState.Hover when isSelected => selectedHoverBackground,
            ItemSurfaceState.Pressed when isSelected => selectedHoverBackground,
            ItemSurfaceState.Hover => hoverBackground,
            ItemSurfaceState.Pressed => pressedBackground,
            _ when isSelected => selectedBackground,
            _ => defaultBackground
        };

        border.Background = new SolidColorBrush(backgroundColor);
        border.BorderBrush = new SolidColorBrush(state == ItemSurfaceState.DropTarget
            ? WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xD8)
            : Colors.Transparent);
        border.BorderThickness = state == ItemSurfaceState.DropTarget
            ? new Thickness(1)
            : new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void ApplyWidgetItemLayout(Border border)
    {
        if (IsWithinItemsListView(border))
        {
            border.Width = double.NaN;
            border.Height = double.NaN;
            border.Margin = ViewModel.ListItemMargin;
            border.Padding = ViewModel.ListItemPadding;
            border.CornerRadius = GetItemSurfaceCornerRadius();

            if (border.Child is Grid listGrid)
            {
                listGrid.ColumnSpacing = 10;

                if (TryGetDescendant<Image>(listGrid, out var icon))
                {
                    icon.Width = ViewModel.ListIconSize;
                    icon.Height = ViewModel.ListIconSize;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var label))
                {
                    label.FontSize = ViewModel.ListLabelFontSize;
                }

                foreach (var textBlock in FindDescendants<TextBlock>(listGrid).Skip(1))
                {
                    textBlock.FontSize = Math.Max(ViewModel.ListLabelFontSize - 2, 9);
                    textBlock.Visibility = ViewModel.ShowListItemDetails
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            return;
        }

        if (border.Parent is FrameworkElement slot && slot.Tag as string == "ItemSlot")
        {
            slot.Width = ViewModel.IconTileWidth;
            slot.Height = ViewModel.IconTileHeight;
            slot.Margin = ViewModel.IconTileMargin;
        }

        border.Width = double.NaN;
        border.Height = double.NaN;
        border.MaxWidth = Math.Max(ViewModel.IconImageSize + 18, ViewModel.IconLabelMaxWidth + 12);
        border.Margin = new Thickness(0);
        border.Padding = ViewModel.IconTilePadding;
        border.CornerRadius = GetItemSurfaceCornerRadius();

        if (border.Child is StackPanel iconStack)
        {
            iconStack.Spacing = ViewModel.IconContentSpacing;

            if (TryGetDescendant<Image>(iconStack, out var icon))
            {
                icon.Width = ViewModel.IconImageSize;
                icon.Height = ViewModel.IconImageSize;
            }

            if (TryGetDescendant<TextBlock>(iconStack, out var label))
            {
                label.FontSize = ViewModel.IconLabelFontSize;
                label.MaxWidth = ViewModel.IconLabelMaxWidth;
            }
        }
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                result = typedChild;
                return true;
            }

            if (TryGetDescendant(child, out result))
            {
                return true;
            }
        }

        result = null!;
        return false;
    }

    private CornerRadius GetItemSurfaceCornerRadius()
    {
        double radius = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 6,
            _ => 5
        };

        return new CornerRadius(radius);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private bool IsWithinItemsListView(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ItemsListView))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel_PropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName is nameof(WidgetViewModel.IsLoading))
        {
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.ShowListItemDetails)
            or nameof(WidgetViewModel.IconTileWidth)
            or nameof(WidgetViewModel.IconTileHeight)
            or nameof(WidgetViewModel.IconTileMargin)
            or nameof(WidgetViewModel.IconTilePadding)
            or nameof(WidgetViewModel.IconContentSpacing)
            or nameof(WidgetViewModel.IconImageSize)
            or nameof(WidgetViewModel.IconLabelFontSize)
            or nameof(WidgetViewModel.IconLabelMaxWidth)
            or nameof(WidgetViewModel.ListItemMargin)
            or nameof(WidgetViewModel.ListItemPadding)
            or nameof(WidgetViewModel.ListIconSize)
            or nameof(WidgetViewModel.ListLabelFontSize))
        {
            ApplyTitleBarLayout();
            UpdateInteractiveSurfaces();
        }
    }

    private ListViewBase? GetActiveItemsView()
    {
        if (ViewModel.IsIconMode)
        {
            return ItemsGridView;
        }

        if (ViewModel.IsListMode)
        {
            return ItemsListView;
        }

        return null;
    }

    private List<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView()?.SelectedItems.OfType<WidgetItem>().ToList() ?? [];
    }

    private WidgetItem? GetPrimarySelectedItem()
    {
        return GetSelectedItems().FirstOrDefault();
    }

    private void ApplySelectionState(ListViewBase? listView)
    {
        var selectedItems = listView?.SelectedItems.OfType<WidgetItem>().ToHashSet() ?? [];
        foreach (var item in ViewModel.Items)
        {
            item.IsSelected = selectedItems.Contains(item);
        }

        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        ClearFolderDropTarget();

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
        if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath) &&
            string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            !IsPointerOverFolderDropTarget())
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = "\u5df2\u5728\u5f53\u524d\u7ec4\u4ef6\u4e2d";
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
        e.AcceptedOperation = GetAcceptedDropOperation(e.DataView.RequestedOperation, movesIntoFolder);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            return;
        }

        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = movesIntoFolder
            ? $"{(e.AcceptedOperation == DataPackageOperation.Copy ? "\u590d\u5236" : "\u79fb\u52a8")}\u5230\u6536\u7eb3\u7ec4\u4ef6"
            : "\u6dfb\u52a0\u5230\u5f15\u7528\u7ec4\u4ef6";
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        ClearFolderDropTarget();
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        ClearFolderDropTarget();

        if (!HasPathDropData(e.DataView))
        {
            return;
        }

        var paths = await GetDropPathsAsync(e.DataView);
        if (paths.Length == 0)
        {
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
        var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
            ? GetAcceptedDropOperation(e.DataView.RequestedOperation, movesIntoFolder)
            : e.AcceptedOperation;
        if (acceptedOperation == DataPackageOperation.None)
        {
            return;
        }

        bool? moveWhenMapped = movesIntoFolder
            ? acceptedOperation != DataPackageOperation.Copy
            : null;

        string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
        if (movesIntoFolder &&
            string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal) &&
            moveWhenMapped == true)
        {
            return;
        }

        await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

        if (moveWhenMapped == true)
        {
            await SyncMoveSourceAsync(
                TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId"),
                TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths"));
        }

        ClearCutState();
    }

    private void ItemsView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        if (!_settingsService.Settings.DoubleClickToOpen)
        {
            ViewModel.OpenItemCommand.Execute(item);
        }
    }

    private void ItemsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            ViewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null && !item.IsSelected)
        {
            listView.SelectedItems.Clear();
            listView.SelectedItems.Add(item);
            ApplySelectionState(listView);
        }

        var selectedItems = GetSelectedItems();
        bool isMultiSelection = selectedItems.Count > 1;
        var flyout = new MenuFlyout();

        if (isMultiSelection)
        {
            AddMultiSelectionItems(flyout, selectedItems.Count);
            ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        var openItem = new MenuFlyoutItem
        {
            Text = "\u6253\u5f00",
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        openItem.Click += (_, _) => ViewModel.OpenItemCommand.Execute(item);
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = "\u590d\u5236",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = "\u526a\u5207",
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var showItem = new MenuFlyoutItem
        {
            Text = "\u5728\u8d44\u6e90\u7ba1\u7406\u5668\u4e2d\u663e\u793a",
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        showItem.Click += (_, _) => ViewModel.ShowInExplorerCommand.Execute(item);
        flyout.Items.Add(showItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = "\u91cd\u547d\u540d",
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) => await StartItemRenameAsync(item);
        flyout.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "\u5220\u9664",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        var propItem = new MenuFlyoutItem
        {
            Text = "\u5c5e\u6027",
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propItem.Click += (_, _) => ShellContextMenuHelper.ShowProperties(_hWnd, item.Path);
        flyout.Items.Add(propItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = "\u79fb\u56de\u684c\u9762",
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                try
                {
                    int movedCount = await ViewModel.MoveItemsBackToDesktopAsync([item], useShellProgress: true);
                    ShowStatusToast(movedCount > 0 ? "已移回桌面" : "没有项目被移动");
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync("\u79fb\u56de\u684c\u9762\u5931\u8d25", ex.Message);
                }
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
    }

    private void AddMultiSelectionItems(MenuFlyout flyout, int selectedCount)
    {
        var titleItem = new MenuFlyoutItem
        {
            Text = $"已选择 {selectedCount} 项",
            Icon = new FontIcon { Glyph = "\uE762" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = "复制",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = "剪切",
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除",
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = "移回桌面",
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) => await MoveSelectedItemsBackToDesktopAsync();
            flyout.Items.Add(moveBackToDesktopItem);
        }
    }

    private void ItemsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            ApplySelectionState(listView);
            _lastSelectionAnchorIndex = GetPrimarySelectedItem() is { } item
                ? ViewModel.Items.IndexOf(item)
                : -1;
        }
    }

    private void ItemsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedItems()
            : [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
        }

        listView.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ItemsView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDragDistance(_selectionStartPoint, _selectionCurrentPoint) < 6.0)
        {
            return;
        }

        _isBoxSelecting = true;
        UpdateSelectionRectangleVisual();
        ApplySelectionRectangle(listView);
        e.Handled = true;
    }

    private void ItemsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            bool handled = _selectionPointerPressed || _isBoxSelecting;
            FinishSelectionRectangle(listView);
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = handled;
        }
    }

    private void ItemsView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishSelectionRectangle(listView);
        }
    }

    private void ItemsView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Cancel = !TryPrepareItemDragPackage(
            e.Data,
            GetDragItems(e.Items.OfType<WidgetItem>().FirstOrDefault()));
    }

    private async void ItemsView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await HandleItemDragCompletedAsync(args.DropResult);
    }

    private async Task HandleItemDragCompletedAsync(DataPackageOperation dropResult)
    {
        var draggedPaths = _activeDragSourcePaths;
        bool hasStorageItems = _activeDragHasStorageItems;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;

        if (draggedPaths.Length == 0)
        {
            return;
        }

        if (ViewModel.FollowsDefaultStoragePath &&
            IsCursorOnDesktop() &&
            !IsCursorOverThisWindow())
        {
            if (hasStorageItems || dropResult == DataPackageOperation.Move)
            {
                await Task.Delay(250);
                await ViewModel.RefreshFromConfigAsync();
                ClearRemovedCutPaths();
                UpdateEmptyState();
                return;
            }

            await MoveDraggedPathsBackToDesktopAsync(draggedPaths, useShellProgress: true);
            return;
        }

        if (dropResult != DataPackageOperation.Move)
        {
            return;
        }

        ClearRemovedCutPaths();
    }

    private IReadOnlyList<WidgetItem> GetDragItems(WidgetItem? fallbackItem)
    {
        var selectedItems = GetSelectedItems();
        if (fallbackItem is null)
        {
            return selectedItems;
        }

        if (selectedItems.Any(item => ReferenceEquals(item, fallbackItem)))
        {
            return selectedItems;
        }

        return [fallbackItem];
    }

    private bool TryPrepareItemDragPackage(DataPackage dataPackage, IReadOnlyList<WidgetItem> draggedItems)
    {
        if (draggedItems.Count == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        var sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        var storageItems = App.Current.FileService.GetStorageItems(sourcePaths);

        dataPackage.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));
        if (storageItems.Count > 0)
        {
            dataPackage.SetStorageItems(storageItems, false);
        }

        dataPackage.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        dataPackage.Properties["DeskBoxSourcePaths"] = sourcePaths;
        dataPackage.Properties.Title = sourcePaths.Length == 1
            ? Path.GetFileName(sourcePaths[0])
            : $"{sourcePaths.Length} 个项目";
        _activeDragSourcePaths = sourcePaths;
        _activeDragHasStorageItems = storageItems.Count > 0;
        return true;
    }

    private async void ItemsView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async Task HandleItemsKeyDownAsync(KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool shiftPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            listView.SelectAll();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectionToClipboardAsync(cut: false);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.X)
        {
            await CopySelectionToClipboardAsync(cut: true);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.V)
        {
            await PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.C)
        {
            CopySelectedPathsToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.N)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await CreateFolderInMappedLocationAsync();
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.O)
        {
            if (GetPrimarySelectedItem() is { } openItem)
            {
                ViewModel.OpenItemCommand.Execute(openItem);
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.R)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            ShowStatusToast("已刷新");
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2)
        {
            if (GetPrimarySelectedItem() is { } renameItem)
            {
                await StartItemRenameAsync(renameItem);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            if (GetSelectedItems().Count > 0)
            {
                await DeleteSelectedItemsAsync();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ClearCutState();
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            ViewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void CopySelectedPathsToClipboard()
    {
        var selectedPaths = GetSelectedItems()
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectedPaths.Length == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, selectedPaths));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowStatusToast($"已复制路径 {selectedPaths.Length} 项");
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        var sourcePaths = selectedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            ShowStatusToast("没有可复制的项目");
            return;
        }

        var package = new DataPackage();
        package.RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy;
        bool shellClipboardSet = ShellClipboardHelper.TrySetFileDropList(sourcePaths, cut);
        if (!shellClipboardSet)
        {
            var storageItems = await App.Current.FileService.GetStorageItemsAsync(sourcePaths);
            package.SetText(string.Join(Environment.NewLine, sourcePaths));
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }

            package.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
            package.Properties["DeskBoxSourcePaths"] = sourcePaths;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = cut
            ? sourcePaths
            : [];

        ApplyCutState();
        UpdateInteractiveSurfaces();
        ShowStatusToast(cut
            ? $"已剪切 {sourcePaths.Length} 项"
            : $"已复制 {sourcePaths.Length} 项");
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = Clipboard.GetContent();
        IReadOnlyList<string> sourcePaths = TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourcePaths.Count == 0 && clipboard.Contains(StandardDataFormats.StorageItems))
        {
            var items = await clipboard.GetStorageItemsAsync();
            sourcePaths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }

        if (sourcePaths.Count == 0)
        {
            return;
        }

        int itemCount = sourcePaths.Count;

        bool? moveWhenMapped = clipboard.RequestedOperation != DataPackageOperation.Copy;

        await ViewModel.ImportPathsAsync(sourcePaths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

        if (moveWhenMapped == true)
        {
            await SyncMoveSourceAsync(
                TryGetPackageString(clipboard.Properties, "DeskBoxSourceWidgetId"),
                TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths"));
        }

        ClearCutState();
        ShowStatusToast(moveWhenMapped == true
            ? $"已移动 {itemCount} 项"
            : $"已粘贴 {itemCount} 项");
    }

    private void ClearCutState()
    {
        _cutClipboardPaths = [];
        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ApplyCutState()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCut = _cutClipboardPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ClearRemovedCutPaths()
    {
        if (_cutClipboardPaths.Length == 0)
        {
            return;
        }

        var currentPaths = ViewModel.Items
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cutClipboardPaths = _cutClipboardPaths
            .Where(currentPaths.Contains)
            .ToArray();
        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ShowStatusToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusToastText.Text = message;
        StatusToast.Visibility = Visibility.Visible;
        StatusToast.Opacity = 1;

        _statusToastTimer ??= DispatcherQueue.CreateTimer();
        _statusToastTimer.Stop();
        _statusToastTimer.Interval = TimeSpan.FromSeconds(2.2);
        _statusToastTimer.Tick -= StatusToastTimer_Tick;
        _statusToastTimer.Tick += StatusToastTimer_Tick;
        _statusToastTimer.Start();
    }

    private void StatusToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        StatusToast.Opacity = 0;
        StatusToast.Visibility = Visibility.Collapsed;
    }

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !IsWithinInteractiveSurface(source) &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, selectionRect.X);
        Canvas.SetTop(SelectionRectangle, selectionRect.Y);
        SelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        SelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        SelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplySelectionRectangle(ListViewBase listView)
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<WidgetItem>(_selectionSnapshot);

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindItemSurface(item) ?? container;
            var topLeft = target.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var itemRect = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);

            if (RectsIntersect(selectionRect, itemRect))
            {
                selectedItems.Add(item);
            }
        }

        listView.SelectedItems.Clear();
        foreach (var item in selectedItems)
        {
            listView.SelectedItems.Add(item);
        }

        ApplySelectionState(listView);
    }

    private void FinishSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;
        _selectionSnapshot = [];

        if (_isBoxSelecting)
        {
            ApplySelectionRectangle(listView);
        }

        _isBoxSelecting = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            ApplySelectionState(listView);
        }
    }

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double x = Math.Min(startPoint.X, endPoint.X);
        double y = Math.Min(startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - startPoint.X);
        double height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private static double GetSelectionDragDistance(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool RectsIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    private async Task SyncMoveSourceAsync(string? sourceWidgetId, IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId) || sourcePaths.Count == 0)
        {
            return;
        }

        if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            return;
        }

        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.NotifyItemsMovedOutAsync(sourceWidgetId, sourcePaths);
        }
    }

    private static string? TryGetPackageString(DataPackagePropertySetView properties, string key)
    {
        return properties.TryGetValue(key, out object? value) ? value as string : null;
    }

    private static IReadOnlyList<string> TryGetPackageStringArray(DataPackagePropertySetView properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value) || value is null)
        {
            return [];
        }

        return value switch
        {
            string[] array => array,
            IReadOnlyList<string> readOnlyList => readOnlyList,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            _ => []
        };
    }

    private static bool HasPathDropData(DataPackageView dataView)
    {
        return TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths").Count > 0 ||
               dataView.Contains(StandardDataFormats.StorageItems) ||
               dataView.Contains(StandardDataFormats.Text);
    }

    private static async Task<string[]> GetDropPathsAsync(DataPackageView dataView)
    {
        var sourcePaths = TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length > 0)
        {
            return sourcePaths;
        }

        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await dataView.GetStorageItemsAsync();
            sourcePaths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourcePaths.Length > 0)
            {
                return sourcePaths;
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return [];
        }

        string text = await dataView.GetTextAsync();
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsWithin(source, TitleBarGrid))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateContentAreaFlyout(), RootGrid, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        var position = e.GetPosition(TitleBarGrid);
        TrackMoreFlyoutAnchor(TitleBarGrid, position);
        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, position);
        e.Handled = true;
    }

    private void WidgetItemSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Hover);
        }
    }

    private void WidgetItemSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            return;
        }

        var point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
            {
                if (!item.IsSelected)
                {
                    listView.SelectedItems.Add(item);
                }
            }
            else if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
            }

            ApplySelectionState(listView);
        }

        RootGrid.Focus(FocusState.Programmatic);
        _surfaceDragCompletionHandled = false;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
    }

    private void WidgetItemSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
    }

    private void WidgetItemSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            bool isInside = e.GetCurrentPoint(border).Position is var point &&
                point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= border.ActualWidth &&
                point.Y <= border.ActualHeight;

            ApplyWidgetItemSurfaceState(border, isInside ? ItemSurfaceState.Hover : ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            args.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        if (!TryPrepareItemDragPackage(args.Data, GetDragItems(item)))
        {
            args.Cancel = true;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private async void WidgetItemSurface_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_surfaceDragCompletionHandled)
        {
            return;
        }

        _surfaceDragCompletionHandled = true;
        await HandleItemDragCompletedAsync(args.DropResult);
    }

    private void WidgetItemSurface_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out var border, out var targetFolder))
        {
            return;
        }

        e.Handled = true;

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        var sourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");
        if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = "不能移动到此文件夹";
            ClearFolderDropTarget();
            return;
        }

        e.AcceptedOperation = GetAcceptedDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        SetFolderDropTarget(border);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = $"{(e.AcceptedOperation == DataPackageOperation.Copy ? "复制" : "移动")}到 “{targetFolder.Name}”";
    }

    private void WidgetItemSurface_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _folderDropTarget))
        {
            ClearFolderDropTarget();
        }
    }

    private async void WidgetItemSurface_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out _, out var targetFolder))
        {
            return;
        }

        e.Handled = true;
        ClearFolderDropTarget();

        if (!HasPathDropData(e.DataView))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var sourcePaths = await GetDropPathsAsync(e.DataView);
            if (sourcePaths.Length == 0)
            {
                return;
            }

            if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
            {
                ShowStatusToast("不能移动到此文件夹");
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? GetAcceptedDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true)
                : e.AcceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                return;
            }

            bool move = acceptedOperation != DataPackageOperation.Copy;
            var results = await App.Current.FileService.TransferItemsWithResultAsync(sourcePaths, targetFolder.Path, move);
            if (results.Count == 0)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            if (move)
            {
                await SyncMoveSourceAsync(
                    TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId"),
                    TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths"));
                ClearRemovedCutPaths();
            }

            ShowStatusToast($"{(move ? "已移动" : "已复制")}到 {targetFolder.Name} {results.Count} 项");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("移动到文件夹失败", ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryGetFolderDropTarget(object sender, out Border border, out WidgetItem folder)
    {
        if (sender is Border targetBorder &&
            targetBorder.DataContext is WidgetItem item &&
            item.IsFolder &&
            Directory.Exists(item.Path))
        {
            border = targetBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private void SetFolderDropTarget(Border border)
    {
        if (ReferenceEquals(_folderDropTarget, border))
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
            return;
        }

        ClearFolderDropTarget();
        _folderDropTarget = border;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
    }

    private void ClearFolderDropTarget()
    {
        if (_folderDropTarget is null)
        {
            return;
        }

        var border = _folderDropTarget;
        _folderDropTarget = null;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
    }

    private bool IsPointerOverFolderDropTarget()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        foreach (var border in FindInteractiveSurfaceBorders(RootGrid))
        {
            if (border.DataContext is not WidgetItem { IsFolder: true } folder ||
                !Directory.Exists(folder.Path) ||
                border.ActualWidth <= 0 ||
                border.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = border.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var left = _appWindow.Position.X + (topLeft.X * scale);
            var top = _appWindow.Position.Y + (topLeft.Y * scale);
            var right = left + (border.ActualWidth * scale);
            var bottom = top + (border.ActualHeight * scale);

            if (cursor.X >= left &&
                cursor.X <= right &&
                cursor.Y >= top &&
                cursor.Y <= bottom)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvalidFolderDrop(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || sourcePaths.Count == 0)
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource = Path.GetFullPath(sourcePath);
            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Directory.Exists(normalizedSource) &&
                FileService.IsPathUnderDirectory(normalizedDestination, normalizedSource))
            {
                return true;
            }
        }

        return false;
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDragging || _isResizing || TitleEditBox.Visibility == Visibility.Visible)
            {
                return;
            }

            StartRename();
        });
    }

    private void StartRename()
    {
        ElevateForInteraction();
        PrepareRenameEditor();
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.SelectAll();
        TitleEditBox.Focus(FocusState.Programmatic);
    }

    private void PrepareRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 36
            : (ViewModel.Name.Length * 9.5) + 36;

        TitleEditBox.Width = Math.Clamp(titleWidth, 120, 220);
        TitleEditBox.Text = ViewModel.Name;
    }

    private void CommitRename()
    {
        if (TitleEditBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = TitleEditBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            ViewModel.Rename(newName);
        }

        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        RestoreDesktopLayer();
    }

    private void CancelRename()
    {
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        RestoreDesktopLayer();
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitRename();
    }

    private void TitleEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFlyoutWithElevation(CreateNewWidgetFlyout(), AddButton);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        if (!ShouldStartTitleDrag(e.OriginalSource))
        {
            return;
        }

        var properties = e.GetCurrentPoint(TitleBarGrid).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        if (CanStartRenameFromTitleArea(e.OriginalSource) && IsTitleBarDoubleClick(cursorPt))
        {
            _hasPendingTitleBarClick = false;
            StartRename();
            e.Handled = true;
            return;
        }

        TrackTitleBarClick(e.OriginalSource, cursorPt);
        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        TitleBarGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (_hasPendingTitleBarClick && dragDistanceSquared > 25)
        {
            _hasPendingTitleBarClick = false;
        }

        if (!_hasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            _hasMovedTitleBarDrag = true;
        }

        int newX = _initialWindowPos.X + deltaX;
        int newY = _initialWindowPos.Y + deltaY;
        ApplyWindowBounds(newX, newY, _initialWindowSize.Width, _initialWindowSize.Height, persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        TitleBarGrid.ReleasePointerCapture(e.Pointer);
        if (_hasMovedTitleBarDrag)
        {
            var finalPosition = _appWindow.Position;
            var finalSize = _appWindow.Size;
            ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
            RestoreDesktopLayer();
        }

        _hasMovedTitleBarDrag = false;
        e.Handled = true;
    }

    private async void MapFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        ElevateForInteraction();

        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await app.WidgetManager.CreateFolderWidgetAsync(folder.Path);
            }
        }
        finally
        {
            RestoreDesktopLayer();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        TrackMoreFlyoutAnchor(CloseButton, null);
        ShowDeleteWidgetFlyout(CloseButton);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        TrackMoreFlyoutAnchor(MoreButton, null);
        ShowFlyoutWithElevation(CreateMoreFlyout(), MoreButton);
    }

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();

        AddCurrentWidgetContentActions(flyout);
        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCreateWidgetItems(flyout);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var pasteItem = new MenuFlyoutItem
        {
            Text = "\u7c98\u8d34",
            Icon = new FontIcon { Glyph = "\uE77F" },
            IsEnabled = CanPasteFromClipboard()
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                await PasteFromClipboardAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("\u7c98\u8d34\u5931\u8d25", ex.Message);
            }
        };
        flyout.Items.Add(pasteItem);

        var refreshItem = new MenuFlyoutItem
        {
            Text = "\u5237\u65b0",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        refreshItem.Click += async (_, _) =>
        {
            try
            {
                await ViewModel.RefreshFromConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("\u5237\u65b0\u5931\u8d25", ex.Message);
            }
        };
        flyout.Items.Add(refreshItem);

        if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var newFolderItem = new MenuFlyoutItem
            {
                Text = "\u65b0\u5efa\u6587\u4ef6\u5939",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            newFolderItem.Click += async (_, _) => await CreateFolderInMappedLocationAsync();
            flyout.Items.Add(newFolderItem);
        }

        return flyout;
    }

    private void AddCurrentWidgetContentActions(MenuFlyout flyout)
    {
        if (ViewModel.FollowsDefaultStoragePath)
        {
            var addFileItem = new MenuFlyoutItem
            {
                Text = "添加文件",
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            addFileItem.Click += async (_, _) => await PickAndImportFilesAsync();
            flyout.Items.Add(addFileItem);
            return;
        }

        var changeMappedPathItem = new MenuFlyoutItem
        {
            Text = "更改映射路径",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        changeMappedPathItem.Click += async (_, _) => await PickAndApplyMappedFolderAsync();
        flyout.Items.Add(changeMappedPathItem);
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        var modeInfo = new MenuFlyoutItem
        {
            Text = $"\u7ec4\u4ef6\u7c7b\u578b\uff1a{ViewModel.ModeLabel}\u7ec4\u4ef6",
            Icon = new FontIcon
            {
                Glyph = ViewModel.IconGlyph
            },
            IsEnabled = false
        };
        flyout.Items.Add(modeInfo);
        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCreateWidgetItems(flyout);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var iconView = new ToggleMenuFlyoutItem
        {
            Text = "\u56fe\u6807\u89c6\u56fe",
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += SetIconView_Click;
        flyout.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = "\u5217\u8868\u89c6\u56fe",
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += SetListView_Click;
        flyout.Items.Add(listView);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var positionLock = new ToggleMenuFlyoutItem
        {
            Text = "\u9501\u5b9a\u4f4d\u7f6e",
            IsChecked = ViewModel.IsPositionLocked
        };
        positionLock.Click += TogglePositionLock_Click;
        flyout.Items.Add(positionLock);

        var sizeLock = new ToggleMenuFlyoutItem
        {
            Text = "\u9501\u5b9a\u5c3a\u5bf8",
            IsChecked = ViewModel.IsSizeLocked
        };
        sizeLock.Click += ToggleSizeLock_Click;
        flyout.Items.Add(sizeLock);

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            var openFolder = new MenuFlyoutItem
            {
                Text = "\u6253\u5f00\u5f53\u524d\u6587\u4ef6\u5939",
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolder.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    Win32Helper.OpenFile(ViewModel.MappedFolderPath);
                }
            };
            flyout.Items.Add(openFolder);
        }

        var rename = new MenuFlyoutItem
        {
            Text = "\u91cd\u547d\u540d",
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += Rename_Click;
        flyout.Items.Add(rename);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteWidget = new MenuFlyoutItem
        {
            Text = "\u5220\u9664\u7ec4\u4ef6",
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += DeleteWidget_Click;
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private void NewWidget_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app)
        {
            _ = app.WidgetManager?.CreateManagedWidgetAsync("\u65b0\u5efa\u7ec4\u4ef6");
        }
    }

    private void AddCreateWidgetItems(MenuFlyout flyout)
    {
        var newWidget = new MenuFlyoutItem
        {
            Text = "\u65b0\u5efa\u7ec4\u4ef6",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        newWidget.Click += NewWidget_Click;
        flyout.Items.Add(newWidget);

        var mapFolder = new MenuFlyoutItem
        {
            Text = "\u65b0\u5efa\u6587\u4ef6\u5939\u6620\u5c04",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        mapFolder.Click += MapFolderButton_Click;
        flyout.Items.Add(mapFolder);
    }

    private MenuFlyout CreateNewWidgetFlyout()
    {
        var flyout = new MenuFlyout();
        AddCreateWidgetItems(flyout);
        return flyout;
    }

    private async Task PickAndImportFilesAsync()
    {
        ElevateForInteraction();

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var files = await picker.PickMultipleFilesAsync();
            var paths = files
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            bool shouldMove = GetManagedDropOperation() != DataPackageOperation.Copy;
            await ViewModel.ImportPathsAsync(paths, shouldMove, useShellProgress: shouldMove);
            ShowStatusToast(paths.Length == 1 ? "已添加文件" : $"已添加 {paths.Length} 个文件");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("添加文件失败", ex.Message);
        }
        finally
        {
            RestoreDesktopLayer();
        }
    }

    private async Task PickAndApplyMappedFolderAsync()
    {
        if (ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        ElevateForInteraction();

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            await ViewModel.UpdateMappedFolderPathAsync(folder.Path);
            ShowStatusToast("映射路径已更新");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("更改映射路径失败", ex.Message);
        }
        finally
        {
            RestoreDesktopLayer();
        }
    }

    private void SetIconView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.Icon)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void SetListView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.List)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        StartRename();
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return;
        }

        try
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(ViewModel.MappedFolderPath, "\u65b0\u5efa\u6587\u4ef6\u5939"));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFromConfigAsync();
            if (ViewModel.Items.FirstOrDefault(existingItem =>
                    string.Equals(existingItem.Path, folderPath, StringComparison.OrdinalIgnoreCase)) is { } newFolderItem)
            {
                SelectSingleItem(newFolderItem);
                await StartItemRenameAsync(newFolderItem);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u65b0\u5efa\u6587\u4ef6\u5939\u5931\u8d25", ex.Message);
        }
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 && GetPrimarySelectedItem() is { } primaryItem)
        {
            selectedItems = [primaryItem];
        }

        if (selectedItems.Count == 0)
        {
            return;
        }

        if (RequiresDeleteConfirmation(selectedItems))
        {
            ShowDeleteItemsConfirmFlyout(selectedItems);
            return;
        }

        await DeleteItemsWithoutConfirmAsync(selectedItems);
    }

    private static bool RequiresDeleteConfirmation(IReadOnlyList<WidgetItem> selectedItems)
    {
        return selectedItems.Any(item => item.IsFolder);
    }

    private void ShowDeleteItemsConfirmFlyout(IReadOnlyList<WidgetItem> selectedItems)
    {
        var items = selectedItems.ToArray();
        int folderCount = items.Count(item => item.IsFolder);
        if (folderCount == 0)
        {
            _ = DeleteItemsWithoutConfirmAsync(items);
            return;
        }

        _itemDeleteConfirmFlyout?.Hide();

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        var titleItem = new MenuFlyoutItem
        {
            Text = items.Length == 1 ? $"将“{items[0].Name}”移到回收站？" : $"将 {items.Length} 项移到回收站？",
            Icon = new FontIcon { Glyph = "\uE946" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);

        string message = items.Length == 1
            ? "这是文件夹，里面的内容也会一起进入回收站"
            : $"其中包含 {folderCount} 个文件夹，文件夹内容也会一起进入回收站";
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = message,
            Icon = new FontIcon { Glyph = "\uE783" },
            IsEnabled = false
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        var confirmItem = new MenuFlyoutItem
        {
            Text = "移到回收站",
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        confirmItem.Click += async (_, _) => await DeleteItemsWithoutConfirmAsync(items);
        flyout.Items.Add(confirmItem);

        var cancelItem = new MenuFlyoutItem
        {
            Text = "取消",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        cancelItem.Click += (_, _) => flyout.Hide();
        flyout.Items.Add(cancelItem);

        _itemDeleteConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_itemDeleteConfirmFlyout, flyout))
            {
                return;
            }

            _itemDeleteConfirmFlyout = null;
            _isInlineFlyoutOpen = false;
            RestoreDesktopLayer();
        };

        _isInlineFlyoutOpen = true;
        ElevateForInteraction();

        var target = items.Length == 1
            ? FindItemSurface(items[0]) ?? GetActiveItemsView() as FrameworkElement ?? RootGrid
            : GetActiveItemsView() as FrameworkElement ?? RootGrid;
        flyout.ShowAt(target);
    }

    private async Task DeleteItemsWithoutConfirmAsync(IReadOnlyList<WidgetItem> selectedItems)
    {
        try
        {
            int deletedCount = selectedItems.Count;
            await ViewModel.DeleteItemsAsync(selectedItems);
            ClearRemovedCutPaths();
            ShowStatusToast($"已移到回收站 {deletedCount} 项");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u5220\u9664\u5931\u8d25", ex.Message);
        }
    }

    private async Task MoveSelectedItemsBackToDesktopAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(selectedItems, useShellProgress: true);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0 ? $"已移回桌面 {movedCount} 项" : "没有项目被移动");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("移回桌面失败", ex.Message);
        }
    }

    private async Task MoveDraggedPathsBackToDesktopAsync(IReadOnlyList<string> sourcePaths, bool useShellProgress)
    {
        var pathSet = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        var draggedItems = ViewModel.Items
            .Where(item =>
                pathSet.Contains(Path.GetFullPath(item.Path)) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToList();
        if (draggedItems.Count == 0)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(draggedItems, useShellProgress);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0 ? $"已移到桌面 {movedCount} 项" : "没有项目被移动");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("移到桌面失败", ex.Message);
            await ViewModel.RefreshFromConfigAsync();
            UpdateEmptyState();
        }
    }

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        var target = FindItemSurface(item);
        if (target is null)
        {
            return;
        }

        SelectSingleItem(item);
        EnsureItemRenameFlyout();

        _itemRenameTarget = item;
        _itemRenameTextBox!.Text = item.Name;
        _itemRenameTextBox.SelectAll();
        ElevateForInteraction();
        _itemRenameFlyout!.ShowAt(target);
        _itemRenameTextBox.Focus(FocusState.Programmatic);
        await Task.CompletedTask;
    }

    private void EnsureItemRenameFlyout()
    {
        if (_itemRenameFlyout is not null && _itemRenameTextBox is not null)
        {
            return;
        }

        _itemRenameTextBox = new TextBox
        {
            Width = 220,
            MinWidth = 160,
            MaxWidth = 260,
            MinHeight = 30,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        _itemRenameTextBox.KeyDown += ItemRenameTextBox_KeyDown;
        _itemRenameTextBox.LostFocus += ItemRenameTextBox_LostFocus;

        _itemRenameFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false,
            Content = _itemRenameTextBox
        };
        _itemRenameFlyout.Closed += (_, _) =>
        {
            _itemRenameTarget = null;
            RestoreDesktopLayer();
        };
    }

    private async void ItemRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _itemRenameFlyout?.Hide();
        }
    }

    private async void ItemRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename || _itemRenameTarget is null || _itemRenameTextBox is null)
        {
            return;
        }

        string newName = _itemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            _itemRenameFlyout?.Hide();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            _itemRenameFlyout?.Hide();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u91cd\u547d\u540d\u5931\u8d25", ex.Message);
            _itemRenameTextBox.Focus(FocusState.Programmatic);
            _itemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (TryGetDescendant<Border>(container, out var border) && border.Tag as string == "InteractiveSurface")
        {
            return border;
        }

        return container;
    }

    private void SelectSingleItem(WidgetItem item)
    {
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        listView.ScrollIntoView(item);
        ApplySelectionState(listView);
    }

    private void TogglePositionLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePositionLockCommand.Execute(null);
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteWidgetFlyout(_lastMoreFlyoutTarget ?? MoreButton, _lastMoreFlyoutPosition);
    }

    private void TrackMoreFlyoutAnchor(FrameworkElement target, Windows.Foundation.Point? position)
    {
        _lastMoreFlyoutTarget = target;
        _lastMoreFlyoutPosition = position;
    }

    private void ShowDeleteWidgetFlyout(FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        if (_deletePending || App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        _deleteWidgetFlyout?.Hide();
        var flyout = CreateDeleteWidgetFlyout(app.WidgetManager);
        _deleteWidgetFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_deleteWidgetFlyout, flyout))
            {
                return;
            }

            _isDeleteWidgetFlyoutOpen = false;
            _deleteWidgetFlyout = null;
            RestoreDesktopLayer();
        };

        _isDeleteWidgetFlyoutOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            ElevateForInteraction();
            if (position is Windows.Foundation.Point point)
            {
                flyout.ShowAt(target, point);
            }
            else
            {
                flyout.ShowAt(target);
            }
        });
    }

    private MenuFlyout CreateDeleteWidgetFlyout(WidgetManager widgetManager)
    {
        var flyout = new MenuFlyout();
        flyout.ShouldConstrainToRootBounds = false;

        var titleItem = new MenuFlyoutItem
        {
            Text = $"删除“{ViewModel.Name}”",
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        bool canCleanupManagedFolder = widgetManager.CanCleanupManagedStorageForWidget(ViewModel.Config.Id);
        if (!canCleanupManagedFolder)
        {
            var noteItem = new MenuFlyoutItem
            {
                Text = "只移除组件，不删除原文件",
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            };
            flyout.Items.Add(noteItem);

            var confirmItem = CreateDeleteActionItem("确认删除组件", WidgetRemovalAction.RemoveWidgetOnly);
            flyout.Items.Add(confirmItem);
            flyout.Items.Add(CreateCancelDeleteItem());
            return flyout;
        }

        var managedInfoItem = new MenuFlyoutItem
        {
            Text = "选择收纳文件夹处理方式",
            Icon = new FontIcon { Glyph = "\uE8B7" },
            IsEnabled = false
        };
        flyout.Items.Add(managedInfoItem);

        flyout.Items.Add(CreateDeleteActionItem("保留收纳文件夹", WidgetRemovalAction.RemoveWidgetOnly, "\uE8B7", false));
        flyout.Items.Add(CreateDeleteActionItem("移回桌面后删除空文件夹", WidgetRemovalAction.MoveManagedFolderContentsToDesktop, "\uE8CA", false));
        flyout.Items.Add(CreateDeleteActionItem("删除文件夹，文件进回收站", WidgetRemovalAction.DeleteManagedFolder, "\uE74D", true));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateCancelDeleteItem());
        return flyout;
    }

    private MenuFlyoutItem CreateDeleteActionItem(
        string text,
        WidgetRemovalAction removalAction,
        string glyph = "\uE74D",
        bool isDanger = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        if (isDanger)
        {
            icon.Foreground = new SolidColorBrush(Colors.Red);
        }

        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon
        };
        item.Click += (_, _) => QueueDeleteWidget(removalAction);
        return item;
    }

    private MenuFlyoutItem CreateCancelDeleteItem()
    {
        var item = new MenuFlyoutItem
        {
            Text = "取消",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        item.Click += (_, _) => _deleteWidgetFlyout?.Hide();
        return item;
    }

    private async Task ConfirmAndDeleteWidgetAsync(WidgetRemovalAction removalAction)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            _deletePending = false;
            return;
        }

        try
        {
            App.Log($"[WidgetDelete] Begin delete widget '{ViewModel.Name}' ({ViewModel.Config.Id})");
            await app.WidgetManager.RemoveWidgetAsync(ViewModel.Config.Id, removalAction);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetDelete] Delete failed for '{ViewModel.Name}' ({ViewModel.Config.Id}): {ex}");
            _deletePending = false;
            RootGrid.IsHitTestVisible = true;
            await ShowErrorDialogAsync("删除组件失败", ex.Message);
            RestoreDesktopLayer();
        }
    }

    private void QueueDeleteWidget(WidgetRemovalAction removalAction)
    {
        if (_deletePending)
        {
            return;
        }

        _deletePending = true;
        RootGrid.IsHitTestVisible = false;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(16);
            await ConfirmAndDeleteWidgetAsync(removalAction);
        });
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsSizeLocked)
        {
            return;
        }

        if (sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeDirection = element.Tag as string ?? string.Empty;
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;

        int newWidth = _initialWindowSize.Width;
        int newHeight = _initialWindowSize.Height;
        int newX = _initialWindowPos.X;
        int newY = _initialWindowPos.Y;

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        ApplyWindowBounds(newX, newY, newWidth, newHeight, persist: false);
        e.Handled = true;
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || sender is not FrameworkElement element)
        {
            return;
        }

        _isResizing = false;
        _resizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        RestoreDesktopLayer();
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = ViewModel.IsSizeLocked
            ? InputSystemCursorShape.Arrow
            : element is FrameworkElement frameworkElement
                ? frameworkElement.Tag switch
                {
                    "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
                    "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
                    "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
                    "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                }
                : InputSystemCursorShape.Arrow;

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.Items.Count == 0 && !ViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void QueueEmptyStateUpdate()
    {
        if (_emptyStateUpdateQueued)
        {
            return;
        }

        _emptyStateUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _emptyStateUpdateQueued = false;
            UpdateEmptyState();
        });
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        string displayMessage = FormatUserFacingError(message);
        var completion = new TaskCompletionSource();

        _messageFlyout?.Hide();

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };

        var messageText = new TextBlock
        {
            Text = displayMessage,
            MaxWidth = 280,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };

        var closeButton = new Button
        {
            Content = "\u786e\u5b9a",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var panel = new StackPanel
        {
            Width = 300,
            Spacing = 12,
            Padding = new Thickness(4)
        };
        panel.Children.Add(titleText);
        panel.Children.Add(messageText);
        panel.Children.Add(closeButton);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false
        };

        _messageFlyout = flyout;
        closeButton.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_messageFlyout, flyout))
            {
                _messageFlyout = null;
            }

            _isInlineFlyoutOpen = false;
            completion.TrySetResult();
            RestoreDesktopLayer();
        };

        _isInlineFlyoutOpen = true;
        ElevateForInteraction();
        flyout.ShowAt(MoreButton);
        await completion.Task;
    }

    private static string FormatUserFacingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "操作没有完成。";
        }

        if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已取消", StringComparison.OrdinalIgnoreCase))
        {
            return "操作已取消。";
        }

        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("没有权限", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return $"没有权限完成这个操作。\n\n{message}";
        }

        if (message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("另一个进程", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase))
        {
            string? path = TryExtractQuotedPath(message);
            return string.IsNullOrWhiteSpace(path)
                ? "文件正在被其他程序使用，请关闭相关程序后再试。"
                : $"文件正在被其他程序使用，请关闭相关程序后再试。\n\n{path}";
        }

        if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已存在", StringComparison.OrdinalIgnoreCase))
        {
            return $"目标位置已经有同名项目。\n\n{message}";
        }

        return message;
    }

    private static string? TryExtractQuotedPath(string message)
    {
        int start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        int end = message.IndexOf('\'', start + 1);
        if (end <= start + 1)
        {
            return null;
        }

        return message[(start + 1)..end];
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        ElevateForInteraction();
        flyout.Closed += (_, _) => RestoreDesktopLayer();

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private bool ShouldStartTitleDrag(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !IsWithin(source, TitleEditBox);
    }

    private bool CanStartRenameFromTitleArea(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return IsWithin(source, TitleText) || IsWithin(source, TitleIcon);
    }

    private bool IsTitleBarDoubleClick(Win32Helper.POINT currentPoint)
    {
        if (!_hasPendingTitleBarClick)
        {
            return false;
        }

        if ((DateTime.UtcNow - _lastTitleBarClickTimeUtc).TotalMilliseconds > 420)
        {
            return false;
        }

        int deltaX = currentPoint.X - _lastTitleBarClickPoint.X;
        int deltaY = currentPoint.Y - _lastTitleBarClickPoint.Y;
        return ((deltaX * deltaX) + (deltaY * deltaY)) <= 36;
    }

    private void TrackTitleBarClick(object? originalSource, Win32Helper.POINT currentPoint)
    {
        if (!CanStartRenameFromTitleArea(originalSource))
        {
            _hasPendingTitleBarClick = false;
            return;
        }

        _lastTitleBarClickTimeUtc = DateTime.UtcNow;
        _lastTitleBarClickPoint = currentPoint;
        _hasPendingTitleBarClick = true;
    }

    private static bool CanPasteFromClipboard()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !IsWithin(source, TitleEditBox) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsWithinInteractiveSurface(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Border border && border.Tag as string == "InteractiveSurface")
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsCursorOverThisWindow()
    {
        if (!Win32Helper.GetCursorPos(out var cursor) ||
            !Win32Helper.GetWindowRect(_hWnd, out var rect))
        {
            return false;
        }

        return cursor.X >= rect.Left &&
               cursor.X <= rect.Right &&
               cursor.Y >= rect.Top &&
               cursor.Y <= rect.Bottom;
    }

    private bool IsCursorOnDesktop()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        try
        {
            var display = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cursor.X, cursor.Y),
                DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;
            return cursor.X >= workArea.X &&
                   cursor.X <= workArea.X + workArea.Width &&
                   cursor.Y >= workArea.Y &&
                   cursor.Y <= workArea.Y + workArea.Height;
        }
        catch
        {
            return true;
        }
    }

    private void EnsureStoryboards()
    {
        if (_showButtonsStoryboard is not null)
        {
            return;
        }

        _showButtonsStoryboard = new Storyboard();

        var showRightOpacity = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(showRightOpacity, "Opacity");
        _showButtonsStoryboard.Children.Add(showRightOpacity);

        var showRightX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(showRightX, "X");
        _showButtonsStoryboard.Children.Add(showRightX);

        _hideButtonsStoryboard = new Storyboard();

        var hideRightOpacity = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(hideRightOpacity, "Opacity");
        _hideButtonsStoryboard.Children.Add(hideRightOpacity);

        var hideRightX = new DoubleAnimation
        {
            To = 12,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(hideRightX, "X");
        _hideButtonsStoryboard.Children.Add(hideRightX);
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        EnsureStoryboards();
        _hideButtonsStoryboard?.Stop();
        _showButtonsStoryboard?.Begin();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        EnsureStoryboards();
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Begin();
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private DataPackageOperation GetManagedDropOperation()
    {
        return string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
    }

    private DataPackageOperation GetAcceptedDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (!movesIntoFolder)
        {
            if (SupportsOperation(requestedOperation, DataPackageOperation.Link))
            {
                return DataPackageOperation.Link;
            }

            return SupportsOperation(requestedOperation, DataPackageOperation.Copy) || requestedOperation == DataPackageOperation.None
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (ctrlPressed && CanUseRequestedOperation(requestedOperation, DataPackageOperation.Copy))
        {
            return DataPackageOperation.Copy;
        }

        var defaultOperation = GetManagedDropOperation();
        if (CanUseRequestedOperation(requestedOperation, defaultOperation))
        {
            return defaultOperation;
        }

        if (CanUseRequestedOperation(requestedOperation, DataPackageOperation.Move))
        {
            return DataPackageOperation.Move;
        }

        return CanUseRequestedOperation(requestedOperation, DataPackageOperation.Copy)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private static bool CanUseRequestedOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return requestedOperation == DataPackageOperation.None ||
               SupportsOperation(requestedOperation, operation);
    }

    private static bool SupportsOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return (requestedOperation & operation) == operation;
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
    }

}
