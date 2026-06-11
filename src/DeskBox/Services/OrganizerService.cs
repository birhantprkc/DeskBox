using DeskBox.Models;

namespace DeskBox.Services;

public sealed class OrganizerService
{
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly Func<string> _desktopPathProvider;

    public OrganizerService(
        SettingsService settingsService,
        FileService fileService,
        Func<string>? desktopPathProvider = null)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _desktopPathProvider = desktopPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    public IReadOnlyList<OrganizationHistoryEntry> GetRecentHistory(int maxCount = 6)
    {
        return _settingsService.Settings.RecentOrganizationHistory
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Max(0, maxCount))
            .ToList();
    }

    public OrganizationHistoryEntry? GetLatestUndoableEntry()
    {
        return _settingsService.Settings.RecentOrganizationHistory
            .Where(entry => entry.CanUndo && !entry.IsUndone && !entry.IsFailed && entry.Items.Count > 0)
            .OrderByDescending(entry => entry.TimestampUtc)
            .FirstOrDefault();
    }

    public async Task<OrganizationHistoryEntry> OrganizeDropAsync(
        WidgetConfig widget,
        string widgetName,
        IEnumerable<string> sourcePaths,
        bool move,
        bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            throw new InvalidOperationException("This widget does not have a managed folder path.");
        }

        string rootPath = Path.GetFullPath(widget.MappedFolderPath);
        var normalizedSourcePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();

        if (normalizedSourcePaths.Count == 0)
        {
            throw new InvalidOperationException("No items were available to organize.");
        }

        try
        {
            var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plans = normalizedSourcePaths
                .Select(path =>
                {
                    string destinationPath = FileService.GetAvailablePath(
                        Path.Combine(rootPath, Path.GetFileName(path)),
                        reservedPaths);
                    return new FileService.FileTransferPlan(path, destinationPath);
                })
                .ToList();

            var results = await _fileService.ExecuteTransferPlanAsync(plans, move, useShellProgress);
            var historyEntry = CreateHistoryEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.ManagedDrop,
                move,
                results.Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath
                }).ToList(),
                canUndo: move);

            await AddHistoryEntryAsync(historyEntry);
            return historyEntry;
        }
        catch (Exception ex)
        {
            await AddHistoryEntryAsync(CreateFailureEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.ManagedDrop,
                move,
                normalizedSourcePaths,
                ex.Message));
            throw;
        }
    }

    public async Task<OrganizationHistoryEntry> MoveItemBackToDesktopAsync(
        WidgetConfig widget,
        string widgetName,
        WidgetItem item,
        bool useShellProgress = false)
    {
        return await MoveItemsBackToDesktopAsync(widget, widgetName, [item.Path], useShellProgress);
    }

    public async Task<OrganizationHistoryEntry> MoveItemsBackToDesktopAsync(
        WidgetConfig widget,
        string widgetName,
        IEnumerable<string> sourcePaths,
        bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            throw new InvalidOperationException("This widget does not have a folder path.");
        }

        var normalizedSourcePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();
        if (normalizedSourcePaths.Count == 0)
        {
            throw new FileNotFoundException("No items to restore could be found.");
        }

        string desktopPath = _desktopPathProvider();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = normalizedSourcePaths
            .Select(sourcePath => new FileService.FileTransferPlan(
                sourcePath,
                FileService.GetAvailablePath(Path.Combine(desktopPath, Path.GetFileName(sourcePath)), reservedPaths)))
            .ToList();

        try
        {
            var results = await _fileService.ExecuteTransferPlanAsync(plans, move: true, useShellProgress);

            var historyEntry = CreateHistoryEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.MoveBackToDesktop,
                move: true,
                results.Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath
                }).ToList(),
                canUndo: true);

            await AddHistoryEntryAsync(historyEntry);
            return historyEntry;
        }
        catch (Exception ex)
        {
            await AddHistoryEntryAsync(CreateFailureEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.MoveBackToDesktop,
                move: true,
                normalizedSourcePaths,
                ex.Message));
            throw;
        }
    }

    public async Task<bool> UndoLatestAsync()
    {
        var latestEntry = GetLatestUndoableEntry();
        if (latestEntry is null)
        {
            return false;
        }

        await UndoAsync(latestEntry.Id);
        return true;
    }

    public async Task UndoAsync(string historyEntryId)
    {
        var historyEntry = _settingsService.Settings.RecentOrganizationHistory
            .FirstOrDefault(entry => string.Equals(entry.Id, historyEntryId, StringComparison.Ordinal));

        if (historyEntry is null || !historyEntry.CanUndo || historyEntry.IsUndone || historyEntry.IsFailed)
        {
            throw new InvalidOperationException("The selected history entry cannot be undone.");
        }

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<FileService.FileTransferPlan>(historyEntry.Items.Count);

        foreach (var item in historyEntry.Items)
        {
            if (!File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath))
            {
                throw new InvalidOperationException($"Could not find undo target: {item.Name}");
            }

            string restorePath = FileService.GetAvailablePath(item.SourcePath, reservedPaths);
            plans.Add(new FileService.FileTransferPlan(item.DestinationPath, restorePath));
        }

        await _fileService.ExecuteTransferPlanAsync(plans, move: true);

        historyEntry.IsUndone = true;
        historyEntry.CanUndo = false;
        for (int index = 0; index < plans.Count; index++)
        {
            historyEntry.Items[index].DestinationPath = plans[index].DestinationPath;
        }

        await _settingsService.SaveAsync();
    }

    private async Task AddHistoryEntryAsync(OrganizationHistoryEntry entry)
    {
        _settingsService.Settings.RecentOrganizationHistory.Insert(0, entry);
        await _settingsService.SaveAsync();
    }

    private static OrganizationHistoryEntry CreateHistoryEntry(
        string widgetId,
        string widgetName,
        string actionType,
        bool move,
        List<OrganizationHistoryItem> items,
        bool canUndo)
    {
        return new OrganizationHistoryEntry
        {
            WidgetId = widgetId,
            WidgetName = widgetName,
            ActionType = actionType,
            TransferMode = move ? "Move" : "Copy",
            CanUndo = canUndo,
            Items = items
        };
    }

    private static OrganizationHistoryEntry CreateFailureEntry(
        string widgetId,
        string widgetName,
        string actionType,
        bool move,
        IEnumerable<string> sourcePaths,
        string errorMessage)
    {
        return new OrganizationHistoryEntry
        {
            WidgetId = widgetId,
            WidgetName = widgetName,
            ActionType = actionType,
            TransferMode = move ? "Move" : "Copy",
            ErrorMessage = errorMessage,
            Items = sourcePaths
                .Select(path => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(path),
                    SourcePath = path,
                    DestinationPath = string.Empty
                })
                .ToList()
        };
    }
}
