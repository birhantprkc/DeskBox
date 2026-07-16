using System.IO.Compression;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DeskBoxDataBackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataRoot;
    private readonly string _exportRoot;

    public DeskBoxDataBackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _appDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "app-data")).FullName;
        _exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "exports")).FullName;
    }

    [Fact]
    public async Task ExportBackupAsync_IncludesManifestDataAndNestedAttachments()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string attachmentDirectory = Directory.CreateDirectory(
            Path.Combine(dataDirectory, "widgets", "todo", "attachments")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{\"language\":\"en-US\"}");
        await File.WriteAllBytesAsync(Path.Combine(attachmentDirectory, "spec.pdf"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "ignored.tmp"), "partial");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string backupPath = await service.ExportBackupAsync(_exportRoot);

        Assert.True(File.Exists(backupPath));
        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("data/settings.json"));
        Assert.NotNull(archive.GetEntry("data/widgets/todo/attachments/spec.pdf"));
        Assert.Null(archive.GetEntry("data/ignored.tmp"));
        ZipArchiveEntry manifestEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("manifest.json"));
        using Stream manifestStream = manifestEntry.Open();
        using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("manual", manifest.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task CreateAutomaticSnapshotIfDueAsync_CreatesOnlyOneSnapshotPerDay()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string? firstPath = await service.CreateAutomaticSnapshotIfDueAsync();
        string? secondPath = await service.CreateAutomaticSnapshotIfDueAsync();

        Assert.NotNull(firstPath);
        Assert.True(File.Exists(firstPath));
        Assert.Null(secondPath);
        Assert.Single(Directory.EnumerateFiles(service.AutomaticSnapshotDirectory, "DeskBox-Auto-*.zip"));
    }

    [Fact]
    public async Task CreateAutomaticSnapshotIfDueAsync_ReturnsNullWhenThereIsNoData()
    {
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string? snapshotPath = await service.CreateAutomaticSnapshotIfDueAsync();

        Assert.Null(snapshotPath);
        Assert.False(Directory.Exists(service.AutomaticSnapshotDirectory));
    }

    [Fact]
    public async Task PrepareAndApplyRestore_ReplacesDataAndRebasesManagedAttachments()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-app-data")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sourceData, "settings.json"), "{\"language\":\"zh-CN\"}");
        string sourceQuickCapture = Directory.CreateDirectory(
            Path.Combine(sourceData, "quick-capture")).FullName;
        string sourceAttachment = Path.Combine(
            sourceQuickCapture,
            "attachments",
            "note",
            "image.png");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAttachment)!);
        await File.WriteAllBytesAsync(sourceAttachment, [1, 2, 3, 4]);
        var sourceStore = new QuickCaptureStore(sourceQuickCapture);
        await sourceStore.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note",
                    Body = "Restored note",
                    ImagePath = sourceAttachment,
                    Attachments =
                    [
                        new TodoAttachment
                        {
                            Id = "image",
                            FilePath = sourceAttachment,
                            DisplayName = "image.png",
                            Type = "image",
                            StorageMode = TodoAttachment.ManagedStorageMode
                        }
                    ]
                }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportBackupAsync(_exportRoot);

        string targetData = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(targetData, "settings.json"), "{\"language\":\"en-US\"}");
        var targetService = new DeskBoxDataBackupService(_appDataRoot);

        DeskBoxRestorePreparation preparation = await targetService.PrepareRestoreAsync(backupPath);
        DeskBoxRestoreApplyResult result = await targetService.ApplyPendingRestoreAsync();

        Assert.True(preparation.FileCount >= 3);
        Assert.True(result.HadPendingRestore);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(File.Exists(targetService.PendingRestoreMarkerPath));
        string restoredAttachment = Path.Combine(
            targetData,
            "quick-capture",
            "attachments",
            "note",
            "image.png");
        Assert.True(File.Exists(restoredAttachment));
        QuickCaptureStoreData restored = await new QuickCaptureStore(
            Path.Combine(targetData, "quick-capture")).LoadAsync();
        QuickCaptureItem restoredItem = Assert.Single(restored.Items);
        Assert.Equal(restoredAttachment, restoredItem.ImagePath, ignoreCase: true);
        Assert.Equal(
            restoredAttachment,
            Assert.Single(restoredItem.Attachments).FilePath,
            ignoreCase: true);
        Assert.Single(Directory.EnumerateFiles(
            targetService.PreRestoreBackupDirectory,
            "DeskBox-PreRestore-*.zip"));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsPathTraversalWithoutLeavingPendingRestore()
    {
        string archivePath = Path.Combine(_exportRoot, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/../outside.txt", "unsafe");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
        Assert.False(File.Exists(Path.Combine(_appDataRoot, "outside.txt")));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsInvalidCoreJson()
    {
        string archivePath = Path.Combine(_exportRoot, "invalid-json.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/settings.json", "{ invalid json");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
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
}
