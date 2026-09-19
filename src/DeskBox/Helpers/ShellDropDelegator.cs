using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// What happened when a drop was offered to an application shortcut. The
/// caller must distinguish "no shortcut owned this drop" (an ordinary import
/// continues) from "the shortcut refused or failed" (the drop is consumed and
/// must never become an import - importing moves the user's files).
/// </summary>
internal enum ShellDropLaunchOutcome
{
    /// <summary>No application-shortcut tile owned this drop.</summary>
    NotAttempted,

    /// <summary>The Shell's shortcut handler accepted and ran the drop.</summary>
    Launched,

    /// <summary>
    /// The linked application does not accept this drop: the Shell's
    /// DragEnter reported DROPEFFECT_NONE (a document target, or an
    /// application that cannot open the dragged file type).
    /// </summary>
    TargetRefused,

    /// <summary>
    /// The Shell could not be asked (no drop target bound) or its Drop failed.
    /// </summary>
    LaunchFailed,

    /// <summary>
    /// Another entry point for the same physical release already resolved this
    /// gesture as a launch. The drop stays consumed (never an import) but must
    /// not be delegated or reported a second time.
    /// </summary>
    AlreadyResolved
}

internal readonly struct ShellDropLaunchResult(
    ShellDropLaunchOutcome outcome,
    uint effect,
    int launchHResult)
{
    internal ShellDropLaunchOutcome Outcome => outcome;

    /// <summary>The Shell ran the drop and the caller must not import it.</summary>
    internal bool Handled => outcome == ShellDropLaunchOutcome.Launched;

    /// <summary>
    /// The drop belonged to an application shortcut and did not launch. The
    /// caller consumes it: no transfer session, no import, and the effect
    /// handed back to the drag source stays DROPEFFECT_NONE so the source's
    /// own move never runs either.
    /// </summary>
    internal bool Consumed => ShortcutDropOutcomePolicy.IsConsumed(outcome);

    internal uint Effect => effect;

    internal int LaunchHResult => launchHResult;

    internal static ShellDropLaunchResult NotAttempted => default;

    internal static ShellDropLaunchResult AlreadyResolved =>
        new(ShellDropLaunchOutcome.AlreadyResolved, NativeDropEffectPolicy.None, 0);

    /// <summary>
    /// The dropped files were opened with the shortcut's application (see
    /// <see cref="ShortcutFileLauncher"/>). The effect handed back to the drag
    /// source is DROPEFFECT_LINK so a move-drag never deletes the source.
    /// </summary>
    internal static ShellDropLaunchResult Launched(uint effect, int hResult) =>
        new(ShellDropLaunchOutcome.Launched, effect, hResult);

    internal static ShellDropLaunchResult Refused(uint effect, int hResult) =>
        new(ShellDropLaunchOutcome.TargetRefused, effect, hResult);

    internal static ShellDropLaunchResult Failed(int hResult) =>
        new(ShellDropLaunchOutcome.LaunchFailed, NativeDropEffectPolicy.None, hResult);
}

/// <summary>
/// The single decision every launch call site shares: a shortcut drop that did
/// not launch is consumed rather than falling back to import, and only a drop
/// no shortcut owned keeps its ordinary import semantics.
/// </summary>
internal static class ShortcutDropOutcomePolicy
{
    internal static bool IsConsumed(ShellDropLaunchOutcome outcome) =>
        outcome is ShellDropLaunchOutcome.TargetRefused or
            ShellDropLaunchOutcome.LaunchFailed or
            ShellDropLaunchOutcome.AlreadyResolved;

    internal static bool ShouldContinueAsImport(ShellDropLaunchOutcome outcome) =>
        outcome == ShellDropLaunchOutcome.NotAttempted;
}

