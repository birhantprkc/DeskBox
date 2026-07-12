using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// COM IDropTarget implementation that bridges native OLE drag-drop to .NET events.
/// Replaces the legacy WM_DROPFILES approach, providing real-time drag-over feedback.
/// </summary>
public sealed class NativeDropTarget : IDisposable
{
    // ── COM Interface definitions ──

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect);
    }

    [ComImport]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface COMIDataObject
    {
        [PreserveSig] int GetData(ref FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);
        [PreserveSig] int QueryGetData(ref FORMATETC format);
        [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);
        [PreserveSig] int SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, [MarshalAs(UnmanagedType.Bool)] bool fRelease);
        [PreserveSig] int EnumFormatEtc(uint dwDirection, out IntPtr enumFormatEtc);
        [PreserveSig] int DAdvise(ref FORMATETC format, uint advf, IntPtr pAdvSink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumAdvise);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FORMATETC
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STGMEDIUM
    {
        public uint tymed;
        public IntPtr unionMember;
        public IntPtr pUnkForRelease;
    }

    // ── Constants ──

    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;

    // ── P/Invoke ──

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll")]
    private static extern uint RegisterClipboardFormatA(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, System.Text.StringBuilder? fileName, uint bufferSize);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    private const uint CF_HDROP = 15;

    // ── State ──

    private readonly IntPtr _hwnd;
    private readonly DropTargetComObject _comObject;
    private bool _registered;

    /// <summary>Fired when a drag enters the window. Provides screen coordinates and whether file data is available.</summary>
    public event Action<int, int, bool>? DragEnterEvent;

    /// <summary>Fired as the drag moves over the window. Provides screen coordinates.</summary>
    public event Action<int, int>? DragOverEvent;

    /// <summary>Fired when the drag leaves the window without dropping.</summary>
    public event Action? DragLeaveEvent;

    /// <summary>Fired when files are dropped. Provides the list of file paths and screen coordinates.</summary>
    public event Action<IReadOnlyList<string>, int, int>? DropEvent;

    /// <summary>
    /// Whether the current drag payload contains file drop data (CF_HDROP).
    /// Valid between DragEnter and DragLeave/Drop.
    /// </summary>
    public bool HasFileData { get; private set; }

    static NativeDropTarget()
    {
        // Ensure OLE is initialized (WinUI 3 usually does this, but call
        // again is harmless if already initialized).
        try
        {
            OleInitialize(IntPtr.Zero);
        }
        catch
        {
        }
    }

    public NativeDropTarget(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _comObject = new DropTargetComObject(this);
    }

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            RegisterDragDrop(_hwnd, _comObject);
            _registered = true;
            App.LogVerbose($"[DropTarget] Registered IDropTarget for hwnd=0x{_hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] RegisterDragDrop failed: {ex.Message}");
        }
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            RevokeDragDrop(_hwnd);
        }
        catch
        {
        }
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
    }

    // ── Inner COM object ──

    /// <summary>
    /// The actual COM IDropTarget implementation. .NET creates a CCW
    /// (COM Callable Wrapper) for this class automatically.
    /// </summary>
    [ComVisible(true)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    private sealed class DropTargetComObject : IDropTarget
    {
        private readonly NativeDropTarget _owner;

        public DropTargetComObject(NativeDropTarget owner)
        {
            _owner = owner;
        }

        public int DragEnter(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            _owner.HasFileData = _owner.TryHasFileData(pDataObj);
            _owner.DragEnterEvent?.Invoke(pt.X, pt.Y, _owner.HasFileData);

            pdwEffect = _owner.HasFileData
                ? DROPEFFECT_COPY | DROPEFFECT_MOVE
                : DROPEFFECT_NONE;
            return S_OK;
        }

        public int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            _owner.DragOverEvent?.Invoke(pt.X, pt.Y);

            pdwEffect = _owner.HasFileData
                ? DROPEFFECT_COPY | DROPEFFECT_MOVE
                : DROPEFFECT_NONE;
            return S_OK;
        }

        public int DragLeave()
        {
            _owner.HasFileData = false;
            _owner.DragLeaveEvent?.Invoke();
            return S_OK;
        }

        public int Drop(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            var paths = _owner.TryExtractFilePaths(pDataObj);
            _owner.HasFileData = false;

            if (paths.Count > 0)
            {
                _owner.DropEvent?.Invoke(paths, pt.X, pt.Y);
                pdwEffect = DROPEFFECT_COPY | DROPEFFECT_MOVE;
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
            }

            return S_OK;
        }
    }

    // ── Data extraction helpers ──

    private bool TryHasFileData(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return false;
        }

        COMIDataObject? dataObj = null;
        try
        {
            dataObj = (COMIDataObject)Marshal.GetObjectForIUnknown(pDataObj);
            var format = new FORMATETC
            {
                cfFormat = (ushort)CF_HDROP,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED_HGLOBAL,
            };

            return dataObj.QueryGetData(ref format) == S_OK;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dataObj is not null)
            {
                try { Marshal.ReleaseComObject(dataObj); } catch { }
            }
        }
    }

    private IReadOnlyList<string> TryExtractFilePaths(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return [];
        }

        COMIDataObject? dataObj = null;
        try
        {
            dataObj = (COMIDataObject)Marshal.GetObjectForIUnknown(pDataObj);
            var format = new FORMATETC
            {
                cfFormat = (ushort)CF_HDROP,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED_HGLOBAL,
            };

            int hr = dataObj.GetData(ref format, out STGMEDIUM medium);
            if (hr != S_OK || medium.unionMember == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                return GetDroppedFiles(medium.unionMember);
            }
            finally
            {
                Marshal.FreeHGlobal(medium.unionMember);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to extract file paths: {ex.Message}");
            return [];
        }
        finally
        {
            if (dataObj is not null)
            {
                try { Marshal.ReleaseComObject(dataObj); } catch { }
            }
        }
    }

    private static IReadOnlyList<string> GetDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        try
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(hDrop, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var builder = new System.Text.StringBuilder((int)length + 1);
                uint copied = DragQueryFile(hDrop, i, builder, (uint)builder.Capacity);
                if (copied > 0)
                {
                    paths.Add(builder.ToString());
                }
            }
        }
        finally
        {
            DragFinish(hDrop);
        }
        return paths;
    }
}
