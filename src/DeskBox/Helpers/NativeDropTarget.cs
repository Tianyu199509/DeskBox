using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskBox.Services;

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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SIZEL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public SIZEL sizel;
        public POINTL pointl;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    // ── Constants ──

    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const uint TYMED_ISTREAM = 4;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    // ── P/Invoke ──

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, System.Text.StringBuilder? fileName, uint bufferSize);

    private const uint CF_HDROP = 15;
    private static readonly ushort s_fileGroupDescriptorFormat;
    private static readonly ushort s_fileContentsFormat;

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
    public event Action<IReadOnlyList<string>, int, int, bool>? DropEvent;

    /// <summary>
    /// Whether the current drag payload contains file drop data (CF_HDROP).
    /// Valid between DragEnter and DragLeave/Drop.
    /// </summary>
    public bool HasFileData { get; private set; }

    public bool HasVirtualFileData { get; private set; }

    static NativeDropTarget()
    {
        s_fileGroupDescriptorFormat = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW");
        s_fileContentsFormat = (ushort)RegisterClipboardFormatW("FileContents");

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
            App.Log($"[DropTarget] Registered IDropTarget for hwnd=0x{_hwnd.ToInt64():X}");
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
            _owner.HasVirtualFileData = _owner.TryHasVirtualFileData(pDataObj);
            _owner.HasFileData = _owner.HasVirtualFileData || _owner.TryHasHDropData(pDataObj);
            _owner.DragEnterEvent?.Invoke(pt.X, pt.Y, _owner.HasFileData);

            pdwEffect = _owner.HasFileData
                ? _owner.HasVirtualFileData ? DROPEFFECT_COPY : DROPEFFECT_COPY | DROPEFFECT_MOVE
                : DROPEFFECT_NONE;
            return S_OK;
        }

        public int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            _owner.DragOverEvent?.Invoke(pt.X, pt.Y);

            pdwEffect = _owner.HasFileData
                ? _owner.HasVirtualFileData ? DROPEFFECT_COPY : DROPEFFECT_COPY | DROPEFFECT_MOVE
                : DROPEFFECT_NONE;
            return S_OK;
        }

        public int DragLeave()
        {
            _owner.HasFileData = false;
            _owner.HasVirtualFileData = false;
            _owner.DragLeaveEvent?.Invoke();
            return S_OK;
        }

        public int Drop(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            var (paths, containsTemporaryFiles) = _owner.TryExtractFilePaths(pDataObj);
            _owner.HasFileData = false;
            _owner.HasVirtualFileData = false;

            // Always log so we can tell whether the native OLE drop target receives
            // CF_HDROP drops (WeChat / Explorer) at all, and what it extracted.
            App.Log($"[DropTarget] NativeDrop received count={paths.Count} temp={containsTemporaryFiles}");

            if (paths.Count > 0)
            {
                _owner.DropEvent?.Invoke(paths, pt.X, pt.Y, containsTemporaryFiles);
                pdwEffect = containsTemporaryFiles
                    ? DROPEFFECT_COPY
                    : DROPEFFECT_COPY | DROPEFFECT_MOVE;
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
            }

            return S_OK;
        }
    }

    // ── Data extraction helpers ──

    private bool TryHasHDropData(IntPtr pDataObj)
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

    private bool TryHasVirtualFileData(IntPtr pDataObj)
    {
        return TryQueryFormat(pDataObj, s_fileGroupDescriptorFormat, TYMED_HGLOBAL, -1);
    }

    private static bool TryQueryFormat(IntPtr pDataObj, ushort clipboardFormat, uint tymed, int index)
    {
        if (pDataObj == IntPtr.Zero || clipboardFormat == 0)
        {
            return false;
        }

        COMIDataObject? dataObj = null;
        try
        {
            dataObj = (COMIDataObject)Marshal.GetObjectForIUnknown(pDataObj);
            var format = new FORMATETC
            {
                cfFormat = clipboardFormat,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT_CONTENT,
                lindex = index,
                tymed = tymed,
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

    private (IReadOnlyList<string> Paths, bool ContainsTemporaryFiles) TryExtractFilePaths(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return ([], false);
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
            if (hr == S_OK && medium.unionMember != IntPtr.Zero)
            {
                try
                {
                    IReadOnlyList<string> paths = GetDroppedFiles(medium.unionMember);
                    if (paths.Count > 0)
                    {
                        return (paths, false);
                    }
                }
                finally
                {
                    ReleaseStgMedium(ref medium);
                }
            }

            IReadOnlyList<string> virtualPaths = ExtractVirtualFiles(dataObj);
            return (virtualPaths, virtualPaths.Count > 0);
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to extract file paths: {ex.Message}");
            return ([], false);
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

        return paths;
    }

    private static IReadOnlyList<string> ExtractVirtualFiles(COMIDataObject dataObj)
    {
        var descriptorFormat = new FORMATETC
        {
            cfFormat = s_fileGroupDescriptorFormat,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };
        if (dataObj.GetData(ref descriptorFormat, out STGMEDIUM descriptorMedium) != S_OK ||
            descriptorMedium.unionMember == IntPtr.Zero)
        {
            return [];
        }

        List<FILEDESCRIPTORW> descriptors;
        try
        {
            descriptors = ReadVirtualFileDescriptors(descriptorMedium.unionMember);
        }
        finally
        {
            ReleaseStgMedium(ref descriptorMedium);
        }

        if (descriptors.Count == 0)
        {
            return [];
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>();
        for (int index = 0; index < descriptors.Count; index++)
        {
            FILEDESCRIPTORW descriptor = descriptors[index];
            if ((descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }

            string fileName = FileService.SanitizeFileSystemName(
                Path.GetFileName(descriptor.cFileName));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"Dropped file {index + 1}";
            }

            string destinationPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, fileName));
            if (TrySaveVirtualFileContents(dataObj, index, destinationPath))
            {
                paths.Add(destinationPath);
            }
        }

        if (paths.Count == 0)
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }

        return paths;
    }

    private static List<FILEDESCRIPTORW> ReadVirtualFileDescriptors(IntPtr descriptorHandle)
    {
        IntPtr pointer = GlobalLock(descriptorHandle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = Marshal.ReadInt32(pointer);
            if (count <= 0 || count > 4096)
            {
                return [];
            }

            int descriptorSize = Marshal.SizeOf<FILEDESCRIPTORW>();
            var descriptors = new List<FILEDESCRIPTORW>(count);
            IntPtr descriptorPointer = IntPtr.Add(pointer, sizeof(uint));
            for (int index = 0; index < count; index++)
            {
                descriptors.Add(Marshal.PtrToStructure<FILEDESCRIPTORW>(
                    IntPtr.Add(descriptorPointer, index * descriptorSize)));
            }

            return descriptors;
        }
        finally
        {
            GlobalUnlock(descriptorHandle);
        }
    }

    private static bool TrySaveVirtualFileContents(
        COMIDataObject dataObj,
        int index,
        string destinationPath)
    {
        // Try TYMED_ISTREAM first — FileContents from browser drag sources
        // (Chrome / Edge / Firefox) is strictly an IStream. Asking for the
        // combined mask TYMED_ISTREAM | TYMED_HGLOBAL in one FORMATETC is
        // unreliable across OLE sources: some reject the combined mask
        // outright (returning DV_E_FORMATETC), which made the widget silently
        // ignore browser drops even though DragEnter/Drop fired. Fall back to
        // TYMED_HGLOBAL for sources that provide the contents as a memory blob.
        STGMEDIUM contentsMedium = default;
        uint actualTymed = 0;
        IntPtr actualMedium = IntPtr.Zero;
        foreach (uint tymed in new uint[] { TYMED_ISTREAM, TYMED_HGLOBAL })
        {
            var contentsFormat = new FORMATETC
            {
                cfFormat = s_fileContentsFormat,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT_CONTENT,
                lindex = index,
                tymed = tymed,
            };
            int hr = dataObj.GetData(ref contentsFormat, out contentsMedium);
            if (hr == S_OK && contentsMedium.unionMember != IntPtr.Zero)
            {
                actualTymed = contentsMedium.tymed;
                actualMedium = contentsMedium.unionMember;
                break;
            }
            // Release any partially populated medium before retrying with a
            // different tymed (GetData may have set unionMember on failure).
            if (contentsMedium.unionMember != IntPtr.Zero)
            {
                ReleaseStgMedium(ref contentsMedium);
                contentsMedium = default;
            }
        }

        if (actualMedium == IntPtr.Zero)
        {
            App.Log(
                $"[DropTarget] No FileContents payload for virtual file index={index} " +
                $"destination='{destinationPath}' (browser may not have provided a stream)");
            return false;
        }

        try
        {
            if ((actualTymed & TYMED_ISTREAM) != 0)
            {
                SaveComStream(actualMedium, destinationPath);
                return true;
            }

            if ((actualTymed & TYMED_HGLOBAL) != 0)
            {
                SaveGlobalMemory(actualMedium, destinationPath);
                return true;
            }

            App.Log(
                $"[DropTarget] Unexpected FileContents tymed=0x{actualTymed:X} " +
                $"for index={index}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to save virtual file '{destinationPath}': {ex.Message}");
            try { File.Delete(destinationPath); } catch { }
            return false;
        }
        finally
        {
            ReleaseStgMedium(ref contentsMedium);
        }
    }

    private static void SaveComStream(IntPtr streamPointer, string destinationPath)
    {
        var source = (IStream)Marshal.GetObjectForIUnknown(streamPointer);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        IntPtr bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(bytesReadPointer, 0);
                source.Read(buffer, buffer.Length, bytesReadPointer);
                int bytesRead = Marshal.ReadInt32(bytesReadPointer);
                if (bytesRead <= 0)
                {
                    break;
                }

                destination.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesReadPointer);
        }
    }

    private static void SaveGlobalMemory(IntPtr memoryHandle, string destinationPath)
    {
        long size = GlobalSize(memoryHandle).ToInt64();
        if (size < 0 || size > int.MaxValue)
        {
            throw new IOException("Virtual file memory payload is too large.");
        }

        IntPtr pointer = GlobalLock(memoryHandle);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Could not lock virtual file memory payload.");
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            File.WriteAllBytes(destinationPath, bytes);
        }
        finally
        {
            GlobalUnlock(memoryHandle);
        }
    }
}
