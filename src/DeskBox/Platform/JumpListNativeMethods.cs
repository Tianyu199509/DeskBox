using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// shell32/propsys/ole32 entry points used by the Jump List AUMID
/// registration that used to declare them privately. Signatures moved
/// verbatim.
/// </summary>
internal static partial class JumpListNativeMethods
{
    [DllImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint flags, ref Guid riid, out IntPtr ppv);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    internal static extern int PropVariantFromString(string psz, IntPtr ppropvar);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(IntPtr ppropvar);
}
