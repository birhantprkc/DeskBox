using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoReminderServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsRoot;
    private readonly string _widgetsDataRoot;

    public TodoReminderServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _settingsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "settings")).FullName;
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public async Task CheckNowAsync_NotifiesOnceAndMarksStore()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(4);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);
        int repeatedCount = await service.CheckNowAsync(now.AddSeconds(20));

        Assert.Equal(1, notifiedCount);
        Assert.Equal(0, repeatedCount);
        var notification = Assert.Single(notifications);
        Assert.Equal(1, notification.Count);
        Assert.Equal("todo-widget", notification.WidgetId);
        Assert.Equal("task", notification.ItemId);
        Assert.True(notification.HasTodayDueItem);
        Assert.Contains("Send build", notification.Message, StringComparison.Ordinal);

        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.Equal(now, item.ReminderLastNotifiedAt);
        Assert.Equal(dueDate, item.ReminderDismissedForDueDate);
    }

    [Fact]
    public async Task CheckNowAsync_NotifiesAgainWhenDueDateChanges()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(4)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        Assert.Equal(1, await service.CheckNowAsync(now));

        var data = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(data.Items);
        item.DueDate = now.AddMinutes(10);
        await CreateStore("todo-widget").SaveAsync(data);

        Assert.Equal(1, await service.CheckNowAsync(now.AddMinutes(6)));
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task CheckNowAsync_SkipsWhenReminderSettingDisabled()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        settingsService.Settings.TodoReminderEnabled = false;
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(4)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);

        Assert.Equal(0, notifiedCount);
        Assert.Empty(notifications);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private SettingsService CreateSettingsService(string widgetId)
    {
        var settingsService = new SettingsService(_settingsRoot);
        var settings = settingsService.Settings;
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
        settings.Widgets =
        [
            new WidgetConfig
            {
                Id = widgetId,
                Name = "Todo",
                WidgetKind = WidgetKind.Todo
            }
        ];
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
        return settingsService;
    }

    private TodoReminderService CreateService(
        SettingsService settingsService,
        DateTimeOffset now,
        List<TodoReminderNotification> notifications)
    {
        var localization = TestServices.CreateLocalizationService();
        return new TodoReminderService(
            settingsService,
            localization,
            dispatcherQueue: null,
            notifications.Add,
            CreateStore,
            () => now);
    }

    private TodoWidgetStore CreateStore(string widgetId)
    {
        return new TodoWidgetStore(_widgetsDataRoot, widgetId);
    }
}
