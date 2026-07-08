using DeskBox.Models;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed record TodoReminderNotification(
    string Title,
    string Message,
    int Count,
    string? WidgetId = null,
    string? ItemId = null,
    bool HasTodayDueItem = false);

public sealed class TodoReminderService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MissedReminderGrace = TimeSpan.FromMinutes(1);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Action<TodoReminderNotification> _notify;
    private readonly Func<string, TodoWidgetStore> _storeFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _sessionNotifiedKeys = new(StringComparer.Ordinal);

    private DispatcherQueueTimer? _timer;
    private bool _isChecking;
    private bool _disposed;

    public TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Action<TodoReminderNotification> notify)
        : this(
            settingsService,
            localizationService,
            dispatcherQueue,
            notify,
            widgetId => new TodoWidgetStore(widgetId),
            () => DateTimeOffset.Now)
    {
    }

    internal TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue? dispatcherQueue,
        Action<TodoReminderNotification> notify,
        Func<string, TodoWidgetStore> storeFactory,
        Func<DateTimeOffset> clock)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue;
        _notify = notify;
        _storeFactory = storeFactory;
        _clock = clock;
    }

    public void Start()
    {
        if (_disposed || _dispatcherQueue is null || _timer is not null)
        {
            return;
        }

        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = ScanInterval;
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _ = RunDelayedInitialCheckAsync();
    }

    public async Task<int> CheckNowAsync(DateTimeOffset now)
    {
        if (_disposed || _isChecking)
        {
            return 0;
        }

        _isChecking = true;
        try
        {
            var settings = _settingsService.Settings;
            if (!settings.TodoReminderEnabled ||
                !FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
            {
                return 0;
            }

            int offsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                settings.TodoDefaultReminderOffsetMinutes);
            var reminderOffset = TimeSpan.FromMinutes(offsetMinutes);
            var widgets = settings.Widgets
                .Where(widget =>
                    widget.WidgetKind == WidgetKind.Todo &&
                    !widget.IsDisabled &&
                    !settings.DeletedWidgetIds.Contains(widget.Id))
                .ToList();

            if (widgets.Count == 0)
            {
                return 0;
            }

            List<TodoReminderCandidate> candidates = [];
            foreach (var widget in widgets)
            {
                await CollectWidgetCandidatesAsync(widget, now, reminderOffset, candidates);
            }

            if (candidates.Count == 0)
            {
                return 0;
            }

            _notify(BuildNotification(candidates));
            return candidates.Count;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Check failed: {ex}");
            return 0;
        }
        finally
        {
            _isChecking = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_timer is not null)
        {
            _timer.Tick -= Timer_Tick;
            _timer.Stop();
            _timer = null;
        }
    }

    internal static bool ShouldNotify(TodoItem item, DateTimeOffset now, TimeSpan reminderOffset)
    {
        if (item.IsCompleted || item.DueDate is not { } dueDate)
        {
            return false;
        }

        if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
            DateTimeOffset.Equals(dismissedForDueDate, dueDate))
        {
            return false;
        }

        DateTimeOffset reminderAt = dueDate - reminderOffset;
        if (now < reminderAt)
        {
            return false;
        }

        return now <= dueDate + MissedReminderGrace;
    }

    private async Task RunDelayedInitialCheckAsync()
    {
        try
        {
            await Task.Delay(StartupDelay);
            await CheckNowAsync(_clock());
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Initial check failed: {ex}");
        }
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        await CheckNowAsync(_clock());
    }

    private async Task CollectWidgetCandidatesAsync(
        WidgetConfig widget,
        DateTimeOffset now,
        TimeSpan reminderOffset,
        List<TodoReminderCandidate> candidates)
    {
        var store = _storeFactory(widget.Id);
        var data = await store.LoadAsync();
        bool changed = false;

        foreach (var item in data.Items)
        {
            if (!ShouldNotify(item, now, reminderOffset))
            {
                continue;
            }

            string reminderKey = GetReminderKey(widget.Id, item);
            if (!_sessionNotifiedKeys.Add(reminderKey))
            {
                continue;
            }

            item.ReminderLastNotifiedAt = now;
            item.ReminderDismissedForDueDate = item.DueDate;
            changed = true;
            candidates.Add(new TodoReminderCandidate(widget.Id, widget.Name, item));
        }

        if (changed)
        {
            await store.SaveAsync(data);
        }
    }

    private TodoReminderNotification BuildNotification(IReadOnlyList<TodoReminderCandidate> candidates)
    {
        var first = candidates
            .OrderBy(candidate => candidate.Item.DueDate)
            .First();
        string title = _localizationService.T("Todo.Reminder.NotificationTitle");
        string dueText = FormatDueDate(first.Item.DueDate!.Value);
        string itemText = NormalizeNotificationText(first.Item.Text);
        string message = candidates.Count == 1
            ? _localizationService.Format("Todo.Reminder.NotificationSingle", itemText, dueText)
            : _localizationService.Format("Todo.Reminder.NotificationMultiple", candidates.Count, itemText, dueText);

        bool hasTodayDueItem = candidates.Any(candidate =>
            candidate.Item.DueDate is { } dueDate &&
            dueDate.ToLocalTime().Date == _clock().Date);

        return new TodoReminderNotification(
            title,
            message,
            candidates.Count,
            first.WidgetId,
            first.Item.Id,
            hasTodayDueItem);
    }

    private string FormatDueDate(DateTimeOffset dueDate)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        var today = _clock().Date;
        string time = localDueDate.Second == 0
            ? localDueDate.ToString("HH:mm")
            : localDueDate.ToString("HH:mm:ss");

        if (localDueDate.Date == today)
        {
            return _localizationService.Format("Todo.Due.TodayAt", time);
        }

        if (localDueDate.Date == today.AddDays(1))
        {
            return _localizationService.Format("Todo.Due.TomorrowAt", time);
        }

        return localDueDate.Second == 0
            ? localDueDate.ToString("yyyy/M/d HH:mm")
            : localDueDate.ToString("yyyy/M/d HH:mm:ss");
    }

    private static string NormalizeNotificationText(string? text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty)
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Task";
        }

        const int maxLength = 48;
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static string GetReminderKey(string widgetId, TodoItem item)
    {
        string dueKey = item.DueDate?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        return $"{widgetId}:{item.Id}:{dueKey}";
    }

    private sealed record TodoReminderCandidate(
        string WidgetId,
        string WidgetName,
        TodoItem Item);
}
