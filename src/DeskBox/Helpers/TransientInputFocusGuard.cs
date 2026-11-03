using System.Runtime.CompilerServices;
using System.Text;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using static DeskBox.Platform.MonitorAndBackdropInterop;

namespace DeskBox.Helpers;

/// <summary>
/// Shared Win32 + XamlRoot keyboard/IME focus activation and transient focus-steal
/// classification (Windows Emoji Picker <c>Win + .</c>, out-of-process CJK IME candidate
/// windows, and WinUI <c>TextCommandBarFlyout</c> / <c>PopupWindowSiteBridge</c> menus).
/// </summary>
internal static class TransientInputFocusGuard
{
    private const uint GwChild = 5;
    private const uint GaRoot = 2;
    private const long HwndClassificationCacheTtlMs = 1500;

    private struct CachedHwndClassification
    {
        public IntPtr Hwnd;
        public long ValidatedAtTick;
        public bool IsTextInputExperience;
    }

    private static readonly CachedHwndClassification[] s_hwndCache = new CachedHwndClassification[4];
    private static int s_hwndCacheWriteIndex;
    private static readonly object s_cacheLock = new();
    private static readonly ConditionalWeakTable<Control, DispatcherQueueTimer> s_blurWatchers = new();

    public static void ActivateWindowAndFocusIsland(
        Window? window,
        IntPtr windowHandle,
        bool clearNoActivate = true)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        if (clearNoActivate)
        {
            ClearNoActivateStyle(windowHandle);
        }

