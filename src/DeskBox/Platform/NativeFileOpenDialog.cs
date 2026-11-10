// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Hand-rolled IFileOpenDialog wrapper (direct vtable calls, RCW-free, so it
/// works under Native AOT like the IFileOperation interop).
/// </summary>
/// <remarks>
/// The WinAppSDK picker exposes no FOS_NODEREFERENCELINKS switch, and the
/// shell file dialog follows shortcuts by default: picking a .lnk silently
/// returns the target's path, which turned "add file" into a move of the
/// target entity (issue 458). This dialog pins the no-dereference option so
/// a selected shortcut is added as the .lnk itself, matching Explorer's own
/// picker semantics. The folder mode deliberately keeps dereferencing:
/// mapping a shortcut should map the real target folder it points to.
/// </remarks>
internal static unsafe class NativeFileOpenDialog
{
    private static readonly Guid FileOpenDialogClassId =
        new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid FileOpenDialogInterfaceId =
        new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosAllowMultiSelect = 0x00000200;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint FosNoDereferenceLinks = 0x00100000;
    private const uint FosPickFolders = 0x00000020;
    private const uint SigdnFileSysPath = 0x80058000;

    /// <summary>COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE.</summary>
    private const uint StaCoInitializationFlags = 0x2 | 0x4;

    private const int ApproximateDialogWidth = 960;
    private const int ApproximateDialogHeight = 640;
    private const int OwnerCascadeOffset = 32;

    internal const uint PickFolderOptions =
        FosPickFolders | FosForceFileSystem | FosPathMustExist;

    private static IntPtr s_activeStaOwnerHwnd;

    // ERROR_CANCELLED is 1223 (0x4C7): IFileDialog::Show returns
    // HRESULT_FROM_WIN32(ERROR_CANCELLED) when the user dismisses the
    // dialog without a selection.
    private const int HResultCancelled = unchecked((int)0x800704C7);

