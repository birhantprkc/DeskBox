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
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow : WidgetWindowBase, IDesktopWidgetWindow
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private const int ItemTransitionRestoreDelayMs = 240;

    // ── Backward-compatible aliases for base class fields ──────
    // These allow the existing WidgetWindow code to reference inherited
    // protected fields by their original private names without a
    // massive find-and-replace across 5000+ lines.
    private SettingsService _settingsService => SettingsService;
    private LocalizationService _localizationService => _localizationSvc;
    private IntPtr _hWnd => HWnd;
    private AppWindow _appWindow => AppWindow;
    private WidgetWindowDiagnostics _diagnostics => Diagnostics;
    private WidgetTrayAnimationController _trayAnimation => TrayAnimation;

    // Mutable field aliases for inherited protected state
    private DesktopAcrylicController? _acrylicController { get => AcrylicController; set => AcrylicController = value; }
    private MicaController? _micaController { get => MicaController; set => MicaController = value; }
    private SystemBackdropConfiguration? _backdropConfiguration { get => BackdropConfiguration; set => BackdropConfiguration = value; }
    private ICompositionSupportsSystemBackdrop? _backdropTarget { get => BackdropTarget; set => BackdropTarget = value; }
    private bool _isDragging { get => IsDragging; set => IsDragging = value; }
    private bool _hasMovedTitleBarDrag { get => HasMovedTitleBarDrag; set => HasMovedTitleBarDrag = value; }
    private bool _isResizing { get => IsResizing; set => IsResizing = value; }
    private bool _isApplyingBounds { get => IsApplyingBounds; set => IsApplyingBounds = value; }
    private string _resizeDirection { get => ResizeDirection; set => ResizeDirection = value; }
    private Win32Helper.POINT _initialCursorPt { get => InitialCursorPt; set => InitialCursorPt = value; }
    private Windows.Graphics.PointInt32 _initialWindowPos { get => InitialWindowPos; set => InitialWindowPos = value; }
    private Windows.Graphics.SizeInt32 _initialWindowSize { get => InitialWindowSize; set => InitialWindowSize = value; }
    private FrameworkElement? _dragCaptureElement { get => DragCaptureElement; set => DragCaptureElement = value; }

    private readonly Microsoft.UI.WindowId _windowId;
    private readonly LocalizationService _localizationSvc;
    private readonly WidgetContentDescriptor _chromeDescriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private bool _isNativeBackdropSuppressedForTrayReveal;

    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private DispatcherQueueTimer? _statusToastTimer;
    private bool _emptyStateUpdateQueued;
    private bool _deletePending;
    private string[] _cutClipboardPaths = [];
    private readonly HashSet<Border> _interactiveSurfaces = [];
    private Windows.Foundation.Point _lastRightTapPoint;
    private DateTime _lastTitleBarClickTimeUtc;
    private Win32Helper.POINT _lastTitleBarClickPoint;
    private bool _hasPendingTitleBarClick;
    private bool _isAtDesktopLayer { get => IsAtDesktopLayer; set => IsAtDesktopLayer = value; }
    private bool _keepRaisedUntilDeactivate { get => KeepRaisedUntilDeactivate; set => KeepRaisedUntilDeactivate = value; }
    private bool _restoreDesktopLayerWhenIdle { get => RestoreDesktopLayerWhenIdle; set => RestoreDesktopLayerWhenIdle = value; }
    private bool _isHideAnimationRunning { get => IsHideAnimationRunning; set => IsHideAnimationRunning = value; }
    private bool _isMigrationBusy;
    private long _backdropRefreshGeneration { get => BackdropRefreshGeneration; set => BackdropRefreshGeneration = value; }
    private bool _areItemTransitionsSuppressed;
    private DispatcherQueueTimer? _autoRestoreTimer;
    private DispatcherQueueTimer? _topMostSafetyTimer { get => TopMostSafetyTimer; set => TopMostSafetyTimer = value; }
    private WidgetDisplayChangeWatcher? _displayChangeWatcher { get => DisplayChangeWatcher; set => DisplayChangeWatcher = value; }
    private TransitionCollection? _savedGridItemTransitions;
    private TransitionCollection? _savedListItemTransitions;

    private Border BackgroundPlate => FileWidgetShell.BackgroundSurface;
    private Border HeaderDivider => FileWidgetShell.Divider;

    public WidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => HWnd;

    public WidgetWindowIdentity Identity => Diagnostics.Identity;

    public override WidgetConfig Config => ViewModel.Config;

    public Windows.Foundation.Rect AnimationBounds => Diagnostics.AnimationBounds;

    // ── WidgetWindowBase abstract overrides ────────────────────
    protected override double WidgetOpacity => ViewModel.WidgetOpacity;
    protected override FrameworkElement RootElement => RootGrid;
    protected override string LogPrefix => "Widget";
    protected override bool IsSizeLocked => ViewModel.IsSizeLocked;
    protected override bool IsPositionLocked => ViewModel.IsPositionLocked;
    protected override bool IsBackdropSuppressedForTrayReveal => _isNativeBackdropSuppressedForTrayReveal;

    protected override void OnElevated()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    protected override bool HasBlockingFlyoutOpen()
    {
        return TitleEditBox.Visibility == Visibility.Visible ||
               _deletePending ||
               _isDeleteWidgetFlyoutOpen ||
               _isInlineFlyoutOpen;
    }

    protected override void ConfigureWindowExtra()
    {
        Win32Helper.AllowShellDragDropMessages(HWnd);
        InstallFileDropSubclass();
    }

    protected override void OnRootElementLoaded()
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
        RootGrid.Focus(FocusState.Programmatic);
    }

    private bool _isVisibleOnDesktop;
    private bool _isClosing { get => IsClosing; set => IsClosing = value; }
    private DateTime _lastElevateForInteractionUtc { get => LastElevateForInteractionUtc; set => LastElevateForInteractionUtc = value; }
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
        QueueBackdropRefresh();
    }

    public WidgetWindow(WidgetViewModel viewModel, SettingsService settingsService, LocalizationService? localizationService = null)
    {
        ViewModel = viewModel;
        SettingsService = settingsService;
        _localizationSvc = localizationService ?? new LocalizationService(settingsService);
        _fileDropSubclassProc = FileDropSubclassProc;
        _chromeDescriptor = new WidgetContentFactory(_localizationSvc).GetDescriptor(WidgetKind.File);
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        ApplyLocalizedText();
        FileWidgetShell.SetDividerMargin(new Thickness(12, 0, 12, 0));

        HWnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(HWnd);
        AppWindow = AppWindow.GetFromWindowId(_windowId);
        Diagnostics = new WidgetWindowDiagnostics("File", ViewModel.Config, () => HWnd);
        TrayAnimation = new WidgetTrayAnimationController(
            AppWindow,
            RootGrid,
            DispatcherQueue,
            HWnd,
            () => Diagnostics.AnimationBounds,
            LogTrayWindow);

        ConfigureWindow();
        SetupEventHandlers();

        ViewModel.Items.CollectionChanged += (_, _) => QueueEmptyStateUpdate();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEmptyState();
        ApplyTitleBarLayout();
    }

    private void ConfigureWindow()
    {
        ConfigureWindowCore();
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;

        Activated += WidgetWindow_Activated;

        _appWindow.Changed += AppWindow_Changed;
        _displayChangeWatcher = new WidgetDisplayChangeWatcher(_hWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);

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
            _isClosing = true;
            Visible = false;
            WidgetLayerService.ReleaseWindow(_hWnd);
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _displayChangeWatcher?.Dispose();
            _displayChangeWatcher = null;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            StopBackdropRefreshTimer();
            RemoveFileDropSubclass();
            _trayAnimation.Stop();
            RestoreItemContainerTransitions();
            DisposeAcrylicController();
            DisposeMicaController();

            foreach (var child in ResizeGrid.Children)
            {
                if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    element.PointerMoved -= ResizeBorder_PointerMoved;
                    element.PointerReleased -= ResizeBorder_PointerReleased;
                }
            }
        };
    }

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    private bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (_isClosing ||
            _isHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (
            _isDragging ||
            _isResizing ||
            _trayAnimation.IsApplyingBounds)
        {
            return false;
        }

        var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(ViewModel.Config);
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (position.X == bounds.X &&
            position.Y == bounds.Y &&
            size.Width == bounds.Width &&
            size.Height == bounds.Height)
        {
            return true;
        }

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: true);
        return true;
    }

    private bool RestoreBoundsAfterDisplayChange()
    {
        // For hidden windows: update config coordinates to the correct screen
        // without actually moving the window. This ensures the widget appears
        // in the right place when it is next shown.
        if (!Visible)
        {
            var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(ViewModel.Config);
            var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
            WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
            _settingsService.SaveDebounced();
            return true;
        }

        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingBounds || _trayAnimation.IsApplyingBounds || (!_isDragging && !_isResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    private void ApplyLocalizedText()
    {
        TitleEditBox.PlaceholderText = _localizationService.T("Widget.TitlePlaceholder");
        ToolTipService.SetToolTip(PositionLockButton, _localizationService.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(SizeLockButton, _localizationService.T("Widget.LockSize"));
        ToolTipService.SetToolTip(FileWidgetShell.PositionLockActionButton, _localizationService.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(FileWidgetShell.SizeLockActionButton, _localizationService.T("Widget.LockSize"));
        ToolTipService.SetToolTip(AddButton, _localizationService.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(MoreButton, _localizationService.T("Widget.Tooltip.More"));
        ToolTipService.SetToolTip(CloseButton, _localizationService.T("Widget.Tooltip.DeleteWidget"));
        MigrationTitleText.Text = _localizationService.T("Widget.Migration.Title");
        MigrationDescriptionText.Text = _localizationService.T("Widget.Migration.Description");
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(_hWnd);
        App.LogVerbose($"[ZOrder] Widget PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    public void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(_hWnd);
        App.LogVerbose($"[ZOrder] Widget ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedAtDesktopLayer");
        _trayAnimation.PrepareHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        _appWindow.Show();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();
        PushToBottom();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        _trayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

    public void RaiseTemporarilyFromTray()
    {
        PrepareTrayShowAnimation();
        ShowPreparedRaisedFromTray();
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedRaisedFromTray");
        _trayAnimation.PrepareHiddenState();
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        HoldTemporaryTopMost();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);
            if (Visible)
            {
                HoldTemporaryTopMost();
            }
        });
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-visible hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (_isAtDesktopLayer)
        {
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED atDesktop hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-raised hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost hwnd=0x{_hWnd.ToInt64():X} atDesktop={_isAtDesktopLayer}");
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(_hWnd);
        HoldTemporaryTopMost();
    }

    public void ActivateRaisedFromTrayBatch()
    {
        if (!Visible)
        {
            return;
        }

        HoldTemporaryTopMost();
        base.Activate();
        RootGrid.Focus(FocusState.Programmatic);
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void PrepareTrayShowAnimation()
    {
        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();
        _isHideAnimationRunning = false;

        var animationProfile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={_trayAnimation.Generation} effect={_settingsService.Settings.WidgetAnimationEffect} " +
            $"speed={_settingsService.Settings.WidgetAnimationSpeed} enabled={animationProfile.IsEnabled} durationMs={animationProfile.DurationMs}");
        _trayAnimation.PrepareVisualState(animationProfile.ShowOffsetX, animationProfile.ShowOffsetY, animationProfile.ShowStartOpacity, animationProfile.ShowStartScale);
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        var animationGeneration = _trayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={animationGeneration}");
        _trayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        RestoreNativeBackdropAfterTrayReveal();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();

        QueueItemContainerTransitionRestore(animationGeneration);
    }

    private void PlayTrayRaiseAnimation()
    {
        var animationGeneration = _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();
        _isHideAnimationRunning = false;

        var animationProfile = GetTrayAnimationProfile();
        if (!animationProfile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={animationGeneration}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={animationGeneration} durationMs={animationProfile.DurationMs}");
        _trayAnimation.PrepareVisualState(animationProfile.ShowOffsetX, animationProfile.ShowOffsetY, animationProfile.ShowStartOpacity, animationProfile.ShowStartScale);
        _trayAnimation.Animate(
            animationProfile.ShowOffsetX,
            animationProfile.ShowOffsetY,
            0,
            0,
            animationProfile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            animationProfile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            animationProfile.DurationMs,
            true,
            animationGeneration,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
        {
            _trayAnimation.RestoreVisualState();
            _trayAnimation.RestoreWindowPosition();
            QueueItemContainerTransitionRestore(animationGeneration);
        });
    }

    private void PlayTrayRaiseAnimationAfterFirstFrame()
    {
        if (Visible)
        {
            PlayTrayRaiseAnimation();
        }
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || _isHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={_isHideAnimationRunning}");
            return false;
        }

        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreNativeBackdropAfterTrayReveal();
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();

        _isHideAnimationRunning = true;
        Visible = false;
        ViewModel.Config.IsVisible = false;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={_trayAnimation.Generation}");
        _trayAnimation.PrepareVisualState(0, 0, WidgetTrayAnimationController.RestingOpacity, WidgetTrayAnimationController.RestingScale);
        return true;
    }

    private void PlayTrayHideAnimation(Action completed)
    {
        var animationGeneration = _trayAnimation.Generation;
        var animationProfile = GetTrayAnimationProfile();
        if (!animationProfile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={animationGeneration}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={animationGeneration} durationMs={animationProfile.DurationMs}");
        _trayAnimation.Animate(
            0,
            0,
            animationProfile.HideOffsetX,
            animationProfile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            animationProfile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            animationProfile.HideEndScale,
            animationProfile.DurationMs,
            false,
            animationGeneration,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
        {
            if (Visible)
            {
                return;
            }
            completed();
        });
    }

    private void SuppressItemContainerTransitions()
    {
        if (_areItemTransitionsSuppressed)
        {
            return;
        }

        _savedGridItemTransitions = ItemsGridView.ItemContainerTransitions;
        _savedListItemTransitions = ItemsListView.ItemContainerTransitions;
        ItemsGridView.ItemContainerTransitions = new TransitionCollection();
        ItemsListView.ItemContainerTransitions = new TransitionCollection();
        _areItemTransitionsSuppressed = true;
    }

    private void RestoreItemContainerTransitions()
    {
        if (!_areItemTransitionsSuppressed)
        {
            return;
        }

        ItemsGridView.ItemContainerTransitions = _savedGridItemTransitions;
        ItemsListView.ItemContainerTransitions = _savedListItemTransitions;
        _savedGridItemTransitions = null;
        _savedListItemTransitions = null;
        _areItemTransitionsSuppressed = false;
    }

    private void QueueItemContainerTransitionRestore(long animationGeneration)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(ItemTransitionRestoreDelayMs);
            if (animationGeneration == _trayAnimation.Generation)
            {
                RestoreItemContainerTransitions();
            }
        });
    }

    public void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            LogTrayWindow("CompleteHide skipped reason=visible-again");
            return;
        }

        _isHideAnimationRunning = false;
        _trayAnimation.Stop();
        RestoreNativeBackdropAfterTrayReveal();
        WidgetLayerService.ClearTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        _trayAnimation.RestoreVisualState();
        QueueItemContainerTransitionRestore(_trayAnimation.Generation);
        _trayAnimation.RestoreWindowPosition();
        LogTrayWindow("CompleteHide");
    }

    private void SuppressNativeBackdropForTrayReveal()
    {
        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = true;
        DisposeAcrylicController();
        Win32Helper.DisableAccentPolicy(_hWnd);
    }

    private void RestoreNativeBackdropAfterTrayReveal()
    {
        if (!_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = false;
        ApplyBackdropPreference();
    }

    private WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return _trayAnimation.CreateProfile(WidgetAnimationSettings.From(_settingsService.Settings));
    }

    private void LogTrayWindow(string message)
    {
        App.LogVerbose(_diagnostics.FormatTrayWindowMessage(message));
    }

    public void RevealFromTray(bool autoRestore = true)
    {
        PrepareTrayShowAnimation();
        ElevateForInteraction();
        _trayAnimation.PrepareHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
        PlayTrayRaiseAnimationAfterFirstFrame();

        if (!autoRestore)
        {
            return;
        }

        _autoRestoreTimer?.Stop();
        _autoRestoreTimer = DispatcherQueue.CreateTimer();
        _autoRestoreTimer.IsRepeating = false;
        _autoRestoreTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _autoRestoreTimer.Tick += (_, _) =>
        {
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer(force: true);
            }
        };
        _autoRestoreTimer.Start();
    }

    public void HideWindow()
    {
        if (!PrepareTrayHideAnimation())
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void CloseWindow()
    {
        WidgetLayerService.ReleaseWindow(_hWnd);
        Close();
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
        QueueBackdropRefresh();
    }

    public void SetMigrationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetMigrationBusy(isBusy));
            return;
        }

        _isMigrationBusy = isBusy;
        MigrationOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        MigrationProgressRing.IsActive = isBusy;
        ResizeGrid.IsHitTestVisible = !isBusy;
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        _lastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            _isAtDesktopLayer = true;
            _keepRaisedUntilDeactivate = false;
            _restoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(_hWnd);
            App.LogVerbose($"[ZOrder] Widget HoldTemporaryTopMost skipped pinned hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(_hWnd);
        App.LogVerbose($"[ZOrder] Widget HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
        StartTopMostSafetyTimer();
    }

    private void StartTopMostSafetyTimer()
    {
        if (_topMostSafetyTimer is null)
        {
            _topMostSafetyTimer = DispatcherQueue.CreateTimer();
            _topMostSafetyTimer.IsRepeating = false;
            _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            _topMostSafetyTimer.Tick += (_, _) =>
            {
                _topMostSafetyTimer?.Stop();
                if (!_isAtDesktopLayer && !_isDragging && !_isResizing &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    App.Log($"[ZOrder] Widget safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                    RestoreDesktopLayer(force: true);
                }
            };
        }
        else
        {
            _topMostSafetyTimer.Stop();
        }
        _topMostSafetyTimer.Start();
    }

    private void WidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ApplyBackdropPreference());

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] Widget Deactivated→QueueRestore hwnd=0x{_hWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }

            return;
        }

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing ||
            WidgetLayerService.UsesDesktopPinnedMode() ||
            (App.Current.WidgetManager is { WidgetsRaisedFromTray: true }))
        {
            App.LogVerbose($"[ZOrder] Widget PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] Widget PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
        StartTopMostSafetyTimer();
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);

            if (!Visible || _isAtDesktopLayer)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            if (App.Current.IsDeskBoxWindow(foregroundWindow))
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("file-window-deactivated"))
                {
                    RestoreDesktopLayer(force: true);
                }
            }
            else
            {
                RestoreDesktopLayer(force: true);
            }
        });
    }

    public void RestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayer(force: true);
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        App.LogVerbose($"[ZOrder] Widget ForceRestore hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer}");
        ForceCancelTransientState();
        RestoreDesktopLayer(force: true);
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            return;
        }

        if (!force && (
            _isDragging ||
            _isResizing ||
            TitleEditBox.Visibility == Visibility.Visible ||
            _deletePending ||
            _isDeleteWidgetFlyoutOpen ||
            _isInlineFlyoutOpen))
        {
            if (force || _restoreDesktopLayerWhenIdle)
            {
                _restoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    private void ForceCancelTransientState()
    {
        _restoreDesktopLayerWhenIdle = true;
        _keepRaisedUntilDeactivate = false;
        _isDeleteWidgetFlyoutOpen = false;
        _isInlineFlyoutOpen = false;
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
        width = Math.Max(minSize.Width, width);
        height = Math.Max(minSize.Height, height);

        _isApplyingBounds = true;
        try
        {
            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        finally
        {
            _isApplyingBounds = false;
        }

        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: false);
        }
    }

    private Windows.Graphics.SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new Windows.Graphics.RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    private void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        ViewModel.Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
    }

    protected override void UpdateConfigBoundsFromPhysical(int x, int y, int width, int height, bool persist)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
        if (persist)
        {
            SettingsService.UpdateWidget(ViewModel.Config, notifySubscribers: false);
        }
    }

    private void OnSettingsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
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
        if (_hWnd == IntPtr.Zero || _isClosing)
        {
            return;
        }

        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            ApplySurfaceStyle();
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);
        string materialType = _settingsService.Settings.WidgetMaterialType;

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            // Apply DWM border color based on border style setting
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (materialType is SettingsService.WidgetMaterialTypeMica)
            {
                controllerApplied = ApplyMicaController(isDark, tintColor, surfaceOpacity);
            }

            if (!controllerApplied && materialType is SettingsService.WidgetMaterialTypeAcrylic)
            {
                controllerApplied = ApplyAcrylicController(isDark, tintColor, surfaceOpacity);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                // Solid: no system backdrop, no accent blur
                DisposeAcrylicController();
                DisposeMicaController();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else
            {
                // Fallback to Win32 accent blur
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DisposeAcrylicController();
                DisposeMicaController();
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{_hWnd.ToInt64():X} material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={_acrylicController is not null} micaController={_micaController is not null}");
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private double GetCornerRadiusFromPreference()
    {
        // Match Windows 11 DWM corner radius values exactly.
        return _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    private void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        // Setting a DWM border color causes a double-border effect with mismatched
        // corner radii (DWM corner vs XAML corner), producing pixel overflow at corners.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(_hWnd, borderNone);
    }

    private bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (_micaController is null)
        {
            // Dispose acrylic FIRST before adding mica to avoid backdrop conflicts
            DisposeAcrylicController();
            _micaController = new MicaController { Kind = MicaKind.Base };

            if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeMicaController();
                return false;
            }
        }
        else
        {
            // Already have a mica controller — just make sure acrylic is gone
            DisposeAcrylicController();
        }

        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _micaController.Kind = MicaKind.Base;
        _micaController.TintColor = tintColor;
        _micaController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _micaController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.5, 0.0, 1.0);
        return true;
    }

    private void DisposeMicaController()
    {
        if (_micaController is null)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
            _micaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _micaController = null;
        }
    }

    // QueueBackdropRefresh, OnBackdropRefreshTick, RefreshBackdropIfCurrent
    // are now inherited from WidgetWindowBase.

    private bool ApplyAcrylicController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
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
            // Dispose mica FIRST before adding acrylic to avoid backdrop conflicts
            DisposeMicaController();
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
        else
        {
            // Already have an acrylic controller — just make sure mica is gone
            DisposeMicaController();
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        double tintOpacity = isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44);
        double luminosityOpacity = isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86);
        _acrylicController.TintOpacity = (float)tintOpacity;
        _acrylicController.LuminosityOpacity = (float)luminosityOpacity;
        return true;
    }

    private bool ApplyTransparentAcrylicController(bool isDark)
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

        if (_acrylicController is null || _acrylicController.IsClosed)
        {
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
        // Near-zero tint and luminosity for a glass-like see-through effect.
        _acrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.TintOpacity = 0.0f;
        _acrylicController.LuminosityOpacity = 0.0f;

        DisposeMicaController();
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

    protected override Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
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

    private void ApplyWidgetItemTooltip(Border border)
    {
        if (!ViewModel.ShowFileItemPathTooltips || border.DataContext is not WidgetItem item)
        {
            ToolTipService.SetToolTip(border, null);
            return;
        }

        var tooltipText = new TextBlock
        {
            Text = item.FullPath
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipTextStyle", out object? textStyleResource)
            && textStyleResource is Style textStyle)
        {
            tooltipText.Style = textStyle;
        }

        var tooltipContent = new Border
        {
            Child = tooltipText
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipCardStyle", out object? cardStyleResource)
            && cardStyleResource is Style cardStyle)
        {
            tooltipContent.Style = cardStyle;
        }

        var tooltip = new ToolTip
        {
            Content = tooltipContent
        };

        if (Application.Current.Resources.TryGetValue("RoundedToolTipStyle", out object? tooltipStyleResource)
            && tooltipStyleResource is Style tooltipStyle)
        {
            tooltip.Style = tooltipStyle;
        }

        ToolTipService.SetToolTip(border, tooltip);
    }

    private void ApplyTitleBarLayout()
    {
        double listIcon = ViewModel.ListIconSize;
        double labelFont = ViewModel.ListLabelFontSize;
        var chromeMode = _chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? labelFont
            : labelFont * 1.27;

        var metrics = WidgetTitleBarMetricsCalculator.Create(
            listIcon * 0.54,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        FileWidgetShell.ChromeMode = chromeMode;
        FileWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        FileWidgetShell.TitleGlyph = ViewModel.IconGlyph;
        FileWidgetShell.TitleIconKind = ViewModel.TitleIconKind;
        FileWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        FileWidgetShell.TitleIconElement.LabelText = ViewModel.Name;
        FileWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        FileWidgetShell.TitleBarContent = chromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden
            ? null
            : TitleBarGrid;
        ApplyLegacyTitleActionButtonVisibility(chromeMode);
        ApplyLockActionIconState();

        FileTitleIcon.IconSize = metrics.TitleIconSize;
        FileTitleIcon.Glyph = ViewModel.IconGlyph;
        FileTitleIcon.IconKind = ViewModel.TitleIconKind;
        FileTitleIcon.LabelText = ViewModel.Name;
        FileTitleIcon.Mode = _settingsService.Settings.WidgetTitleIconMode;
        TitleText.FontSize = metrics.TitleTextSize;
        TitleEditBox.FontSize = Math.Max(metrics.TitleTextSize - 1, 11);

        ApplyTitleActionButtonConfiguration(chromeMode);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.PositionLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.SizeLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.AddActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.MoreActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.CloseActionButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            metrics);
        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.AddActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.MoreActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.CloseActionIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        FileWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
    }

    private void ApplyTitleActionButtonConfiguration(WidgetChromeMode chromeMode)
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
        bool showPositionLock = actions.Contains(SettingsService.WidgetHoverActionLockPosition);
        bool showSizeLock = actions.Contains(SettingsService.WidgetHoverActionLockSize);
        bool showAdd = actions.Contains(SettingsService.WidgetHoverActionAdd) &&
            ViewModel.TopAddButtonVisibility == Visibility.Visible;
        bool showMore = actions.Contains(SettingsService.WidgetHoverActionMore);
        bool showDelete = actions.Contains(SettingsService.WidgetHoverActionDelete);

        PositionLockButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        SizeLockButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = showAdd ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;

        FileWidgetShell.PositionLockActionButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.SizeLockActionButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.ShowAddButton = showAdd;
        FileWidgetShell.MoreActionButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.CloseActionButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            PositionLockButtonIcon,
            PositionLockButtonFilledIcon,
            ViewModel.IsPositionLocked,
            SizeLockButtonIcon,
            SizeLockButtonFilledIcon,
            ViewModel.IsSizeLocked);
        WidgetActionIconHelper.ApplyLockState(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            ViewModel.IsPositionLocked,
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            ViewModel.IsSizeLocked);
    }

    private void ApplyLegacyTitleActionButtonVisibility(WidgetChromeMode chromeMode)
    {
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Stop();

        bool showButtons = _settingsService.Settings.ShowHoverButtons &&
            chromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden);
        RightActionButtons.Opacity = showButtons ? 1 : 0;
        RightActionButtons.IsHitTestVisible = showButtons;
        RightButtonsTransform.X = showButtons ? 0 : 12;
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

    private void ApplyWidgetItemLayout(Border border)
    {
        if (IsWithinItemsListView(border))
        {
            border.Width = double.NaN;
            border.Height = double.NaN;
            border.HorizontalAlignment = HorizontalAlignment.Left;
            border.Margin = ViewModel.ListItemMargin;
            border.Padding = ViewModel.ListItemPadding;
            border.CornerRadius = GetItemSurfaceCornerRadius();

            if (border.Child is Grid listGrid)
            {
                listGrid.ColumnSpacing = 10;
                listGrid.HorizontalAlignment = HorizontalAlignment.Left;

                if (TryGetDescendant<Image>(listGrid, out var icon))
                {
                    icon.Width = ViewModel.ListIconSize;
                    icon.Height = ViewModel.ListIconSize;
                }

                if (TryGetDescendant<FontIcon>(listGrid, out var fallbackIcon))
                {
                    fallbackIcon.Width = ViewModel.ListIconSize;
                    fallbackIcon.Height = ViewModel.ListIconSize;
                    fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.ListIconSize * 0.72), 12, 20);
                }

                double textMaxWidth = GetListItemTextMaxWidth();

                if (TryGetDescendant<StackPanel>(listGrid, out var textHost, "ListItemTextHost"))
                {
                    textHost.MaxWidth = textMaxWidth;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var label))
                {
                    label.FontSize = ViewModel.ListLabelFontSize;
                    label.MaxWidth = textMaxWidth;
                    label.HorizontalAlignment = HorizontalAlignment.Left;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var nameText, "ListItemNameText"))
                {
                    nameText.FontSize = ViewModel.ListLabelFontSize;
                    nameText.MaxWidth = textMaxWidth;
                    nameText.HorizontalAlignment = HorizontalAlignment.Left;
                    nameText.Visibility = Visibility.Visible;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var detailText, "ListItemDetailText"))
                {
                    detailText.FontSize = Math.Max(ViewModel.ListLabelFontSize - 2, 9);
                    detailText.MaxWidth = textMaxWidth;
                    detailText.HorizontalAlignment = HorizontalAlignment.Left;
                    detailText.Visibility = ViewModel.ShowListItemDetails
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

            if (TryGetDescendant<FontIcon>(iconStack, out var fallbackIcon))
            {
                fallbackIcon.Width = ViewModel.IconImageSize;
                fallbackIcon.Height = ViewModel.IconImageSize;
                fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.IconImageSize * 0.72), 16, 34);
            }

            if (TryGetDescendant<TextBlock>(iconStack, out var label, "IconItemNameText"))
            {
                label.FontSize = ViewModel.IconLabelFontSize;
                label.MaxWidth = ViewModel.IconLabelMaxWidth;
            }
        }
    }

    private double GetListItemTextMaxWidth()
    {
        double availableWidth = ItemsListView.ActualWidth > 0 ? ItemsListView.ActualWidth : ViewModel.Config.Width;
        double horizontalPadding = ViewModel.ListItemPadding.Left + ViewModel.ListItemPadding.Right;
        double reservedWidth = ViewModel.ListIconSize + 10 + horizontalPadding + 24;
        return Math.Max(56, availableWidth - reservedWidth);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result) where T : DependencyObject
    {
        return TryGetDescendant(parent, out result, null);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result, string? name) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild &&
                (string.IsNullOrWhiteSpace(name) ||
                 (typedChild is FrameworkElement frameworkElement &&
                  string.Equals(frameworkElement.Name, name, StringComparison.Ordinal))))
            {
                result = typedChild;
                return true;
            }

            if (TryGetDescendant(child, out result, name))
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
        else if (e.PropertyName is nameof(WidgetViewModel.TitleIconKind)
            or nameof(WidgetViewModel.IconGlyph)
            or nameof(WidgetViewModel.FollowsDefaultStoragePath))
        {
            ApplyTitleBarLayout();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.ViewMode)
            or nameof(WidgetViewModel.IconViewVisibility)
            or nameof(WidgetViewModel.ListViewVisibility)
            or nameof(WidgetViewModel.ShowListItemDetails)
            or nameof(WidgetViewModel.ShowFileItemPathTooltips)
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
            QueueEmptyStateUpdate();
        }
    }

    private void ItemsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInteractiveSurfaces();
    }

    public void RevealSavedItem(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RevealSavedItem(itemPath));
            return;
        }

        var item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, itemPath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectSingleItem(item);
        ShowStatusToast(_localizationService.T("Widget.SavedHere"));
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
            OpenItem(item);
        }
    }

    private void ItemsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private void OpenItem(WidgetItem item)
    {
        ViewModel.OpenItem(item, _hWnd);
        ClearRemovedCutPaths();
        UpdateEmptyState();
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
            return;
        }

        _lastRightTapPoint = e.GetPosition(element);

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            ClearOtherWidgetSelections();
            if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
                ApplySelectionState(listView);
            }
        }

        var selectedItems = GetSelectedItems();
        bool isMultiSelection = selectedItems.Count > 1;

        if (isMultiSelection)
        {
            var multiFlyout = new MenuFlyout();
            AddMultiSelectionItems(multiFlyout, selectedItems.Count);
            ShowFlyoutWithElevation(multiFlyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        // ─── Win11-style MenuFlyout (custom) with "Show more options" ───
        // We always show our WinUI 3 MenuFlyout first (which IS Win11-styled:
        // rounded corners, acrylic background). At the bottom, we add a
        // "Show more options" item that invokes the full classic native shell
        // context menu — mirroring exactly what Windows 11 File Explorer does.
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Open"),
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        openItem.Click += (_, _) => OpenItem(item);
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        if (CanCopyImageText(item))
        {
            var copyImageTextItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.CopyImageText"),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyImageTextItem.Click += async (_, _) => await CopyImageTextAsync(item);
            flyout.Items.Add(copyImageTextItem);
        }

        var copyPathItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.CopyPath"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) => CopySelectedPathsToClipboard();
        flyout.Items.Add(copyPathItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var showItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.ShowInExplorer"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        showItem.Click += (_, _) => ViewModel.ShowInExplorerCommand.Execute(item);
        flyout.Items.Add(showItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) => await StartItemRenameAsync(item);
        flyout.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        var propItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Properties"),
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propItem.Click += (_, _) => ShellContextMenuHelper.ShowProperties(_hWnd, item.Path);
        flyout.Items.Add(propItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.MoveBackToDesktop"),
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                try
                {
                    int movedCount = await ViewModel.MoveItemsBackToDesktopAsync([item], useShellProgress: true);
                    ShowStatusToast(movedCount > 0
                        ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                        : _localizationService.T("Widget.NoItemsMoved"));
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
                }
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        // ─── "Show more options" — launches the full classic native shell menu ───
        bool pathExists = !string.IsNullOrEmpty(item.Path) && (System.IO.File.Exists(item.Path) || System.IO.Directory.Exists(item.Path));
        if (pathExists)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var showMoreItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.ShowMoreOptions"),
                Icon = new FontIcon { Glyph = "\uE712" }
            };
            showMoreItem.Click += (_, _) =>
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    TryShowNativeContextMenu(item, element, _lastRightTapPoint);
                });
            };
            flyout.Items.Add(showMoreItem);
        }

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
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
        if (e.Handled || _isMigrationBusy)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            HasAncestorOfType<TextBox>(source))
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
            ClearOtherWidgetSelections();
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
                OpenItem(openItem);
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.R)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            ShowStatusToast(_localizationService.T("Widget.Refreshed"));
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
            ClearItemSelectionCore(clearCutState: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearCutState()
    {
        _cutClipboardPaths = [];
        ApplyCutState();
        UpdateInteractiveSurfaces();
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

    private static bool HasFallbackFileFormats(DataPackageView dataView)
    {
        foreach (string format in dataView.AvailableFormats)
        {
            if (IsLikelyFileTransferFormat(format))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyFileTransferFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        if (format.StartsWith("Preferred DropEffect", StringComparison.Ordinal))
        {
            return false;
        }

        return format.Contains("StorageItems", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("StorageItem", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string[]> TryGetLegacyFormatPathsAsync(DataPackageView dataView)
    {
        var paths = new List<string>();
        foreach (string format in dataView.AvailableFormats)
        {
            if (!MayContainLegacyPathText(format))
            {
                continue;
            }

            try
            {
                object? data = await dataView.GetDataAsync(format);
                AppendCandidatePaths(paths, data);
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Legacy format read failed format='{format}': {ex.Message}");
            }
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MayContainLegacyPathText(string format)
    {
        return format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell IDList Array", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("ShellIDListArray", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameW", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameMap", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCandidatePaths(List<string> paths, object? data)
    {
        switch (data)
        {
            case null:
                return;
            case string text:
                AppendCandidatePathText(paths, text);
                return;
            case IEnumerable<string> strings:
                foreach (string value in strings)
                {
                    AppendCandidatePathText(paths, value);
                }

                return;
        }

        App.Log($"[DropDiagnostic] Legacy format returned unsupported type: {data.GetType().FullName}");
    }

    private static void AppendCandidatePathText(List<string> paths, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string candidate in text.Split(
                     ["\0", "\r\n", "\n"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeDroppedPath(candidate, out string normalizedPath))
            {
                paths.Add(normalizedPath);
            }
        }
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (FileWidgetShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden) &&
            e.OriginalSource is DependencyObject source &&
            IsWithin(source, TitleBarGrid))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateContentAreaFlyout(), RootGrid, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        var position = e.GetPosition(TitleBarGrid);
        TrackMoreFlyoutAnchor(TitleBarGrid, position);
        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, position);
        e.Handled = true;
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

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            return;
        }

        var target = sender as FrameworkElement ?? AddButton;
        ShowFlyoutWithElevation(CreateNewWidgetFlyout(), target);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

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
        BeginWindowDrag(e, TitleBarGrid, cursorPt);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        var captureElement = FileWidgetShell.DragHandleElement;
        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        _hasPendingTitleBarClick = false;
        BeginWindowDrag(e, captureElement, cursorPt);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement, Win32Helper.POINT cursorPt)
    {
        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        _displayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        // Begin drag-move snap session
        App.Current?.ResizeGuideOverlay.BeginDrag(_hWnd, RootGrid);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
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

        // Apply drag-move snap
        var proposedBounds = new Windows.Graphics.RectInt32(
            newX, newY, _initialWindowSize.Width, _initialWindowSize.Height);
        var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
            ?? proposedBounds;

        ApplyWindowBounds(snappedBounds.X, snappedBounds.Y, snappedBounds.Width, snappedBounds.Height, persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragCaptureElement?.ReleasePointerCapture(e.Pointer);
        _dragCaptureElement = null;

        // End drag-move snap session
        App.Current?.ResizeGuideOverlay.EndDrag();

        if (_hasMovedTitleBarDrag)
        {
            var finalPosition = _appWindow.Position;
            var finalSize = _appWindow.Size;
            CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
            UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
            ReleaseInteractionLayer("file-drag-ended");
        }

        _displayChangeWatcher?.ResumeRestore();
        _hasMovedTitleBarDrag = false;
        e.Handled = true;
    }

    private async void MapFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        BeginInteractionLayer("file-map-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await app.WidgetManager.CreateFolderWidgetAsync(folderPath);
            }
        }
        finally
        {
            ReleaseInteractionLayer("file-picker-closed");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? CloseButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowDeleteWidgetFlyout(target);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? MoreButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowFlyoutWithElevation(CreateMoreFlyout(), target);
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(ViewModel.Config, mode);
        _settingsService.UpdateWidget(ViewModel.Config);
        ApplyTitleBarLayout();
    }

    private async Task PickAndImportFilesAsync()
    {
        BeginInteractionLayer("file-import-picker-opened");

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
            ShowStatusToast(paths.Length == 1
                ? _localizationService.T("Widget.AddedFile")
                : _localizationService.Format("Widget.AddedFiles", paths.Length));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.AddFileFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
        }
    }

    private async Task PickAndApplyMappedFolderAsync()
    {
        if (ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        BeginInteractionLayer("file-mapped-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            await ViewModel.UpdateMappedFolderPathAsync(folderPath);
            ShowStatusToast(_localizationService.T("Widget.MappedPathUpdated"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.ChangeMappedPathFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
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
                Path.Combine(ViewModel.MappedFolderPath, _localizationService.T("Widget.NewFolderName")));
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
            await ShowErrorDialogAsync(_localizationService.T("Widget.CreateFolderFailed"), ex.Message);
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

    private async Task DeleteItemsWithoutConfirmAsync(IReadOnlyList<WidgetItem> selectedItems)
    {
        try
        {
            int deletedCount = selectedItems.Count;
            await ViewModel.DeleteItemsAsync(selectedItems);
            ClearRemovedCutPaths();
            ShowStatusToast(_localizationService.Format("Widget.MovedToRecycleBin", deletedCount));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteFailed"), ex.Message);
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
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
        }
    }

    private void TogglePositionLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePositionLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteWidgetFlyout(_lastMoreFlyoutTarget ?? MoreButton, _lastMoreFlyoutPosition);
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
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteWidgetFailed"), ex.Message);
            ReleaseInteractionLayer("file-delete-widget-failed");
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
        _displayChangeWatcher?.SuppressRestore();
        BeginInteractionLayer("file-resize-started");
        Win32Helper.GetCursorPos(out InitialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(_hWnd, RootGrid);
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
        var minSize = GetPhysicalMinimumWindowSize(
            _initialWindowPos.X,
            _initialWindowPos.Y,
            _initialWindowSize.Width,
            _initialWindowSize.Height);

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new Windows.Graphics.RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, _resizeDirection);
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
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
        App.Current.ResizeGuideOverlay.EndResize();
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        ReleaseInteractionLayer("file-resize-ended");
        _displayChangeWatcher?.ResumeRestore();
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

    private void BeginInteractionLayer(string reason, bool elevate = true)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction(reason);
        if (elevate)
        {
            ElevateForInteraction();
        }
    }

    private void ReleaseInteractionLayer(string reason)
    {
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    private bool ShouldStartTitleDrag(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, PositionLockButton) &&
               !IsWithin(source, SizeLockButton) &&
               !IsWithin(source, AddButton) &&
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

        return IsWithin(source, TitleText) || IsWithin(source, FileTitleIcon);
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

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, PositionLockButton) &&
               !IsWithin(source, SizeLockButton) &&
               !IsWithin(source, AddButton) &&
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
        ApplyLegacyTitleActionButtonVisibility(_chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor));
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyLegacyTitleActionButtonVisibility(_chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor));
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

    private static bool CanUseRequestedOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return requestedOperation == DataPackageOperation.None ||
               SupportsOperation(requestedOperation, operation);
    }

}