using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Helpers;

/// <summary>
/// Shows the native Windows Explorer context menu for a file or folder using
/// COM interop with IContextMenu / IContextMenu2 / IContextMenu3.
/// </summary>
public static class ShellContextMenuHelper
{
    // ─── P/Invoke: shell32 ───

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl,
        [In] ref Guid riid,
        out IntPtr ppv,
        out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern bool ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    // ─── P/Invoke: user32 (menu) ───

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // ─── Constants ───

    private const uint SHOP_FILEPATH = 0x2;

    // TrackPopupMenuEx flags
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_VERTICAL = 0x0040;

    // Window messages
    private const uint WM_NULL = 0x0000;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_DESTROY = 0x0002;

    // IIDs
    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu2 = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu3 = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");

    // QueryContextMenu flags
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXPLORE = 0x00000004;
    private const uint CMF_ITEMMENU = 0x00000080;

    private const int SW_SHOWNORMAL = 1;

    /// <summary>
    /// Result of showing the native context menu.
    /// </summary>
    public enum NativeMenuResult
    {
        /// <summary>The native menu was shown and a command was invoked.</summary>
        Invoked,
        /// <summary>The user dismissed the menu without selecting anything.</summary>
        Cancelled,
        /// <summary>The native menu could not be created; caller should fall back.</summary>
        Failed,
    }

    // ─── COM Interface Definitions ───
    // We define two versions: one with the standard IContextMenu signature for
    // QueryContextMenu/GetCommandString, and a separate interface for InvokeCommand
    // that accepts a raw IntPtr so we can pass lpVerb as a numeric offset.

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwnd, [In] ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [In] ref Guid riid, ref uint rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct STRRET
    {
        [FieldOffset(0)] public uint uType;
        [FieldOffset(4)] public IntPtr pOleStr;
    }

