using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DeskBoxAttachmentHealthService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataDirectory;

    public DeskBoxAttachmentHealthService()
        : this(DeskBoxDataPathService.Current.DataDirectory)
    {
    }

    internal DeskBoxAttachmentHealthService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public Task<DeskBoxAttachmentHealthReport> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanCore(cancellationToken), cancellationToken);
    }

    private DeskBoxAttachmentHealthReport ScanCore(CancellationToken cancellationToken)
    {
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingLinkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingManagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unreadableStoreCount = 0;

        string quickCapturePath = Path.Combine(
            _dataDirectory,
            "quick-capture",
            "quick-capture.json");
        if (File.Exists(quickCapturePath))
        {
            try
            {
                QuickCaptureStoreData data = ReadJson<QuickCaptureStoreData>(quickCapturePath);
                AddAttachments(
                    (data.Items ?? []).Concat(data.RecentItems ?? [])
                    .SelectMany(item => item.Attachments ?? []),
                    referencedPaths,
                    missingLinkedPaths,
                    missingManagedPaths);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadableStoreCount++;
                App.Log($"[AttachmentHealth] Failed to read '{quickCapturePath}': {ex.Message}");
            }
        }

        string widgetsDirectory = Path.Combine(_dataDirectory, "widgets");
        if (Directory.Exists(widgetsDirectory))
        {
            foreach (string todoPath in Directory.EnumerateFiles(
                         widgetsDirectory,
                         "todo.json",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    TodoWidgetData data = ReadJson<TodoWidgetData>(todoPath);
                    AddAttachments(
                        (data.Items ?? []).SelectMany(item => item.Attachments ?? []),
                        referencedPaths,
                        missingLinkedPaths,
                        missingManagedPaths);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadableStoreCount++;
                    App.Log($"[AttachmentHealth] Failed to read '{todoPath}': {ex.Message}");
                }
            }
        }

        var orphanManagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string managedDirectory in EnumerateManagedAttachmentDirectories(widgetsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string filePath in Directory.EnumerateFiles(
                         managedDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                string normalizedPath = Path.GetFullPath(filePath);
                if (!referencedPaths.Contains(normalizedPath))
                {
                    orphanManagedPaths.Add(normalizedPath);
                }
            }
        }

        return new DeskBoxAttachmentHealthReport(
            referencedPaths.Count,
            missingLinkedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            missingManagedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            orphanManagedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            unreadableStoreCount);
    }

    private IEnumerable<string> EnumerateManagedAttachmentDirectories(string widgetsDirectory)
    {
        string quickCaptureAttachments = Path.Combine(_dataDirectory, "quick-capture", "attachments");
        if (Directory.Exists(quickCaptureAttachments))
        {
            yield return quickCaptureAttachments;
        }

        if (!Directory.Exists(widgetsDirectory))
        {
            yield break;
        }

        foreach (string widgetDirectory in Directory.EnumerateDirectories(widgetsDirectory))
        {
            string attachmentDirectory = Path.Combine(widgetDirectory, "attachments");
            if (Directory.Exists(attachmentDirectory))
            {
                yield return attachmentDirectory;
            }
        }
    }

    private static T ReadJson<T>(string path)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), s_jsonOptions) ??
               throw new JsonException("The JSON document contains null.");
    }

    private static void AddAttachments(
        IEnumerable<TodoAttachment> attachments,
        HashSet<string> referencedPaths,
        HashSet<string> missingLinkedPaths,
        HashSet<string> missingManagedPaths)
    {
        foreach (TodoAttachment attachment in attachments.Where(attachment =>
                     attachment is not null && !string.IsNullOrWhiteSpace(attachment.FilePath)))
        {
            string path;
            try
            {
                path = Path.GetFullPath(attachment.FilePath);
            }
            catch
            {
                path = attachment.FilePath;
            }

            referencedPaths.Add(path);
            if (File.Exists(path))
            {
                continue;
            }

            if (attachment.IsManagedCopy)
            {
                missingManagedPaths.Add(path);
            }
            else
            {
                missingLinkedPaths.Add(path);
            }
        }
    }
}

public sealed record DeskBoxAttachmentHealthReport(
    int ReferencedFileCount,
    IReadOnlyList<string> MissingLinkedFiles,
    IReadOnlyList<string> MissingManagedFiles,
    IReadOnlyList<string> OrphanManagedFiles,
    int UnreadableStoreCount)
{
    public bool IsHealthy =>
        MissingLinkedFiles.Count == 0 &&
        MissingManagedFiles.Count == 0 &&
        OrphanManagedFiles.Count == 0 &&
        UnreadableStoreCount == 0;
}
