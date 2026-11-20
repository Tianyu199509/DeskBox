using System.Runtime.InteropServices;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Services.DragDrop;

namespace DeskBox.Platform.DragDrop;

/// <summary>
/// Windows 10 non-elevated mouse drag-out driver backed by native Win32
/// <c>shell32!SHDoDragDrop</c>, <see cref="FileDragSourceGuardDataObject"/>
/// (Target-Aware Fail-Closed <c>Preferred DropEffect</c> + completion receipt firewall),
/// <see cref="Win10DragImageHelper"/> (<c>IDragSourceHelper2</c>), and
/// <see cref="Win10ShellDropSource"/> (300ms input-starvation watchdog).
/// </summary>
internal sealed unsafe partial class Win10ShellDragOutDriver
{
    private const uint WmCancelMode = 0x001F;
    private const int VkLButton = 0x01;
    private const int E_FAIL = unchecked((int)0x80004005);

    internal static Win10ShellDragOutDriver Shared { get; } = new();

    /// <summary>
    /// Releases Win32 mouse capture and cancels any in-flight modal menu/input mode
    /// on <paramref name="hostWindowHandle"/>, its same-thread child input bridges
    /// (<c>DesktopChildSiteBridge</c> / <c>InputSite</c>), and the active capture HWND
    /// before entering <c>SHDoDragDrop</c>.
    /// </summary>
    internal static void ReleaseWin32InputCapture(IntPtr hostWindowHandle)
    {
        try
        {
            uint currentThreadId = GetCurrentThreadId();
            IntPtr captureHwnd = GetCapture();
            if (captureHwnd != IntPtr.Zero &&
                captureHwnd != hostWindowHandle &&
                GetWindowThreadProcessId(captureHwnd, out _) == currentThreadId)
            {
                _ = SendMessageW(captureHwnd, WmCancelMode, 0, 0);
            }

            if (hostWindowHandle != IntPtr.Zero)
            {
                _ = Win32Helper.EnumChildWindows(
                    hostWindowHandle,
                    (childHwnd, lParam) =>
                    {
                        _ = lParam;
                        if (childHwnd != IntPtr.Zero &&
                            childHwnd != captureHwnd &&
                            GetWindowThreadProcessId(childHwnd, out _) == currentThreadId)
                        {
                            _ = SendMessageW(childHwnd, WmCancelMode, 0, 0);
                        }

                        return true;
                    },
                    IntPtr.Zero);
                if (GetWindowThreadProcessId(hostWindowHandle, out _) == currentThreadId)
                {
                    _ = SendMessageW(hostWindowHandle, WmCancelMode, 0, 0);
                }
            }

            _ = ReleaseCapture();
        }
        catch
        {
        }
    }

    internal static bool IsPrimaryMouseButtonPhysicallyDown() =>
        (GetAsyncKeyState(VkLButton) & unchecked((short)0x8000)) != 0;

    public FileDragOutResult ExecuteSync(FileDragOutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourcePaths.Length == 0)
        {
            return new FileDragOutResult(0, Completed: false, Canceled: true, WatchdogAborted: false, HResult: 0);
        }

        NativeOleDragEffects effects = OutboundDragRoutePolicy.ResolveNativeOleDragEffects(
            request.DragOutAction,
            request.IsProtectedCopyOnly,
            request.IsDefaultCopySpecialMedia,
            request.HasAppExecutionAlias);

