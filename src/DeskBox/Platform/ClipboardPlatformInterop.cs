using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

internal enum ClipboardPasteOutcome
{
    Pasted,
    CopiedFallback
}

internal readonly record struct ClipboardSourceAppInfo(
    string ExecutablePath,
    string DisplayName);

/// <summary>
/// Win32 clipboard monitoring, Windows privacy-format inspection, PNG/DIB
/// zero-loss extraction, source-process attribution, and safe Ctrl+V auto-paste.
/// All P/Invoke stays inside <c>DeskBox.Platform</c>.
/// </summary>
internal static class ClipboardPlatformInterop
{
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int MaxFriendlyAppNameCacheEntries = 512;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly int CurrentProcessId = Environment.ProcessId;
    private static readonly int[] RetryDelaysMs = [15, 35, 75];
    private static readonly ConcurrentDictionary<string, string> s_friendlyAppNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static uint s_excludeMonitorFormat;
    private static uint s_canIncludeHistoryFormat;
    private static uint s_viewerIgnoreFormat;
    private static uint s_preferredDropEffectFormat;
    private static uint s_pngFormat;
    private static uint s_htmlFormat;
    private static uint s_rtfFormat;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref uint lpdwSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private static void EnsureFormatsRegistered()
    {
        if (s_pngFormat != 0)
        {
            return;
        }

        s_excludeMonitorFormat = ClipboardNativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        s_canIncludeHistoryFormat = ClipboardNativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        s_viewerIgnoreFormat = ClipboardNativeMethods.RegisterClipboardFormat("Clipboard Viewer Ignore");
        s_preferredDropEffectFormat = ClipboardNativeMethods.RegisterClipboardFormat("Preferred DropEffect");
        s_pngFormat = ClipboardNativeMethods.RegisterClipboardFormat("PNG");
        s_htmlFormat = ClipboardNativeMethods.RegisterClipboardFormat("HTML Format");
        s_rtfFormat = ClipboardNativeMethods.RegisterClipboardFormat("Rich Text Format");
    }