/// <summary>
/// Delegates a drop to the Shell's own IDropTarget for a shortcut, which is
/// the exact code path Explorer runs when files land on a .lnk: the shortcut
/// drop handler resolves the target and forwards to its drop handler, so exe
/// targets launch with the dropped files as arguments, document targets refuse
/// (DROPEFFECT_NONE), elevation flags and link tracking behave natively, and
/// arguments stored in the shortcut are concatenated by the Shell itself.
///
/// The vtable plumbing follows <see cref="NativeOleDataObject"/>: raw interface
/// pointers and function pointers only, no COM callable wrappers, so the whole
/// boundary stays Native AOT safe. Must be called on the STA thread that owns
/// the data object.
/// </summary>
internal static unsafe partial class ShellDropDelegator
{
    // BHID_SFUIObject - ShlGuid.h {3981e225-f559-11d3-8e3a-00c04f6837d5}:
    // "restricts usage to GetUIObjectOf". Asking a .lnk item for its UI object
    // hands out the shortcut's own drop handler, which resolves the link target
    // and forwards the drop there.
    private static readonly Guid ShellFolderUiObjectHandler =
        new("3981E225-F559-11D3-8E3A-00C04F6837D5");
    // The riid SHCreateItemFromParsingName must be asked for. Passing a BHID
    // here is what returned E_NOINTERFACE (0x80004002) and made every
    // delegation fail before it could reach the Shell.
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid DropTargetIid = new("00000122-0000-0000-C000-000000000046");

    private const int BindToHandlerVtableSlot = 3;
    private const int DragEnterVtableSlot = 3;
    private const int DragLeaveVtableSlot = 5;
    private const int DropVtableSlot = 6;
    private const int ReleaseVtableSlot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellPointL
    {
        public int X;
        public int Y;
    }



