using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Provides snap-to-edge alignment detection and visual edge highlights
/// during widget resize operations.  No overlay window is used — highlights
/// are drawn directly on each widget's own root Grid using Border elements,
/// avoiding all transparent-window rendering issues.
/// </summary>
public sealed class ResizeGuideOverlayService
{
    // ── Snap threshold & visual constants ───────────────────────────────

    private const double SnapThreshold = 8.0;       // Physical pixels
    private const int HighlightThickness = 3;        // DIPs
    private const int HighlightZIndex = 100;

    // ── Active highlight elements (keyed by widget HWND) ────────────────

    private readonly Dictionary<IntPtr, Border> _activeHighlights = new();

    // ── Resize session state ─────────────────────────────────────────────

    private IntPtr _resizingWidgetHwnd;
    private FrameworkElement? _resizingWidgetRoot;
    private Windows.UI.Color _highlightColor;
    private IntPtr _currentTargetHwnd;
    private FrameworkElement? _currentTargetRoot;

    /// <summary>
    /// Whether a resize session is currently active.
    /// </summary>
    public bool IsActive { get; private set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a widget resize operation begins.
    /// </summary>
    public void BeginResize(IntPtr resizingWidgetHwnd, FrameworkElement resizingWidgetRoot)
    {
        _resizingWidgetHwnd = resizingWidgetHwnd;
        _resizingWidgetRoot = resizingWidgetRoot;
        _highlightColor = GetHighlightColor();
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        IsActive = true;

        App.LogVerbose($"[ResizeGuide] BeginResize hwnd=0x{resizingWidgetHwnd.ToInt64():X}");
    }

    /// <summary>
    /// Called on every PointerMoved during resize.  Checks the proposed bounds
    /// against all other widget edges and work-area edges, snaps if within
    /// threshold, and shows edge highlights on both the resizing widget and
    /// the nearest target widget.
    /// Returns the (possibly snapped) bounds to apply.
    /// </summary>
    public RectInt32 UpdateGuidesAndSnap(RectInt32 proposedBounds, string resizeDirection)
    {
        if (!IsActive)
        {
            return proposedBounds;
        }

        var otherBounds = GetOtherWidgetBounds();
        var snapped = proposedBounds;
        SnapEdge? snapEdge = null;
        int snapCoordinate = 0;
        IntPtr? targetHwnd = null;

        // ── Horizontal edge snapping (Left / Right) ──────────────────────

        bool checkRight = resizeDirection.Contains("Right", StringComparison.Ordinal);
        bool checkLeft = resizeDirection.Contains("Left", StringComparison.Ordinal);

        if (checkRight || checkLeft)
        {
            int edgeX = checkRight
                ? proposedBounds.X + proposedBounds.Width
                : proposedBounds.X;

            var (snapX, target) = FindSnapEdgeX(edgeX, otherBounds);
            if (snapX.HasValue)
            {
                if (checkRight)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        snapX.Value - snapped.X,
                        snapped.Height);
                    snapEdge = SnapEdge.Right;
                }
                else
                {
                    int rightEdge = snapped.X + snapped.Width;
                    snapped = new RectInt32(
                        snapX.Value, snapped.Y,
                        rightEdge - snapX.Value,
                        snapped.Height);
                    snapEdge = SnapEdge.Left;
                }

                snapCoordinate = snapX.Value;
                targetHwnd = target;
            }
        }

        // ── Vertical edge snapping (Top / Bottom) ────────────────────────

        bool checkBottom = resizeDirection.Contains("Bottom", StringComparison.Ordinal);
        bool checkTop = resizeDirection.Contains("Top", StringComparison.Ordinal);

        if (checkBottom || checkTop)
        {
            int edgeY = checkBottom
                ? proposedBounds.Y + proposedBounds.Height
                : proposedBounds.Y;

            var (snapY, target) = FindSnapEdgeY(edgeY, otherBounds);
            if (snapY.HasValue)
            {
                if (checkBottom)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        snapped.Width,
                        snapY.Value - snapped.Y);
                    snapEdge = SnapEdge.Bottom;
                }
                else
                {
                    int bottomEdge = snapped.Y + snapped.Height;
                    snapped = new RectInt32(
                        snapped.X, snapY.Value,
                        snapped.Width,
                        bottomEdge - snapY.Value);
                    snapEdge = SnapEdge.Top;
                }

