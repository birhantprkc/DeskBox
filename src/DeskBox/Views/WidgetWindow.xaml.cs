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

        ViewModel.Items.CollectionChanged += ViewModel_ItemsCollectionChanged;
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
                element.PointerCaptureLost += ResizeBorder_PointerCaptureLost;
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
            ViewModel.Items.CollectionChanged -= ViewModel_ItemsCollectionChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _displayChangeWatcher?.Dispose();
            _displayChangeWatcher = null;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            StopBackdropRefreshTimer();
            _topMostSafetyTimer?.Stop();
            _topMostSafetyTimer = null;
            try { RemoveFileDropSubclass(); } catch (Exception ex) { App.Log($"[WidgetWindow] RemoveFileDropSubclass failed during close: {ex.Message}"); }
            try { _trayAnimation.Stop(); } catch { }
            try { RestoreItemContainerTransitions(); } catch { }
            try { DisposeAcrylicController(); } catch { }
            try { DisposeMicaController(); } catch { }
            try { StopDragHighlight(); } catch { }
            try { TrackWindowClosedForDiagnostics(); } catch { }

            foreach (var child in ResizeGrid.Children)
            {
                if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    element.PointerMoved -= ResizeBorder_PointerMoved;
                    element.PointerReleased -= ResizeBorder_PointerReleased;
                    element.PointerCaptureLost -= ResizeBorder_PointerCaptureLost;
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
        Win32Helper.SetForegroundWindow(_hWnd);
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
        if (isBusy)
        {
            MigrationTitleText.Text = _localizationService.T("Widget.Migration.Title");
            MigrationDescriptionText.Text = _localizationService.T("Widget.Migration.Description");
        }
        MigrationOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        MigrationProgressRing.IsActive = isBusy;
        ResizeGrid.IsHitTestVisible = !isBusy;
        RootGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Shows the overlay with "importing" text during drag-drop file transfers.
    /// Separate from SetMigrationBusy to use different localized text.
    /// </summary>
    public void SetImportBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetImportBusy(isBusy));
            return;
        }

        _isMigrationBusy = isBusy;
        if (isBusy)
        {
            MigrationTitleText.Text = _localizationService.T("Widget.Import.Title");
            MigrationDescriptionText.Text = _localizationService.T("Widget.Import.Description");
            StopDragHighlight();
        }
        MigrationOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        MigrationProgressRing.IsActive = isBusy;
        ResizeGrid.IsHitTestVisible = !isBusy;
    }

    private void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        _lastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
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
        if (!Win32Helper.IsWindowTopMost(_hWnd))
        {
            _topMostSafetyTimer?.Stop();
            return;
        }

        if (_topMostSafetyTimer is null)
        {
            _topMostSafetyTimer = DispatcherQueue.CreateTimer();
            _topMostSafetyTimer.IsRepeating = false;
            _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            _topMostSafetyTimer.Tick += (_, _) =>
            {
                _topMostSafetyTimer?.Stop();
                if (!_isAtDesktopLayer &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    if (ShouldDeferDesktopLayerRestore())
                    {
                        App.LogVerbose($"[ZOrder] Widget safety timer: defer restore hwnd=0x{_hWnd.ToInt64():X}");
                        _topMostSafetyTimer?.Start();
                        return;
                    }

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

    private void ResizeBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        _resizeDirection = string.Empty;
        _dragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        ReleaseInteractionLayer("file-resize-capture-lost");
        _displayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
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

    private void ViewModel_ItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        QueueEmptyStateUpdate();
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

        return ShouldOpenTitleBarFlyout(source) && IsWithin(source, TitleBarGrid);
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
