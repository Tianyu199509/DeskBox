using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Reads the stored target PIDL of a Shell link (.lnk) through raw vtable
/// COM (see <see cref="ComVtable"/>): classic ComImport RCW marshaling does
/// not exist on NativeAOT, and every RCW link reader in this repo lives
/// behind <c>#if !DESKBOX_NATIVE_AOT</c> with the Rust backend substituting —
/// a backend that does not return the IDList. Only the Shell's own link
/// object is created and loaded here; no third-party shell extension runs
/// in-process, so the shell-extension isolation contract holds.
/// Vtable slots verified against the Windows SDK 10.0.26100 headers:
/// IPersistFile — GetClassID 3, IsDirty 4, Load 5, Save 6; IShellLinkW —
/// GetPath 3, GetIDList 4, SetIDList 5; IShellItem — BindToHandler 3,
/// GetParent 4, GetDisplayName 5. Call from an STA thread (the menu-build
/// UI thread); Shell link parsing follows the Shell COM apartment rules.
/// </summary>
internal static class ShellLinkVtableInterop
{
    // CLSID_ShellLink, IID_IShellLinkW, IID_IPersistFile, IID_IShellItem.
    private static readonly Guid s_clsidShellLink =
        new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIShellLinkW =
        new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIPersistFile =
        new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIShellItem =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // SIGDN_DESKTOPABSOLUTEPARSING from ShObjIdl_core.h — an enum value,
    // not a GUID, so it crosses as a plain int.
    private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);
    private const uint StgmRead = 0;

    private const int PersistFileLoadSlot = 5;
    private const int PersistFileSaveSlot = 6;
    private const int ShellLinkGetIdListSlot = 4;
    private const int ShellItemGetDisplayNameSlot = 5;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PersistFileLoadInvoker(IntPtr self, string path, uint stgm);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PersistFileSaveInvoker(
        IntPtr self,
        string path,
        [MarshalAs(UnmanagedType.Bool)] bool remember);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkGetIdListInvoker(IntPtr self, out IntPtr pidl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellItemGetDisplayNameInvoker(
        IntPtr self, int sigdn, out IntPtr displayName);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateItemFromIDList(
        IntPtr pidl,
        ref Guid riid,
        out IntPtr shellItem);

    /// <summary>
    /// Persists a fresh, target-less Shell link at <paramref name="shortcutPath"/>
    /// — the New→Shortcut placeholder. The user completes it in the link's
    /// Properties sheet. Only the Shell's own link object runs here, on the
    /// menu-build STA thread like the reader.
    /// </summary>
    internal static bool TryCreateEmptyShortcut(string shortcutPath)
    {
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        try
        {
            Guid clsid = s_clsidShellLink;
            Guid linkIid = s_iidIShellLinkW;
            if (!ComVtable.TryCreateInProcessInstance(
                    ref clsid,
                    ref linkIid,
                    out link))
            {
                return false;
            }

            Guid persistIid = s_iidIPersistFile;
            if (ComVtable.QueryInterface(link, ref persistIid, out persistFile) < 0 ||
                persistFile == IntPtr.Zero)
            {
                return false;
            }

            int saveResult = ComVtable.Slot<PersistFileSaveInvoker>(
                persistFile,
                PersistFileSaveSlot)(persistFile, shortcutPath, true);
            return saveResult >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (persistFile != IntPtr.Zero)
            {
                _ = Marshal.Release(persistFile);
            }

            if (link != IntPtr.Zero)
            {
                _ = Marshal.Release(link);
            }
        }
    }

    /// <summary>
    /// The link's stored target as a desktop-absolute parsing name
    /// ("::{20D04FE0-…}" for a namespace target), or null when the link has
    /// no IDList or any step fails. Loading the .lnk only parses the stored
    /// bytes; it never resolves or searches the target, so the call cannot
    /// stall on a missing target.
    /// </summary>
    internal static string? TryReadTargetParsingName(string shortcutPath)
    {
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        IntPtr shellItem = IntPtr.Zero;
        IntPtr pidl = IntPtr.Zero;
        IntPtr displayName = IntPtr.Zero;
        try
        {
            Guid clsid = s_clsidShellLink;
            Guid linkIid = s_iidIShellLinkW;
            if (!ComVtable.TryCreateInProcessInstance(
                    ref clsid,
                    ref linkIid,
                    out link))
            {
                return null;
            }

            Guid persistIid = s_iidIPersistFile;
            if (ComVtable.QueryInterface(link, ref persistIid, out persistFile) < 0 ||
                persistFile == IntPtr.Zero)
            {
                return null;
            }

            int loadResult = ComVtable.Slot<PersistFileLoadInvoker>(
                persistFile,
                PersistFileLoadSlot)(persistFile, shortcutPath, StgmRead);
            if (loadResult < 0)
            {
                return null;
            }

            int idListResult = ComVtable.Slot<ShellLinkGetIdListInvoker>(
                link,
                ShellLinkGetIdListSlot)(link, out pidl);
            if (idListResult < 0 || pidl == IntPtr.Zero)
            {
                // No stored IDList: a path-target link (or a broken one).
                return null;
            }

            try
            {
                Guid itemIid = s_iidIShellItem;
                SHCreateItemFromIDList(pidl, ref itemIid, out shellItem);
            }
            catch
            {
                return null;
            }

            if (shellItem == IntPtr.Zero)
            {
                return null;
            }

            int nameResult = ComVtable.Slot<ShellItemGetDisplayNameInvoker>(
                shellItem,
                ShellItemGetDisplayNameSlot)(
                shellItem,
                SigdnDesktopAbsoluteParsing,
                out displayName);
            if (nameResult < 0 || displayName == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(displayName);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (displayName != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(displayName);
            }

            if (pidl != IntPtr.Zero)
            {
                // PIDLs ride the COM task allocator; ILFree is a thin wrapper
                // over the same free.
                Marshal.FreeCoTaskMem(pidl);
            }

            if (shellItem != IntPtr.Zero)
            {
                _ = Marshal.Release(shellItem);
            }

            if (persistFile != IntPtr.Zero)
            {
                _ = Marshal.Release(persistFile);
            }

            if (link != IntPtr.Zero)
            {
                _ = Marshal.Release(link);
            }
        }
    }
}