    /// <summary>
    /// Runs DragEnter + Drop on the shortcut's Shell drop target. Returns
    /// NotAttempted when this drop does not belong to a shortcut, and
    /// TargetRefused/LaunchFailed when the shortcut did not launch it - the
    /// caller consumes those outcomes instead of importing. The environment is
    /// scrubbed around the delegation the same way
    /// <see cref="Win32Helper.OpenFileOrChooseApp"/> scrubs it, because the
    /// handler launches the process from inside this one.
    /// </summary>
    internal static ShellDropLaunchResult TryDelegateDrop(
        string shortcutPath,
        nint dataObject,
        uint keyState,
        int screenX,
        int screenY,
        uint allowedEffects)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return ShellDropLaunchResult.NotAttempted;
        }

        if (dataObject == 0 || allowedEffects == NativeDropEffectPolicy.None)
        {
            // A launch was intended; it cannot be attempted, so the caller must
            // not silently convert the gesture into an import.
            return ShellDropLaunchResult.Failed(0);
        }

        nint dropTarget = BindShortcutDropTarget(shortcutPath);
        if (dropTarget == 0)
        {
            App.Log(
                $"[ShortcutLaunch] No Shell drop target for '{shortcutPath}'.");
            return ShellDropLaunchResult.Failed(0);
        }

        string? savedElectronRunAsNode =
            Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
        if (savedElectronRunAsNode is not null)
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        }

        try
        {
            var point = new ShellPointL { X = screenX, Y = screenY };
            uint effect = allowedEffects;
            int dragEnterHResult = CallDragEnter(
                dropTarget,
                dataObject,
                keyState,
                point,
                ref effect);
            if (dragEnterHResult < 0 || effect == NativeDropEffectPolicy.None)
            {
                // The target does not accept this drop (a document shortcut or
                // an application that cannot open the dragged file type).
                // DragLeave only follows a successful DragEnter.
                if (dragEnterHResult >= 0)
                {
                    _ = CallDragLeave(dropTarget);
                }

                ReleaseObject(dropTarget);
                App.Log(
                    $"[ShortcutLaunch] Shell refused drop on '{shortcutPath}' " +
                    $"hr=0x{dragEnterHResult:X8} effect={effect}.");
                return dragEnterHResult >= 0
                    ? ShellDropLaunchResult.Refused(effect, dragEnterHResult)
                    : ShellDropLaunchResult.Failed(dragEnterHResult);
            }

            int dropHResult = CallDrop(
                dropTarget,
                dataObject,
                keyState,
                point,
                ref effect);
            ReleaseObject(dropTarget);
            bool handled = dropHResult >= 0 &&
                effect != NativeDropEffectPolicy.None;
            App.Log(
                $"[ShortcutLaunch] Delegated drop on '{shortcutPath}' " +
                $"hr=0x{dropHResult:X8} effect={effect} handled={handled}.");
            return handled
                ? new ShellDropLaunchResult(
                    ShellDropLaunchOutcome.Launched,
                    effect,
                    dropHResult)
                : ShellDropLaunchResult.Failed(dropHResult);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShortcutLaunch] Delegation failed for '{shortcutPath}': " +
                $"{ex.Message}");
            return ShellDropLaunchResult.Failed(0);
        }
        finally
        {
            if (savedElectronRunAsNode is not null)
            {
                Environment.SetEnvironmentVariable(
                    "ELECTRON_RUN_AS_NODE",
                    savedElectronRunAsNode);
            }
        }
    }

    internal static void ReleaseObject(nint comObject)
    {
        if (comObject == 0)
        {
            return;
        }

        try
        {
            var release = (delegate* unmanaged[Stdcall]<nint, nint>)
                GetVtableEntry(comObject, ReleaseVtableSlot);
            _ = release(comObject);
        }
        catch
        {
        }
    }

    private static nint BindShortcutDropTarget(string shortcutPath)
    {
        try
        {
            int createHResult = Shell32NativeMethods.SHCreateItemFromParsingName(
                shortcutPath,
                nint.Zero,
                ShellItemIid,
                out nint shellItem);
            if (createHResult < 0 || shellItem == 0)
            {
                App.Log(
                    $"[ShortcutLaunch] SHCreateItemFromParsingName failed for " +
                    $"'{shortcutPath}': hr=0x{createHResult:X8}.");
                return 0;
            }

            try
            {
                var bindToHandler =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        nint,
                        Guid*,
                        Guid*,
                        nint*,
                        int>)GetVtableEntry(
                        shellItem,
                        BindToHandlerVtableSlot);
                Guid handlerId = ShellFolderUiObjectHandler;
                Guid interfaceId = DropTargetIid;
                nint dropTarget = 0;
                // Stack locals are already fixed: taking their address does not
                // need (and forbids) a fixed statement.
                int bindHResult = bindToHandler(
                    shellItem,
                    nint.Zero,
                    &handlerId,
                    &interfaceId,
                    &dropTarget);

                if (bindHResult < 0)
                {
                    App.Log(
                        $"[ShortcutLaunch] BindToHandler(IDropTarget) failed " +
                        $"for '{shortcutPath}': hr=0x{bindHResult:X8}.");
                    return 0;
                }

                return dropTarget;
            }
            finally
            {
                ReleaseObject(shellItem);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShortcutLaunch] Binding drop target failed for " +
                $"'{shortcutPath}': {ex.Message}");
            return 0;
        }
    }

    private static int CallDragEnter(
        nint dropTarget,
        nint dataObject,
        uint keyState,
        ShellPointL point,
        ref uint effect)
    {
        var dragEnter = (delegate* unmanaged[Stdcall]<
            nint,
            nint,
            uint,
            ShellPointL,
            uint*,
            int>)GetVtableEntry(dropTarget, DragEnterVtableSlot);

        return InvokeEffectCall(dragEnter, dropTarget, dataObject, keyState, point, ref effect);
    }

    private static int CallDrop(
        nint dropTarget,
        nint dataObject,
        uint keyState,
        ShellPointL point,
        ref uint effect)
    {
        var drop = (delegate* unmanaged[Stdcall]<
            nint,
            nint,
            uint,
            ShellPointL,
            uint*,
            int>)GetVtableEntry(dropTarget, DropVtableSlot);

        return InvokeEffectCall(drop, dropTarget, dataObject, keyState, point, ref effect);
    }

    private static int InvokeEffectCall(
        delegate* unmanaged[Stdcall]<nint, nint, uint, ShellPointL, uint*, int> method,
        nint dropTarget,
        nint dataObject,
        uint keyState,
        ShellPointL point,
        ref uint effect)
    {
        uint localEffect = effect;
        int result;
        unsafe
        {
            result = method(dropTarget, dataObject, keyState, point, &localEffect);
        }

        effect = localEffect;
        return result;
    }

    private static int CallDragLeave(nint dropTarget)
    {
        var dragLeave = (delegate* unmanaged[Stdcall]<nint, int>)
            GetVtableEntry(dropTarget, DragLeaveVtableSlot);
        return dragLeave(dropTarget);
    }

    private static nint GetVtableEntry(nint comObject, int slot)
    {
        unsafe
        {
            nint* vtable = *(nint**)comObject;
            if (vtable == null || vtable[slot] == 0)
            {
                throw new InvalidOperationException(
                    $"The Shell COM object vtable does not contain slot {slot}.");
            }

            return vtable[slot];
        }
    }
}