        nint shellDataObject = 0;
        nint guardedDataObject = 0;
        nint dropSourcePtr = 0;
        try
        {
            shellDataObject = DesktopOutboundDropCoordinator.TryCreateShellDataObjectWithTimeout(
                request.SourcePaths,
                timeoutMs: 650,
                out string[] resolvedPhysicalPaths);
            if (shellDataObject == 0 || !NativeShellFileDragProvider.HasFileDropFormat(shellDataObject))
            {
                App.Log(
                    $"[Win10ShellDrag] Aborted: Shell data object unavailable paths={request.SourcePaths.Length}");
                return new FileDragOutResult(
                    0,
                    Completed: false,
                    Canceled: true,
                    WatchdogAborted: false,
                    HResult: E_FAIL);
            }

            guardedDataObject = FileDragSourceGuardDataObject.CreateTargetAwareInterfacePointer(
                shellDataObject);
            if (guardedDataObject == 0 || !NativeShellFileDragProvider.HasFileDropFormat(guardedDataObject))
            {
                App.Log("[Win10ShellDrag] Aborted: guarded IDataObject failed CF_HDROP self-check.");
                return new FileDragOutResult(
                    0,
                    Completed: false,
                    Canceled: true,
                    WatchdogAborted: false,
                    HResult: E_FAIL);
            }

            if (effects.PreferredDropEffect != 0)
            {
                TryWritePreferredDropEffect(guardedDataObject, effects.PreferredDropEffect);
            }

            Win10DragImageAttachResult dragImage = Win10DragImageHelper.TryAttachDragImageWithDiagnostics(
                guardedDataObject,
                resolvedPhysicalPaths.Length > 0 ? resolvedPhysicalPaths : request.SourcePaths,
                request.HostWindowHandle);

            var dropSource = new Win10ShellDropSource(isSecondaryButtonDrag: false);
            dropSourcePtr = Win10ShellDropSource.CreateInterfacePointer(dropSource);

            IntPtr captureBeforeDrag = GetCapture();
            if (captureBeforeDrag != IntPtr.Zero)
            {
                ReleaseWin32InputCapture(request.HostWindowHandle);
            }

            App.Log(
                $"[Win10ShellDrag] stage=SHDoDragDropEnter widget={request.SourceWidgetId} " +
                $"paths={request.SourcePaths.Length} allowed=0x{effects.AllowedEffects:X} " +
                $"preferred=0x{effects.PreferredDropEffect:X} " +
                $"dragImage={dragImage.Attached} dragImgHr=0x{dragImage.HResult:X8} " +
                $"dragImgPx={dragImage.SizePx} capBefore=0x{captureBeforeDrag.ToInt64():X}");

            // Pass IntPtr.Zero for hwnd because Win10DragImageHelper already attached
            // our DIB via IDragSourceHelper2::InitializeFromBitmap. Passing a WinUI
            // HWND that does not handle DI_GETDRAGIMAGE risks SHDoDragDrop invoking
            // InitializeFromWindow and replacing DragImageBits with a blank 96x96 image.
            int hr = SHDoDragDrop(
                IntPtr.Zero,
                guardedDataObject,
                dropSourcePtr,
                effects.AllowedEffects,
                out uint performedEffect);

            bool dropped = hr == Win10ShellDropSource.DRAGDROP_S_DROP;
            bool canceled = hr == Win10ShellDropSource.DRAGDROP_S_CANCEL || dropSource.WatchdogAborted;
            App.Log(
                $"[Win10ShellDrag] stage=SHDoDragDropExit widget={request.SourceWidgetId} " +
                $"hr=0x{hr:X8} effect=0x{performedEffect:X} dropped={dropped} " +
                $"canceled={canceled} watchdog={dropSource.WatchdogAborted} " +
                $"qcd={dropSource.QueryContinueCalls} gfb={dropSource.GiveFeedbackCalls} " +
                $"lastKey=0x{dropSource.LastKeyState:X}");

            return new FileDragOutResult(
                PerformedEffect: dropped ? performedEffect : 0u,
                Completed: dropped,
                Canceled: canceled || !dropped,
                WatchdogAborted: dropSource.WatchdogAborted,
                HResult: hr);
        }
        catch (Exception ex)
        {
            App.Log($"[Win10ShellDrag] SHDoDragDrop exception: {ex}");
            return new FileDragOutResult(
                0,
                Completed: false,
                Canceled: true,
                WatchdogAborted: false,
                HResult: ex.HResult);
        }
        finally
        {
            Win10ShellDropSource.ReleaseInterfacePointer(dropSourcePtr);
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(
                guardedDataObject,
                keepManagedRootForActiveDrag: false);
            NativeShellFileDragProvider.ReleaseInterface(shellDataObject);
        }
    }

    private static void TryWritePreferredDropEffect(nint dataObjectPtr, uint preferredEffect)
    {
        ushort formatId = Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect");
        if (formatId == 0)
        {
            return;
        }

        if (!Win32Helper.TryCreateGlobalMemory(BitConverter.GetBytes(preferredEffect), out nint hGlobal))
        {
            return;
        }

        var format = new NativeFormatEtc
        {
            ClipboardFormat = formatId,
            TargetDevice = 0,
            Aspect = 1,
            Index = -1,
            MediumType = 1,
        };
        var medium = new NativeStorageMedium
        {
            MediumType = 1,
            Content = hGlobal,
            ReleaseUnknown = 0,
        };

        int hr = new NativeOleDataObject(dataObjectPtr).SetData(ref format, ref medium, release: true);
        if (hr < 0)
        {
            Win32Helper.ReleaseStorageMedium(ref medium);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHDoDragDrop(
        IntPtr hwnd,
        nint pdata,
        nint pdsrc,
        uint dwEffect,
        out uint pdwEffect);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(
        IntPtr hWnd,
        uint msg,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