        _ = Win32Helper.AllowSetForegroundWindow((uint)Environment.ProcessId);
        _ = SetForegroundWindow(windowHandle);
        _ = SetActiveWindow(windowHandle);
        window?.Activate();
        FocusContentIsland(windowHandle);
    }

    public static void SetActiveAndFocusIsland(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        _ = SetActiveWindow(windowHandle);
        FocusContentIsland(windowHandle);
    }

    public static void FocusContentIsland(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        IntPtr child = GetWindow(windowHandle, GwChild);
        _ = SetFocus(child != IntPtr.Zero ? child : windowHandle);
    }

    public static void FocusContentIslandIfForeground(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero && Win32Helper.GetForegroundWindow() == windowHandle)
        {
            FocusContentIsland(windowHandle);
        }
    }

    public static void ClearNoActivateStyle(IntPtr windowHandle)
    {
        int exStyle = Win32Helper.GetWindowLong(windowHandle, Win32Helper.GWL_EXSTYLE);
        if ((exStyle & Win32Helper.WS_EX_NOACTIVATE) != 0)
        {
            _ = Win32Helper.SetWindowLong(
                windowHandle,
                Win32Helper.GWL_EXSTYLE,
                exStyle & ~Win32Helper.WS_EX_NOACTIVATE);
        }
    }

    public static bool IsInsideTextInputControl(DependencyObject? source)
    {
        for (DependencyObject? cur = source; cur is not null; cur = SafeGetParent(cur))
        {
            if (cur is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasOpenPopupsInXamlRoot(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return false;
        }

        try
        {
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCurrentProcessPopupBridge(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd))
        {
            return false;
        }

        IntPtr root = Win32Helper.GetAncestor(hwnd, GaRoot);
        IntPtr candidate = root != IntPtr.Zero ? root : hwnd;

        IReadOnlyList<IntPtr> bridges = Win32Helper.GetVisiblePopupBridgeWindows();
        for (int i = 0; i < bridges.Count; i++)
        {
            IntPtr bridge = bridges[i];
            if (bridge == candidate || bridge == hwnd)
            {
                return true;
            }
        }

        _ = Win32Helper.GetWindowThreadProcessId(candidate, out uint pid);
        if (pid == (uint)Environment.ProcessId)
        {
            var sb = new StringBuilder(32);
            if (Win32Helper.GetClassName(candidate, sb, sb.Capacity) > 0 &&
                string.Equals(sb.ToString(), "#32768", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsWindowsTextInputExperienceWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd))
        {
            return false;
        }

        long now = Environment.TickCount64;
        lock (s_cacheLock)
        {
            for (int i = 0; i < s_hwndCache.Length; i++)
            {
                ref CachedHwndClassification entry = ref s_hwndCache[i];
                if (entry.Hwnd == hwnd && now - entry.ValidatedAtTick < HwndClassificationCacheTtlMs)
                {
                    return entry.IsTextInputExperience;
                }
            }
        }

        bool result = ClassifyWindowsTextInputExperienceWindowCore(hwnd);
        lock (s_cacheLock)
        {
            s_hwndCache[s_hwndCacheWriteIndex] = new CachedHwndClassification
            {
                Hwnd = hwnd,
                ValidatedAtTick = now,
                IsTextInputExperience = result
            };
            s_hwndCacheWriteIndex = (s_hwndCacheWriteIndex + 1) % s_hwndCache.Length;
        }

        return result;
    }

    internal static bool IsKnownTextInputProcessOrClass(string className, string processName)
    {
        if (string.IsNullOrEmpty(className) && string.IsNullOrEmpty(processName))
        {
            return false;
        }

        // OS-standard IMM32 / TSF candidate & status UI window classes.
        if (string.Equals(className, "IME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("Microsoft.IME.UIManager.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        // Windows 10/11 Modern Emoji / Clipboard / Voice / Candidate host & Touch Keyboard.
        if (string.Equals(processName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "TabTip", StringComparison.OrdinalIgnoreCase))
        {
            return className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("InputSite", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("IPTip_Main_Window", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("IME", StringComparison.OrdinalIgnoreCase);
        }

        // First-party & major CJK out-of-process IME candidate/toolbar hosts.
        return string.Equals(processName, "chsime", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "chtspime", StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith("imjp", StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith("imkr", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "SogouCloud", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "SGTool", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "WeType", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "WeChatInput", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "WeChatIME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "BaiduPinyin", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "QQPYUserCenter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "WeaselServer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guards inline rename/edit <c>LostFocus</c> handlers against premature teardown when:
    /// (1) the editor was just opened (&lt; 300ms grace),
    /// (2) the user right-clicked the editor (<c>TextCommandBarFlyout</c> / <c>PopupWindowSiteBridge</c>), or
    /// (3) the user opened <c>Win + .</c> Emoji Picker or clicked an out-of-process IME candidate bar.
    /// When deferred due to (2) or (3), watches until the transient surface closes and either restores
    /// focus to <paramref name="editor"/> (if the host window stays active) or invokes
    /// <paramref name="commitOnExternalDismissAsync"/> (if the user clicked an external app).
    /// </summary>
    public static bool ShouldDeferInlineEditorBlurCommit(
        Control? editor,
        long openedAtTick,
        IntPtr hostWindowHandle = default,
        Func<bool>? isStillEditing = null,
        Func<Task>? commitOnExternalDismissAsync = null)
    {
        if (editor is TextBox tb &&
            openedAtTick != 0 &&
            InlineEditorFocus.TryRecoverFocusWithinGrace(openedAtTick, tb))
        {
            return true;
        }

        if (editor is null)
        {
            return false;
        }

        bool hasXamlPopup = HasOpenPopupsInXamlRoot(editor.XamlRoot);
        IntPtr fg = Win32Helper.GetForegroundWindow();
        bool hasBridgeOrIme = IsCurrentProcessPopupBridge(fg) || IsWindowsTextInputExperienceWindow(fg);
        if (!hasXamlPopup && !hasBridgeOrIme)
        {
            return false;
        }

        if (isStillEditing is not null && commitOnExternalDismissAsync is not null)
        {
            IntPtr resolvedHost = hostWindowHandle != IntPtr.Zero
                ? hostWindowHandle
                : ResolveOwnerWindowHandle(editor);
            StartTransientBlurWatcher(editor, resolvedHost, isStillEditing, commitOnExternalDismissAsync);
        }

        return true;
    }

    public static IntPtr ResolveOwnerWindowHandle(DependencyObject? element)
    {
        if (element is FrameworkElement fe &&
            DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(fe) is var resolved &&
            resolved != IntPtr.Zero)
        {
            return resolved;
        }

        return Win32Helper.GetForegroundWindow();
    }

    public static void WatchPopupWindowInputExperienceFocus(
        DispatcherQueue? dispatcherQueue,
        IntPtr hostWindowHandle,
        Func<bool> isStillOpen,
        Action onDismissedExternally)
    {
        if (dispatcherQueue is null || hostWindowHandle == IntPtr.Zero)
        {
            return;
        }

        DispatcherQueueTimer timer = dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (!isStillOpen() || !Win32Helper.IsWindow(hostWindowHandle))
            {
                timer.Stop();
                return;
            }

            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (fg == hostWindowHandle)
            {
                FocusContentIsland(hostWindowHandle);
                timer.Stop();
                return;
            }

            if (IsWindowsTextInputExperienceWindow(fg) || IsCurrentProcessPopupBridge(fg))
            {
                return;
            }

            if (fg == IntPtr.Zero && IsCursorInsideWindow(hostWindowHandle))
            {
                return;
            }

            timer.Stop();
            onDismissedExternally();
        };
        timer.Start();
    }

    private static void StartTransientBlurWatcher(
        Control editor,
        IntPtr hostWindowHandle,
        Func<bool> isStillEditing,
        Func<Task> commitOnExternalDismissAsync)
    {
        DispatcherQueue? dq = editor.DispatcherQueue;
        if (dq is null)
        {
            return;
        }

        if (s_blurWatchers.TryGetValue(editor, out DispatcherQueueTimer? existingTimer))
        {
            existingTimer.Stop();
            s_blurWatchers.Remove(editor);
        }

        DispatcherQueueTimer timer = dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = true;
        s_blurWatchers.AddOrUpdate(editor, timer);

        void StopWatcher()
        {
            timer.Stop();
            s_blurWatchers.Remove(editor);
        }

        timer.Tick += async (_, _) =>
        {
            if (!isStillEditing() || !editor.IsLoaded || editor.Visibility != Visibility.Visible)
            {
                StopWatcher();
                return;
            }

            if (editor.FocusState != FocusState.Unfocused)
            {
                // Focus already returned to the editor; future LostFocus will handle commit.
                StopWatcher();
                return;
            }

            bool hasXamlPopup = HasOpenPopupsInXamlRoot(editor.XamlRoot);
            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (hasXamlPopup || IsCurrentProcessPopupBridge(fg) || IsWindowsTextInputExperienceWindow(fg))
            {
                return;
            }

            StopWatcher();

            if (hostWindowHandle != IntPtr.Zero &&
                (fg == hostWindowHandle ||
                 (fg == IntPtr.Zero && IsCursorInsideWindow(hostWindowHandle))))
            {
                _ = editor.Focus(FocusState.Programmatic);
                FocusContentIsland(hostWindowHandle);
                return;
            }

            await commitOnExternalDismissAsync();
        };

        timer.Start();
    }

    private static bool IsCursorInsideWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero &&
        Win32Helper.GetCursorPos(out Win32Helper.POINT pt) &&
        Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT r) &&
        pt.X >= r.Left && pt.X < r.Right &&
        pt.Y >= r.Top && pt.Y < r.Bottom;

    private static bool ClassifyWindowsTextInputExperienceWindowCore(IntPtr hwnd)
    {
        var classBuf = new StringBuilder(96);
        string className = Win32Helper.GetClassName(hwnd, classBuf, classBuf.Capacity) > 0
            ? classBuf.ToString()
            : string.Empty;

        if (string.Equals(className, "IME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("Microsoft.IME.UIManager.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _ = Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        string procName = ProcessImageNameResolver.GetProcessName(pid);
        if (string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(className, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr child = GetWindow(hwnd, GwChild);
            if (child != IntPtr.Zero)
            {
                _ = Win32Helper.GetWindowThreadProcessId(child, out uint childPid);
                if (childPid != 0 && childPid != pid)
                {
                    procName = ProcessImageNameResolver.GetProcessName(childPid);
                }
            }
        }

        return IsKnownTextInputProcessOrClass(className, procName);
    }

    private static DependencyObject? SafeGetParent(DependencyObject node)
    {
        try
        {
            return VisualTreeHelper.GetParent(node);
        }
        catch
        {
            return null;
        }
    }
}
