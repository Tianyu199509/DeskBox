using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static DeskBox.Platform.MonitorAndBackdropInterop;

namespace DeskBox.Helpers;

/// <summary>
/// WinUI hosts a <see cref="Flyout"/> shown with
/// <c>ShouldConstrainToRootBounds = false</c> in a per-show
/// "Microsoft.UI.Content.PopupWindowSiteBridge" window. Those bridges are
/// created with WS_EX_NOACTIVATE, so a TextBox inside one receives raw ASCII
/// keystrokes but never gets a TSF/IME context — CJK composition is dropped
/// while the editor otherwise looks healthy.
///
/// <para>StackPopoverHostWindow.ActivateInput already applies this exact
/// recipe to a window it owns (strip no-activate, claim foreground, focus the
/// content island). This helper is the same unit for bridges WinUI creates on
/// our behalf, which the widget window cannot reach through
/// <c>RootElement.GotFocus</c>: flyout content lives under the XamlRoot popup
/// root, not in the widget's visual tree, so the desktop-pinned keyboard
/// activation there never fires for a flyout editor.</para>
/// </summary>
internal static class FlyoutTextInputActivation
{
    private const uint GwChild = 5;

    /// <summary>
    /// Called from a GotFocus handler attached to the flyout's own content
    /// root, i.e. the earliest moment the editor actually holds focus. Returns
    /// true when a bridge was claimed.
    /// </summary>
    public static bool ActivateVisibleBridges(string logScope, object? originalSource = null)
    {
        try
        {
            if (originalSource is DependencyObject sourceDep &&
                !IsInsideTextInputControl(sourceDep))
            {
                return false;
            }

            IReadOnlyList<IntPtr> bridges = Win32Helper.GetVisiblePopupBridgeWindows();
            if (bridges.Count == 0)
            {
                App.Log(
                    $"[{logScope}] no visible popup bridge; editor window unknown " +
                    $"(foreground=0x{Win32Helper.GetForegroundWindow().ToInt64():X})");
                return false;
            }

            IntPtr foregroundBefore = Win32Helper.GetForegroundWindow();
            for (int i = 0; i < bridges.Count; i++)
            {
                if (bridges[i] == foregroundBefore)
                {
                    ClearNoActivate(foregroundBefore);
                    IntPtr activeIsland = GetWindow(foregroundBefore, GwChild);
                    if (activeIsland != IntPtr.Zero)
                    {
                        _ = SetFocus(activeIsland);
                    }
                    return true;
                }
            }

            IntPtr targetBridge = SelectTargetBridge(bridges);
            ClearNoActivate(targetBridge);
            _ = Win32Helper.AllowSetForegroundWindow((uint)Environment.ProcessId);
            _ = SetForegroundWindow(targetBridge);
            _ = SetActiveWindow(targetBridge);
            IntPtr island = GetWindow(targetBridge, GwChild);
            if (island != IntPtr.Zero)
            {
                _ = SetFocus(island);
            }

            App.Log(
                $"[{logScope}] bridges={bridges.Count} target=0x{targetBridge.ToInt64():X} " +
                $"before=0x{foregroundBefore.ToInt64():X} " +
                $"after=0x{Win32Helper.GetForegroundWindow().ToInt64():X}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[{logScope}] bridge activation failed: {ex.Message}");
            return false;
        }
    }

    private static IntPtr SelectTargetBridge(IReadOnlyList<IntPtr> bridges)
    {
        if (bridges.Count == 1)
        {
            return bridges[0];
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            for (int i = 0; i < bridges.Count; i++)
            {
                IntPtr candidate = bridges[i];
                if (Win32Helper.GetWindowRect(candidate, out Win32Helper.RECT rect) &&
                    pt.X >= rect.Left && pt.X < rect.Right &&
                    pt.Y >= rect.Top && pt.Y < rect.Bottom)
                {
                    return candidate;
                }
            }
        }

        return bridges[0];
    }

    private static bool IsInsideTextInputControl(DependencyObject? source)
    {
        DependencyObject? current = source;
        for (int depth = 0; depth < 12 && current is not null; depth++)
        {
            if (current is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static void ClearNoActivate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !Win32Helper.IsWindow(hWnd))
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
        if ((extendedStyle & Win32Helper.WS_EX_NOACTIVATE) == 0)
        {
            return;
        }

        _ = Win32Helper.SetWindowLong(
            hWnd,
            Win32Helper.GWL_EXSTYLE,
            extendedStyle & ~Win32Helper.WS_EX_NOACTIVATE);
        App.Log($"[FlyoutIme] cleared WS_EX_NOACTIVATE bridge=0x{hWnd.ToInt64():X}");
    }
}
