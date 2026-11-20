using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Recycle-bin verbs, stock-icon location query, and shell change-notification
/// primitives shared by the namespace shortcut menu and Custom Widget RecycleBin cards.
/// Entry points are pinned with ExactSpelling + EntryPoint so a resolver
/// mismatch surfaces as a compile-time constant here rather than as a
/// first-call EntryPointNotFound that unit tests can never see.
/// </summary>
internal static class ShellRecycleBinInterop
{
    internal const string RecycleBinParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    internal const string RecycleBinShellOpenArg = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";

    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoSound = 0x00000004;

    private const uint SiidRecycler = 31;
    private const uint SiidRecyclerFull = 32;
    private const uint ShgsiIconLocation = 0;

    private const int ShcnrfInterruptLevel = 0x0001;
    private const int ShcnrfShellLevel = 0x0002;
    private const int ShcnrfNewDelivery = 0x8000;
    private const uint ShcneAllEvents = 0x7FFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long totalSize;
        public long totalItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        public fixed char szPath[260];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        public int fRecursive;
    }

    // A null root aggregates every drive's bin — what the desktop icon's
    // native menu reports and empties.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(
        string? rootPath,
        ref SHQUERYRBINFO queryInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(
        IntPtr ownerWindow,
        string? rootPath,
        uint flags);

    [DllImport("shell32.dll", ExactSpelling = true, EntryPoint = "SHGetStockIconInfo")]
    private static extern int SHGetStockIconInfo(
        uint siid,
        uint uFlags,
        ref SHSTOCKICONINFO psii);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHParseDisplayName")]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true, EntryPoint = "ILFree")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true, EntryPoint = "SHChangeNotifyRegister")]
    private static extern uint SHChangeNotifyRegister(
        IntPtr hwnd,
        int fSources,
        uint fEvents,
        uint wMsg,
        int cEntries,
        ref SHChangeNotifyEntry pshcne);

    [DllImport("shell32.dll", ExactSpelling = true, EntryPoint = "SHChangeNotifyDeregister")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint ulID);

    /// <summary>
    /// Total item count across all bins; false when the aggregate query
    /// itself fails (callers then keep the menu row enabled rather than
    /// graying a working verb).
    /// </summary>
    internal static bool TryQueryTotalItemCount(out long totalItems)
    {
        totalItems = 0;
        try
        {
            var info = new SHQUERYRBINFO
            {
                cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>()
            };
            if (SHQueryRecycleBin(null, ref info) < 0)
            {
                return false;
            }

            totalItems = Math.Max(0, info.totalItems);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Empties every bin with flags 0, so the system shows its own
    /// confirmation and progress dialogs — the same behavior as the native
    /// desktop verb.
    /// </summary>
    internal static bool TryEmptyAllBins(IntPtr ownerWindow)
    {
        try
        {
            return SHEmptyRecycleBin(ownerWindow, null, 0) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Empties every bin without a duplicate system confirmation prompt when
    /// the caller has already presented a dedicated confirmation dialog.
    /// Must run off the UI thread.
    /// </summary>
    internal static bool TryEmptyAllBinsConfirmed(IntPtr ownerWindow)
    {
        try
        {
            return SHEmptyRecycleBin(ownerWindow, null, SherbNoConfirmation | SherbNoSound) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the Windows stock Recycle Bin artwork (empty or full) via
    /// SHDefExtractIcon without binding third-party shell extensions in-process.
    /// </summary>
    internal static unsafe byte[]? ExtractRecycleBinStockIconPngBytes(bool isEmpty, int pixelSize = 32)
    {
        try
        {
            var sii = new SHSTOCKICONINFO
            {
                cbSize = (uint)sizeof(SHSTOCKICONINFO)
            };
            uint stockId = isEmpty ? SiidRecycler : SiidRecyclerFull;
            if (SHGetStockIconInfo(stockId, ShgsiIconLocation, ref sii) >= 0)
            {
                string iconModulePath = new(sii.szPath);
                if (!string.IsNullOrWhiteSpace(iconModulePath))
                {
                    byte[]? stockBytes = Helpers.IconHelper.GetIndexedIconPngBytes(
                        iconModulePath,
                        sii.iIcon,
                        pixelSize);
                    if (stockBytes is { Length: > 0 })
                    {
                        return stockBytes;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            string imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
            return Helpers.IconHelper.GetIndexedIconPngBytes(
                imageres,
                isEmpty ? -55 : -54,
                pixelSize);
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryRegisterRecycleBinChangeNotify(
        IntPtr hwnd,
        uint messageId,
        out uint registrationId)
    {
        registrationId = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr binPidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(RecycleBinParsingName, IntPtr.Zero, out binPidl, 0, out _) != 0 ||
                binPidl == IntPtr.Zero)
            {
                return false;
            }

            var entry = new SHChangeNotifyEntry
            {
                pidl = binPidl,
                fRecursive = 1
            };
            registrationId = SHChangeNotifyRegister(
                hwnd,
                ShcnrfInterruptLevel | ShcnrfShellLevel | ShcnrfNewDelivery,
                ShcneAllEvents,
                messageId,
                1,
                ref entry);
            return registrationId != 0;
        }
        catch
        {
            registrationId = 0;
            return false;
        }
        finally
        {
            if (binPidl != IntPtr.Zero)
            {
                ILFree(binPidl);
            }
        }
    }

    internal static void UnregisterRecycleBinChangeNotify(uint registrationId)
    {
        if (registrationId == 0)
        {
            return;
        }

        try
        {
            _ = SHChangeNotifyDeregister(registrationId);
        }
        catch
        {
        }
    }

    internal readonly record struct RecycleBinChangeWatcherHandle(IntPtr Hwnd, uint RegistrationId)
    {
        public bool IsValid => Hwnd != IntPtr.Zero && RegistrationId != 0;
    }

    private const string NotifyWindowClassName = "DeskBoxCustomWidgetRecycleNotifyWindow";
    private const uint NotifyWindowMessage = 0x8000 + 0x019A; // WM_APP + 0x19A
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    private static readonly object s_windowClassGate = new();
    private static bool s_windowClassRegistered;
    private static readonly ConcurrentDictionary<IntPtr, Action> s_notifyCallbacks = new();

    internal static unsafe bool TryStartRecycleBinChangeWatcher(
        Action onChanged,
        out RecycleBinChangeWatcherHandle handle)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        handle = default;

        try
        {
            IntPtr hInstance = GetModuleHandleW(null);
            lock (s_windowClassGate)
            {
                if (!s_windowClassRegistered)
                {
                    fixed (char* classNamePtr = NotifyWindowClassName)
                    {
                        var wc = new WNDCLASSEXW
                        {
                            cbSize = (uint)sizeof(WNDCLASSEXW),
                            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&NotifyWndProc,
                            hInstance = hInstance,
                            lpszClassName = (IntPtr)classNamePtr
                        };
                        if (RegisterClassExW(&wc) != 0)
                        {
                            s_windowClassRegistered = true;
                        }
                    }
                }
            }

            IntPtr hwnd = CreateWindowExW(
                WsExToolWindow,
                NotifyWindowClassName,
                string.Empty,
                WsPopup,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            s_notifyCallbacks[hwnd] = onChanged;
            if (TryRegisterRecycleBinChangeNotify(hwnd, NotifyWindowMessage, out uint regId))
            {
                handle = new RecycleBinChangeWatcherHandle(hwnd, regId);
                return true;
            }

            s_notifyCallbacks.TryRemove(hwnd, out _);
            _ = DestroyWindow(hwnd);
        }
        catch (Exception ex)
        {
            App.Log($"[RecycleBin] Native watcher setup failed: {ex.Message}");
        }

        return false;
    }

    internal static void StopRecycleBinChangeWatcher(RecycleBinChangeWatcherHandle handle)
    {
        if (handle.RegistrationId != 0)
        {
            UnregisterRecycleBinChangeNotify(handle.RegistrationId);
        }

        if (handle.Hwnd != IntPtr.Zero)
        {
            s_notifyCallbacks.TryRemove(handle.Hwnd, out _);
            try
            {
                _ = DestroyWindow(handle.Hwnd);
            }
            catch
            {
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr NotifyWndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == NotifyWindowMessage)
        {
            if (s_notifyCallbacks.TryGetValue(hWnd, out Action? cb))
            {
                try
                {
                    cb();
                }
                catch
                {
                }
            }
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    internal static void OpenRecycleBinInExplorer()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", RecycleBinShellOpenArg)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Log($"[RecycleBin] Failed to open Explorer Recycle Bin: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true)]
    private static extern unsafe ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
}