    /// <summary>True when the dialog ended with a plain user cancel.</summary>
    internal static bool IsUserCancelled(int hr) => hr == HResultCancelled;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int CreateShellItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr shellItem);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr block);

    private static FileOpenDialogNative CreateDialog()
    {
        Guid classId = FileOpenDialogClassId;
        Guid interfaceId = FileOpenDialogInterfaceId;
        int hr = CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            1, // CLSCTX_INPROC_SERVER
            ref interfaceId,
            out IntPtr dialogPointer);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return new FileOpenDialogNative(dialogPointer);
    }

    /// <summary>
    /// Runs a modal folder-picker operation on a dedicated background STA
    /// thread, creating and destroying a same-thread invisible popup owner
    /// window on the target monitor so IFileOpenDialog::Show never couples to
    /// or blocks the main XAML UI thread.
    /// </summary>
    public static Task<T> RunOnDedicatedStaAsync<T>(
        IntPtr referenceOwnerHwnd,
        Func<IntPtr, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        _ = Win32Helper.AllowSetForegroundWindow((uint)Environment.ProcessId);
        (int anchorX, int anchorY) = ComputeDialogOwnerAnchor(referenceOwnerHwnd);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                int hr = Ole32NativeMethods.CoInitializeEx(
                    IntPtr.Zero,
                    StaCoInitializationFlags);
                Marshal.ThrowExceptionForHR(hr);
                T result;
                try
                {
                    IntPtr staOwnerHwnd = CreateStaDialogOwnerWindow(anchorX, anchorY);
                    try
                    {
                        Volatile.Write(ref s_activeStaOwnerHwnd, staOwnerHwnd);
                        result = operation(staOwnerHwnd);
                    }
                    finally
                    {
                        Volatile.Write(ref s_activeStaOwnerHwnd, IntPtr.Zero);
                        if (staOwnerHwnd != IntPtr.Zero)
                        {
                            _ = Win32Helper.DestroyWindow(staOwnerHwnd);
                        }

                        DrainCurrentThreadMessages();
                    }
                }
                finally
                {
                    Ole32NativeMethods.CoUninitialize();
                    DrainCurrentThreadMessages();
                }

                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox FolderPicker STA",
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }

    /// <summary>
    /// Brings an in-flight folder picker dialog to the foreground when the
    /// user invokes a folder-pick entrance again while a dialog is already open.
    /// </summary>
    public static bool TryActivateActiveFolderDialog()
    {
        IntPtr activeOwner = Volatile.Read(ref s_activeStaOwnerHwnd);
        if (activeOwner == IntPtr.Zero || !Win32Helper.IsWindow(activeOwner))
        {
            return false;
        }

        IReadOnlyList<IntPtr> dialogs = Win32Helper.FindVisibleDialogWindowsForCurrentProcess(
            excludeHwnd: activeOwner,
            requiredOwnerHwnd: activeOwner);
        if (dialogs.Count == 0)
        {
            return false;
        }

        return Win32Helper.SetForegroundWindow(dialogs[0]);
    }

    private static IntPtr CreateStaDialogOwnerWindow(int x, int y)
    {
        IntPtr hwnd = Win32Helper.CreateWindowEx(
            Win32Helper.WS_EX_TOOLWINDOW |
                Win32Helper.WS_EX_LAYERED |
                Win32Helper.WS_EX_TRANSPARENT,
            "STATIC",
            "DeskBox FolderPicker Host",
            Win32Helper.WS_POPUP,
            x,
            y,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32Helper.GetModuleHandle(null),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        _ = Win32Helper.SetLayeredWindowAttributes(hwnd, 0, 0, Win32Helper.LWA_ALPHA);
        _ = Win32Helper.ShowWindow(hwnd, Win32Helper.SW_SHOWNORMAL);
        if (!Win32Helper.SetForegroundWindow(hwnd))
        {
            // When Windows foreground lock blocks activation (e.g. cold-start
            // jump-list launch), lift the invisible owner to the topmost band
            // so the owned #32770 dialog never opens hidden behind other apps.
            _ = Win32Helper.SetWindowPos(
                hwnd,
                Win32Helper.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOACTIVATE);
        }

        DrainCurrentThreadMessages();
        return hwnd;
    }

    internal static (int X, int Y) ComputeDialogOwnerAnchor(IntPtr referenceOwnerHwnd)
    {
        if (referenceOwnerHwnd != IntPtr.Zero &&
            Win32Helper.IsWindow(referenceOwnerHwnd) &&
            Win32Helper.IsWindowVisible(referenceOwnerHwnd) &&
            Win32Helper.GetWindowRect(referenceOwnerHwnd, out Win32Helper.RECT ownerRect) &&
            (ownerRect.Right - ownerRect.Left) > 8 &&
            (ownerRect.Bottom - ownerRect.Top) > 8)
        {
            IntPtr ownerMonitor = Win32Helper.MonitorFromWindow(
                referenceOwnerHwnd,
                Win32Helper.MONITOR_DEFAULTTONEAREST);
            if (TryGetWorkArea(ownerMonitor, out Win32Helper.RECT ownerWorkArea))
            {
                int maxX = Math.Max(ownerWorkArea.Left, ownerWorkArea.Right - 1);
                int maxY = Math.Max(ownerWorkArea.Top, ownerWorkArea.Bottom - 1);
                return (
                    Math.Clamp(ownerRect.Left + OwnerCascadeOffset, ownerWorkArea.Left, maxX),
                    Math.Clamp(ownerRect.Top + OwnerCascadeOffset, ownerWorkArea.Top, maxY));
            }

            return (ownerRect.Left + OwnerCascadeOffset, ownerRect.Top + OwnerCascadeOffset);
        }

        IntPtr targetMonitor = Win32Helper.GetCursorPos(out Win32Helper.POINT cursor)
            ? Win32Helper.MonitorFromPoint(cursor, Win32Helper.MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;
        if (targetMonitor == IntPtr.Zero &&
            referenceOwnerHwnd != IntPtr.Zero &&
            Win32Helper.IsWindow(referenceOwnerHwnd))
        {
            targetMonitor = Win32Helper.MonitorFromWindow(
                referenceOwnerHwnd,
                Win32Helper.MONITOR_DEFAULTTONEAREST);
        }

        if (targetMonitor == IntPtr.Zero)
        {
            targetMonitor = Win32Helper.MonitorFromPoint(
                default,
                Win32Helper.MONITOR_DEFAULTTOPRIMARY);
        }

        if (TryGetWorkArea(targetMonitor, out Win32Helper.RECT workArea))
        {
            int workWidth = Math.Max(1, workArea.Right - workArea.Left);
            int workHeight = Math.Max(1, workArea.Bottom - workArea.Top);
            return (
                workArea.Left + Math.Max(0, (workWidth - ApproximateDialogWidth) / 2),
                workArea.Top + Math.Max(0, (workHeight - ApproximateDialogHeight) / 2));
        }

        return (120, 120);
    }

    private static bool TryGetWorkArea(IntPtr monitor, out Win32Helper.RECT workArea)
    {
        if (monitor == IntPtr.Zero)
        {
            workArea = default;
            return false;
        }

        var info = new Win32Helper.MONITORINFO
        {
            cbSize = sizeof(Win32Helper.MONITORINFO),
        };
        if (!Win32Helper.GetMonitorInfo(monitor, ref info))
        {
            workArea = default;
            return false;
        }

        workArea = info.rcWork;
        return true;
    }

    private static void DrainCurrentThreadMessages()
    {
        while (Win32Helper.PeekMessage(
                   out Win32Helper.MSG msg,
                   IntPtr.Zero,
                   0,
                   0,
                   Win32Helper.PM_REMOVE))
        {
            _ = Win32Helper.TranslateMessage(in msg);
            _ = Win32Helper.DispatchMessage(in msg);
        }
    }

    /// <summary>
    /// Shows the modal folder picker on the caller's STA thread. Returns the
    /// picked folder's file-system path; cancelling returns null.
    /// </summary>
    public static string? ShowPickFolder(IntPtr ownerHwnd)
    {
        using var dialog = CreateDialog();
        Marshal.ThrowExceptionForHR(dialog.SetOptions(PickFolderOptions));

        int hr = dialog.Show(ownerHwnd);
        if (IsUserCancelled(hr))
        {
            return null;
        }

        Marshal.ThrowExceptionForHR(hr);
        Marshal.ThrowExceptionForHR(dialog.GetResult(out IntPtr item));
        if (item == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            hr = ShellItemNative.GetDisplayName(item, SigdnFileSysPath, out IntPtr name);
            if (hr != 0 || name == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(name) is { Length: > 0 } path ? path : null;
            }
            finally
            {
                CoTaskMemFree(name);
            }
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    /// <summary>
    /// Shows the modal multi-select open dialog on the caller's STA thread
    /// (the owner window's UI thread). Returns the picked file-system
    /// paths; cancelling returns an empty list.
    /// </summary>
    public static IReadOnlyList<string> ShowPickFiles(
        IntPtr ownerHwnd,
        string? defaultFolder)
    {
        using var dialog = CreateDialog();
        uint options = FosForceFileSystem |
            FosAllowMultiSelect |
            FosPathMustExist |
            FosFileMustExist |
            FosNoDereferenceLinks;
        Marshal.ThrowExceptionForHR(dialog.SetOptions(options));

        int hr;
        if (!string.IsNullOrWhiteSpace(defaultFolder))
        {
            Guid shellItemId = ShellItemInterfaceId;
            hr = CreateShellItemFromParsingName(
                defaultFolder,
                IntPtr.Zero,
                ref shellItemId,
                out IntPtr folderItem);
            if (hr == 0 && folderItem != IntPtr.Zero)
            {
                dialog.SetDefaultFolder(folderItem);
                Marshal.Release(folderItem);
            }
            // A suggested folder that cannot be shelved (recently deleted,
            // detached drive) falls back to the dialog default location.
        }

        hr = dialog.Show(ownerHwnd);
        if (IsUserCancelled(hr))
        {
            return [];
        }

        Marshal.ThrowExceptionForHR(hr);
        Marshal.ThrowExceptionForHR(dialog.GetResults(out IntPtr resultsPointer));
        using var results = new ShellItemArrayNative(resultsPointer);

        Marshal.ThrowExceptionForHR(results.GetCount(out uint count));
        var paths = new List<string>(capacity: (int)count);
        for (uint index = 0; index < count; index++)
        {
            hr = results.GetItemAt(index, out IntPtr item);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = ShellItemNative.GetDisplayName(
                item,
                SigdnFileSysPath,
                out IntPtr name);
            Marshal.Release(item);
            if (hr != 0 || name == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                if (Marshal.PtrToStringUni(name) is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
            finally
            {
                CoTaskMemFree(name);
            }
        }

        return paths;
    }

    /// <summary>An owned native wrapper avoids RCWs and works in Native AOT too.</summary>
    private sealed unsafe class FileOpenDialogNative(IntPtr pointer) : IDisposable
    {
        private void** Table => *(void***)pointer;

        public int Show(IntPtr owner) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table[3])(pointer, owner);

        public int GetResult(out IntPtr result)
        {
            fixed (IntPtr* value = &result)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Table[20])(
                    pointer,
                    value);
            }
        }

        public int SetOptions(uint options) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table[9])(pointer, options);

        public int SetDefaultFolder(IntPtr folder) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table[11])(pointer, folder);

        public int GetResults(out IntPtr results)
        {
            fixed (IntPtr* value = &results)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Table[27])(
                    pointer,
                    value);
            }
        }

        public void Dispose() => Marshal.Release(pointer);
    }

    private sealed unsafe class ShellItemArrayNative(IntPtr pointer) : IDisposable
    {
        private void** Table => *(void***)pointer;

        public int GetCount(out uint count)
        {
            fixed (uint* value = &count)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Table[7])(
                    pointer,
                    value);
            }
        }

        public int GetItemAt(uint index, out IntPtr item)
        {
            fixed (IntPtr* value = &item)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Table[8])(
                    pointer,
                    index,
                    value);
            }
        }

        public void Dispose() => Marshal.Release(pointer);
    }

    private static class ShellItemNative
    {
        public static int GetDisplayName(IntPtr item, uint kind, out IntPtr name)
        {
            fixed (IntPtr* value = &name)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(
                    *(void***)item)[5])(item, kind, value);
            }
        }
    }
}
