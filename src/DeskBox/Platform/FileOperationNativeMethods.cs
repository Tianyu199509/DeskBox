using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// ole32 COM apartment/creation and shell32 parsing-name entry points used
/// by the IFileOperation transfer engine that used to declare them
/// privately. Signatures moved verbatim.
/// </summary>
internal static partial class FileOperationNativeMethods
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr reserved, uint coInitialize);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(ref Guid classId, IntPtr outerUnknown, uint classContext, ref Guid interfaceId, out IntPtr fileOperation);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IntPtr shellItem);

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr memory);
}
