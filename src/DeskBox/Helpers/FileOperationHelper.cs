using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Modern file operations using the IFileOperation COM interface (Vista+),
/// replacing the deprecated SHFileOperation API.
/// </summary>
public static class FileOperationHelper
{
    // ── Flags (same values as FOF_*, passed to SetOperationFlags) ──

    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;

    // ── GUIDs ──

    private static readonly Guid s_clsid_FileOperation =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid s_iid_IFileOperation =
        new("947AAB5F-0A5C-47C3-9F80-DAF05E6A3F72");
    private static readonly Guid s_iid_IShellItem =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    // ── P/Invoke ──

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        [In] ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IFileOperation ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    // ── Public API ──

    /// <summary>
    /// Send a file or directory to the Recycle Bin.
    /// </summary>
    public static void DeleteToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var fileOp = CreateFileOperation(
            FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT);

        IShellItem item = CreateShellItem(path);
        try
        {
            fileOp.DeleteItem(item, IntPtr.Zero);
            fileOp.PerformOperations();
            fileOp.GetAnyOperationsAborted(out bool aborted);
            if (aborted)
            {
                App.LogVerbose("[FileOperation] Delete to recycle bin aborted by user");
            }
        }
        catch (COMException ex)
        {
            // 0x800704C7 = ERROR_CANCELLED (user cancelled)
            if (ex.ErrorCode != unchecked((int)0x800704C7))
            {
                throw new Win32Exception(ex.ErrorCode);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(fileOp);
        }
    }

    /// <summary>
    /// Move files and/or directories to their destination paths with the
    /// native shell progress dialog.  All operations are submitted as a
    /// single batch and executed atomically via PerformOperations().
    /// </summary>
    public static void MoveItemsWithProgress(IReadOnlyList<(string Source, string Destination)> operations)
    {
        if (operations.Count == 0)
        {
            return;
        }

        // Ensure destination directories exist
        foreach (var (_, destination) in operations)
        {
            string? destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
        }

        var fileOp = CreateFileOperation(FOF_NOCONFIRMMKDIR);

        var items = new List<(IShellItem Source, IShellItem DestFolder, string NewName)>(operations.Count);
        try
        {
            foreach (var (source, destination) in operations)
            {
                IShellItem sourceItem = CreateShellItem(source);
                IShellItem destFolder = CreateShellItem(
                    Path.GetDirectoryName(destination) ?? string.Empty);
                string newName = Path.GetFileName(destination);

                fileOp.MoveItem(sourceItem, destFolder, newName, IntPtr.Zero);
                items.Add((sourceItem, destFolder, newName));
            }

            try
            {
                fileOp.PerformOperations();
                fileOp.GetAnyOperationsAborted(out bool aborted);
                if (aborted)
                {
                    App.LogVerbose("[FileOperation] Move aborted by user");
                }
            }
            catch (COMException ex)
            {
                // 0x800704C7 = ERROR_CANCELLED
                if (ex.ErrorCode != unchecked((int)0x800704C7))
                {
                    throw new Win32Exception(ex.ErrorCode);
                }
            }
        }
        finally
        {
            foreach (var (source, destFolder, _) in items)
            {
                Marshal.ReleaseComObject(source);
                Marshal.ReleaseComObject(destFolder);
            }
            Marshal.ReleaseComObject(fileOp);
        }
    }

    // ── Private helpers ──

    private static IFileOperation CreateFileOperation(uint flags)
    {
        Guid clsid = s_clsid_FileOperation;
        Guid iid = s_iid_IFileOperation;
        CoCreateInstance(
            ref clsid,
            IntPtr.Zero,
            1, // CLSCTX_INPROC_SERVER
            ref iid,
            out IFileOperation fileOp);

        fileOp.SetOperationFlags(flags);
        return fileOp;
    }

    private static IShellItem CreateShellItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or whitespace.", nameof(path));
        }

        Guid iid = s_iid_IShellItem;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        return item;
    }

    // ── COM Interface Definitions ──

    [ComImport]
    [Guid("947AAB5F-0A5C-47C3-9F80-DAF05E6A3F72")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndParent);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsIf);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsIf);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsIf);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsIf);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsIf);
        void PerformOperations();
        void GetAnyOperationsAborted(out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, [In] uint hint, out int piOrder);
    }
}
