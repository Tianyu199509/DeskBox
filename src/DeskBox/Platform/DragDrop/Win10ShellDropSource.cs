using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeskBox.Platform.DragDrop;

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface INativeDropSource
{
    [PreserveSig]
    int QueryContinueDrag(int fEscapePressed, uint grfKeyState);

    [PreserveSig]
    int GiveFeedback(uint dwEffect);
}

/// <summary>
/// AOT-safe COM <c>IDropSource</c> for Win10 native <c>SHDoDragDrop</c> sessions.
/// Includes a physical-button input-starvation watchdog (default 300ms) so even if
/// a WinUI host bridge ever starves OLE mouse-up delivery, the modal drag loop exits
/// cleanly instead of deadlocking the UI thread.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class Win10ShellDropSource : INativeDropSource
{
    internal const int S_OK = 0;
    internal const int DRAGDROP_S_DROP = 0x00040100;
    internal const int DRAGDROP_S_CANCEL = 0x00040101;
    internal const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    internal const uint MkLButton = 0x0001;
    internal const uint MkRButton = 0x0002;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    internal const long DefaultButtonReleaseWatchdogMs = 300;

    private readonly uint _dragButtonMask;
    private readonly long _watchdogMs;
    private readonly Func<long> _tickProvider;
    private readonly Func<bool> _isPhysicalButtonDownProvider;
    private long _physicalReleasedSinceTick;
    private bool _watchdogAborted;
    private int _queryContinueCalls;
    private int _giveFeedbackCalls;
    private uint _lastKeyState;

    internal Win10ShellDropSource(
        bool isSecondaryButtonDrag = false,
        long watchdogMs = DefaultButtonReleaseWatchdogMs,
        Func<long>? tickProvider = null,
        Func<bool>? isPhysicalButtonDownProvider = null)
    {
        _dragButtonMask = isSecondaryButtonDrag ? MkRButton : MkLButton;
        _watchdogMs = Math.Max(50, watchdogMs);
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
        int vk = isSecondaryButtonDrag ? VkRButton : VkLButton;
        _isPhysicalButtonDownProvider = isPhysicalButtonDownProvider ??
            (() => (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0);
    }

    internal bool WatchdogAborted => Volatile.Read(ref _watchdogAborted);

    internal int QueryContinueCalls => Volatile.Read(ref _queryContinueCalls);

    internal int GiveFeedbackCalls => Volatile.Read(ref _giveFeedbackCalls);

    internal uint LastKeyState => Volatile.Read(ref _lastKeyState);

    internal static nint CreateInterfacePointer(Win10ShellDropSource instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        nint pointer = (nint)ComInterfaceMarshaller<INativeDropSource>
            .ConvertToUnmanaged(instance);
        if (pointer == 0)
        {
            throw new InvalidOperationException(
                "Source-generated IDropSource marshalling returned a null pointer.");
        }

        return pointer;
    }

    internal static void ReleaseInterfacePointer(nint comPtr)
    {
        if (comPtr != 0)
        {
            ComInterfaceMarshaller<INativeDropSource>.Free((void*)comPtr);
        }
    }

    public int QueryContinueDrag(int fEscapePressed, uint grfKeyState)
    {
        Interlocked.Increment(ref _queryContinueCalls);
        Volatile.Write(ref _lastKeyState, grfKeyState);
        if (fEscapePressed != 0)
        {
            return DRAGDROP_S_CANCEL;
        }

        if ((grfKeyState & _dragButtonMask) == 0)
        {
            return DRAGDROP_S_DROP;
        }

        // Physical input-starvation watchdog: if OLE still claims MK_LBUTTON is held
        // but hardware GetAsyncKeyState has observed button-up continuously for >= 300ms,
        // cancel cleanly so the modal SHDoDragDrop loop can never wedge the UI thread.
        if (_isPhysicalButtonDownProvider())
        {
            _physicalReleasedSinceTick = 0;
            return S_OK;
        }

        long now = _tickProvider();
        if (_physicalReleasedSinceTick == 0)
        {
            _physicalReleasedSinceTick = now;
            return S_OK;
        }

        if (now - _physicalReleasedSinceTick >= _watchdogMs)
        {
            Volatile.Write(ref _watchdogAborted, true);
            return DRAGDROP_S_CANCEL;
        }

        return S_OK;
    }

    public int GiveFeedback(uint dwEffect)
    {
        Interlocked.Increment(ref _giveFeedbackCalls);
        _ = dwEffect;
        return DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
