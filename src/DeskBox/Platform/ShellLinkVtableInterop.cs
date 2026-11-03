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
    // CLSID_ShellLink, IID_IShellLinkW, IID_IPersistFile, IID_IShellItem,
    // IID_IShellLinkDataList, IID_IPropertyStore.
    private static readonly Guid s_clsidShellLink =
        new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIShellLinkW =
        new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIPersistFile =
        new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid s_iidIShellItem =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid s_iidIShellLinkDataList =
        new("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1");
    private static readonly Guid s_iidIPropertyStore =
        new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid s_fmtidLinkTargetParsingPath =
        new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25");

    // SIGDN_DESKTOPABSOLUTEPARSING from ShObjIdl_core.h — an enum value,
    // not a GUID, so it crosses as a plain int.
    private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);
    private const uint StgmRead = 0;
    private const uint CoInitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint SlgpRawPath = 0x4;
    private const uint ExpSzIconSig = 0xA000_0007;
    private const uint SldfHasDarwinId = 0x0000_1000;
    private const ushort VtLpwstr = 31;
    private const int MaxValidatedPidlBytes = 64 * 1024;
    private const int ShellLinkStringBufferChars = 1024;

    private const int PersistFileLoadSlot = 5;
    private const int PersistFileSaveSlot = 6;
    private const int ShellLinkGetPathSlot = 3;
    private const int ShellLinkGetIdListSlot = 4;
    private const int ShellLinkSetIdListSlot = 5;
    private const int ShellLinkSetDescriptionSlot = 7;
    private const int ShellLinkGetWorkingDirectorySlot = 8;
    private const int ShellLinkSetWorkingDirectorySlot = 9;
    private const int ShellLinkGetIconLocationSlot = 16;
    private const int ShellLinkSetIconLocationSlot = 17;
    private const int ShellLinkSetRelativePathSlot = 18;
    private const int ShellLinkSetPathSlot = 20;
    private const int ShellItemGetDisplayNameSlot = 5;
    private const int ShellLinkDataListCopyDataBlockSlot = 4;
    private const int ShellLinkDataListRemoveDataBlockSlot = 5;
    private const int ShellLinkDataListGetFlagsSlot = 6;
    private const int PropertyStoreGetValueSlot = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr PointerValue;
        public IntPtr Padding;
    }

    private readonly struct CoInitializeScope : IDisposable
    {
        private readonly bool _shouldUninitialize;

        private CoInitializeScope(bool shouldUninitialize)
        {
            _shouldUninitialize = shouldUninitialize;
        }

        public static CoInitializeScope Enter()
        {
            int hr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            if (hr == 0 || hr == 1)
            {
                return new CoInitializeScope(shouldUninitialize: true);
            }

            if (hr == RpcEChangedMode)
            {
                return new CoInitializeScope(shouldUninitialize: false);
            }

            return new CoInitializeScope(shouldUninitialize: false);
        }

        public void Dispose()
        {
            if (_shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PersistFileLoadInvoker(IntPtr self, string path, uint stgm);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PersistFileSaveInvoker(
        IntPtr self,
        string path,
        [MarshalAs(UnmanagedType.Bool)] bool remember);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkGetPathInvoker(
        IntPtr self,
        IntPtr pszFile,
        int cch,
        IntPtr pfd,
        uint fFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkGetIdListInvoker(IntPtr self, out IntPtr pidl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkSetIdListInvoker(IntPtr self, IntPtr pidl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkSetStringInvoker(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkGetStringInvoker(IntPtr self, IntPtr buffer, int cch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkGetIconLocationInvoker(
        IntPtr self,
        IntPtr pszIconPath,
        int cch,
        out int piIcon);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkSetIconLocationInvoker(
        IntPtr self,
        IntPtr pszIconPath,
        int iIcon);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkSetRelativePathInvoker(
        IntPtr self,
        IntPtr pszPathRel,
        uint dwReserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellItemGetDisplayNameInvoker(
        IntPtr self, int sigdn, out IntPtr displayName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkDataListCopyDataBlockInvoker(
        IntPtr self,
        uint dwSig,
        out IntPtr ppDataBlock);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkDataListRemoveDataBlockInvoker(
        IntPtr self,
        uint dwSig);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkDataListGetFlagsInvoker(
        IntPtr self,
        out uint pdwFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValueInvoker(
        IntPtr self,
        ref PropertyKey key,
        out PropVariant value);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

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
        using CoInitializeScope comScope = CoInitializeScope.Enter();
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
        using CoInitializeScope comScope = CoInitializeScope.Enter();
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

    /// <summary>
    /// Copies a validated binary ITEMIDLIST (including its trailing 2-byte zero
    /// terminator) from unmanaged memory. Returns an empty array if the pointer
    /// is null, empty, or malformed.
    /// </summary>
    internal static byte[] CopyValidatedPidlBytes(IntPtr pidl)
    {
        if (!TryMeasureValidatedPidlByteLength(pidl, out int byteLength))
        {
            return [];
        }

        byte[] copy = new byte[byteLength];
        Marshal.Copy(pidl, copy, 0, byteLength);
        return copy;
    }

    internal static bool IsValidPidlBytes(ReadOnlySpan<byte> rawPidlBytes)
    {
        if (rawPidlBytes.Length < 4 || rawPidlBytes.Length > MaxValidatedPidlBytes)
        {
            return false;
        }

        int offset = 0;
        while (offset + 2 <= rawPidlBytes.Length)
        {
            ushort cb = (ushort)(rawPidlBytes[offset] | (rawPidlBytes[offset + 1] << 8));
            if (cb == 0)
            {
                return offset > 0 && offset + 2 == rawPidlBytes.Length;
            }

            if (cb < 2 || offset + cb + 2 > rawPidlBytes.Length)
            {
                return false;
            }

            offset += cb;
        }

        return false;
    }

    /// <summary>
    /// Persists a Shell link whose target is the exact binary ITEMIDLIST
    /// (<c>IShellLinkW::SetIDList</c>). Preserves embedded property stores in
    /// AppsFolder/UWP/PWA/Electron and virtual namespace PIDLs that string
    /// re-parsing would otherwise discard.
    /// </summary>
    internal static bool TryCreateShortcutFromPidlBytes(
        string shortcutPath,
        ReadOnlySpan<byte> rawPidlBytes,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !IsValidPidlBytes(rawPidlBytes))
        {
            return false;
        }

        using CoInitializeScope comScope = CoInitializeScope.Enter();
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        IntPtr nativePidl = IntPtr.Zero;
        IntPtr descriptionPtr = IntPtr.Zero;
        try
        {
            string? directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Guid clsid = s_clsidShellLink;
            Guid linkIid = s_iidIShellLinkW;
            if (!ComVtable.TryCreateInProcessInstance(
                    ref clsid,
                    ref linkIid,
                    out link))
            {
                return false;
            }

            nativePidl = Marshal.AllocCoTaskMem(rawPidlBytes.Length);
            byte[] pidlManagedCopy = rawPidlBytes.ToArray();
            Marshal.Copy(pidlManagedCopy, 0, nativePidl, pidlManagedCopy.Length);

            int setIdListHr = ComVtable.Slot<ShellLinkSetIdListInvoker>(
                link,
                ShellLinkSetIdListSlot)(link, nativePidl);
            if (setIdListHr < 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                descriptionPtr = Marshal.StringToCoTaskMemUni(description);
                _ = ComVtable.Slot<ShellLinkSetStringInvoker>(
                    link,
                    ShellLinkSetDescriptionSlot)(link, descriptionPtr);
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
            return saveResult >= 0 && File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (descriptionPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(descriptionPtr);
            }

            if (nativePidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(nativePidl);
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

    internal static bool TryReadRawPidlBytes(
        string shortcutPath,
        out byte[] rawPidlBytes)
    {
        rawPidlBytes = [];
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return false;
        }

        using CoInitializeScope comScope = CoInitializeScope.Enter();
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (!TryLoadShellLink(shortcutPath, out link, out persistFile))
            {
                return false;
            }

            int hr = ComVtable.Slot<ShellLinkGetIdListInvoker>(
                link,
                ShellLinkGetIdListSlot)(link, out pidl);
            if (hr < 0 || pidl == IntPtr.Zero)
            {
                return false;
            }

            rawPidlBytes = CopyValidatedPidlBytes(pidl);
            return rawPidlBytes.Length >= 4;
        }
        catch
        {
            rawPidlBytes = [];
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
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

    /// <summary>
    /// Reads the expandable icon block (<c>EXP_SZ_ICON_SIG</c>) and the full
    /// untruncated target parsing path (<c>PKEY_Link_TargetParsingPath</c>,
    /// including paths beyond 260 chars) from a Shell link without invoking
    /// third-party shell extensions.
    /// </summary>
    internal static bool TryReadExpandedShortcutPaths(
        string shortcutPath,
        out string? expandedIconLocation,
        out int iconIndex,
        out string? longTargetPath)
    {
        expandedIconLocation = null;
        iconIndex = 0;
        longTargetPath = null;
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return false;
        }

        using CoInitializeScope comScope = CoInitializeScope.Enter();
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        IntPtr dataList = IntPtr.Zero;
        IntPtr propStore = IntPtr.Zero;
        IntPtr textBuffer = IntPtr.Zero;
        try
        {
            if (!TryLoadShellLink(shortcutPath, out link, out persistFile))
            {
                return false;
            }

            textBuffer = Marshal.AllocCoTaskMem(ShellLinkStringBufferChars * sizeof(char));
            int iconHr = ComVtable.Slot<ShellLinkGetIconLocationInvoker>(
                link,
                ShellLinkGetIconLocationSlot)(
                link,
                textBuffer,
                ShellLinkStringBufferChars,
                out iconIndex);
            string? headerIcon = iconHr >= 0
                ? Marshal.PtrToStringUni(textBuffer)?.Trim()
                : null;

            Guid dataListIid = s_iidIShellLinkDataList;
            if (ComVtable.QueryInterface(link, ref dataListIid, out dataList) >= 0 &&
                dataList != IntPtr.Zero)
            {
                string? blockIcon = TryReadExpandedDataBlockString(dataList, ExpSzIconSig);
                if (!string.IsNullOrWhiteSpace(blockIcon))
                {
                    headerIcon = blockIcon;
                }
            }

            if (!string.IsNullOrWhiteSpace(headerIcon))
            {
                expandedIconLocation = Environment.ExpandEnvironmentVariables(headerIcon)
                    .Trim()
                    .Trim('"');
            }

            Guid propStoreIid = s_iidIPropertyStore;
            if (ComVtable.QueryInterface(link, ref propStoreIid, out propStore) >= 0 &&
                propStore != IntPtr.Zero)
            {
                PropertyKey key = new()
                {
                    FmtId = s_fmtidLinkTargetParsingPath,
                    Pid = 2
                };
                int propHr = ComVtable.Slot<PropertyStoreGetValueInvoker>(
                    propStore,
                    PropertyStoreGetValueSlot)(propStore, ref key, out PropVariant pv);
                if (propHr >= 0)
                {
                    try
                    {
                        if (pv.Vt == VtLpwstr && pv.PointerValue != IntPtr.Zero)
                        {
                            string? candidate = Marshal.PtrToStringUni(pv.PointerValue)?.Trim();
                            if (!string.IsNullOrWhiteSpace(candidate))
                            {
                                longTargetPath = Environment.ExpandEnvironmentVariables(candidate);
                            }
                        }
                    }
                    finally
                    {
                        _ = PropVariantClear(ref pv);
                    }
                }
            }

            return !string.IsNullOrWhiteSpace(expandedIconLocation) ||
                !string.IsNullOrWhiteSpace(longTargetPath);
        }
        catch
        {
            expandedIconLocation = null;
            longTargetPath = null;
            return false;
        }
        finally
        {
            if (textBuffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(textBuffer);
            }

            if (propStore != IntPtr.Zero)
            {
                _ = Marshal.Release(propStore);
            }

            if (dataList != IntPtr.Zero)
            {
                _ = Marshal.Release(dataList);
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

    /// <summary>
    /// Updates a moved <c>.lnk</c> file in place when its target, working
    /// directory, or icon location moved in the same batch (or used a relative
    /// path anchored at <paramref name="originalShortcutPath"/>'s directory).
    /// Leaves MSI Darwin shortcuts and unmodified links untouched.
    /// </summary>
    internal static bool TryRebaseShortcutPathsInPlace(
        string shortcutPath,
        string originalShortcutPath,
        IReadOnlyList<(string OldPath, string NewPath)> movedEntries)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return false;
        }

        (string OldPath, string NewPath)[] normalizedEntries = movedEntries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.OldPath) &&
                !string.IsNullOrWhiteSpace(entry.NewPath) &&
                !PathsEqual(entry.OldPath, entry.NewPath))
            .Select(entry => (
                OldPath: NormalizeComparablePath(entry.OldPath),
                NewPath: NormalizeComparablePath(entry.NewPath)))
            .OrderByDescending(entry => entry.OldPath.Length)
            .ToArray();

        string? originalShortcutDir = SafeGetDirectoryName(originalShortcutPath);
        string? currentShortcutDir = SafeGetDirectoryName(shortcutPath);
        bool shortcutDirectoryChanged =
            !string.IsNullOrWhiteSpace(originalShortcutDir) &&
            !string.IsNullOrWhiteSpace(currentShortcutDir) &&
            !PathsEqual(originalShortcutDir, currentShortcutDir);

        if (normalizedEntries.Length == 0 && !shortcutDirectoryChanged)
        {
            return false;
        }

        using CoInitializeScope comScope = CoInitializeScope.Enter();
        IntPtr link = IntPtr.Zero;
        IntPtr persistFile = IntPtr.Zero;
        IntPtr dataList = IntPtr.Zero;
        IntPtr propStore = IntPtr.Zero;
        IntPtr textBuffer = IntPtr.Zero;
        try
        {
            if (!TryLoadShellLink(shortcutPath, out link, out persistFile))
            {
                return false;
            }

            bool hasDarwinId = false;
            string? expandedIconBlock = null;
            Guid dataListIid = s_iidIShellLinkDataList;
            if (ComVtable.QueryInterface(link, ref dataListIid, out dataList) >= 0 &&
                dataList != IntPtr.Zero)
            {
                if (ComVtable.Slot<ShellLinkDataListGetFlagsInvoker>(
                        dataList,
                        ShellLinkDataListGetFlagsSlot)(dataList, out uint flags) >= 0 &&
                    (flags & SldfHasDarwinId) != 0)
                {
                    hasDarwinId = true;
                }

                expandedIconBlock = TryReadExpandedDataBlockString(dataList, ExpSzIconSig);
            }

            textBuffer = Marshal.AllocCoTaskMem(ShellLinkStringBufferChars * sizeof(char));

            string? rawTarget = null;
            if (!hasDarwinId)
            {
                Marshal.WriteInt16(textBuffer, 0, 0);
                int pathHr = ComVtable.Slot<ShellLinkGetPathInvoker>(
                    link,
                    ShellLinkGetPathSlot)(
                    link,
                    textBuffer,
                    ShellLinkStringBufferChars,
                    IntPtr.Zero,
                    SlgpRawPath);
                if (pathHr == 0)
                {
                    rawTarget = Marshal.PtrToStringUni(textBuffer)?.Trim();
                }

                // Recover paths > MAX_PATH via PKEY_Link_TargetParsingPath when
                // GetPath truncated or returned empty on a filesystem link.
                if (string.IsNullOrWhiteSpace(rawTarget) || rawTarget.Length >= 259)
                {
                    Guid propStoreIid = s_iidIPropertyStore;
                    if (ComVtable.QueryInterface(link, ref propStoreIid, out propStore) >= 0 &&
                        propStore != IntPtr.Zero)
                    {
                        PropertyKey key = new()
                        {
                            FmtId = s_fmtidLinkTargetParsingPath,
                            Pid = 2
                        };
                        if (ComVtable.Slot<PropertyStoreGetValueInvoker>(
                                propStore,
                                PropertyStoreGetValueSlot)(propStore, ref key, out PropVariant pv) >= 0)
                        {
                            try
                            {
                                if (pv.Vt == VtLpwstr && pv.PointerValue != IntPtr.Zero)
                                {
                                    string? longCandidate = Marshal.PtrToStringUni(pv.PointerValue)?.Trim();
                                    if (!string.IsNullOrWhiteSpace(longCandidate) &&
                                        IsFilesystemLikePath(longCandidate))
                                    {
                                        rawTarget = longCandidate;
                                    }
                                }
                            }
                            finally
                            {
                                _ = PropVariantClear(ref pv);
                            }
                        }
                    }
                }
            }

            Marshal.WriteInt16(textBuffer, 0, 0);
            string? rawWorkingDir = ComVtable.Slot<ShellLinkGetStringInvoker>(
                link,
                ShellLinkGetWorkingDirectorySlot)(
                link,
                textBuffer,
                ShellLinkStringBufferChars) >= 0
                ? Marshal.PtrToStringUni(textBuffer)?.Trim()
                : null;

            Marshal.WriteInt16(textBuffer, 0, 0);
            int iconIndex = 0;
            string? rawIconLocation = ComVtable.Slot<ShellLinkGetIconLocationInvoker>(
                link,
                ShellLinkGetIconLocationSlot)(
                link,
                textBuffer,
                ShellLinkStringBufferChars,
                out iconIndex) >= 0
                ? Marshal.PtrToStringUni(textBuffer)?.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(expandedIconBlock))
            {
                rawIconLocation = expandedIconBlock;
            }

            string? updatedTarget = ComputeRebasedLinkField(
                rawTarget,
                originalShortcutDir,
                shortcutDirectoryChanged,
                normalizedEntries);
            string? updatedWorkingDir = ComputeRebasedLinkField(
                rawWorkingDir,
                originalShortcutDir,
                shortcutDirectoryChanged,
                normalizedEntries);
            string? updatedIconLocation = ComputeRebasedLinkField(
                rawIconLocation,
                originalShortcutDir,
                shortcutDirectoryChanged,
                normalizedEntries);

            if (updatedTarget is null &&
                updatedWorkingDir is null &&
                updatedIconLocation is null)
            {
                return false;
            }

            if (updatedTarget is not null)
            {
                if (!InvokeSetString(link, ShellLinkSetPathSlot, updatedTarget))
                {
                    return false;
                }

                IntPtr relPtr = Marshal.StringToCoTaskMemUni(shortcutPath);
                try
                {
                    _ = ComVtable.Slot<ShellLinkSetRelativePathInvoker>(
                        link,
                        ShellLinkSetRelativePathSlot)(link, relPtr, 0);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(relPtr);
                }
            }

            if (updatedWorkingDir is not null)
            {
                _ = InvokeSetString(
                    link,
                    ShellLinkSetWorkingDirectorySlot,
                    updatedWorkingDir);
            }

            if (updatedIconLocation is not null)
            {
                if (dataList != IntPtr.Zero && !updatedIconLocation.Contains('%'))
                {
                    _ = ComVtable.Slot<ShellLinkDataListRemoveDataBlockInvoker>(
                        dataList,
                        ShellLinkDataListRemoveDataBlockSlot)(dataList, ExpSzIconSig);
                }

                IntPtr iconPtr = Marshal.StringToCoTaskMemUni(updatedIconLocation);
                try
                {
                    _ = ComVtable.Slot<ShellLinkSetIconLocationInvoker>(
                        link,
                        ShellLinkSetIconLocationSlot)(link, iconPtr, iconIndex);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(iconPtr);
                }
            }

            int saveHr = ComVtable.Slot<PersistFileSaveInvoker>(
                persistFile,
                PersistFileSaveSlot)(persistFile, shortcutPath, true);
            return saveHr >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (textBuffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(textBuffer);
            }

            if (propStore != IntPtr.Zero)
            {
                _ = Marshal.Release(propStore);
            }

            if (dataList != IntPtr.Zero)
            {
                _ = Marshal.Release(dataList);
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

    private static bool TryLoadShellLink(
        string shortcutPath,
        out IntPtr link,
        out IntPtr persistFile)
    {
        link = IntPtr.Zero;
        persistFile = IntPtr.Zero;
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
            _ = Marshal.Release(link);
            link = IntPtr.Zero;
            return false;
        }

        int loadResult = ComVtable.Slot<PersistFileLoadInvoker>(
            persistFile,
            PersistFileLoadSlot)(persistFile, shortcutPath, StgmRead);
        if (loadResult < 0)
        {
            _ = Marshal.Release(persistFile);
            _ = Marshal.Release(link);
            persistFile = IntPtr.Zero;
            link = IntPtr.Zero;
            return false;
        }

        return true;
    }

    private static bool InvokeSetString(IntPtr link, int slot, string value)
    {
        IntPtr valuePtr = Marshal.StringToCoTaskMemUni(value);
        try
        {
            return ComVtable.Slot<ShellLinkSetStringInvoker>(link, slot)(
                link,
                valuePtr) >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePtr);
        }
    }

    private static string? TryReadExpandedDataBlockString(IntPtr dataList, uint signature)
    {
        IntPtr block = IntPtr.Zero;
        try
        {
            int hr = ComVtable.Slot<ShellLinkDataListCopyDataBlockInvoker>(
                dataList,
                ShellLinkDataListCopyDataBlockSlot)(dataList, signature, out block);
            if (hr < 0 || block == IntPtr.Zero)
            {
                return null;
            }

            int cbSize = Marshal.ReadInt32(block, 0);
            // EXP_SZ_LINK layout: DWORD cbSize (4) + DWORD dwSignature (4) +
            // CHAR szTarget[260] (260) + WCHAR swzTarget[260] (520) = 788 bytes.
            const int wideTargetOffset = 268;
            if (cbSize < wideTargetOffset + sizeof(char))
            {
                return null;
            }

            int maxChars = Math.Min(260, (cbSize - wideTargetOffset) / sizeof(char));
            string? text = Marshal.PtrToStringUni(block + wideTargetOffset, maxChars);
            if (text is null)
            {
                return null;
            }

            int terminator = text.IndexOf('\0');
            if (terminator >= 0)
            {
                text = text[..terminator];
            }

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (block != IntPtr.Zero)
            {
                _ = LocalFree(block);
            }
        }
    }

    private static bool TryMeasureValidatedPidlByteLength(
        IntPtr pidl,
        out int totalByteCount)
    {
        totalByteCount = 0;
        if (pidl == IntPtr.Zero)
        {
            return false;
        }

        int offset = 0;
        while (offset + 2 <= MaxValidatedPidlBytes)
        {
            ushort cb = (ushort)Marshal.ReadInt16(pidl, offset);
            if (cb == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                totalByteCount = offset + 2;
                return true;
            }

            if (cb < 2 || offset + cb + 2 > MaxValidatedPidlBytes)
            {
                return false;
            }

            offset += cb;
        }

        return false;
    }

    private static string? ComputeRebasedLinkField(
        string? rawFieldValue,
        string? originalShortcutDirectory,
        bool shortcutDirectoryChanged,
        (string OldPath, string NewPath)[] normalizedEntries)
    {
        if (string.IsNullOrWhiteSpace(rawFieldValue))
        {
            return null;
        }

        string trimmed = rawFieldValue.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.StartsWith("::", StringComparison.Ordinal) ||
            trimmed.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool hasEnvVars = trimmed.Contains('%');
        string candidate = hasEnvVars
            ? Environment.ExpandEnvironmentVariables(trimmed).Trim().Trim('"')
            : trimmed;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!Path.IsPathRooted(candidate))
        {
            if (string.IsNullOrWhiteSpace(originalShortcutDirectory))
            {
                return null;
            }

            string resolvedFromOldShortcutDir;
            try
            {
                resolvedFromOldShortcutDir = Path.GetFullPath(
                    Path.Combine(originalShortcutDirectory, candidate));
            }
            catch
            {
                return null;
            }

            if (TryMapMovedPath(
                    resolvedFromOldShortcutDir,
                    normalizedEntries,
                    out string? mappedRelative))
            {
                return mappedRelative;
            }

            if (shortcutDirectoryChanged &&
                (File.Exists(resolvedFromOldShortcutDir) ||
                 Directory.Exists(resolvedFromOldShortcutDir)))
            {
                return resolvedFromOldShortcutDir;
            }

            return null;
        }

        string normalizedCandidate = NormalizeComparablePath(candidate);
        if (TryMapMovedPath(
                normalizedCandidate,
                normalizedEntries,
                out string? mappedRooted) &&
            !PathsEqual(normalizedCandidate, mappedRooted))
        {
            return mappedRooted;
        }

        return null;
    }

    private static bool TryMapMovedPath(
        string candidatePath,
        (string OldPath, string NewPath)[] normalizedEntries,
        out string mappedPath)
    {
        foreach ((string oldPath, string newPath) in normalizedEntries)
        {
            if (string.Equals(candidatePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                mappedPath = newPath;
                return true;
            }

            string oldPrefix = oldPath.EndsWith(Path.DirectorySeparatorChar)
                ? oldPath
                : oldPath + Path.DirectorySeparatorChar;
            if (candidatePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = candidatePath[oldPrefix.Length..];
                mappedPath = newPath.EndsWith(Path.DirectorySeparatorChar)
                    ? newPath + suffix
                    : newPath + Path.DirectorySeparatorChar + suffix;
                return true;
            }
        }

        mappedPath = string.Empty;
        return false;
    }

    private static bool IsFilesystemLikePath(string path)
    {
        return (path.Length >= 3 &&
                char.IsLetter(path[0]) &&
                path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/')) ||
               path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string? SafeGetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch
        {
            return Path.GetDirectoryName(path);
        }
    }

    private static string NormalizeComparablePath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            full = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeComparablePath(left),
            NormalizeComparablePath(right),
            StringComparison.OrdinalIgnoreCase);
}
