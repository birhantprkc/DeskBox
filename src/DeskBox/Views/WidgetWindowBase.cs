// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Shared base class for all desktop widget windows (file, content, quick-capture).
/// Consolidates window setup, backdrop management, layer/Z-order control,
/// drag/resize logic, and display-change restoration that was previously
/// duplicated across WidgetWindow, ContentWidgetWindow, and QuickCaptureWidgetWindow.
/// </summary>
public abstract class WidgetWindowBase : Window
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

    private static readonly int[] BackdropRefreshDelays = [80, 240, 580];
    private static readonly TimeSpan InactiveBackdropControllerRetention = TimeSpan.FromSeconds(3);
    private static readonly ConditionalWeakTable<SolidColorBrush, object> MutableBrushes = new();
    private static readonly object MutableBrushMarker = new();

    // ── Protected state: window identity & services ────────────
    // Set by derived classes in their constructors before calling ConfigureWindowCore().
    protected SettingsService SettingsService = null!;
    protected IntPtr HWnd;
    protected AppWindow AppWindow = null!;
    protected WidgetWindowDiagnostics Diagnostics = null!;
    protected WidgetTrayAnimationController TrayAnimation = null!;
    internal WidgetDisplayChangeWatcher? DisplayChangeWatcher;

    // ── Protected state: backdrop controllers ──────────────────
    protected DesktopAcrylicController? AcrylicController;
    protected MicaController? MicaController;
    protected bool AcrylicControllerAttached;
    protected bool MicaControllerAttached;
    private bool? _acrylicControllerUsesBase;
    private bool? _micaControllerUsesAlt;
    protected SystemBackdropConfiguration? BackdropConfiguration;
    protected ICompositionSupportsSystemBackdrop? BackdropTarget;

    // ── Protected state: backdrop refresh ──────────────────────
    protected long BackdropRefreshGeneration;
    private DispatcherQueueTimer? _backdropRefreshTimer;
    private DispatcherQueueTimer? _inactiveBackdropCleanupTimer;
    private int _backdropRefreshStage;
    private bool _isTrackedForDiagnostics;

    // ── Protected state: drag & resize ─────────────────────────
    protected bool IsDragging;
    protected bool HasMovedTitleBarDrag;
    protected bool IsResizing;
    protected bool IsApplyingBounds;
    protected string ResizeDirection = string.Empty;
    protected Win32Helper.POINT InitialCursorPt;
    protected PointInt32 InitialWindowPos;
    protected SizeInt32 InitialWindowSize;
    protected FrameworkElement? DragCaptureElement;

    // ── Protected state: layer / Z-order ───────────────────────
    protected bool IsAtDesktopLayer;
    protected bool KeepRaisedUntilDeactivate;
    protected bool RestoreDesktopLayerWhenIdle;
    protected bool IsHideAnimationRunning;
    protected DateTime LastElevateForInteractionUtc = DateTime.MinValue;
    protected DispatcherQueueTimer? TopMostSafetyTimer;

    // ── Protected state: closing ───────────────────────────────
    protected bool IsClosing;

    /// <summary>
    /// Parameterless constructor required by the WinUI 3 XAML compiler.
    /// Derived classes must set the protected fields (SettingsService, HWnd, etc.)
    /// in their own constructors before calling ConfigureWindowCore().
    /// </summary>
    protected WidgetWindowBase() { }

    // ── Abstract members: each subclass must provide ───────────

    /// <summary>The widget configuration for this window.</summary>
    public abstract WidgetConfig Config { get; }

    /// <summary>The opacity value (0–1) used for backdrop tinting.</summary>
    protected abstract double WidgetOpacity { get; }

    /// <summary>The root XAML element (typically RootGrid).</summary>
    protected abstract FrameworkElement RootElement { get; }

    /// <summary>Log prefix used in Z-order and backdrop log messages.</summary>
    protected abstract string LogPrefix { get; }

    /// <summary>Whether the window size is locked by the user.</summary>
    protected abstract bool IsSizeLocked { get; }

    /// <summary>Whether the window position is locked by the user.</summary>
    protected abstract bool IsPositionLocked { get; }

    /// <summary>Build the native backdrop tint color for the current theme.</summary>
    protected abstract Windows.UI.Color BuildNativeBackdropTintColor(bool isDark);

    /// <summary>Update the config object from physical bounds.</summary>
    protected abstract void UpdateConfigBoundsFromPhysical(
        int x, int y, int width, int height, bool persist);

    // ── Virtual hooks: subclasses can override for specific behavior ──

    /// <summary>Apply XAML-level surface styling (border brush, plate color, etc.).</summary>
    protected virtual void ApplySurfaceStyle() { }

    /// <summary>Extra guards that block RestoreDesktopLayer (e.g. open flyouts).</summary>
    protected virtual bool HasBlockingFlyoutOpen() => false;

    /// <summary>Called after elevation for interaction (e.g. set focus).</summary>
    protected virtual void OnElevated() { }

    /// <summary>Called when a drag has moved beyond the threshold.</summary>
    protected virtual void OnDragMoved() { }

    /// <summary>Called when drag ends with whether it actually moved.</summary>
    protected virtual void OnDragEnd(bool hasMoved) { }

    /// <summary>Called when resize ends.</summary>
    protected virtual void OnResizeEnd() { }

    /// <summary>Called when resize starts (after elevate).</summary>
    protected virtual void OnResizeStart() { }

    /// <summary>Whether to queue backdrop refresh after loading.</summary>
    protected virtual bool SupportsBackdropRefresh => true;

    /// <summary>Whether the native backdrop is temporarily suppressed for tray reveal animation.</summary>
    protected virtual bool IsBackdropSuppressedForTrayReveal => false;

    /// <summary>Called during ConfigureWindow to install subclass-specific hooks (e.g. file drop subclass).</summary>
    protected virtual void ConfigureWindowExtra() { }

    /// <summary>Called during ConfigureWindow's RootGrid.Loaded handler.</summary>
    protected virtual void OnRootElementLoaded() { }

    /// <summary>Called during ConfigureWindow's RootGrid.ActualThemeChanged handler.</summary>
    protected virtual void OnRootElementThemeChanged() { }

    // ── Window configuration ───────────────────────────────────

    protected void ConfigureWindowCore()
    {
        if (!_isTrackedForDiagnostics)
        {
            PerformanceLogger.TrackWindowOpen(LogPrefix);
            _isTrackedForDiagnostics = true;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        ConfigureWindowExtra();

        int style = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            HWnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;

        var config = Config;
        var workArea = DisplayArea.GetFromRect(
            new RectInt32(
                (int)Math.Round(config.X),
                (int)Math.Round(config.Y),
                (int)Math.Round(config.Width),
                (int)Math.Round(config.Height)),
            DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WidgetPositioningService.ResolveBounds(
            config,
            workArea,
            WidgetPositioningService.GetAvailableWorkAreas());
        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false);

        ApplyDwmBorderStyle(RootElement.ActualTheme == ElementTheme.Dark);
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(HWnd);
        ApplyBackdropPreference();

        RootElement.Loaded += (_, _) =>
        {
            OnRootElementLoaded();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(HWnd);
            if (SupportsBackdropRefresh)
            {
                QueueBackdropRefresh();
            }
        };
        RootElement.ActualThemeChanged += (_, _) =>
        {
            ApplyBackdropPreference();
            OnRootElementThemeChanged();
        };
    }

    // ── Bounds management ──────────────────────────────────────

    protected void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
        width = Math.Max(minSize.Width, width);
        height = Math.Max(minSize.Height, height);

        IsApplyingBounds = true;
        try
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally
        {
            IsApplyingBounds = false;
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

    protected SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    protected void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new RectInt32(x, y, width, height);
        // Use the window center point to determine the owning display.
        // This prevents incorrect anchor capture when the window straddles
        // two monitors during a cross-screen drag.
        var center = new PointInt32(
            x + Math.Max(1, width) / 2,
            y + Math.Max(1, height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
    }

    // ── Display change restoration ─────────────────────────────

    protected bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (IsClosing || IsHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (IsDragging || IsResizing || TrayAnimation.IsApplyingBounds)
        {
            return false;
        }

        var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        var position = AppWindow.Position;
        var size = AppWindow.Size;
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

    protected bool RestoreBoundsAfterDisplayChange()
    {
        if (!Visible)
        {
            var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
            var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
            WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(Config, bounds, workArea);
            SettingsService.SaveDebounced();
            return true;
        }

        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    // ── AppWindow change handling ──────────────────────────────

    protected void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (IsApplyingBounds || TrayAnimation.IsApplyingBounds || (!IsDragging && !IsResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    // ── Window corner & DWM border ─────────────────────────────

    protected void ApplyWindowCornerPreference()
    {
        int cornerPreference = SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    protected void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(HWnd, borderNone);
    }

    protected double GetCornerRadiusFromPreference()
    {
        return SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    // ── Backdrop preference ────────────────────────────────────

    protected void ApplyBackdropPreference()
    {
        if (HWnd == IntPtr.Zero || IsClosing)
        {
            return;
        }

        if (IsBackdropSuppressedForTrayReveal)
        {
            ApplySurfaceStyle();
            return;
        }

        bool isDark = RootElement.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);
        string materialType = SettingsService.Settings.WidgetMaterialType;

        try
        {
            Win32Helper.SetWindowTheme(HWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(HWnd);
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (SettingsService.IsMicaMaterial(materialType))
            {
                controllerApplied = ApplyMicaController(
                    isDark,
                    tintColor,
                    materialType == SettingsService.WidgetMaterialTypeMicaAlt);
            }

            if (!controllerApplied && SettingsService.IsAcrylicMaterial(materialType))
            {
                controllerApplied = ApplyAcrylicController(
                    isDark,
                    tintColor,
                    surfaceOpacity,
                    materialType == SettingsService.WidgetMaterialTypeAcrylicBase);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                Win32Helper.ApplyAccentBlur(HWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{HWnd.ToInt64():X} material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={AcrylicController is not null} micaController={MicaController is not null}");

            ScheduleInactiveBackdropControllerCleanup(materialType);
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(HWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    protected static SolidColorBrush GetOrUpdateSolidColorBrush(Brush? current, Windows.UI.Color color)
    {
        if (current is SolidColorBrush brush && MutableBrushes.TryGetValue(brush, out _))
        {
            try
            {
                if (!brush.Color.Equals(color))
                {
                    brush.Color = color;
                }

                return brush;
            }
            catch (Exception)
            {
                MutableBrushes.Remove(brush);
            }
        }

        var replacement = new SolidColorBrush(color);
        MutableBrushes.Add(replacement, MutableBrushMarker);
        return replacement;
    }

    protected (double Thickness, Windows.UI.Color BorderColor, Windows.UI.Color DividerColor)
        GetWidgetBorderVisuals(bool isDark, Windows.UI.Color accentColor)
    {
        string borderStyle = SettingsService.Settings.WidgetBorderStyle;
        string colorMode = SettingsService.Settings.WidgetBorderColorMode;
        var (thickness, alpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            _ => (0.8d, (byte)0x18)
        };

        if (colorMode == SettingsService.WidgetBorderColorModeNone)
        {
            thickness = 0;
            alpha = 0;
        }

        bool useAccent = colorMode == SettingsService.WidgetBorderColorModeAccent;
        byte borderAlpha = useAccent
            ? (byte)Math.Clamp(Math.Round(alpha * 1.35), 0, 255)
            : alpha;
        byte red = useAccent ? accentColor.R : isDark ? (byte)0xFF : (byte)0x00;
        byte green = useAccent ? accentColor.G : isDark ? (byte)0xFF : (byte)0x00;
        byte blue = useAccent ? accentColor.B : isDark ? (byte)0xFF : (byte)0x00;
        var borderColor = ColorHelper.FromArgb(borderAlpha, red, green, blue);
        var dividerColor = ColorHelper.FromArgb(
            (byte)Math.Clamp(Math.Round(borderAlpha * (isDark ? 0.66 : 0.42)), 0, 255),
            red,
            green,
            blue);
        return (thickness, borderColor, dividerColor);
    }

    protected void ScheduleInactiveBackdropControllerCleanup(string materialType)
    {
        bool hasInactiveController = materialType switch
        {
            SettingsService.WidgetMaterialTypeMica or SettingsService.WidgetMaterialTypeMicaAlt =>
                AcrylicController is not null,
            SettingsService.WidgetMaterialTypeAcrylic or SettingsService.WidgetMaterialTypeAcrylicBase =>
                MicaController is not null,
            _ => AcrylicController is not null || MicaController is not null
        };

        if (!hasInactiveController)
        {
            _inactiveBackdropCleanupTimer?.Stop();
            return;
        }

        if (_inactiveBackdropCleanupTimer is null)
        {
            _inactiveBackdropCleanupTimer = DispatcherQueue.CreateTimer();
            _inactiveBackdropCleanupTimer.IsRepeating = false;
            _inactiveBackdropCleanupTimer.Tick += InactiveBackdropCleanupTimer_Tick;
        }

        _inactiveBackdropCleanupTimer.Stop();
        _inactiveBackdropCleanupTimer.Interval = InactiveBackdropControllerRetention;
        _inactiveBackdropCleanupTimer.Start();
    }

    private void InactiveBackdropCleanupTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        string materialType = SettingsService.Settings.WidgetMaterialType;
        bool releasedController = false;

        if (!SettingsService.IsAcrylicMaterial(materialType) && AcrylicController is not null)
        {
            DisposeAcrylicController();
            releasedController = true;
        }

        if (!SettingsService.IsMicaMaterial(materialType) && MicaController is not null)
        {
            DisposeMicaController();
            releasedController = true;
        }

        if (releasedController)
        {
            App.ScheduleLightMemoryCleanup();
        }
    }

    private static double NormalizeMaterialIntensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                value,
                SettingsService.MinWidgetMaterialIntensity,
                SettingsService.MaxWidgetMaterialIntensity)
            : SettingsService.DefaultWidgetMaterialIntensity;

    private static double LerpMaterialValue(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));

    protected bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        bool useAlt)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (MicaController is not null && _micaControllerUsesAlt != useAlt)
        {
            DisposeMicaController();
        }

        if (MicaController is null)
        {
            DetachAcrylicControllerTarget();
            MicaController = new MicaController
            {
                Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base
            };
            _micaControllerUsesAlt = useAlt;
        }

        DetachAcrylicControllerTarget();
        if (!MicaControllerAttached)
        {
            if (!MicaController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeMicaController();
                return false;
            }

            MicaControllerAttached = true;
            MicaController.SetSystemBackdropConfiguration(BackdropConfiguration);
        }

        MicaController.Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base;
        MicaController.TintColor = tintColor;
        MicaController.FallbackColor = useAlt
            ? isDark
                ? ColorHelper.FromArgb(0xFF, 0x16, 0x18, 0x1D)
                : ColorHelper.FromArgb(0xFF, 0xE8, 0xEA, 0xEF)
            : isDark
                ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double intensity = NormalizeMaterialIntensity(
            SettingsService.Settings.WidgetMaterialIntensity);
        double tintOpacity = useAlt
            ? LerpMaterialValue(0.28, 0.82, intensity)
            : LerpMaterialValue(0.04, 0.46, intensity);
        double luminosityOpacity = useAlt
            ? LerpMaterialValue(isDark ? 0.34 : 0.42, isDark ? 0.72 : 0.76, intensity)
            : LerpMaterialValue(isDark ? 0.78 : 0.82, isDark ? 0.94 : 0.96, intensity);

        MicaController.TintOpacity = (float)tintOpacity;
        MicaController.LuminosityOpacity = (float)luminosityOpacity;
        return true;
    }

    protected void DisposeMicaController()
    {
        if (MicaController is null)
        {
            return;
        }

        try
        {
            MicaController.RemoveAllSystemBackdropTargets();
            MicaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            MicaController = null;
            MicaControllerAttached = false;
            _micaControllerUsesAlt = null;
        }
    }

    protected void DetachMicaControllerTarget()
    {
        if (MicaController is null || !MicaControllerAttached)
        {
            return;
        }

        try
        {
            MicaController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            MicaControllerAttached = false;
        }
    }

    protected bool ApplyAcrylicController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity,
        bool useBase)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        BackdropConfiguration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (AcrylicController is not null &&
            !AcrylicController.IsClosed &&
            _acrylicControllerUsesBase != useBase)
        {
            DisposeAcrylicController();
        }

        if (AcrylicController is null || AcrylicController.IsClosed)
        {
            DetachMicaControllerTarget();
            AcrylicController = new DesktopAcrylicController
            {
                Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin
            };
            _acrylicControllerUsesBase = useBase;
        }

        DetachMicaControllerTarget();
        if (!AcrylicControllerAttached)
        {
            if (!AcrylicController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }

            AcrylicControllerAttached = true;
            AcrylicController.SetSystemBackdropConfiguration(BackdropConfiguration);
        }

        AcrylicController.Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = tintColor;
        AcrylicController.FallbackColor = tintColor;

        double intensity = NormalizeMaterialIntensity(
            SettingsService.Settings.WidgetMaterialIntensity);
        double surfaceStrength = LerpMaterialValue(0.08, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = useBase
            ? LerpMaterialValue(isDark ? 0.18 : 0.12, isDark ? 0.72 : 0.62, intensity)
            : LerpMaterialValue(isDark ? 0.04 : 0.02, isDark ? 0.42 : 0.34, intensity);
        double luminosityOpacity = useBase
            ? LerpMaterialValue(isDark ? 0.38 : 0.46, isDark ? 0.82 : 0.90, intensity)
            : LerpMaterialValue(isDark ? 0.16 : 0.22, isDark ? 0.56 : 0.64, intensity);

        AcrylicController.TintOpacity = (float)Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0);
        AcrylicController.LuminosityOpacity = (float)Math.Clamp(
            luminosityOpacity * surfaceStrength,
            0.0,
            1.0);
        return true;
    }

    protected bool ApplyTransparentAcrylicController(bool isDark)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (AcrylicController is null || AcrylicController.IsClosed)
        {
            AcrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

        }

        DetachMicaControllerTarget();
        if (!AcrylicControllerAttached)
        {
            if (!AcrylicController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }

            AcrylicControllerAttached = true;
            AcrylicController.SetSystemBackdropConfiguration(BackdropConfiguration);
        }

        AcrylicController.Kind = DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.TintOpacity = 0.0f;
        AcrylicController.LuminosityOpacity = 0.0f;

        return true;
    }

    protected void DisposeAcrylicController()
    {
        if (AcrylicController is null)
        {
            return;
        }

        try
        {
            AcrylicController.RemoveAllSystemBackdropTargets();
            AcrylicController.Dispose();
        }
        catch
        {
        }
        finally
        {
            AcrylicController = null;
            AcrylicControllerAttached = false;
            _acrylicControllerUsesBase = null;
        }
    }

    protected void DetachAcrylicControllerTarget()
    {
        if (AcrylicController is null || !AcrylicControllerAttached)
        {
            return;
        }

        try
        {
            AcrylicController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            AcrylicControllerAttached = false;
        }
    }

    // ── Backdrop refresh timer ─────────────────────────────────

    protected void QueueBackdropRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueBackdropRefresh);
            return;
        }

        ++BackdropRefreshGeneration;
        _backdropRefreshStage = 0;

        if (_backdropRefreshTimer is null)
        {
            _backdropRefreshTimer = DispatcherQueue.CreateTimer();
            _backdropRefreshTimer.Tick += (_, _) => OnBackdropRefreshTick(BackdropRefreshGeneration);
        }
        else
        {
            _backdropRefreshTimer.Stop();
        }

        _backdropRefreshTimer.Interval = TimeSpan.FromMilliseconds(BackdropRefreshDelays[0]);
        _backdropRefreshTimer.Start();
    }

    private void OnBackdropRefreshTick(long generation)
    {
        if (generation != BackdropRefreshGeneration)
        {
            _backdropRefreshTimer?.Stop();
            return;
        }

        RefreshBackdropIfCurrent(generation);

        int nextStage = _backdropRefreshStage + 1;
        _backdropRefreshStage = nextStage;

        if (nextStage < BackdropRefreshDelays.Length)
        {
            _backdropRefreshTimer!.Interval = TimeSpan.FromMilliseconds(BackdropRefreshDelays[nextStage]);
        }
        else
        {
            _backdropRefreshTimer!.Stop();
        }
    }

    private void RefreshBackdropIfCurrent(long generation)
    {
        if (generation != BackdropRefreshGeneration)
        {
            return;
        }

        if (!Visible || IsHideAnimationRunning)
        {
            return;
        }

        // Skip backdrop refresh during drag/resize — the window is moving
        // and the backdrop will be refreshed once when the operation ends.
        if (IsDragging || IsResizing)
        {
            return;
        }

        ApplyBackdropPreference();
    }

    protected void StopBackdropRefreshTimer()
    {
        _backdropRefreshTimer?.Stop();
        _backdropRefreshTimer = null;
    }

    // ── Layer / Z-order management ─────────────────────────────

    protected void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        LastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        OnElevated();
    }

    protected void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = true;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost skipped pinned hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        IsAtDesktopLayer = false;
        KeepRaisedUntilDeactivate = true;
        RestoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost hwnd=0x{HWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    protected void StartTopMostSafetyTimer()
    {
        if (!Win32Helper.IsWindowTopMost(HWnd))
        {
            TopMostSafetyTimer?.Stop();
            return;
        }

        if (TopMostSafetyTimer is null)
        {
            TopMostSafetyTimer = DispatcherQueue.CreateTimer();
            TopMostSafetyTimer.IsRepeating = false;
            TopMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            TopMostSafetyTimer.Tick += (_, _) =>
            {
                TopMostSafetyTimer?.Stop();
                if (!IsAtDesktopLayer &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    if (ShouldDeferDesktopLayerRestore())
                    {
                        App.LogVerbose($"[ZOrder] {LogPrefix} safety timer: defer restore hwnd=0x{HWnd.ToInt64():X}");
                        TopMostSafetyTimer?.Start();
                        return;
                    }

                    App.Log($"[ZOrder] {LogPrefix} safety timer: force restore hwnd=0x{HWnd.ToInt64():X}");
                    RestoreDesktopLayer(force: true);
                }
            };
        }
        else
        {
            TopMostSafetyTimer.Stop();
        }
        TopMostSafetyTimer.Start();
    }

    protected bool ShouldDeferDesktopLayerRestore()
    {
        if (IsDragging ||
            IsResizing ||
            HasBlockingFlyoutOpen() ||
            App.Current.WidgetManager is { IsWidgetInteractionActive: true })
        {
            return true;
        }

        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        return foregroundWindow == HWnd ||
               Win32Helper.GetAncestor(foregroundWindow, Win32Helper.GA_ROOTOWNER) == HWnd;
    }

    protected void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !RestoreDesktopLayerWhenIdle && KeepRaisedUntilDeactivate)
        {
            return;
        }

        if (!force && (IsDragging || IsResizing || HasBlockingFlyoutOpen()))
        {
            if (force || RestoreDesktopLayerWhenIdle)
            {
                RestoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    protected void ClearTopMostOnly()
    {
        IsAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} ClearTopMostOnly hwnd=0x{HWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    // ── Drag logic ─────────────────────────────────────────────

    protected void BeginWindowDragCore(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        IsDragging = true;
        HasMovedTitleBarDrag = false;
        DisplayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        InitialWindowPos = AppWindow.Position;
        InitialWindowSize = AppWindow.Size;
        DragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        App.Current?.ResizeGuideOverlay.BeginDrag(HWnd, RootElement);
    }

    protected void ContinueWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (!HasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            HasMovedTitleBarDrag = true;
        }

        int newX = InitialWindowPos.X + deltaX;
        int newY = InitialWindowPos.Y + deltaY;

        var proposedBounds = new RectInt32(newX, newY, InitialWindowSize.Width, InitialWindowSize.Height);
        var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
            ?? proposedBounds;

        ApplyWindowBounds(snappedBounds.X, snappedBounds.Y, snappedBounds.Width, snappedBounds.Height, persist: false);
        e.Handled = true;
    }

    protected void EndWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement?.ReleasePointerCapture(e.Pointer);
        DragCaptureElement = null;

        App.Current?.ResizeGuideOverlay.EndDrag();

        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnDragEnd(hasMoved);

        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
        e.Handled = true;
    }

    // ── Resize logic ───────────────────────────────────────────

    protected void ResizeBorder_PointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (IsSizeLocked || sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        IsResizing = true;
        ResizeDirection = element.Tag as string ?? string.Empty;
        DisplayChangeWatcher?.SuppressRestore();
        OnResizeStart();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        InitialWindowPos = AppWindow.Position;
        InitialWindowSize = AppWindow.Size;
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(HWnd, RootElement);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerMovedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;

        int newWidth = InitialWindowSize.Width;
        int newHeight = InitialWindowSize.Height;
        int newX = InitialWindowPos.X;
        int newY = InitialWindowPos.Y;
        var minSize = GetPhysicalMinimumWindowSize(
            InitialWindowPos.X,
            InitialWindowPos.Y,
            InitialWindowSize.Width,
            InitialWindowSize.Height);

        if (ResizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width + deltaX);
        }
        else if (ResizeDirection.Contains("Left"))
        {
            int rightEdge = InitialWindowPos.X + InitialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (ResizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height + deltaY);
        }
        else if (ResizeDirection.Contains("Top"))
        {
            int bottomEdge = InitialWindowPos.Y + InitialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, ResizeDirection);
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing || sender is not FrameworkElement element)
        {
            return;
        }

        IsResizing = false;
        ResizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnResizeEnd();
        DisplayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    /// <summary>
    /// Handles PointerCaptureLost for resize borders.  If the system steals
    /// pointer capture mid-resize (e.g., alt-tab, UAC, tablet mode), the
    /// PointerReleased event never fires and the resize guide highlights
    /// would be leaked forever.  This method ensures cleanup.
    /// </summary>
    protected void ResizeBorder_PointerCaptureLostCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing)
        {
            return;
        }

        IsResizing = false;
        ResizeDirection = string.Empty;
        DragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnResizeEnd();
        DisplayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    /// <summary>
    /// Handles PointerCaptureLost for drag-move.  Same rationale as
    /// ResizeBorder_PointerCaptureLostCore — ensures EndDrag is called
    /// even when the system steals pointer capture mid-drag.
    /// </summary>
    protected void DragPointerCaptureLostCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndDrag();
        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnDragEnd(hasMoved);
        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
        e.Handled = true;
    }

    // ── Interaction layer helpers ──────────────────────────────

    protected void BeginInteractionLayer(string reason, bool elevate = true)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction(reason);
        if (elevate)
        {
            ElevateForInteraction();
        }
    }

    protected void ReleaseInteractionLayer(string reason)
    {
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    // ── Tray animation helpers ─────────────────────────────────

    protected WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return TrayAnimation.CreateProfile(WidgetAnimationSettings.From(SettingsService.Settings));
    }

    protected void LogTrayWindow(string message)
    {
        App.LogVerbose(Diagnostics.FormatTrayWindowMessage(message));
    }

    // ── Cleanup ────────────────────────────────────────────────

    protected void CleanupBase()
    {
        StopBackdropRefreshTimer();
        if (_inactiveBackdropCleanupTimer is not null)
        {
            _inactiveBackdropCleanupTimer.Stop();
            _inactiveBackdropCleanupTimer.Tick -= InactiveBackdropCleanupTimer_Tick;
            _inactiveBackdropCleanupTimer = null;
        }
        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        DisplayChangeWatcher?.Dispose();
        DisplayChangeWatcher = null;
        DisposeAcrylicController();
        DisposeMicaController();
        WidgetLayerService.ReleaseWindow(HWnd);
        TrackWindowClosedForDiagnostics();
    }

    protected void TrackWindowClosedForDiagnostics()
    {
        if (!_isTrackedForDiagnostics)
        {
            return;
        }

        PerformanceLogger.TrackWindowClose(LogPrefix);
        _isTrackedForDiagnostics = false;
    }
}