                snapCoordinate = snapY.Value;
                targetHwnd = target;
            }
        }

        // ── Update highlights ────────────────────────────────────────────

        if (snapEdge.HasValue)
        {
            // Highlight resizing widget's edge
            ShowHighlight(_resizingWidgetHwnd, _resizingWidgetRoot, snapEdge.Value);

            // Highlight target widget's matched edge
            if (targetHwnd.HasValue && targetHwnd.Value != IntPtr.Zero)
            {
                var targetRoot = App.Current?.WidgetManager
                    ?.GetWidgetRootElementByHandle(targetHwnd.Value);
                if (targetRoot is not null)
                {
                    // Determine which edge of the target was actually matched
                    // by comparing the snap coordinate to the target's bounds.
                    var targetEdge = ResolveTargetEdge(
                        targetHwnd.Value, snapCoordinate, snapEdge.Value);
                    ShowHighlight(targetHwnd.Value, targetRoot, targetEdge);

                    // Clear previous target if it changed
                    if (_currentTargetHwnd != IntPtr.Zero &&
                        _currentTargetHwnd != targetHwnd.Value)
                    {
                        RemoveHighlight(_currentTargetHwnd);
                    }

                    _currentTargetHwnd = targetHwnd.Value;
                    _currentTargetRoot = targetRoot;
                }
            }
        }
        else
        {
            ClearAllHighlights();
        }

        return snapped;
    }

    /// <summary>
    /// Called when the resize operation ends.  Clears all highlights.
    /// </summary>
    public void EndResize()
    {
        if (!IsActive)
        {
            return;
        }

        ClearAllHighlights();
        IsActive = false;
        _resizingWidgetHwnd = IntPtr.Zero;
        _resizingWidgetRoot = null;
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;

        App.LogVerbose("[ResizeGuide] EndResize");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Snap detection
    // ─────────────────────────────────────────────────────────────────────

    private List<(RectInt32 Bounds, IntPtr Hwnd)> GetOtherWidgetBounds()
    {
        var bounds = new List<(RectInt32, IntPtr)>();
        var manager = App.Current?.WidgetManager;
        if (manager is null)
        {
            return bounds;
        }

        foreach (var hwnd in manager.GetAllWidgetWindowHandles())
        {
            if (hwnd == _resizingWidgetHwnd)
            {
                continue;
            }

            if (Win32Helper.GetWindowRect(hwnd, out var rect) &&
                rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                bounds.Add((
                    new RectInt32(rect.Left, rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top),
                    hwnd));
            }
        }

        return bounds;
    }

    private (int? SnapX, IntPtr? TargetHwnd) FindSnapEdgeX(
        int edgeX, List<(RectInt32 Bounds, IntPtr Hwnd)> otherBounds)
    {
        int bestDelta = (int)Math.Ceiling(SnapThreshold);
        int? bestSnap = null;
        IntPtr? bestTarget = null;

        foreach (var (other, hwnd) in otherBounds)
        {
            TrySnap(edgeX, other.X, hwnd, ref bestDelta, ref bestSnap, ref bestTarget);
            TrySnap(edgeX, other.X + other.Width, hwnd, ref bestDelta, ref bestSnap, ref bestTarget);
        }

        // Work area edges (no target widget)
        int waLeft = 0;
        int waRight = int.MaxValue;
        if (Win32Helper.GetWindowRect(_resizingWidgetHwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2;
            int cy = (wr.Top + wr.Bottom) / 2;
            if (Win32Helper.TryGetMonitorWorkArea(cx, cy, out _, out var workArea))
            {
                waLeft = workArea.Left;
                waRight = workArea.Right;
            }
        }

        TrySnap(edgeX, waLeft, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);
        TrySnap(edgeX, waRight, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);

        return (bestSnap, bestTarget);
    }

    private (int? SnapY, IntPtr? TargetHwnd) FindSnapEdgeY(
        int edgeY, List<(RectInt32 Bounds, IntPtr Hwnd)> otherBounds)
    {
        int bestDelta = (int)Math.Ceiling(SnapThreshold);
        int? bestSnap = null;
        IntPtr? bestTarget = null;

        foreach (var (other, hwnd) in otherBounds)
        {
            TrySnap(edgeY, other.Y, hwnd, ref bestDelta, ref bestSnap, ref bestTarget);
            TrySnap(edgeY, other.Y + other.Height, hwnd, ref bestDelta, ref bestSnap, ref bestTarget);
        }

        // Work area edges
        int waTop = 0;
        int waBottom = int.MaxValue;
        if (Win32Helper.GetWindowRect(_resizingWidgetHwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2;
            int cy = (wr.Top + wr.Bottom) / 2;
            if (Win32Helper.TryGetMonitorWorkArea(cx, cy, out _, out var workArea))
            {
                waTop = workArea.Top;
                waBottom = workArea.Bottom;
            }
        }

        TrySnap(edgeY, waTop, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);
        TrySnap(edgeY, waBottom, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);

        return (bestSnap, bestTarget);
    }

    private static void TrySnap(
        int edge, int candidate, IntPtr hwnd,
        ref int bestDelta, ref int? bestSnap, ref IntPtr? bestTarget)
    {
        int delta = Math.Abs(edge - candidate);
        if (delta < bestDelta)
        {
            bestDelta = delta;
            bestSnap = candidate;
            bestTarget = hwnd;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Edge highlight management
    // ─────────────────────────────────────────────────────────────────────

    private void ShowHighlight(IntPtr hwnd, FrameworkElement? root, SnapEdge edge)
    {
        if (root is not Grid grid)
        {
            return;
        }

        // Remove existing highlight for this widget if any
        if (_activeHighlights.TryGetValue(hwnd, out var existing))
        {
            grid.Children.Remove(existing);
            _activeHighlights.Remove(hwnd);
        }

        var border = new Border
        {
            Background = new SolidColorBrush(_highlightColor),
            IsHitTestVisible = false,
            HorizontalAlignment = edge switch
            {
                SnapEdge.Left => HorizontalAlignment.Left,
                SnapEdge.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch
            },
            VerticalAlignment = edge switch
            {
                SnapEdge.Top => VerticalAlignment.Top,
                SnapEdge.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch
            },
            Width = edge is SnapEdge.Left or SnapEdge.Right ? HighlightThickness : double.NaN,
            Height = edge is SnapEdge.Top or SnapEdge.Bottom ? HighlightThickness : double.NaN,
            CornerRadius = new CornerRadius(1.5),
        };

        Grid.SetRowSpan(border, 20);
        Grid.SetColumnSpan(border, 20);
        Grid.SetRow(border, 0);
        Grid.SetColumn(border, 0);
        border.SetValue(Canvas.ZIndexProperty, HighlightZIndex);

        grid.Children.Add(border);
        _activeHighlights[hwnd] = border;
    }

    private void RemoveHighlight(IntPtr hwnd)
    {
        if (!_activeHighlights.TryGetValue(hwnd, out var border))
        {
            return;
        }

        if (border.Parent is Grid grid)
        {
            grid.Children.Remove(border);
        }

        _activeHighlights.Remove(hwnd);
    }

    private void ClearAllHighlights()
    {
        foreach (var kvp in _activeHighlights)
        {
            if (kvp.Value.Parent is Grid grid)
            {
                grid.Children.Remove(kvp.Value);
            }
        }
        _activeHighlights.Clear();
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines which edge of the target widget was actually matched by
    /// comparing the snap coordinate to the target's physical bounds.
    /// For example, if A's right edge snaps to B's right edge, this returns
    /// SnapEdge.Right (not Left), so B's right edge is highlighted.
    /// </summary>
    private static SnapEdge ResolveTargetEdge(
        IntPtr targetHwnd, int snapCoordinate, SnapEdge resizingEdge)
    {
        if (!Win32Helper.GetWindowRect(targetHwnd, out var rect))
        {
            // Fallback: use opposite edge
            return resizingEdge switch
            {
                SnapEdge.Left => SnapEdge.Right,
                SnapEdge.Right => SnapEdge.Left,
                SnapEdge.Top => SnapEdge.Bottom,
                SnapEdge.Bottom => SnapEdge.Top,
                _ => resizingEdge
            };
        }

        // Horizontal snap: determine if snap X matches target's left or right
        if (resizingEdge is SnapEdge.Left or SnapEdge.Right)
        {
            int distLeft = Math.Abs(snapCoordinate - rect.Left);
            int distRight = Math.Abs(snapCoordinate - rect.Right);
            return distLeft <= distRight ? SnapEdge.Left : SnapEdge.Right;
        }

        // Vertical snap: determine if snap Y matches target's top or bottom
        int distTop = Math.Abs(snapCoordinate - rect.Top);
        int distBottom = Math.Abs(snapCoordinate - rect.Bottom);
        return distTop <= distBottom ? SnapEdge.Top : SnapEdge.Bottom;
    }

    private static Windows.UI.Color GetHighlightColor()
    {
        return App.Current?.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
    }

    private enum SnapEdge
    {
        Left,
        Right,
        Top,
        Bottom
    }
}
