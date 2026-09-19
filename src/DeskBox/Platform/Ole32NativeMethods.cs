using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// ole32 entry points shared by the COM call sites that used to declare them
/// privately (STA workers, the Task Scheduler XML reader, drag-drop
/// registration, Explorer foreground transfer).
/// </summary>
internal static partial class Ole32NativeMethods
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("ole32.dll")]
    internal static partial int CoAllowSetForegroundWindow(nint unknown, nint reserved);

    [LibraryImport("ole32.dll")]
    internal static partial int RegisterDragDrop(nint hwnd, nint dropTarget);

    [LibraryImport("ole32.dll")]
    internal static partial int RevokeDragDrop(nint hwnd);
}
