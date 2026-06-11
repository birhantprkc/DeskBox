using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class OrganizerServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _desktopRoot;
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;

    public OrganizerServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;

        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _fileService = new FileService();
        _organizerService = new OrganizerService(_settingsService, _fileService, () => _desktopRoot);
    }

    [Fact]
    public async Task OrganizeDropAsync_Move_RecordsUndoableHistoryAndMovesFile()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);

        var history = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: true);

        string destinationPath = Path.Combine(targetDirectory, "note.txt");
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.True(history.CanUndo);
        Assert.False(history.IsFailed);
        Assert.Equal(OrganizationActionType.ManagedDrop, history.ActionType);
        Assert.Equal("Move", history.TransferMode);
        var item = Assert.Single(history.Items);
        Assert.Equal(sourcePath, item.SourcePath);
        Assert.Equal(destinationPath, item.DestinationPath);
        Assert.Same(history, Assert.Single(_settingsService.Settings.RecentOrganizationHistory));
    }

    [Fact]
    public async Task OrganizeDropAsync_Copy_RecordsNonUndoableHistoryAndKeepsSourceFile()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);

        var history = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: false);

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(Path.Combine(targetDirectory, "note.txt")));
        Assert.False(history.CanUndo);
        Assert.Equal("Copy", history.TransferMode);
    }

    [Fact]
    public async Task UndoLatestAsync_RestoresMovedFileAndMarksHistoryUndone()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);
        var history = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: true);

        bool undone = await _organizerService.UndoLatestAsync();

        Assert.True(undone);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(targetDirectory, "note.txt")));
        Assert.True(history.IsUndone);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task MoveItemBackToDesktopAsync_RejectsWidgetsWithoutMappedFolder()
    {
        var widget = CreateWidget(string.Empty, followsDefaultStoragePath: false);
        var item = new WidgetItem
        {
            Path = Path.Combine(_tempRoot, "missing.txt"),
            Name = "missing"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _organizerService.MoveItemBackToDesktopAsync(widget, "Widget", item));
    }

    [Fact]
    public async Task MoveItemBackToDesktopAsync_AllowsMappedFolderWidgets()
    {
        string widgetFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        string sourcePath = Path.Combine(widgetFolder, "mapped-note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(widgetFolder, followsDefaultStoragePath: false);
        var item = new WidgetItem
        {
            Path = sourcePath,
            Name = "mapped-note"
        };

        var history = await _organizerService.MoveItemBackToDesktopAsync(widget, "Widget", item);

        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(history.Items.Single().DestinationPath));
        Assert.Equal(_desktopRoot, Path.GetDirectoryName(history.Items.Single().DestinationPath));
    }

    private static WidgetConfig CreateWidget(string folderPath, bool followsDefaultStoragePath = true)
    {
        return new WidgetConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Widget",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = followsDefaultStoragePath,
            ManagedFolderName = Path.GetFileName(folderPath)
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
