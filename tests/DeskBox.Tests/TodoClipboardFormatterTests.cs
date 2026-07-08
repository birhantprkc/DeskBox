using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class TodoClipboardFormatterTests
{
    [Fact]
    public void FormatSingleText_TrimsText()
    {
        var item = new TodoItemViewModel(new TodoItem
        {
            Text = "  review release notes  "
        });

        string formatted = TodoClipboardFormatter.FormatSingleText(item);

        Assert.Equal("review release notes", formatted);
    }

    [Fact]
    public void FormatBatch_UsesMarkdownFriendlyLines()
    {
        var localization = TestServices.CreateLocalizationService();
        var active = new TodoItemViewModel(new TodoItem
        {
            Text = "review release notes",
            ColorMarker = TodoItem.RedColorMarker,
            DueDate = DateTimeOffset.Now.AddHours(1)
        }, localization);
        var noDue = new TodoItemViewModel(new TodoItem
        {
            Text = "clean inbox"
        }, localization);
        var completed = new TodoItemViewModel(new TodoItem
        {
            Text = "send build",
            IsCompleted = true,
            CompletedAt = DateTimeOffset.Now.AddMinutes(-10)
        }, localization);

        string formatted = TodoClipboardFormatter.FormatBatch([active, noDue, completed], localization);
        string[] lines = formatted.Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("- [ ] \U0001F534 review release notes\uFF5CDue:", lines[0], StringComparison.Ordinal);
        Assert.Equal("- [ ] clean inbox\uFF5CNo due date", lines[1]);
        Assert.StartsWith("- [x] \u2705 send build\uFF5CCompleted:", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void DueStatusVisibility_SeparatesOverdueFromNormalDueDate()
    {
        var overdue = new TodoItemViewModel(new TodoItem
        {
            Text = "overdue",
            DueDate = DateTimeOffset.Now.AddMinutes(-5)
        });
        var upcoming = new TodoItemViewModel(new TodoItem
        {
            Text = "upcoming",
            DueDate = DateTimeOffset.Now.AddMinutes(5)
        });

        Assert.Equal(Visibility.Collapsed, overdue.DueStatusNormalVisibility);
        Assert.Equal(Visibility.Visible, overdue.DueStatusOverdueVisibility);
        Assert.Equal(Visibility.Visible, upcoming.DueStatusNormalVisibility);
        Assert.Equal(Visibility.Collapsed, upcoming.DueStatusOverdueVisibility);
    }

    [Fact]
    public void DueStatusText_AppendsOverdueSuffixAfterTime()
    {
        var localization = TestServices.CreateLocalizationService();
        var item = new TodoItemViewModel(new TodoItem
        {
            Text = "overdue",
            DueDate = DateTimeOffset.Now.AddMinutes(-5)
        }, localization);

        Assert.Contains(localization.T("Todo.Due.OverdueSuffix"), item.DueStatusText, StringComparison.Ordinal);
        Assert.Contains("\u00B7", item.DueStatusText, StringComparison.Ordinal);
    }
}
