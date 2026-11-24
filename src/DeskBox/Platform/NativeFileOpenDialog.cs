// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

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
internal static class NativeFileOpenDialog
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

    internal enum DedicatedStaDialogKind
    {
        Folder,
        File,
    }

    internal const uint PickFolderOptions =
        FosPickFolders | FosForceFileSystem | FosPathMustExist;

    private static IntPtr s_activeFolderStaOwnerHwnd;
    private static IntPtr s_activeFileStaOwnerHwnd;
    private static int s_activeFolderProxyPid;
    private static int s_activeFileProxyPid;

    private const int ProxyExitPicked = 0;
    private const int ProxyExitCancelled = 2;

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
        Func<IntPtr, T> operation,
        DedicatedStaDialogKind dialogKind = DedicatedStaDialogKind.Folder)
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
                        if (dialogKind == DedicatedStaDialogKind.File)
                        {
                            Interlocked.Exchange(ref s_activeFileStaOwnerHwnd, staOwnerHwnd);
                        }
                        else
                        {
                            Interlocked.Exchange(ref s_activeFolderStaOwnerHwnd, staOwnerHwnd);
                        }

                        result = operation(staOwnerHwnd);
                    }
                    finally
                    {
                        if (dialogKind == DedicatedStaDialogKind.File)
                        {
                            Interlocked.CompareExchange(ref s_activeFileStaOwnerHwnd, IntPtr.Zero, staOwnerHwnd);
                        }
                        else
                        {
                            Interlocked.CompareExchange(ref s_activeFolderStaOwnerHwnd, IntPtr.Zero, staOwnerHwnd);
                        }

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
            Name = dialogKind == DedicatedStaDialogKind.File
                ? "DeskBox FilePicker STA"
                : "DeskBox FolderPicker STA",
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
    public static bool TryActivateActiveFolderDialog() =>
        TryActivateProxyDialog(Volatile.Read(ref s_activeFolderProxyPid)) ||
        TryActivateActiveDialog(Volatile.Read(ref s_activeFolderStaOwnerHwnd));

    public static bool TryActivateActiveFileDialog() =>
        TryActivateProxyDialog(Volatile.Read(ref s_activeFileProxyPid)) ||
        TryActivateActiveDialog(Volatile.Read(ref s_activeFileStaOwnerHwnd));

    /// <summary>
    /// Hosts <c>IFileOpenDialog::Show</c> out-of-process in the single-STA native
    /// proxy (<c>DeskBox.ThumbnailProxy.exe --pick-folder</c>) so Shell / Quick Access
    /// <c>ThreadingModel=Single</c> COM components can never marshal back to WinUI's
    /// Main STA and deadlock cross-thread (<c>AppHangB1 / Cross-thread</c>, #1267).
    /// Falls back to <see cref="RunOnDedicatedStaAsync{T}"/> only when the proxy
    /// executable is absent (e.g. isolated unit-test environments).
    /// </summary>
    public static async Task<string?> PickFolderOutOfProcessAsync(IntPtr referenceOwnerHwnd)
    {
        string proxyPath = Path.Combine(
            AppContext.BaseDirectory,
            DeskBox.Helpers.ShellThumbnailProxy.ExecutableName);
        if (!File.Exists(proxyPath))
        {
            return await RunOnDedicatedStaAsync(
                referenceOwnerHwnd,
                static staOwnerHwnd => ShowPickFolder(staOwnerHwnd)).ConfigureAwait(false);
        }

        (int anchorX, int anchorY) = ComputeDialogOwnerAnchor(referenceOwnerHwnd);
        string themeToken = ResolvePickerThemeToken(referenceOwnerHwnd);
        IReadOnlyList<string> results = await RunProxyPickerAsync(
            proxyPath,
            DedicatedStaDialogKind.Folder,
            [
                "--pick-folder",
                anchorX.ToString(CultureInfo.InvariantCulture),
                anchorY.ToString(CultureInfo.InvariantCulture),
                "--theme",
                themeToken,
                "--watch-stdin"
            ])
            .ConfigureAwait(false);
        return results.Count > 0 ? results[0] : null;
    }

    public static async Task<IReadOnlyList<string>> PickFilesOutOfProcessAsync(
        IntPtr referenceOwnerHwnd,
        string? defaultFolder,
        IReadOnlyList<string>? fileTypeFilters = null,
        bool allowMultiSelect = true,
        bool noDereferenceLinks = true)
    {
        string normalizedFilterSpec = NormalizeFileFilterSpec(
            fileTypeFilters,
            out bool includesShortcutFilter);
        bool effectiveNoDereferenceLinks = noDereferenceLinks || includesShortcutFilter;

        string proxyPath = Path.Combine(
            AppContext.BaseDirectory,
            DeskBox.Helpers.ShellThumbnailProxy.ExecutableName);
        if (!File.Exists(proxyPath))
        {
            return await RunOnDedicatedStaAsync(
                referenceOwnerHwnd,
                staOwnerHwnd => ShowPickFiles(
                    staOwnerHwnd,
                    defaultFolder,
                    normalizedFilterSpec,
                    allowMultiSelect,
                    effectiveNoDereferenceLinks),
                DedicatedStaDialogKind.File).ConfigureAwait(false);
        }

        (int anchorX, int anchorY) = ComputeDialogOwnerAnchor(referenceOwnerHwnd);
        string themeToken = ResolvePickerThemeToken(referenceOwnerHwnd);
        List<string> args =
        [
            "--pick-files",
            anchorX.ToString(CultureInfo.InvariantCulture),
            anchorY.ToString(CultureInfo.InvariantCulture),
            "--theme",
            themeToken,
            "--watch-stdin"
        ];
        if (!allowMultiSelect)
        {
            args.Add("--single");
        }

        if (!effectiveNoDereferenceLinks)
        {
            args.Add("--dereference-links");
        }

        if (!string.IsNullOrWhiteSpace(defaultFolder))
        {
            args.Add("--default-folder");
            args.Add(defaultFolder);
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilterSpec))
        {
            args.Add("--filter");
            args.Add(normalizedFilterSpec);
        }

        return await RunProxyPickerAsync(
            proxyPath,
            DedicatedStaDialogKind.File,
            args).ConfigureAwait(false);
    }

    internal static string NormalizeFileFilterSpec(
        IReadOnlyList<string>? fileTypeFilters,
        out bool includesShortcutFilter)
    {
        includesShortcutFilter = false;
        if (fileTypeFilters is null || fileTypeFilters.Count == 0)
        {
            return string.Empty;
        }

        var patterns = new List<string>(fileTypeFilters.Count);
        foreach (string? raw in fileTypeFilters)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (trimmed is "*" or "*.*" or ".*")
            {
                includesShortcutFilter = false;
                return string.Empty;
            }

            string normalized = trimmed.StartsWith("*.", StringComparison.Ordinal)
                ? trimmed
                : trimmed.StartsWith('.')
                    ? $"*{trimmed}"
                    : $"*.{trimmed}";
            if (normalized.Length <= 2)
            {
                continue;
            }

            if (string.Equals(normalized, "*.lnk", StringComparison.OrdinalIgnoreCase))
            {
                includesShortcutFilter = true;
            }

            if (!patterns.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                patterns.Add(normalized);
            }
        }

        return patterns.Count == 0 ? string.Empty : string.Join(';', patterns);
    }

    internal static string ResolvePickerThemeToken(IntPtr referenceOwnerHwnd)
    {
        try
        {
            if (App.Current is { ThemeService: { } themeService })
            {
                return themeService.CurrentTheme switch
                {
                    Microsoft.UI.Xaml.ElementTheme.Dark => "dark",
                    Microsoft.UI.Xaml.ElementTheme.Light => "light",
                    _ => Win32Helper.IsSystemDarkMode() ? "dark" : "light",
                };
            }

            if (referenceOwnerHwnd != IntPtr.Zero &&
                Win32Helper.IsWindow(referenceOwnerHwnd) &&
                Win32Helper.DwmGetWindowAttribute(
                    referenceOwnerHwnd,
                    Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    out int darkAttr,
                    sizeof(int)) == 0)
            {
                return darkAttr != 0 ? "dark" : "light";
            }
        }
        catch
        {
            // Fall back to the registry probe below.
        }

        return Win32Helper.IsSystemDarkMode() ? "dark" : "light";
    }

    private static async Task<IReadOnlyList<string>> RunProxyPickerAsync(
        string proxyPath,
        DedicatedStaDialogKind dialogKind,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = proxyPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirect stdin and keep the write pipe open for the lifetime of
            // the child process: if DeskBox or SettingsHost exits or crashes
            // while IFileOpenDialog::Show is open, the OS closes the pipe and
            // the proxy's --watch-stdin thread dismisses #32770 and exits.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        bool started;
        using (DeskBox.Services.ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope())
        {
            started = process.Start();
        }

        if (!started)
        {
            throw new COMException("Failed to start native picker proxy process.", unchecked((int)0x80004005));
        }

        int pid = process.Id;
        _ = Win32Helper.AllowSetForegroundWindow((uint)pid);
        if (dialogKind == DedicatedStaDialogKind.File)
        {
            Interlocked.Exchange(ref s_activeFileProxyPid, pid);
        }
        else
        {
            Interlocked.Exchange(ref s_activeFolderProxyPid, pid);
        }

        try
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            int exitCode = process.ExitCode;
            if (exitCode == ProxyExitCancelled)
            {
                return [];
            }

            if (exitCode == ProxyExitPicked)
            {
                return stdout
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static line => line.Length > 0)
                    .ToArray();
            }

            int hr = ParseProxyFailureHResult(stderr);
            string message = string.IsNullOrWhiteSpace(stderr)
                ? $"Out-of-process picker failed with exit code {exitCode}."
                : stderr.Trim();
            throw new COMException(message, hr);
        }
        finally
        {
            if (dialogKind == DedicatedStaDialogKind.File)
            {
                Interlocked.CompareExchange(ref s_activeFileProxyPid, 0, pid);
            }
            else
            {
                Interlocked.CompareExchange(ref s_activeFolderProxyPid, 0, pid);
            }
        }
    }

    internal static int ParseProxyFailureHResult(string? stderr)
    {
        const int fallbackHr = unchecked((int)0x80004005);
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return fallbackHr;
        }

        int marker = stderr.IndexOf("hr=0x", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && stderr.Length >= marker + 13)
        {
            ReadOnlySpan<char> hex = stderr.AsSpan(marker + 5, 8);
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) &&
                parsed != 0)
            {
                return unchecked((int)parsed);
            }
        }

        return fallbackHr;
    }

    private static bool TryActivateProxyDialog(int proxyPid)
    {
        if (proxyPid <= 0)
        {
            return false;
        }

        uint targetPid = (uint)proxyPid;
        IntPtr foundDialog = IntPtr.Zero;
        Win32Helper.EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == IntPtr.Zero || !Win32Helper.IsWindowVisible(hWnd))
            {
                return true;
            }

            _ = Win32Helper.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != targetPid)
            {
                return true;
            }

            Span<char> classBuf = stackalloc char[32];
            int len = Win32Helper.GetClassName(hWnd, classBuf);
            if (len > 0 && classBuf[..len].SequenceEqual("#32770"))
            {
                foundDialog = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (foundDialog == IntPtr.Zero)
        {
            return false;
        }

        _ = Win32Helper.AllowSetForegroundWindow(targetPid);
        return Win32Helper.SetForegroundWindow(foundDialog);
    }

    private static bool TryActivateActiveDialog(IntPtr activeOwner)
    {
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
            // Briefly pulse TOPMOST -> NOTOPMOST so the owned #32770 dialog rises
            // above existing windows on cold-start without remaining permanently sticky
            // above unrelated applications if the user switches away.
            const uint flags = Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE;
            _ = Win32Helper.SetWindowPos(hwnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0, flags);
            _ = Win32Helper.SetWindowPos(hwnd, Win32Helper.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
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

    private static unsafe bool TryGetWorkArea(IntPtr monitor, out Win32Helper.RECT workArea)
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
        string? defaultFolder,
        string? normalizedFilterSpec = null,
        bool allowMultiSelect = true,
        bool noDereferenceLinks = true)
    {
        using var dialog = CreateDialog();
        uint options = FosForceFileSystem |
            FosPathMustExist |
            FosFileMustExist;
        if (allowMultiSelect)
        {
            options |= FosAllowMultiSelect;
        }

        if (noDereferenceLinks)
        {
            options |= FosNoDereferenceLinks;
        }

        Marshal.ThrowExceptionForHR(dialog.SetOptions(options));
        if (!string.IsNullOrWhiteSpace(normalizedFilterSpec))
        {
            Marshal.ThrowExceptionForHR(dialog.SetSingleFilterSpec(normalizedFilterSpec));
        }

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
        if (!allowMultiSelect)
        {
            Marshal.ThrowExceptionForHR(dialog.GetResult(out IntPtr singleItem));
            if (singleItem == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                hr = ShellItemNative.GetDisplayName(singleItem, SigdnFileSysPath, out IntPtr singleName);
                if (hr != 0 || singleName == IntPtr.Zero)
                {
                    return [];
                }

                try
                {
                    return Marshal.PtrToStringUni(singleName) is { Length: > 0 } singlePath
                        ? [singlePath]
                        : [];
                }
                finally
                {
                    CoTaskMemFree(singleName);
                }
            }
            finally
            {
                Marshal.Release(singleItem);
            }
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct ComDlgFilterSpecNative
        {
            public IntPtr PszName;
            public IntPtr PszSpec;
        }

        public int Show(IntPtr owner) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table[3])(pointer, owner);

        public int SetSingleFilterSpec(string spec)
        {
            string label = spec.Replace(";", "; ", StringComparison.Ordinal);
            IntPtr wideLabel = Marshal.StringToCoTaskMemUni(label);
            IntPtr wideSpec = Marshal.StringToCoTaskMemUni(spec);
            try
            {
                var filter = new ComDlgFilterSpecNative
                {
                    PszName = wideLabel,
                    PszSpec = wideSpec,
                };
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, ComDlgFilterSpecNative*, int>)Table[4])(
                    pointer,
                    1u,
                    &filter);
                if (hr < 0)
                {
                    return hr;
                }

                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table[5])(pointer, 1u);
            }
            finally
            {
                Marshal.FreeCoTaskMem(wideLabel);
                Marshal.FreeCoTaskMem(wideSpec);
            }
        }

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

    private static unsafe class ShellItemNative
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
