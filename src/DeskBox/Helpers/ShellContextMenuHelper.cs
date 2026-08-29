using System.Runtime.InteropServices;
using DeskBox.Helpers;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Services;
#endif

namespace DeskBox.Helpers;

/// <summary>
/// Shows the native Windows Explorer context menu for a file or folder using
/// COM interop with IContextMenu / IContextMenu2 / IContextMenu3.
/// </summary>
public static unsafe class ShellContextMenuHelper
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
    private static extern void ILFree(IntPtr pidl);

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
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_VERTICAL = 0x0040;

    // Window messages
    private const uint WM_NULL = 0x0000;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_MENUCHAR = 0x0120;
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

    // Raw COM vtable slots. Native AOT cannot create runtime callable wrappers
    // through Marshal.GetObjectForIUnknown unless a custom ComWrappers instance
    // is registered. Calling the stable Shell COM ABI directly keeps this path
    // usable in both Native AOT and framework-dependent builds.
    private const int IUnknownQueryInterfaceSlot = 0;
    private const int IUnknownReleaseSlot = 2;
    private const int ShellFolderGetUiObjectOfSlot = 10;
    private const int ContextMenuQuerySlot = 3;
    private const int ContextMenuInvokeSlot = 4;
    private const int ContextMenu2HandleMenuMessageSlot = 6;
    private const int ContextMenu3HandleMenuMessage2Slot = 7;

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

    internal enum ContextMenuMessageTarget
    {
        None,
        ContextMenu2,
        ContextMenu3,
    }

    internal static ContextMenuMessageTarget GetContextMenuMessageTarget(
        uint message,
        UIntPtr wParam) =>
        message switch
        {
            WM_MENUCHAR => ContextMenuMessageTarget.ContextMenu3,
            WM_INITMENUPOPUP => ContextMenuMessageTarget.ContextMenu2,
            WM_DRAWITEM or WM_MEASUREITEM when wParam == UIntPtr.Zero =>
                ContextMenuMessageTarget.ContextMenu2,
            _ => ContextMenuMessageTarget.None,
        };

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
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    // ─── Subclass for IContextMenu2/3 message handling ───

    private class ContextMenuSubclass : IDisposable
    {
        private readonly IntPtr _hWnd;
        private readonly Win32Helper.SubclassProc _subclassProc;
        private readonly IntPtr _contextMenu2;
        private readonly IntPtr _contextMenu3;
        private bool _disposed;

        private static readonly UIntPtr SubclassId = new(0xDDB1);

        public ContextMenuSubclass(
            IntPtr hWnd,
            IntPtr contextMenu2,
            IntPtr contextMenu3)
        {
            _hWnd = hWnd;
            // IContextMenu3 inherits IContextMenu2, so its pointer also exposes
            // the base HandleMenuMsg slot. Avoid a second QueryInterface/AddRef.
            _contextMenu2 = contextMenu2 != IntPtr.Zero
                ? contextMenu2
                : contextMenu3;
            _contextMenu3 = contextMenu3;
            _subclassProc = SubclassProc;
            Win32Helper.SetWindowSubclass(_hWnd, _subclassProc, SubclassId, UIntPtr.Zero);
        }

        private IntPtr SubclassProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            try
            {
                switch (GetContextMenuMessageTarget(msg, wParam))
                {
                    case ContextMenuMessageTarget.ContextMenu3
                        when _contextMenu3 != IntPtr.Zero:
                    {
                        int hr = HandleContextMenuMessage2(
                            _contextMenu3,
                            msg,
                            (IntPtr)wParam,
                            lParam,
                            out IntPtr result);
                        if (hr == 0)
                        {
                            // The result can legitimately be zero. S_OK means
                            // the extension handled the message, so do not pass
                            // it through to the WinUI owner a second time.
                            return result;
                        }

                        if (hr < 0)
                        {
                            App.Log(
                                $"[ShellContextMenu] stage=message-handler-failed interface=IContextMenu3 msg=0x{msg:X4} hr=0x{hr:X8}");
                        }

                        break;
                    }

                    case ContextMenuMessageTarget.ContextMenu2
                        when _contextMenu2 != IntPtr.Zero:
                    {
                        int hr = HandleContextMenuMessage(
                            _contextMenu2,
                            msg,
                            (IntPtr)wParam,
                            lParam);
                        if (hr == 0)
                        {
                            return IntPtr.Zero;
                        }

                        if (hr < 0)
                        {
                            App.Log(
                                $"[ShellContextMenu] stage=message-handler-failed interface=IContextMenu2 msg=0x{msg:X4} hr=0x{hr:X8}");
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[ShellContextMenu] stage=message-handler-exception msg=0x{msg:X4} exception={ex}");
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
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        bool trackedInvocation =
            AotFilePropertiesFixture.TryBeginInvocation(hwnd, filePath);
#endif
        try
        {
            bool invoked = SHObjectProperties(
                hwnd,
                SHOP_FILEPATH,
                filePath,
                null);
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            if (trackedInvocation)
            {
                AotFilePropertiesFixture.RecordInvocationResult(
                    invoked,
                    error: null);
            }
#endif
            return invoked;
        }
        catch (Exception ex)
        {
            _ = ex;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            if (trackedInvocation)
            {
                AotFilePropertiesFixture.RecordInvocationResult(
                    invoked: false,
                    ex.ToString());
            }
#endif
            throw;
        }
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
        IntPtr pContextMenu2Ptr = IntPtr.Zero;
        IntPtr pContextMenu3Ptr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        ContextMenuSubclass? subclass = null;

        try
        {
            App.Log(
                $"[ShellContextMenu] stage=begin hwnd=0x{hwnd.ToInt64():X} point={screenX},{screenY}");

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

            // Step 3: Get IContextMenu
            Guid iidContextMenu = IID_IContextMenu;
            hr = GetShellUiObject(
                pShellFolderPtr,
                hwnd,
                pidlChild,
                iidContextMenu,
                out pContextMenuPtr);
            if (hr != 0 || pContextMenuPtr == IntPtr.Zero)
            {
                App.Log($"[ShellContextMenu] GetUIObjectOf failed: hr=0x{hr:X8}");
                return NativeMenuResult.Failed;
            }

            // Step 4: Query for IContextMenu3 / IContextMenu2 (for owner-drawn items)
            pContextMenu3Ptr = TryQueryInterface(
                pContextMenuPtr,
                IID_IContextMenu3);
            if (pContextMenu3Ptr == IntPtr.Zero)
            {
                pContextMenu2Ptr = TryQueryInterface(
                    pContextMenuPtr,
                    IID_IContextMenu2);
            }
            IntPtr activeContextMenu = pContextMenu3Ptr != IntPtr.Zero
                ? pContextMenu3Ptr
                : pContextMenu2Ptr != IntPtr.Zero
                    ? pContextMenu2Ptr
                    : pContextMenuPtr;
            string activeInterface = pContextMenu3Ptr != IntPtr.Zero
                ? "IContextMenu3"
                : pContextMenu2Ptr != IntPtr.Zero
                    ? "IContextMenu2"
                    : "IContextMenu";

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

            App.Log(
                $"[ShellContextMenu] stage=query-begin interface={activeInterface}");
            hr = QueryContextMenu(
                activeContextMenu,
                hMenu,
                idCmdFirst,
                idCmdLast,
                queryFlags);
            App.Log(
                $"[ShellContextMenu] stage=query-end interface={activeInterface} hr=0x{hr:X8}");

            if (hr < 0)
            {
                App.Log($"[ShellContextMenu] QueryContextMenu failed: hr=0x{hr:X8}");
                return NativeMenuResult.Failed;
            }

            // Step 6: Install subclass for owner-drawn menu messages (icons, submenus)
            subclass = new ContextMenuSubclass(
                hwnd,
                pContextMenu2Ptr,
                pContextMenu3Ptr);

            // Step 7: Show the menu (TPM_RETURNCMD returns the selected command ID)
            uint tpFlags = TPM_RETURNCMD | TPM_NONOTIFY |
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_VERTICAL;
            App.Log(
                $"[ShellContextMenu] stage=track-begin interface={activeInterface}");
            int cmd = TrackPopupMenuEx(hMenu, tpFlags, screenX, screenY, hwnd, IntPtr.Zero);
            App.Log(
                $"[ShellContextMenu] stage=track-end interface={activeInterface} commandId={cmd}");

            // Force the shell to release its menu handle
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            // Step 8: Remove subclass before invoking command
            subclass.Dispose();
            subclass = null;

            // Step 9: Execute the selected command
            if (cmd == 0)
            {
                App.Log("[ShellContextMenu] stage=cancelled");
                return NativeMenuResult.Cancelled;
            }

            uint cmdOffset = (uint)cmd - idCmdFirst;
            App.Log(
                $"[ShellContextMenu] stage=invoke-begin interface={activeInterface} commandOffset={cmdOffset}");
            int invokeResult = InvokeCommand(
                activeContextMenu,
                hwnd,
                cmdOffset);
            App.Log(
                $"[ShellContextMenu] stage=invoke-end interface={activeInterface} hr=0x{invokeResult:X8}");
            if (invokeResult < 0)
            {
                App.Log($"[ShellContextMenu] InvokeCommand failed: hr=0x{invokeResult:X8}");
                return NativeMenuResult.Failed;
            }

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

            // 4. Release every raw COM reference exactly once. QueryInterface,
            //    GetUIObjectOf, and SHBindToParent each return an add-ref'd pointer.
            try { ReleaseInterface(pContextMenu3Ptr); } catch { }
            try { ReleaseInterface(pContextMenu2Ptr); } catch { }
            try { ReleaseInterface(pContextMenuPtr); } catch { }
            try { ReleaseInterface(pShellFolderPtr); } catch { }

            // 5. Free the full PIDL.
            //    CRITICAL: pidlChild from SHBindToParent is a pointer INTO pidlFull,
            //    NOT a separately allocated PIDL. We must NOT ILFree(pidlChild).
            //    Only free pidlFull.
            if (pidlFull != IntPtr.Zero)
            {
                try { ILFree(pidlFull); } catch { }
            }
        }
    }

    private static int GetShellUiObject(
        IntPtr shellFolder,
        IntPtr ownerWindow,
        IntPtr childItemIdList,
        Guid interfaceId,
        out IntPtr resultPointer)
    {
        resultPointer = IntPtr.Zero;
        if (shellFolder == IntPtr.Zero || childItemIdList == IntPtr.Zero)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        var getUiObject = (delegate* unmanaged[Stdcall]<
            IntPtr,
            IntPtr,
            uint,
            IntPtr*,
            Guid*,
            uint*,
            IntPtr*,
            int>)GetVtableEntry(
                shellFolder,
                ShellFolderGetUiObjectOfSlot,
                "IShellFolder.GetUIObjectOf");
        IntPtr child = childItemIdList;
        Guid requestedInterface = interfaceId;
        IntPtr localResult = IntPtr.Zero;
        int result = getUiObject(
            shellFolder,
            ownerWindow,
            1,
            &child,
            &requestedInterface,
            null,
            &localResult);
        resultPointer = localResult;
        return result;
    }

    private static int QueryContextMenu(
        IntPtr contextMenu,
        IntPtr menu,
        uint firstCommandId,
        uint lastCommandId,
        uint flags)
    {
        var queryContextMenu = (delegate* unmanaged[Stdcall]<
            IntPtr,
            IntPtr,
            uint,
            uint,
            uint,
            uint,
            int>)GetVtableEntry(
                contextMenu,
                ContextMenuQuerySlot,
                "IContextMenu.QueryContextMenu");
        return queryContextMenu(
            contextMenu,
            menu,
            0,
            firstCommandId,
            lastCommandId,
            flags);
    }

    /// <summary>
    /// Invokes a context menu command by numeric offset. The complete native
    /// CMINVOKECOMMANDINFO layout is required because handlers inspect cbSize.
    /// </summary>
    private static int InvokeCommand(
        IntPtr contextMenu,
        IntPtr hwnd,
        uint cmdOffset)
    {
        var info = new CMINVOKECOMMANDINFO
        {
            cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
            fMask = 0,
            hwnd = hwnd,
            lpVerb = (IntPtr)cmdOffset,
            lpParameters = IntPtr.Zero,
            lpDirectory = IntPtr.Zero,
            nShow = SW_SHOWNORMAL,
            dwHotKey = 0,
            hIcon = IntPtr.Zero,
        };
        var invokeCommand = (delegate* unmanaged[Stdcall]<
            IntPtr,
            CMINVOKECOMMANDINFO*,
            int>)GetVtableEntry(
                contextMenu,
                ContextMenuInvokeSlot,
                "IContextMenu.InvokeCommand");
        return invokeCommand(contextMenu, &info);
    }

    private static int HandleContextMenuMessage(
        IntPtr contextMenu2,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        var handleMessage = (delegate* unmanaged[Stdcall]<
            IntPtr,
            uint,
            IntPtr,
            IntPtr,
            int>)GetVtableEntry(
                contextMenu2,
                ContextMenu2HandleMenuMessageSlot,
                "IContextMenu2.HandleMenuMsg");
        return handleMessage(contextMenu2, message, wParam, lParam);
    }

    private static int HandleContextMenuMessage2(
        IntPtr contextMenu3,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr messageResult)
    {
        var handleMessage = (delegate* unmanaged[Stdcall]<
            IntPtr,
            uint,
            IntPtr,
            IntPtr,
            IntPtr*,
            int>)GetVtableEntry(
                contextMenu3,
                ContextMenu3HandleMenuMessage2Slot,
                "IContextMenu3.HandleMenuMsg2");
        IntPtr localResult = IntPtr.Zero;
        int result = handleMessage(
            contextMenu3,
            message,
            wParam,
            lParam,
            &localResult);
        messageResult = localResult;
        return result;
    }

    private static IntPtr TryQueryInterface(
        IntPtr unknown,
        Guid interfaceId)
    {
        int result = QueryInterface(
            unknown,
            interfaceId,
            out IntPtr interfacePointer);
        if (result >= 0 && interfacePointer != IntPtr.Zero)
        {
            return interfacePointer;
        }

        if (interfacePointer != IntPtr.Zero)
        {
            ReleaseInterface(interfacePointer);
        }

        return IntPtr.Zero;
    }

    private static int QueryInterface(
        IntPtr unknown,
        Guid interfaceId,
        out IntPtr resultPointer)
    {
        resultPointer = IntPtr.Zero;
        if (unknown == IntPtr.Zero)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        var queryInterface = (delegate* unmanaged[Stdcall]<
            IntPtr,
            Guid*,
            IntPtr*,
            int>)GetVtableEntry(
                unknown,
                IUnknownQueryInterfaceSlot,
                "IUnknown.QueryInterface");
        Guid requestedInterface = interfaceId;
        IntPtr localResult = IntPtr.Zero;
        int result = queryInterface(
            unknown,
            &requestedInterface,
            &localResult);
        resultPointer = localResult;
        return result;
    }

    private static void ReleaseInterface(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)
            GetVtableEntry(
                unknown,
                IUnknownReleaseSlot,
                "IUnknown.Release");
        _ = release(unknown);
    }

    private static IntPtr GetVtableEntry(
        IntPtr unknown,
        int slot,
        string methodName)
    {
        if (unknown == IntPtr.Zero)
        {
            throw new ArgumentException(
                $"The COM pointer for {methodName} is null.",
                nameof(unknown));
        }

        IntPtr* vtable = *(IntPtr**)unknown;
        if (vtable == null || vtable[slot] == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The COM vtable does not contain {methodName} at slot {slot}.");
        }

        return vtable[slot];
    }
}
