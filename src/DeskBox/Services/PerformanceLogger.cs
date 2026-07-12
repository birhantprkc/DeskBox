using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace DeskBox.Services;

/// <summary>
/// Lightweight opt-in timing logs for performance baseline work.
/// Enable by setting DESKBOX_PERF_LOG=1 before launching DeskBox.
/// </summary>
public static class PerformanceLogger
{
    public const string EnabledEnvironmentVariable = "DESKBOX_PERF_LOG";

    private static readonly Lazy<bool> s_isEnabled = new(
        () => IsEnabledSetting(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)));

    public static bool IsEnabled => s_isEnabled.Value;

    // ── Memory & resource diagnostics ───────────────────────────

    /// <summary>Working set (bytes) at the last sample.</summary>
    public static long LastWorkingSet { get; private set; }

    /// <summary>Private memory (bytes) at the last sample.</summary>
    public static long LastPrivateMemory { get; private set; }

    /// <summary>Managed heap size (bytes) at the last sample.</summary>
    public static long LastManagedHeap { get; private set; }

    /// <summary>Handle count at the last sample.</summary>
    public static int LastHandleCount { get; private set; }

    /// <summary>Thumbnail cache entry count, updated by IconHelper.</summary>
    public static int ThumbnailCacheCount { get; set; }

    /// <summary>Icon cache entry count, updated by IconHelper.</summary>
    public static int IconCacheCount { get; set; }

    /// <summary>Music cover decode count since launch.</summary>
    public static int MusicCoverDecodeCount => Volatile.Read(ref s_musicCoverDecodeCount);

    /// <summary>Active music timer count (progress + visualizer + transition).</summary>
    public static int ActiveMusicTimerCount { get; set; }

    private static int s_musicCoverDecodeCount;

    public static void RecordMusicCoverDecode()
    {
        Interlocked.Increment(ref s_musicCoverDecodeCount);
    }

    private static readonly ConcurrentDictionary<string, int> s_windowCounts = new();

    /// <summary>
    /// Records a widget window as open (called by window constructors).
    /// </summary>
    public static void TrackWindowOpen(string windowKind)
    {
        if (!IsEnabled)
        {
            return;
        }

        s_windowCounts.AddOrUpdate(windowKind, 1, (_, c) => c + 1);
    }

    /// <summary>
    /// Records a widget window as closed.
    /// </summary>
    public static void TrackWindowClose(string windowKind)
    {
        if (!IsEnabled)
        {
            return;
        }

        s_windowCounts.AddOrUpdate(windowKind, 0, (_, c) => Math.Max(0, c - 1));
    }

    /// <summary>
    /// Samples the current process memory and handle usage and logs a
    /// diagnostic line.  Only runs when perf logging is enabled.
    /// </summary>
    public static void SampleMemory()
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            LastWorkingSet = proc.WorkingSet64;
            LastPrivateMemory = proc.PrivateMemorySize64;
            LastHandleCount = proc.HandleCount;
            LastManagedHeap = GC.GetTotalMemory(forceFullCollection: false);

            int windowCount = 0;
            foreach (var kv in s_windowCounts)
            {
                windowCount += kv.Value;
            }

            App.Log(
                $"[Perf] MemorySample " +
                $"workingSetMB={LastWorkingSet / (1024.0 * 1024):F1} " +
                $"privateMB={LastPrivateMemory / (1024.0 * 1024):F1} " +
                $"managedHeapMB={LastManagedHeap / (1024.0 * 1024):F1} " +
                $"handles={LastHandleCount} " +
                $"thumbCache={ThumbnailCacheCount} " +
                $"iconCache={IconCacheCount} " +
                $"musicCoverDecodes={MusicCoverDecodeCount} " +
                $"musicTimers={ActiveMusicTimerCount} " +
                $"windows={windowCount}");
        }
        catch
        {
            // Best-effort diagnostics — never crash the app.
        }
    }

    public static IDisposable Measure(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return EmptyScope.Instance;
        }

        return new Scope(operation, details);
    }

    public static void Mark(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        App.Log(FormatMessage(operation, null, details));
    }

    internal static bool IsEnabledSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        return normalizedValue switch
        {
            "1" => true,
            _ when normalizedValue.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("enabled", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static string FormatMessage(string operation, double? elapsedMs, string? details)
    {
        string message = elapsedMs.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"[Perf] {operation} elapsedMs={elapsedMs.Value:F1}")
            : $"[Perf] {operation}";

        if (string.IsNullOrWhiteSpace(details))
        {
            return message;
        }

        return $"{message} {details.ReplaceLineEndings(" ")}";
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _operation;
        private readonly string? _details;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(string operation, string? details)
        {
            _operation = operation;
            _details = details;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            App.Log(FormatMessage(_operation, _stopwatch.Elapsed.TotalMilliseconds, _details));
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}
