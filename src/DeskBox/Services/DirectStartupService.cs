using Microsoft.Win32;

namespace DeskBox.Services;

public sealed class DirectStartupService : IStartupService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DeskBox";

    public bool IsEnabled()
    {
        try
        {
            return GetRunValue() is not null;
        }
        catch
        {
            return false;
        }
    }

    public string? GetRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(AppName) as string;
        }
        catch
        {
            return null;
        }
    }

    public void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.SetValue(AppName, $"\"{exePath}\" --startup");

            // Clean up any legacy startup folder shortcut from previous versions.
            TryRemoveLegacyStartupShortcut();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectStartupService] Failed to enable startup: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.DeleteValue(AppName, false);

            // Clean up any legacy startup folder shortcut from previous versions.
            TryRemoveLegacyStartupShortcut();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectStartupService] Failed to disable startup: {ex.Message}");
        }
    }

    private static void TryRemoveLegacyStartupShortcut()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = System.IO.Path.Combine(startupFolder, AppName + ".lnk");
            if (System.IO.File.Exists(shortcutPath))
            {
                System.IO.File.Delete(shortcutPath);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore failures.
        }
    }

    /// <summary>
    /// Removes the legacy startup folder shortcut if it exists.
    /// Called once at app startup to clean up entries from older versions.
    /// </summary>
    internal static void TryRemoveLegacyStartupShortcutSafe()
    {
        try
        {
            TryRemoveLegacyStartupShortcut();
        }
        catch
        {
            // Best-effort cleanup; ignore failures.
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
            return;
        }

        Disable();
    }
}