    internal static bool IsShiftKeyPressed() =>
        (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    internal static ReadOnlySpan<int> OpenRetryDelaysMs => RetryDelaysMs;

    internal static bool TryOpenClipboardOnce() =>
        ClipboardNativeMethods.OpenClipboard(IntPtr.Zero);

    internal static bool TryOpenClipboardWithRetry() =>
        TryOpenClipboardOnce();

    /// <summary>
    /// Inspects standard Windows privacy clipboard formats and Explorer move-only drop flags
    /// in a single non-blocking OpenClipboard session, plus extracts UTF-16 text, HTML/RTF,
    /// or native PNG when no file/text payload takes precedence. Never blocks with Thread.Sleep
    /// and never eagerly copies CF_DIB into a multi-megabyte buffer.
    /// </summary>
    internal static bool TryReadNativeSnapshot(
        bool ignoreSystemPrivacyFormats,
        bool allowFiles,
        bool allowImages,
        long maxImageBytes,
        bool allowText,
        long maxTextBytes,
        out bool ignoredByPrivacyOrCut,
        out bool hasFilesFormat,
        out bool hasNonWhitespaceText,
        out string? unicodeText,
        out bool unicodeTextExceededLimit,
        out byte[]? rawPngBytes,
        out int pngWidth,
        out int pngHeight,
        out bool hasDibFormat,
        out string? htmlFragment,
        out string? rtfText)
    {
        ignoredByPrivacyOrCut = false;
        hasFilesFormat = false;
        hasNonWhitespaceText = false;
        unicodeText = null;
        unicodeTextExceededLimit = false;
        rawPngBytes = null;
        pngWidth = 0;
        pngHeight = 0;
        hasDibFormat = false;
        htmlFragment = null;
        rtfText = null;

        EnsureFormatsRegistered();

        if (!TryOpenClipboardOnce())
        {
            return false;
        }

        try
        {
            if (ignoreSystemPrivacyFormats)
            {
                if ((s_excludeMonitorFormat != 0 && ClipboardNativeMethods.IsClipboardFormatAvailable(s_excludeMonitorFormat)) ||
                    (s_viewerIgnoreFormat != 0 && ClipboardNativeMethods.IsClipboardFormatAvailable(s_viewerIgnoreFormat)))
                {
                    ignoredByPrivacyOrCut = true;
                    return true;
                }

                if (s_canIncludeHistoryFormat != 0 &&
                    ClipboardNativeMethods.IsClipboardFormatAvailable(s_canIncludeHistoryFormat) &&
                    TryReadClipboardDword(s_canIncludeHistoryFormat, out uint canInclude) &&
                    canInclude == 0)
                {
                    ignoredByPrivacyOrCut = true;
                    return true;
                }
            }

            hasFilesFormat = ClipboardNativeMethods.IsClipboardFormatAvailable(CF_HDROP);

            // Never record Explorer Ctrl+X (Cut / DROPEFFECT_MOVE without COPY) file operations.
            if (hasFilesFormat &&
                s_preferredDropEffectFormat != 0 &&
                ClipboardNativeMethods.IsClipboardFormatAvailable(s_preferredDropEffectFormat) &&
                TryReadClipboardDword(s_preferredDropEffectFormat, out uint dropEffect) &&
                (dropEffect & DROPEFFECT_MOVE) != 0 &&
                (dropEffect & DROPEFFECT_COPY) == 0)
            {
                ignoredByPrivacyOrCut = true;
                return true;
            }

            if (hasFilesFormat && allowFiles)
            {
                return true;
            }

            if (ClipboardNativeMethods.IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                TryReadClipboardUnicodeText(
                    allowText,
                    maxTextBytes,
                    out hasNonWhitespaceText,
                    out unicodeText,
                    out unicodeTextExceededLimit);
            }

            if (allowText || !hasNonWhitespaceText)
            {
                if (s_htmlFormat != 0 && ClipboardNativeMethods.IsClipboardFormatAvailable(s_htmlFormat))
                {
                    byte[]? htmlBytes = TryReadClipboardBytes(s_htmlFormat, maxTextBytes);
                    if (htmlBytes is { Length: > 0 })
                    {
                        htmlFragment = TrimEmbeddedNulls(Encoding.UTF8.GetString(htmlBytes));
                    }
                }

                if (s_rtfFormat != 0 && ClipboardNativeMethods.IsClipboardFormatAvailable(s_rtfFormat))
                {
                    byte[]? rtfBytes = TryReadClipboardBytes(s_rtfFormat, maxTextBytes);
                    if (rtfBytes is { Length: > 0 })
                    {
                        rtfText = TrimEmbeddedNulls(Encoding.ASCII.GetString(rtfBytes));
                    }
                }
            }

            if (allowImages && !hasFilesFormat && !hasNonWhitespaceText)
            {
                // Fast path: native PNG buffer (preserves alpha, zero full-image decode/re-encode).
                if (s_pngFormat != 0 && ClipboardNativeMethods.IsClipboardFormatAvailable(s_pngFormat))
                {
                    byte[]? candidatePng = TryReadClipboardBytes(s_pngFormat, maxImageBytes);
                    if (candidatePng is not null &&
                        TryParsePngDimensions(candidatePng, out int w, out int h) &&
                        w > 0 && h > 0)
                    {
                        rawPngBytes = candidatePng;
                        pngWidth = w;
                        pngHeight = h;
                    }
                }

                if (rawPngBytes is null)
                {
                    hasDibFormat = ClipboardNativeMethods.IsClipboardFormatAvailable(CF_DIB);
                }
            }

            return true;
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Reads <c>CF_DIB</c> in a single non-blocking <c>OpenClipboard</c> session directly into
    /// a single buffer with the 14-byte <c>BITMAPFILEHEADER</c> prepended (zero double-LOH copy).
    /// Returns <see langword="false"/> only when <c>OpenClipboard</c> fails so callers can retry asynchronously.
    /// </summary>
    internal static unsafe bool TryReadClipboardDibAsBmpOnce(long maxImageBytes, out byte[]? bmpBytes)
    {
        bmpBytes = null;
        if (!TryOpenClipboardOnce())
        {
            return false;
        }

        try
        {
            if (!ClipboardNativeMethods.IsClipboardFormatAvailable(CF_DIB))
            {
                return true;
            }

            IntPtr handle = ClipboardNativeMethods.GetClipboardData(CF_DIB);
            if (handle == IntPtr.Zero)
            {
                return true;
            }

            long dibSize = ClipboardNativeMethods.GlobalSize(handle).ToInt64();
            if (dibSize < 12 || dibSize > maxImageBytes || 14L + dibSize > int.MaxValue)
            {
                return true;
            }

            IntPtr locked = ClipboardNativeMethods.GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return true;
            }

            try
            {
                int dibLength = (int)dibSize;
                int headerInspectLength = Math.Min(dibLength, 40);
                var headerSpan = new ReadOnlySpan<byte>((void*)locked, headerInspectLength);
                if (!TryComputeDibPixelOffset(headerSpan, dibLength, out uint pixelOffset))
                {
                    return true;
                }

                int totalLength = 14 + dibLength;
                var result = new byte[totalLength];
                result[0] = (byte)'B';
                result[1] = (byte)'M';
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2, 4), (uint)totalLength);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10, 4), pixelOffset);
                Marshal.Copy(locked, result, 14, dibLength);
                bmpBytes = result;
                return true;
            }
            finally
            {
                ClipboardNativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            ClipboardNativeMethods.CloseClipboard();
        }
    }

    internal static ClipboardSourceAppInfo? DetectSourceApp(IntPtr fallbackExternalHwnd)
    {
        IntPtr ownerHwnd = GetClipboardOwner();
        if (TryGetProcessInfoFromHwnd(ownerHwnd, allowOwnProcess: false, out var ownerInfo))
        {
            return ownerInfo;
        }

        IntPtr foregroundHwnd = GetForegroundWindow();
        if (TryGetProcessInfoFromHwnd(foregroundHwnd, allowOwnProcess: false, out var fgInfo))
        {
            return fgInfo;
        }

        if (fallbackExternalHwnd != IntPtr.Zero &&
            TryGetProcessInfoFromHwnd(fallbackExternalHwnd, allowOwnProcess: false, out var lastInfo))
        {
            return lastInfo;
        }

        return null;
    }

    internal static bool IsEligibleExternalPasteTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)CurrentProcessId)
        {
            return false;
        }

        var classBuilder = new StringBuilder(64);
        int len = GetClassName(hwnd, classBuilder, classBuilder.Capacity);
        if (len > 0)
        {
            string className = classBuilder.ToString();
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return false;
            }
        }

        return true;
    }

    internal static unsafe ClipboardPasteOutcome TryPasteToActiveOrLastWindow(IntPtr lastExternalHwnd)
    {
        IntPtr targetHwnd = GetForegroundWindow();
        if (!IsEligibleExternalPasteTarget(targetHwnd))
        {
            if (!IsEligibleExternalPasteTarget(lastExternalHwnd))
            {
                return ClipboardPasteOutcome.CopiedFallback;
            }

            if (!SetForegroundWindow(lastExternalHwnd))
            {
                return ClipboardPasteOutcome.CopiedFallback;
            }

            Thread.Sleep(30);
            targetHwnd = GetForegroundWindow();
            if (!IsEligibleExternalPasteTarget(targetHwnd))
            {
                return ClipboardPasteOutcome.CopiedFallback;
            }
        }

        // Release Shift/Alt logically if physically held so Ctrl+V is interpreted cleanly
        // as standard paste (we already stripped formatting in clipboard payload when Shift was used).
        bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        int count = 4 + (shiftDown ? 1 : 0) + (altDown ? 1 : 0);
        INPUT* inputs = stackalloc INPUT[8];
        int idx = 0;

        if (shiftDown)
        {
            inputs[idx++] = CreateKeyInput(VK_SHIFT, KEYEVENTF_KEYUP);
        }

        if (altDown)
        {
            inputs[idx++] = CreateKeyInput(VK_MENU, KEYEVENTF_KEYUP);
        }

        inputs[idx++] = CreateKeyInput(VK_CONTROL, 0);
        inputs[idx++] = CreateKeyInput(VK_V, 0);
        inputs[idx++] = CreateKeyInput(VK_V, KEYEVENTF_KEYUP);
        inputs[idx++] = CreateKeyInput(VK_CONTROL, KEYEVENTF_KEYUP);

        uint sent = SendInput((uint)count, inputs, sizeof(INPUT));
        if (sent == (uint)count)
        {
            return ClipboardPasteOutcome.Pasted;
        }

        // Best-effort key-up safety so Ctrl or V can never stick on partial UIPI failure.
        INPUT* release = stackalloc INPUT[2];
        release[0] = CreateKeyInput(VK_V, KEYEVENTF_KEYUP);
        release[1] = CreateKeyInput(VK_CONTROL, KEYEVENTF_KEYUP);
        _ = SendInput(2, release, sizeof(INPUT));

        return ClipboardPasteOutcome.CopiedFallback;
    }

    private static INPUT CreateKeyInput(ushort vk, uint flags) =>
        new()
        {
            Type = INPUT_KEYBOARD,
            Data = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = vk,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };

    private static bool TryGetProcessInfoFromHwnd(
        IntPtr hwnd,
        bool allowOwnProcess,
        out ClipboardSourceAppInfo info)
    {
        info = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || (!allowOwnProcess && pid == (uint)CurrentProcessId))
        {
            return false;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(hProcess, 0, sb, ref size) || size == 0)
            {
                return false;
            }

            string exePath = sb.ToString(0, (int)size);
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            // Skip UWP ApplicationFrameHost wrapper when it has no meaningful identity of its own.
            string stem = Path.GetFileNameWithoutExtension(exePath);
            if (string.Equals(stem, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string displayName = ResolveFriendlyAppName(exePath, stem);
            info = new ClipboardSourceAppInfo(exePath, displayName);
            return true;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    internal static void ClearFriendlyAppNameCacheForTesting() =>
        s_friendlyAppNameCache.Clear();

    internal static string ResolveFriendlyAppName(string exePath, string? fallbackStem = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return fallbackStem ?? string.Empty;
        }

        if (s_friendlyAppNameCache.TryGetValue(exePath, out string? cached))
        {
            return cached;
        }

        string stem = fallbackStem ?? Path.GetFileNameWithoutExtension(exePath);
        string? resolved = null;
        try
        {
            if (File.Exists(exePath))
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                {
                    resolved = versionInfo.FileDescription.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                {
                    resolved = versionInfo.ProductName.Trim();
                }
            }
        }
        catch
        {
            // Ignore locked or inaccessible binaries and fall back to stem.
        }

        string finalName = !string.IsNullOrWhiteSpace(resolved)
            ? resolved
            : string.IsNullOrWhiteSpace(stem)
                ? exePath
                : stem;

        if (s_friendlyAppNameCache.Count >= MaxFriendlyAppNameCacheEntries)
        {
            s_friendlyAppNameCache.Clear();
        }

        s_friendlyAppNameCache.TryAdd(exePath, finalName);
        return finalName;
    }

    private static unsafe void TryReadClipboardUnicodeText(
        bool materializeText,
        long maxTextBytes,
        out bool hasNonWhitespaceText,
        out string? unicodeText,
        out bool exceededLimit)
    {
        hasNonWhitespaceText = false;
        unicodeText = null;
        exceededLimit = false;

        IntPtr handle = ClipboardNativeMethods.GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        long byteSize = ClipboardNativeMethods.GlobalSize(handle).ToInt64();
        if (byteSize < sizeof(char))
        {
            return;
        }

        IntPtr locked = ClipboardNativeMethods.GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return;
        }

        try
        {
            char* ptr = (char*)locked;
            long maxCharsInBuffer = Math.Min(byteSize / sizeof(char), int.MaxValue);
            int len = 0;
            while (len < maxCharsInBuffer && ptr[len] != '\0')
            {
                if (!hasNonWhitespaceText && !char.IsWhiteSpace(ptr[len]))
                {
                    hasNonWhitespaceText = true;
                    if (!materializeText)
                    {
                        return;
                    }
                }

                len++;
                if (hasNonWhitespaceText && len > maxTextBytes)
                {
                    exceededLimit = true;
                    return;
                }
            }

            if (!hasNonWhitespaceText || !materializeText || len <= 0)
            {
                return;
            }

            if (len > maxTextBytes)
            {
                exceededLimit = true;
                return;
            }

            unicodeText = new string(ptr, 0, len);
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(handle);
        }
    }

    private static bool TryReadClipboardDword(uint format, out uint value)
    {
        value = 0;
        IntPtr handle = ClipboardNativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        long size = ClipboardNativeMethods.GlobalSize(handle).ToInt64();
        if (size < sizeof(uint))
        {
            return false;
        }

        IntPtr locked = ClipboardNativeMethods.GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            value = unchecked((uint)Marshal.ReadInt32(locked));
            return true;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(handle);
        }
    }

    private static byte[]? TryReadClipboardBytes(uint format, long maxBytes)
    {
        IntPtr handle = ClipboardNativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        long size = ClipboardNativeMethods.GlobalSize(handle).ToInt64();
        if (size <= 0 || size > maxBytes || size > int.MaxValue)
        {
            return null;
        }

        IntPtr locked = ClipboardNativeMethods.GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new byte[(int)size];
            Marshal.Copy(locked, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            ClipboardNativeMethods.GlobalUnlock(handle);
        }
    }

    private static string TrimEmbeddedNulls(string value)
    {
        int nullIdx = value.IndexOf('\0');
        return nullIdx >= 0 ? value[..nullIdx] : value;
    }

    internal static bool TryParsePngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        // PNG signature (8 bytes) + IHDR length (4) + "IHDR" (4) + width (4) + height (4) = 24 bytes
        if (bytes.Length < 24)
        {
            return false;
        }

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!bytes[..8].SequenceEqual(signature) ||
            bytes[12] != (byte)'I' ||
            bytes[13] != (byte)'H' ||
            bytes[14] != (byte)'D' ||
            bytes[15] != (byte)'R')
        {
            return false;
        }

        uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        if (w == 0 || h == 0 || w > 32768 || h > 32768)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>
    /// Synthesizes a valid 14-byte BITMAPFILEHEADER in front of a raw CF_DIB buffer
    /// so Windows Imaging Component (BitmapDecoder) can decode screenshots from WeChat/QQ/WPS.
    /// </summary>
    internal static bool TryBuildBmpFromDib(ReadOnlySpan<byte> dibBytes, out byte[]? bmpBytes)
    {
        bmpBytes = null;
        if (!TryComputeDibPixelOffset(dibBytes, dibBytes.Length, out uint pixelOffset))
        {
            return false;
        }

        long totalLength = 14L + dibBytes.Length;
        if (totalLength > int.MaxValue)
        {
            return false;
        }

        var result = new byte[(int)totalLength];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10, 4), pixelOffset);
        dibBytes.CopyTo(result.AsSpan(14));
        bmpBytes = result;
        return true;
    }

    internal static bool TryComputeDibPixelOffset(
        ReadOnlySpan<byte> headerBytes,
        int fullDibLength,
        out uint pixelOffset)
    {
        pixelOffset = 0;
        if (fullDibLength < 12 || headerBytes.Length < 12)
        {
            return false;
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[..4]);
        if (headerSize < 12 || headerSize > (uint)fullDibLength)
        {
            return false;
        }

        ushort bitCount;
        uint compression = 0;
        uint clrUsed = 0;

        if (headerSize == 12)
        {
            // BITMAPCOREHEADER
            ushort width = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(4, 2));
            ushort height = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(6, 2));
            if (width == 0 || height == 0)
            {
                return false;
            }

            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(10, 2));
        }
        else
        {
            if (headerSize < 40 || fullDibLength < 40 || headerBytes.Length < 40)
            {
                return false;
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(4, 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(8, 4));
            if (width <= 0 || height == 0 || height == int.MinValue)
            {
                return false;
            }

            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(14, 2));
            compression = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(16, 4));
            clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(32, 4));
        }

        if (bitCount is not (0 or 1 or 2 or 4 or 8 or 16 or 24 or 32))
        {
            return false;
        }

        ulong paletteBytes = 0;
        try
        {
            checked
            {
                if (bitCount is 1 or 2 or 4 or 8)
                {
                    ulong entrySize = headerSize == 12 ? 3UL : 4UL;
                    ulong colors = clrUsed != 0 ? clrUsed : (1UL << bitCount);
                    paletteBytes = colors * entrySize;
                }
                else if (headerSize == 40 && compression == 3 /* BI_BITFIELDS */)
                {
                    paletteBytes = 12;
                }
                else if (headerSize == 40 && compression == 6 /* BI_ALPHABITFIELDS */)
                {
                    paletteBytes = 16;
                }
                else if (clrUsed != 0 && headerSize >= 40)
                {
                    paletteBytes = (ulong)clrUsed * 4UL;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (paletteBytes > (ulong)uint.MaxValue)
        {
            return false;
        }

        ulong candidateOffset = 14UL + headerSize + paletteBytes;
        ulong totalLength = 14UL + (ulong)fullDibLength;
        if (totalLength > int.MaxValue || candidateOffset > totalLength || candidateOffset > uint.MaxValue)
        {
            return false;
        }

        pixelOffset = (uint)candidateOffset;
        return true;
    }

    /// <summary>
    /// Message-only Win32 listener window + on-demand foreground-window tracker bound to the UI thread.
    /// </summary>
    internal sealed class ListenerSession : IDisposable
    {
        private readonly Action _onClipboardUpdate;
        private readonly WndProcDelegate _wndProc;
        private readonly WinEventDelegate _winEventProc;
        private IntPtr _hwnd;
        private IntPtr _foregroundHook;
        private IntPtr _lastExternalHwnd;
        private uint _lastSequenceNumber;
        private bool _disposed;

        public ListenerSession(Action onClipboardUpdate)
        {
            _onClipboardUpdate = onClipboardUpdate;
            _wndProc = WndProc;
            _winEventProc = OnForegroundWinEvent;
            _lastSequenceNumber = GetClipboardSequenceNumber();

            CaptureCurrentExternalForeground();

            string className = $"DeskBoxClipboardListener_{Guid.NewGuid():N}";
            IntPtr hInstance = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = className
            };

            _ = RegisterClassEx(ref wc);
            _hwnd = CreateWindowEx(
                0,
                className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                _ = AddClipboardFormatListener(_hwnd);
            }
        }

        public IntPtr LastExternalForegroundWindow => _lastExternalHwnd;

        public bool IsForegroundTrackingHookInstalled => _foregroundHook != IntPtr.Zero;

        public void CaptureCurrentExternalForeground()
        {
            if (_disposed)
            {
                return;
            }

            IntPtr currentFg = GetForegroundWindow();
            if (IsEligibleExternalPasteTarget(currentFg))
            {
                _lastExternalHwnd = currentFg;
            }
        }

        public void SetForegroundTrackingEnabled(bool enabled)
        {
            if (_disposed)
            {
                return;
            }

            if (enabled)
            {
                CaptureCurrentExternalForeground();
                if (_foregroundHook == IntPtr.Zero)
                {
                    _foregroundHook = SetWinEventHook(
                        EVENT_SYSTEM_FOREGROUND,
                        EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero,
                        _winEventProc,
                        0,
                        0,
                        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }
            }
            else if (_foregroundHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLIPBOARDUPDATE && !_disposed)
            {
                uint seq = GetClipboardSequenceNumber();
                if (seq != _lastSequenceNumber)
                {
                    _lastSequenceNumber = seq;
                    _onClipboardUpdate();
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void OnForegroundWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (!_disposed && IsEligibleExternalPasteTarget(hwnd))
            {
                _lastExternalHwnd = hwnd;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_foregroundHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                _ = RemoveClipboardFormatListener(_hwnd);
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
