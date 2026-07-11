// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing ZOrder logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private DispatcherQueueTimer? _trayLayerRestoreTimer;
    private readonly Win32Helper.LowLevelMouseProc _mouseHookProc;
    private IntPtr _mouseHookHandle;
    private bool _widgetsRaisedFromTray;
    private bool _isTogglingWidgetsDesktopLayer;
    private string _lastWidgetLayerMode;
    private DateTime _lastTrayLayerToggleUtc = DateTime.MinValue;
    private DateTime _suppressTrayLayerRestoreUntilUtc = DateTime.MinValue;

    public void RestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: false);
    }

    public void ForceRestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: true);
    }

    public void BringAllVisibleWidgetsToFront(IntPtr exceptHwnd = default)
    {
        foreach (var window in GetLoadedDesktopWindows())
        {
            if (window.Visible && window.WindowHandle != exceptHwnd)
            {
                WidgetLayerService.BringToFront(window.WindowHandle);
            }
        }
    }

    public bool RequestRestoreRaisedWidgetsToDesktopLayer(string reason = "interaction-ended")
    {
        if (!_widgetsRaisedFromTray)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest ignored reason={reason} state=not-raised");
            return false;
        }

        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => RequestRestoreRaisedWidgetsToDesktopLayer(reason));
            return true;
        }

        StartTrayLayerRestoreMonitor(hasRaisedWidgets: true);
        App.LogVerbose($"[TrayBatch] RestoreRequest queued reason={reason}");
        return true;
    }

    private void QueueRequestedLayerRestoreCheck(string reason, TimeSpan delay)
    {
        long generation = _trayRaiseBatchGeneration;
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            TryRestoreRaisedWidgetsAfterInteraction(reason, generation);
        });
    }

    private void TryRestoreRaisedWidgetsAfterInteraction(string reason, long generation)
    {
        if (!_widgetsRaisedFromTray ||
            generation != _trayRaiseBatchGeneration ||
            _isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        Win32Helper.POINT? cursor = TryGetCursorPosition();
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        bool foregroundIsWidget = IsWidgetWindow(foreground);
        bool pointerOverWidget = cursor.HasValue && IsWidgetWindow(Win32Helper.WindowFromPoint(cursor.Value));
        bool pointerOverTaskbar = IsPointerOverTaskbar(cursor);

        if (foregroundIsWidget || pointerOverWidget || pointerOverTaskbar)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest kept reason={reason} fgWidget={foregroundIsWidget} ptrWidget={pointerOverWidget} ptrTaskbar={pointerOverTaskbar}");
            return;
        }

        App.LogVerbose($"[TrayBatch] RestoreRequest restoring reason={reason} cursor={FormatPoint(cursor)}");
        RestoreRaisedWidgetsToDesktopLayer();
    }

    private void StartTrayLayerRestoreMonitor(bool hasRaisedWidgets)
    {
        if (!hasRaisedWidgets)
        {
            App.LogVerbose("[TrayBatch] RestoreMonitor not-started reason=no-raised-windows");
            StopTrayLayerRestoreMonitor();
            return;
        }

        _ = Win32Helper.HasMouseButtonActivity();

        _trayLayerRestoreTimer ??= App.UiDispatcherQueue.CreateTimer();
        _trayLayerRestoreTimer.Stop();
        _trayLayerRestoreTimer.Interval = TimeSpan.FromMilliseconds(200);
        _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Tick += TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Start();
        InstallTrayLayerMouseHook();
        App.LogVerbose("[TrayBatch] RestoreMonitor started intervalMs=40");
    }

    private void StopTrayLayerRestoreMonitor()
    {
        if (_trayLayerRestoreTimer is null)
        {
            return;
        }

        _trayLayerRestoreTimer.Stop();
        _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
        UninstallTrayLayerMouseHook();
        App.LogVerbose("[TrayBatch] RestoreMonitor stopped");
    }

    private void InstallTrayLayerMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookHandle = Win32Helper.SetWindowsMouseHookEx(
            Win32Helper.WH_MOUSE_LL,
            _mouseHookProc,
            Win32Helper.GetModuleHandle(null),
            0);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            App.Log($"[TrayBatch] RestoreMouseHook install failed error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return;
        }

        App.LogVerbose("[TrayBatch] RestoreMouseHook installed");
    }

    private void UninstallTrayLayerMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        App.LogVerbose("[TrayBatch] RestoreMouseHook removed");
    }

    private IntPtr TrayLayerMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseDownMessage(wParam))
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32Helper.MSLLHOOKSTRUCT>(lParam);
            App.UiDispatcherQueue.TryEnqueue(() => RestoreRaisedWidgetsForExternalMouseDown(data.pt));
        }

        return Win32Helper.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static bool IsMouseDownMessage(IntPtr message)
    {
        int value = message.ToInt32();
        return value is Win32Helper.WM_LBUTTONDOWN or
               Win32Helper.WM_RBUTTONDOWN or
               Win32Helper.WM_MBUTTONDOWN or
               Win32Helper.WM_XBUTTONDOWN;
    }

    private void RestoreRaisedWidgetsForExternalMouseDown(Win32Helper.POINT cursor)
    {
        if (!_widgetsRaisedFromTray)
        {
            return;
        }

        bool overTaskbar = IsPointerOverTaskbar(cursor);
        if (overTaskbar)
        {
            App.LogVerbose($"[TrayBatch] RestoreMouseHook kept taskbar");
            return;
        }

        bool overWidget = IsCursorOverAnyWidget(cursor);
        if (overWidget)
        {
            App.LogVerbose($"[TrayBatch] RestoreMouseHook kept-all over-widget cursor={cursor.X},{cursor.Y}");
            return;
        }

        IntPtr targetWindow = Win32Helper.WindowFromPoint(cursor);
        App.LogVerbose($"[TrayBatch] RestoreMouseHook restoring-all cursor={cursor.X},{cursor.Y} hwnd=0x{targetWindow.ToInt64():X}");
        RestoreRaisedWidgetsToDesktopLayer(force: true);
    }

    private bool IsCursorOverAnyWidget(Win32Helper.POINT cursor)
    {
        foreach (var (_, (window, _)) in _widgets)
        {
            if (IsCursorOverWindow(window, cursor))
            {
                return true;
            }
        }

        foreach (var (_, (window, _)) in _quickCaptureWidgets)
        {
            if (IsCursorOverWindow(window, cursor))
            {
                return true;
            }
        }

        foreach (var (_, window) in _contentWidgets)
        {
            if (IsCursorOverWindow(window, cursor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCursorOverWindow(IDesktopWidgetWindow window, Win32Helper.POINT cursor)
    {
        if (!window.Visible)
        {
            return false;
        }

        var bounds = window.AnimationBounds;
        return cursor.X >= bounds.X &&
               cursor.X < bounds.X + (int)bounds.Width &&
               cursor.Y >= bounds.Y &&
               cursor.Y < bounds.Y + (int)bounds.Height;
    }

    private void TrayLayerRestoreTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_widgetsRaisedFromTray)
        {
            StopTrayLayerRestoreMonitor();
            return;
        }

        if (_isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        InstallTrayLayerMouseHook();
    }

    private static bool IsPointerOverDeskBoxWindow(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        return App.Current.IsDeskBoxWindow(pointerWindow);
    }

    private static bool IsForegroundDeskBoxWindow()
    {
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        return IsDeskBoxForegroundWindow(foregroundWindow);
    }

    private static bool IsDeskBoxForegroundWindow(IntPtr foregroundWindow)
    {
        return foregroundWindow != IntPtr.Zero &&
               App.Current.IsDeskBoxWindow(foregroundWindow);
    }

    private static bool IsPointerOverTaskbar(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        if (pointerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = pointerWindow;
        while (currentWindow != IntPtr.Zero)
        {
            if (IsTaskbarWindow(currentWindow))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(pointerWindow, Win32Helper.GA_ROOT);
        return IsTaskbarWindow(rootWindow);
    }

    private static bool IsTaskbarWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "NotifyIconOverflowWindow", StringComparison.Ordinal));
    }

    private static bool IsDesktopShellWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Progman", StringComparison.Ordinal) ||
                     string.Equals(value, "WorkerW", StringComparison.Ordinal));
    }

    private static bool WindowOrAncestorHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = hWnd;
        while (currentWindow != IntPtr.Zero)
        {
            if (WindowHasClass(currentWindow, predicate))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(hWnd, Win32Helper.GA_ROOT);
        return WindowHasClass(rootWindow, predicate);
    }

    private static bool WindowHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new System.Text.StringBuilder(256);
        int length = Win32Helper.GetClassName(hWnd, className, className.Capacity);
        return length > 0 && predicate(className.ToString());
    }

    private static Win32Helper.POINT? TryGetCursorPosition()
    {
        return Win32Helper.GetCursorPos(out var cursor) ? cursor : null;
    }

    private void SetWidgetsRaisedFromTray(bool raised)
    {
        if (_widgetsRaisedFromTray == raised)
        {
            if (raised)
            {
                _sessionManager.MarkRaisedSession("raised-state-kept");
            }

            return;
        }

        App.LogVerbose($"[TrayBatch] RaisedState changed { _widgetsRaisedFromTray } -> {raised}");
        _widgetsRaisedFromTray = raised;
        if (raised)
        {
            _sessionManager.MarkRaisedSession("tray-raised");
        }
        else if (HasVisibleWidgets)
        {
            _sessionManager.MarkDesktopResting("tray-restored");
        }
        else
        {
            _sessionManager.MarkHidden("tray-hidden");
        }

        TrayLayerStateChanged?.Invoke(raised);
    }

    private static string FormatWidgetList(IReadOnlyList<WidgetConfig> widgets)
    {
        return widgets.Count == 0
            ? "[]"
            : "[" + string.Join(", ", widgets.Select(FormatWidget)) + "]";
    }

    private static string FormatWidget(WidgetConfig widget)
    {
        return $"{widget.Name}#{ShortId(widget.Id)} kind={widget.WidgetKind} visible={widget.IsVisible} disabled={widget.IsDisabled}";
    }

    private static string FormatHostWindow(IDesktopWidgetWindow window)
    {
        var identity = window.Identity;
        return $"{identity.LogDisplayName} kind={identity.WidgetKind} hwnd=0x{identity.WindowHandle.ToInt64():X}";
    }

    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "none"
            : id.Length <= 8 ? id : id[..8];
    }

    private static string FormatPoint(Win32Helper.POINT? point)
    {
        return point.HasValue ? $"{point.Value.X},{point.Value.Y}" : "unknown";
    }

}