using DeskBox.Platform;

namespace DeskBox.Services.DragDrop;

internal enum OutboundMoveTargetTrust
{
    Untrusted = 0,
    ExplorerFolderWindow = 1,
    DesktopOrDeskBox = 2,
}

/// <summary>
/// Target-aware Fail-Closed policy for OLE <c>Preferred DropEffect</c> on outbound drags.
/// When DeskBox is configured to prefer <c>Move</c>:
/// - Verified Desktop (<c>Progman</c>/<c>WorkerW</c>) and DeskBox's own windows receive
///   <c>DROPEFFECT_MOVE</c> so Desktop ↔ DeskBox transfers move cleanly even across drives.
/// - Explorer folder windows (<c>CabinetWClass</c>/<c>ExploreWClass</c>) receive unset
///   (<c>DROPEFFECT_NONE</c>, <c>DV_E_FORMATETC</c>) so Windows Explorer applies its native
///   same-volume Move / cross-volume Copy rule — protecting USB drives and NAS shares from
///   accidental cross-volume Move deletions while same-drive folders still move normally.
/// - Every third-party target (WeChat, QQ, browsers, Electron apps, editors, unknown windows,
///   taskbar/tray) receives <c>DROPEFFECT_COPY</c> so plain drags never trigger receiver-side
///   deletion of originals.
/// </summary>
internal static class TargetAwareDropEffectPolicy
{
    internal const uint DropEffectNone = 0;
    internal const uint DropEffectCopy = 1;
    internal const uint DropEffectMove = 2;
    internal const uint DropEffectLink = 4;

    /// <summary>
    /// Resolves the effective <c>Preferred DropEffect</c> value to answer for a reader's
    /// <c>IDataObject::GetData</c> / <c>GetDataHere</c> query.
    /// Non-Move values (<c>None</c>, <c>Copy</c>, <c>Link</c>) pass through unchanged;
    /// <c>Move</c> maps <see cref="OutboundMoveTargetTrust.DesktopOrDeskBox"/> to
    /// <c>DROPEFFECT_MOVE</c>, <see cref="OutboundMoveTargetTrust.ExplorerFolderWindow"/> to
    /// <c>DROPEFFECT_NONE</c> (unset — Explorer native same-volume Move / cross-volume Copy),
    /// and <see cref="OutboundMoveTargetTrust.Untrusted"/> to <c>DROPEFFECT_COPY</c>.
    /// </summary>
    internal static uint ResolvePreferredDropEffect(
        uint configuredPreferredEffect,
        Func<OutboundMoveTargetTrust>? targetTrustProbe)
    {
        if (configuredPreferredEffect != DropEffectMove)
        {
            return configuredPreferredEffect;
        }

        try
        {
            OutboundMoveTargetTrust trust = targetTrustProbe is not null
                ? targetTrustProbe()
                : ClassifyCurrentCursorMoveTargetTrust();
            return trust switch
            {
                OutboundMoveTargetTrust.DesktopOrDeskBox => DropEffectMove,
                OutboundMoveTargetTrust.ExplorerFolderWindow => DropEffectNone,
                _ => DropEffectCopy,
            };
        }
        catch
        {
            return DropEffectCopy;
        }
    }

    internal static uint ResolvePreferredDropEffect(
        uint configuredPreferredEffect,
        Func<bool>? isTrustedMoveTargetProbe)
    {
        if (isTrustedMoveTargetProbe is null)
        {
            return ResolvePreferredDropEffect(configuredPreferredEffect, (Func<OutboundMoveTargetTrust>?)null);
        }

        return ResolvePreferredDropEffect(
            configuredPreferredEffect,
            () => isTrustedMoveTargetProbe()
                ? OutboundMoveTargetTrust.DesktopOrDeskBox
                : OutboundMoveTargetTrust.Untrusted);
    }

    internal static uint ResolvePreferredDropEffect(uint configuredPreferredEffect) =>
        ResolvePreferredDropEffect(configuredPreferredEffect, (Func<OutboundMoveTargetTrust>?)null);

    /// <summary>
    /// Pure classification predicate deciding how a target window should receive
    /// an outbound <c>Move</c> preference (Desktop/DeskBox forced Move, Explorer
    /// folder window native volume rule, or untrusted Copy).
    /// Checks <paramref name="rootPid"/> rather than a child window's PID so wallpaper
    /// engines embedded inside Explorer's <c>WorkerW</c> still classify as Desktop, while
    /// non-Explorer processes hosting spoofed class names stay fail-closed.
    /// </summary>
    internal static OutboundMoveTargetTrust ClassifyMoveTargetWindow(
        IntPtr hitHwnd,
        IntPtr rootHwnd,
        uint rootPid,
        uint shellWindowPid,
        bool isCurrentProcess,
        bool isSettingsHostProcess,
        string rootClass,
        string hitClass,
        Func<uint, bool> isExplorerProcessProbe)
    {
        if (hitHwnd == IntPtr.Zero && rootHwnd == IntPtr.Zero)
        {
            return OutboundMoveTargetTrust.Untrusted;
        }

        if (isCurrentProcess || isSettingsHostProcess)
        {
            return OutboundMoveTargetTrust.DesktopOrDeskBox;
        }

        if (DesktopToastHudInterop.IsExplorerTaskbarOrTrayClass(hitClass) ||
            DesktopToastHudInterop.IsExplorerTaskbarOrTrayClass(rootClass))
        {
            return OutboundMoveTargetTrust.Untrusted;
        }

        bool isExplorerRoot = rootPid != 0 &&
            ((shellWindowPid != 0 && rootPid == shellWindowPid) ||
             isExplorerProcessProbe(rootPid));
        if (!isExplorerRoot)
        {
            return OutboundMoveTargetTrust.Untrusted;
        }

        if (DesktopToastHudInterop.IsDesktopRootWindowClass(rootClass))
        {
            return OutboundMoveTargetTrust.DesktopOrDeskBox;
        }

        if (rootClass.Equals("CabinetWClass", StringComparison.Ordinal) ||
            rootClass.Equals("ExploreWClass", StringComparison.Ordinal))
        {
            return OutboundMoveTargetTrust.ExplorerFolderWindow;
        }

        return OutboundMoveTargetTrust.Untrusted;
    }

    internal static bool IsTrustedMoveTargetWindow(
        IntPtr hitHwnd,
        IntPtr rootHwnd,
        uint rootPid,
        uint shellWindowPid,
        bool isCurrentProcess,
        bool isSettingsHostProcess,
        string rootClass,
        string hitClass,
        Func<uint, bool> isExplorerProcessProbe) =>
        ClassifyMoveTargetWindow(
            hitHwnd,
            rootHwnd,
            rootPid,
            shellWindowPid,
            isCurrentProcess,
            isSettingsHostProcess,
            rootClass,
            hitClass,
            isExplorerProcessProbe) != OutboundMoveTargetTrust.Untrusted;

    internal static OutboundMoveTargetTrust ClassifyCurrentCursorMoveTargetTrust()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            return OutboundMoveTargetTrust.Untrusted;
        }

        return DesktopToastHudInterop.ClassifyPointMoveTargetTrust(pt);
    }

    internal static bool IsCurrentCursorOverTrustedMoveTarget() =>
        ClassifyCurrentCursorMoveTargetTrust() != OutboundMoveTargetTrust.Untrusted;
}
