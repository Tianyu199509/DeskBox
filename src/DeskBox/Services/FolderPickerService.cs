using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Services;

public static class FolderPickerService
{
    private const uint FosPickfolders = 0x00000020;
    private const uint FosForcefilesystem = 0x00000040;
    private const uint FosPathmustexist = 0x00000800;
    private const uint FosFilemustexist = 0x00001000;
    private const uint SigDnFileSysPath = 0x80058000;
    private const int HResultCancelled = unchecked((int)0x800704C7);

    public static string? PickFolder(IntPtr ownerHwnd)
    {
        var raisedDialogWindows = new HashSet<IntPtr>();
        using var raiseCts = new CancellationTokenSource();

        try
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FosPickfolders | FosForcefilesystem | FosPathmustexist | FosFilemustexist);

            int result;
            Task? raiseTask = StartDialogTopMostMonitor(ownerHwnd, raisedDialogWindows, raiseCts.Token);
            try
            {
                result = dialog.Show(ownerHwnd);
            }
            finally
            {
                raiseCts.Cancel();
                WaitForMonitorToStop(raiseTask);
                ClearTopMost(ownerHwnd, raisedDialogWindows);
            }

            if (result == HResultCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SigDnFileSysPath, out IntPtr pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        catch (COMException ex) when (ex.HResult == HResultCancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            App.Log($"[FolderPicker] Failed to pick folder: {ex}");
            return null;
        }
    }

    private static Task? StartDialogTopMostMonitor(
        IntPtr ownerHwnd,
        HashSet<IntPtr> raisedDialogWindows,
        CancellationToken token)
    {
        if (ownerHwnd == IntPtr.Zero)
        {
            return null;
        }

        return Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                Win32Helper.SetWindowTopMost(ownerHwnd, showWindow: false);
                foreach (IntPtr dialogHwnd in Win32Helper.FindVisibleDialogWindowsForCurrentProcess(ownerHwnd))
                {
                    lock (raisedDialogWindows)
                    {
                        raisedDialogWindows.Add(dialogHwnd);
                    }

                    Win32Helper.SetWindowTopMost(dialogHwnd);
                }

                try
                {
                    await Task.Delay(50, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private static void WaitForMonitorToStop(Task? raiseTask)
    {
        if (raiseTask is null)
        {
            return;
        }

        try
        {
            raiseTask.Wait(TimeSpan.FromMilliseconds(120));
        }
        catch
        {
        }
    }

    private static void ClearTopMost(IntPtr ownerHwnd, HashSet<IntPtr> raisedDialogWindows)
    {
        List<IntPtr> dialogs;
        lock (raisedDialogWindows)
        {
            dialogs = raisedDialogWindows.ToList();
        }

        foreach (IntPtr dialogHwnd in dialogs)
        {
            Win32Helper.ClearWindowTopMost(dialogHwnd);
        }

        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.ClearWindowTopMost(ownerHwnd);
        Win32Helper.BringWindowTemporarilyToFront(ownerHwnd);
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
