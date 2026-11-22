using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Services.DragDrop;

internal enum OutboundDragDriverKind
{
    WinRtXaml = 0,
    Win10NativeShell = 1,
    ElevatedPointer = 2,
}

internal readonly record struct NativeOleDragEffects(
    uint AllowedEffects,
    uint PreferredDropEffect);

/// <summary>
/// Pure routing and native OLE effect policy for outbound file drags across
/// Windows 10, Windows 11, touch/pen, and elevated sessions.
/// </summary>
internal static class OutboundDragRoutePolicy
{
    internal const uint DropEffectCopy = 1;
    internal const uint DropEffectMove = 2;
    internal const uint DropEffectLink = 4;
    internal const uint DropEffectAll = DropEffectCopy | DropEffectMove | DropEffectLink;
    internal const uint DropEffectCopyLink = DropEffectCopy | DropEffectLink;

    private static readonly Lazy<bool> s_forceWin10ShellDrag = new(static () =>
        string.Equals(
            Environment.GetEnvironmentVariable("DESKBOX_FORCE_WIN10_SHELL_DRAG"),
            "1",
            StringComparison.Ordinal));

    private static readonly Lazy<bool> s_disableWin10ShellDrag = new(static () =>
        string.Equals(
            Environment.GetEnvironmentVariable("DESKBOX_DISABLE_WIN10_SHELL_DRAG"),
            "1",
            StringComparison.Ordinal));

    /// <summary>
    /// WinUI 3 (<c>Microsoft.UI.Input</c> / <c>Microsoft.ui.xaml</c>) enables
    /// Mouse-in-Pointer (<c>EnableMouseInPointerForWindow</c>), which delivers
    /// active mouse motion as <c>WM_POINTERUPDATE</c> rather than legacy
    /// <c>WM_MOUSEMOVE</c> and starves in-process <c>ole32!DoDragDrop</c>
    /// (<c>SHDoDragDrop</c>) on both Windows 10 and Windows 11. Non-elevated
    /// outbound drags therefore default to WinUI <c>StartDragAsync</c>
    /// (<see cref="OutboundDragDriverKind.WinRtXaml"/>) on every OS unless
    /// explicitly opted into <c>SHDoDragDrop</c> via
    /// <c>DESKBOX_FORCE_WIN10_SHELL_DRAG=1</c> for diagnostics.
    /// </summary>
    internal static bool ShouldUseWin10NativeShellForProcess(
        bool? isElevatedOverride = null,
        bool? isWindows11OrLaterOverride = null,
        bool? forceOverride = null,
        bool? disableOverride = null)
    {
        _ = isWindows11OrLaterOverride;
        bool isElevated = isElevatedOverride ??
            DragDropPermissionService.IsCurrentProcessElevatedCached();
        if (isElevated)
        {
            return false;
        }

        bool disabled = disableOverride ?? s_disableWin10ShellDrag.Value;
        if (disabled)
        {
            return false;
        }

        return forceOverride ?? s_forceWin10ShellDrag.Value;
    }

    internal static OutboundDragDriverKind ResolveDriverKind(
        bool isElevated,
        bool isWindows11OrLater,
        PointerDeviceType pointerDeviceType,
        bool forceWin10ShellDrag = false,
        bool disableWin10ShellDrag = false)
    {
        _ = isWindows11OrLater;
        if (isElevated)
        {
            return OutboundDragDriverKind.ElevatedPointer;
        }

        if (forceWin10ShellDrag &&
            !disableWin10ShellDrag &&
            pointerDeviceType is PointerDeviceType.Mouse or PointerDeviceType.Touchpad)
        {
            return OutboundDragDriverKind.Win10NativeShell;
        }

        return OutboundDragDriverKind.WinRtXaml;
    }

    /// <summary>
    /// Computes the native Win32 OLE (<c>dwOKEffects</c>, <c>Preferred DropEffect</c>) pair
    /// for <c>SHDoDragDrop</c>. Unlike Win10's brokered WinRT <c>StartDragAsync</c>, native
    /// <c>SHDoDragDrop</c> preserves mouse button state so Explorer never opens its
    /// <c>#32768</c> popup when advertising multiple effects, and honors
    /// <c>Preferred DropEffect</c> (gated by <see cref="TargetAwareDropEffectPolicy"/>).
    /// </summary>
    internal static NativeOleDragEffects ResolveNativeOleDragEffects(
        string? dragOutAction,
        bool isProtectedCopyOnly,
        bool isDefaultCopySpecialMedia,
        bool hasAppExecutionAlias)
    {
        if (hasAppExecutionAlias)
        {
            return new NativeOleDragEffects(DropEffectLink, DropEffectLink);
        }

        if (isProtectedCopyOnly)
        {
            return new NativeOleDragEffects(DropEffectCopyLink, DropEffectCopy);
        }

        if (isDefaultCopySpecialMedia)
        {
            return new NativeOleDragEffects(DropEffectAll, DropEffectCopy);
        }

        return dragOutAction switch
        {
            SettingsService.ManagedDragOutActionCopy =>
                new NativeOleDragEffects(DropEffectAll, DropEffectCopy),
            SettingsService.ManagedDragOutActionMove =>
                new NativeOleDragEffects(DropEffectAll, DropEffectMove),
            _ =>
                new NativeOleDragEffects(DropEffectAll, 0),
        };
    }

    internal static DataPackageOperation ToAllowedDataPackageOperation(uint allowedEffects)
    {
        DataPackageOperation op = DataPackageOperation.None;
        if ((allowedEffects & DropEffectCopy) != 0)
        {
            op |= DataPackageOperation.Copy;
        }

        if ((allowedEffects & DropEffectMove) != 0)
        {
            op |= DataPackageOperation.Move;
        }

        if ((allowedEffects & DropEffectLink) != 0)
        {
            op |= DataPackageOperation.Link;
        }

        return op;
    }
}
