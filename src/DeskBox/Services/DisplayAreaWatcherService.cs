using DeskBox.Helpers;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Monitors display configuration changes (hot-plug add/remove, resolution
/// changes, DPI changes) by periodically polling the display topology via
/// Win32 <c>EnumDisplayMonitors</c>.
/// <para>
/// This service provides a consolidated <see cref="DisplaysChanged"/> event
/// with debouncing, allowing the application to reposition widgets and
/// invalidate caches when the display topology changes.
/// </para>
/// </summary>
public sealed class DisplayAreaWatcherService : IDisposable
{
    private const int PollIntervalMs = 2000;
    private const int DebounceDelayMs = 500;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _debounceTimer;
    private bool _isDisposed;
    private int _displayCount;
    private string _displaySignature = string.Empty;

    /// <summary>
    /// Fired when the set of displays has changed (add, remove, resolution,
    /// or DPI change).  Fires after a short debounce to avoid spamming
    /// during rapid display configuration changes.
    /// </summary>
    public event Action? DisplaysChanged;

    /// <summary>
    /// The current number of displays.
    /// </summary>
    public int DisplayCount => _displayCount;

    public DisplayAreaWatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _pollTimer = dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(PollIntervalMs);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += PollTimer_Tick;

        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    public void Start()
    {
        if (_isDisposed)
        {
            return;
        }

        _displayCount = CountDisplays();
        _displaySignature = GetDisplaySignature();
        App.Log($"[DisplayAreaWatcher] Started, initial display count: {_displayCount}, signature: {_displaySignature}");
        _pollTimer.Start();
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        int newCount = CountDisplays();
        string newSignature = GetDisplaySignature();

        if (newCount != _displayCount || !string.Equals(newSignature, _displaySignature, StringComparison.Ordinal))
        {
            bool isCountChange = newCount != _displayCount;
            _displayCount = newCount;
            _displaySignature = newSignature;

            App.Log(
                $"[DisplayAreaWatcher] Display topology changed: " +
                $"count={newCount} countChanged={isCountChange} " +
                $"signature={newSignature}");

            _debounceTimer.Start();
        }
    }

    private void DebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _debounceTimer.Stop();
        DisplaysChanged?.Invoke();
    }

    /// <summary>
    /// Creates a string signature of the current display topology
    /// (monitor bounds + work areas) to detect any geometry changes.
    /// </summary>
    private static string GetDisplaySignature()
    {
        try
        {
            var areas = Win32Helper.GetMonitorWorkAreaInfos();
            return string.Join("|", areas.Select(a =>
                $"{a.Monitor.Left},{a.Monitor.Top},{a.Monitor.Right},{a.Monitor.Bottom};" +
                $"{a.WorkArea.Left},{a.WorkArea.Top},{a.WorkArea.Right},{a.WorkArea.Bottom}"));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CountDisplays()
    {
        try
        {
            return Win32Helper.GetMonitorWorkAreas().Count;
        }
        catch
        {
            return 1;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
    }
}