    /// <summary>
    /// CMINVOKECOMMANDINFO with lpVerb as IntPtr so we can pass a numeric offset.
    /// When HIWORD(lpVerb) == 0, the shell treats LOWORD(lpVerb) as the command offset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;        // IntPtr — can be a string pointer or a command offset
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
    }

    /// <summary>
    /// IContextMenu COM interface. InvokeCommand takes a raw IntPtr so we can
    /// pass a native CMINVOKECOMMANDINFO struct with lpVerb as a numeric offset.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, ref IntPtr plResult);
    }

    // ─── Subclass for IContextMenu2/3 message handling ───

    private class ContextMenuSubclass : IDisposable
    {
        private readonly IntPtr _hWnd;
        private readonly Win32Helper.SubclassProc _subclassProc;
        private readonly IContextMenu2? _cm2;
        private readonly IContextMenu3? _cm3;
        private bool _disposed;

        private static readonly UIntPtr SubclassId = new(0xDDB1);

        public ContextMenuSubclass(IntPtr hWnd, IContextMenu2? cm2, IContextMenu3? cm3)
        {
            _hWnd = hWnd;
            _cm2 = cm2;
            _cm3 = cm3;
            _subclassProc = SubclassProc;
            Win32Helper.SetWindowSubclass(_hWnd, _subclassProc, SubclassId, UIntPtr.Zero);
        }

        private IntPtr SubclassProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            // IContextMenu3 takes priority — it has HandleMenuMsg2 which returns a result
            if (_cm3 is not null)
            {
                IntPtr result = IntPtr.Zero;
                int hr = _cm3.HandleMenuMsg2(msg, (IntPtr)wParam, lParam, ref result);
                if (hr == 0 && result != IntPtr.Zero)
                {
                    return result;
                }
            }

            // IContextMenu2's HandleMenuMsg — just needs to be called, no return value
            if (_cm2 is not null)
            {
                _cm2.HandleMenuMsg(msg, (IntPtr)wParam, lParam);
            }

            if (msg == WM_DESTROY)
            {
                Remove();
            }

            return Win32Helper.DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        public void Remove()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Win32Helper.RemoveWindowSubclass(_hWnd, _subclassProc, SubclassId);
            }
            catch
            {
                // Ignore
            }
        }

        public void Dispose()
        {
            Remove();
            _disposed = true;
        }
    }

    // ─── Public API ───

    /// <summary>
    /// Show the native properties dialog for a file.
    /// </summary>
    public static bool ShowProperties(IntPtr hwnd, string filePath)
    {
        return !string.IsNullOrWhiteSpace(filePath) &&
               SHObjectProperties(hwnd, SHOP_FILEPATH, filePath, null);
    }

    /// <summary>
    /// Shows the native Windows Explorer context menu for a single file at the specified screen coordinates.
    /// </summary>
    /// <param name="hwnd">The owner window handle (must be a top-level Win32 window).</param>
    /// <param name="filePath">The full path to the file or folder.</param>
    /// <param name="screenX">Screen X coordinate (physical pixels) for the menu position.</param>
    /// <param name="screenY">Screen Y coordinate (physical pixels) for the menu position.</param>
    /// <returns>The result of showing the menu.</returns>
    public static NativeMenuResult ShowContextMenu(IntPtr hwnd, string filePath, int screenX, int screenY)
    {
        if (string.IsNullOrEmpty(filePath) || hwnd == IntPtr.Zero)
        {
            return NativeMenuResult.Failed;
        }

        IntPtr pidlFull = IntPtr.Zero;
        IntPtr pidlChild = IntPtr.Zero;
        IntPtr pShellFolderPtr = IntPtr.Zero;
        IntPtr pContextMenuPtr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        ContextMenuSubclass? subclass = null;

        IContextMenu? cm = null;
        IContextMenu2? cm2 = null;
        IContextMenu3? cm3 = null;
        IShellFolder? shellFolder = null;

        try
        {
            // Step 1: Parse file path → PIDL
            int hr = SHParseDisplayName(filePath, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0 || pidlFull == IntPtr.Zero)
            {
                App.Log($"[ShellContextMenu] SHParseDisplayName failed: hr=0x{hr:X8}, path={filePath}");
                return NativeMenuResult.Failed;
            }

            // Step 2: Bind to parent IShellFolder + get child PIDL
            Guid iidShellFolder = IID_IShellFolder;
            hr = SHBindToParent(pidlFull, ref iidShellFolder, out pShellFolderPtr, out pidlChild);
            if (hr != 0 || pShellFolderPtr == IntPtr.Zero || pidlChild == IntPtr.Zero)
            {
                App.Log($"[ShellContextMenu] SHBindToParent failed: hr=0x{hr:X8}");
                return NativeMenuResult.Failed;
            }

            shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pShellFolderPtr);

            // Step 3: Get IContextMenu
            Guid iidContextMenu = IID_IContextMenu;
            uint reserved = 0;
            IntPtr[] apidl = [pidlChild];
            hr = shellFolder.GetUIObjectOf(hwnd, 1, apidl, ref iidContextMenu, ref reserved, out pContextMenuPtr);
            if (hr != 0 || pContextMenuPtr == IntPtr.Zero)
            {
                App.Log($"[ShellContextMenu] GetUIObjectOf failed: hr=0x{hr:X8}");
                return NativeMenuResult.Failed;
            }

            // Step 4: Query for IContextMenu3 / IContextMenu2 (for owner-drawn items)
            cm3 = TryQueryInterface<IContextMenu3>(pContextMenuPtr, IID_IContextMenu3);
            if (cm3 is null)
            {
                cm2 = TryQueryInterface<IContextMenu2>(pContextMenuPtr, IID_IContextMenu2);
            }
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pContextMenuPtr);

            // Step 5: Build the menu
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                App.Log("[ShellContextMenu] CreatePopupMenu failed");
                return NativeMenuResult.Failed;
            }

            const uint idCmdFirst = 1;
            const uint idCmdLast = 0x7000;
            uint queryFlags = CMF_NORMAL | CMF_EXPLORE | CMF_ITEMMENU;

            // QueryContextMenu can be called on any of the three interfaces — use cm3/cm2 if available
            if (cm3 is not null)
            {
                hr = cm3.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, queryFlags);
            }
            else if (cm2 is not null)
            {
                hr = cm2.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, queryFlags);
            }
            else
            {
                hr = cm.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, queryFlags);
            }

            if (hr < 0)
            {
                App.Log($"[ShellContextMenu] QueryContextMenu failed: hr=0x{hr:X8}");
                return NativeMenuResult.Failed;
            }

            // Step 6: Install subclass for owner-drawn menu messages (icons, submenus)
            subclass = new ContextMenuSubclass(hwnd, cm2, cm3);

            // Step 7: Show the menu (TPM_RETURNCMD returns the selected command ID)
            uint tpFlags = TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_VERTICAL;
            int cmd = TrackPopupMenuEx(hMenu, tpFlags, screenX, screenY, hwnd, IntPtr.Zero);

            // Force the shell to release its menu handle
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            // Step 8: Remove subclass before invoking command
            subclass.Dispose();
            subclass = null;

            // Step 9: Execute the selected command
            if (cmd == 0)
            {
                return NativeMenuResult.Cancelled;
            }

            uint cmdOffset = (uint)cmd - idCmdFirst;
            InvokeCommand(cm, cm2, cm3, hwnd, cmdOffset);

            return NativeMenuResult.Invoked;
        }
        catch (Exception ex)
        {
            App.Log($"[ShellContextMenu] Exception: {ex.Message}");
            return NativeMenuResult.Failed;
        }
        finally
        {
            // 1. Remove subclass first
            try { subclass?.Dispose(); } catch { }

            // 2. Post WM_NULL to force shell to finish any pending work
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            // 3. Destroy the popup menu
            if (hMenu != IntPtr.Zero)
            {
                try { DestroyMenu(hMenu); } catch { }
            }

            // 4. Release managed RCW references.
            //    GetObjectForIUnknown added a ref; ReleaseComObject releases that ref.
            try { if (cm3 is not null) Marshal.ReleaseComObject(cm3); } catch { }
            try { if (cm2 is not null) Marshal.ReleaseComObject(cm2); } catch { }
            try { if (cm is not null) Marshal.ReleaseComObject(cm); } catch { }
            try { if (shellFolder is not null) Marshal.ReleaseComObject(shellFolder); } catch { }

            // 5. Release the raw COM pointers.
            //    SHBindToParent and GetUIObjectOf return addref'd pointers.
            //    GetObjectForIUnknown added its own ref (released by ReleaseComObject above),
            //    but the original ref from the COM function still needs to be released.
            try { if (pContextMenuPtr != IntPtr.Zero) Marshal.Release(pContextMenuPtr); } catch { }
            try { if (pShellFolderPtr != IntPtr.Zero) Marshal.Release(pShellFolderPtr); } catch { }

            // 6. Free the full PIDL.
            //    CRITICAL: pidlChild from SHBindToParent is a pointer INTO pidlFull,
            //    NOT a separately allocated PIDL. We must NOT ILFree(pidlChild).
            //    Only free pidlFull.
            if (pidlFull != IntPtr.Zero)
            {
                try { ILFree(pidlFull); } catch { }
            }
        }
    }

    /// <summary>
    /// Invokes a context menu command by numeric offset.
    /// Builds a native CMINVOKECOMMANDINFO struct with lpVerb = offset (HIWORD = 0).
    /// </summary>
    private static void InvokeCommand(IContextMenu cm, IContextMenu2? cm2, IContextMenu3? cm3, IntPtr hwnd, uint cmdOffset)
    {
        var info = new CMINVOKECOMMANDINFO
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            fMask = 0,
            hwnd = hwnd,
            lpVerb = (IntPtr)cmdOffset,  // HIWORD = 0 → shell uses LOWORD as offset
            lpParameters = IntPtr.Zero,
            lpDirectory = IntPtr.Zero,
            nShow = SW_SHOWNORMAL,
        };

        IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);

            int hr;
            if (cm3 is not null)
            {
                hr = cm3.InvokeCommand(pInfo);
            }
            else if (cm2 is not null)
            {
                hr = cm2.InvokeCommand(pInfo);
            }
            else
            {
                hr = cm.InvokeCommand(pInfo);
            }

            if (hr != 0)
            {
                App.Log($"[ShellContextMenu] InvokeCommand hr=0x{hr:X8}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pInfo);
        }
    }

    /// <summary>
    /// Queries for a specific COM interface from the given COM pointer.
    /// Returns null if the interface is not available.
    /// </summary>
    private static T? TryQueryInterface<T>(IntPtr pUnk, Guid iid) where T : class
    {
        try
        {
            int hr = Marshal.QueryInterface(pUnk, iid, out IntPtr pIntf);
            if (hr != 0 || pIntf == IntPtr.Zero)
            {
                return null;
            }

            // Marshal.GetObjectForIUnknown adds a ref, so we need to release the one from QueryInterface
            T obj = (T)Marshal.GetObjectForIUnknown(pIntf);
            Marshal.Release(pIntf);
            return obj;
        }
        catch
        {
            return null;
        }
    }
}
