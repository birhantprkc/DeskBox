using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

public static class ShellClipboardHelper
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;

    private static readonly uint PreferredDropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

    public static bool TrySetFileDropList(IReadOnlyList<string> paths, bool cut)
    {
        var validPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validPaths.Length == 0 || PreferredDropEffectFormat == 0)
        {
            return false;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        IntPtr dropHandle = IntPtr.Zero;
        IntPtr effectHandle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            dropHandle = CreateDropFilesHandle(validPaths);
            effectHandle = CreateDropEffectHandle(cut ? DropEffectMove : DropEffectCopy);

            if (SetClipboardData(CfHdrop, dropHandle) == IntPtr.Zero)
            {
                return false;
            }

            dropHandle = IntPtr.Zero;

            if (SetClipboardData(PreferredDropEffectFormat, effectHandle) == IntPtr.Zero)
            {
                return false;
            }

            effectHandle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (dropHandle != IntPtr.Zero)
            {
                GlobalFree(dropHandle);
            }

            if (effectHandle != IntPtr.Zero)
            {
                GlobalFree(effectHandle);
            }

            CloseClipboard();
        }
    }

    private static unsafe IntPtr CreateDropFilesHandle(IReadOnlyList<string> paths)
    {
        string pathList = string.Join('\0', paths) + "\0\0";
        byte[] pathBytes = Encoding.Unicode.GetBytes(pathList);
        int headerSize = Marshal.SizeOf<DropFiles>();
        nuint totalSize = (nuint)(headerSize + pathBytes.Length);
        IntPtr handle = GlobalAlloc(GmemMoveable | GmemZeroinit, totalSize);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            var dropFiles = new DropFiles
            {
                FilesOffset = (uint)headerSize,
                Wide = true
            };
            Marshal.StructureToPtr(dropFiles, pointer, false);
            Marshal.Copy(pathBytes, 0, pointer + headerSize, pathBytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static IntPtr CreateDropEffectHandle(uint effect)
    {
        IntPtr handle = GlobalAlloc(GmemMoveable | GmemZeroinit, sizeof(uint));
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardAllocate"));
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException(Localize("Widget.Error.ClipboardWrite"));
        }

        try
        {
            Marshal.WriteInt32(pointer, unchecked((int)effect));
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static string Localize(string key)
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService?.T(key) ?? key;
        }
        catch
        {
            return key;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint FilesOffset;
        public int X;
        public int Y;
        public bool NonClient;
        public bool Wide;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memoryHandle);
}
