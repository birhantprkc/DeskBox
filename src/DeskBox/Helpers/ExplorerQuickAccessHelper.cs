using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

public enum QuickAccessPinState
{
    Unknown,
    NotPinned,
    Pinned
}

/// <summary>
/// Shell helpers for exposing DeskBox folders in File Explorer entry points.
/// </summary>
public static class ExplorerQuickAccessHelper
{
    private const string QuickAccessNamespace = "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
    private const string PinVerb = "pintohome";
    private const string UnpinVerb = "unpinfromhome";
    private const string IsPinnedProperty = "System.IsPinnedToNameSpaceTree";

    public static QuickAccessPinState GetQuickAccessPinState(string folderPath, out string? error)
    {
        error = null;

        if (!TryNormalizeFolderPath(folderPath, out string fullPath, out error))
        {
            return QuickAccessPinState.Unknown;
        }

        if (!Directory.Exists(fullPath))
        {
            return QuickAccessPinState.NotPinned;
        }

        try
        {
            if (!TryCreateShellApplication(out object? shellObject, out error) ||
                shellObject is null)
            {
                return QuickAccessPinState.Unknown;
            }

            dynamic shell = shellObject;
            dynamic? quickAccessFolder = shell.NameSpace(QuickAccessNamespace);
            if (quickAccessFolder is null)
            {
                error = "Quick Access namespace is unavailable.";
                return QuickAccessPinState.Unknown;
            }

            foreach (dynamic item in quickAccessFolder.Items())
            {
                if (!TryGetFolderItemPath(item, out string itemPath) ||
                    !PathsEqual(itemPath, fullPath))
                {
                    continue;
                }

                return TryGetPinnedProperty(item, out bool isPinned)
                    ? isPinned ? QuickAccessPinState.Pinned : QuickAccessPinState.NotPinned
                    : QuickAccessPinState.Unknown;
            }

            return QuickAccessPinState.NotPinned;
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return QuickAccessPinState.Unknown;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return QuickAccessPinState.Unknown;
        }
    }

    public static bool TryPinFolderToQuickAccess(string folderPath, out string? error)
    {
        return TryInvokeFolderVerb(folderPath, PinVerb, createFolder: true, out error);
    }

    public static bool TryUnpinFolderFromQuickAccess(string folderPath, out string? error)
    {
        error = null;

        QuickAccessPinState state = GetQuickAccessPinState(folderPath, out error);
        if (state == QuickAccessPinState.NotPinned)
        {
            error = null;
            return true;
        }

        try
        {
            if (!TryNormalizeFolderPath(folderPath, out string fullPath, out error) ||
                !TryCreateShellApplication(out object? shellObject, out error) ||
                shellObject is null)
            {
                return false;
            }

            dynamic shell = shellObject;
            dynamic? quickAccessFolder = shell.NameSpace(QuickAccessNamespace);
            if (quickAccessFolder is not null)
            {
                foreach (dynamic item in quickAccessFolder.Items())
                {
                    if (TryGetFolderItemPath(item, out string itemPath) &&
                        PathsEqual(itemPath, fullPath))
                    {
                        item.InvokeVerb(UnpinVerb);
                        return true;
                    }
                }
            }

            return TryInvokeFolderVerb(folderPath, UnpinVerb, createFolder: false, out error);
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryInvokeFolderVerb(string folderPath, string verb, bool createFolder, out string? error)
    {
        error = null;

        try
        {
            if (!TryCreateShellApplication(out object? shellObject, out error) ||
                shellObject is null ||
                !TryGetFolderItem(shellObject, folderPath, createFolder, out object? folderItemObject, out error) ||
                folderItemObject is null)
            {
                return false;
            }

            dynamic folderItem = folderItemObject;
            folderItem.InvokeVerb(verb);
            return true;
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCreateShellApplication(out object? shell, out string? error)
    {
        shell = null;
        error = null;

        Type? shellApplicationType = Type.GetTypeFromProgID("Shell.Application");
        if (shellApplicationType is null)
        {
            error = "Shell.Application is unavailable.";
            return false;
        }

        shell = Activator.CreateInstance(shellApplicationType);
        if (shell is null)
        {
            error = "Could not create Shell.Application.";
            return false;
        }

        return true;
    }

    private static bool TryGetFolderItem(
        object shellObject,
        string folderPath,
        bool createFolder,
        out object? folderItem,
        out string? error)
    {
        folderItem = null;
        error = null;

        if (!TryNormalizeFolderPath(folderPath, out string fullPath, out error))
        {
            return false;
        }

        if (createFolder)
        {
            Directory.CreateDirectory(fullPath);
        }
        else if (!Directory.Exists(fullPath))
        {
            error = "Folder does not exist.";
            return false;
        }

        string? parentPath = Path.GetDirectoryName(fullPath);
        string folderName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(folderName))
        {
            error = "Folder path is invalid.";
            return false;
        }

        dynamic shell = shellObject;
        dynamic? parentFolder = shell.NameSpace(parentPath);
        folderItem = parentFolder?.ParseName(folderName);
        if (folderItem is null)
        {
            error = "Could not locate the folder in File Explorer.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeFolderPath(string folderPath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            error = "Folder path is empty.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(folderPath);
            string trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(trimmedPath) && !string.Equals(trimmedPath, Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                fullPath = trimmedPath;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetFolderItemPath(dynamic item, out string path)
    {
        path = string.Empty;

        try
        {
            string? itemPath = item.Path;
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return false;
            }

            path = Path.GetFullPath(itemPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPinnedProperty(dynamic item, out bool isPinned)
    {
        isPinned = false;

        try
        {
            object? value = item.ExtendedProperty(IsPinnedProperty);
            switch (value)
            {
                case bool boolValue:
                    isPinned = boolValue;
                    return true;
                case int intValue:
                    isPinned = intValue != 0;
                    return true;
                case string stringValue when bool.TryParse(stringValue, out bool boolValue):
                    isPinned = boolValue;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
