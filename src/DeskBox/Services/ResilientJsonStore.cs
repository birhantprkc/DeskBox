namespace DeskBox.Services;

internal static class ResilientJsonStore
{
    internal static string GetBackupPath(string storePath) => $"{storePath}.bak";

    public static async Task<T> LoadAsync<T>(
        string storePath,
        Func<string, T> deserialize,
        Func<T> createDefault,
        string logName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(createDefault);

        if (File.Exists(storePath))
        {
            try
            {
                return deserialize(await File.ReadAllTextAsync(storePath));
            }
            catch (Exception ex)
            {
                App.Log($"[{logName}] Primary store is invalid: {ex}");
                QuarantineCorruptFile(storePath, logName);
            }
        }

        string backupPath = GetBackupPath(storePath);
        if (!File.Exists(backupPath))
        {
            return createDefault();
        }

        try
        {
            string backupJson = await File.ReadAllTextAsync(backupPath);
            T recovered = deserialize(backupJson);
            await RestorePrimaryAsync(storePath, backupJson);
            App.Log($"[{logName}] Restored store from '{backupPath}'.");
            return recovered;
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Backup store is invalid: {ex}");
            return createDefault();
        }
    }

    public static async Task SaveAsync(string storePath, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(json);

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            if (File.Exists(storePath))
            {
                File.Replace(
                    tempPath,
                    storePath,
                    GetBackupPath(storePath),
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, storePath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void QuarantineCorruptFile(string storePath, string logName)
    {
        string corruptPath = $"{storePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(storePath, corruptPath);
            App.Log($"[{logName}] Preserved corrupt store as '{corruptPath}'.");
        }
        catch (Exception ex)
        {
            App.Log($"[{logName}] Failed to quarantine corrupt store: {ex}");
        }
    }

    private static async Task RestorePrimaryAsync(string storePath, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string tempPath = $"{storePath}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
