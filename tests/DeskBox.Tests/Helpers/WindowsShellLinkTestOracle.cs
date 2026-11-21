using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Tests.Helpers;

/// <summary>
/// Test-only classic <c>ComImport</c> ShellLink oracle kept out of the product
/// assembly so JIT differential tests and test fixtures can compare or build
/// <c>.lnk</c> artifacts against Windows' in-proc COM implementation.
/// </summary>
internal static class WindowsShellLinkTestOracle
{
    private const int LongPathBufferCapacity = 4096;
    private const int ArgumentBufferCapacity = 4096;

    internal static ShortcutInfo? Resolve(string shortcutPath, ushort timeoutMs = 0)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;

            try
            {
                file.Load(shortcutPath, 0); // STGM_READ
            }
            catch when (!shortcutPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                file.Load(StorageMigrationNativeMethods.Extended(shortcutPath), 0);
            }

            try
            {
                uint resolveFlags = (uint)(SLR_FLAGS.SLR_NO_UI | SLR_FLAGS.SLR_NOSEARCH) |
                    ((uint)timeoutMs << 16);
                link.Resolve(IntPtr.Zero, (SLR_FLAGS)resolveFlags);
            }
            catch
            {
                // Keep reading the stored shortcut metadata even if the target is unavailable.
            }

            return ReadShellLinkMetadata(link);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                try
                {
                    _ = Marshal.FinalReleaseComObject(link);
                }
                catch
                {
                }
            }
        }
    }

    internal static ShortcutInfo ReadStoredMetadataUncached(string shortcutPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            try
            {
                file.Load(shortcutPath, 0); // STGM_READ
            }
            catch when (!shortcutPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                file.Load(StorageMigrationNativeMethods.Extended(shortcutPath), 0);
            }

            return ReadShellLinkMetadata(link);
        }
        finally
        {
            if (link is not null)
            {
                try
                {
                    _ = Marshal.FinalReleaseComObject(link);
                }
                catch
                {
                }
            }
        }
    }

    internal static void CreateOrUpdateFolderShortcut(
        string normalizedShortcutPath,
        string normalizedTargetPath,
        string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            bool usedIdList = false;
            if (normalizedTargetPath.Length >= 258 &&
                ShellLinkVtableInterop.TryParseDisplayNameWithLongPathFallback(
                    normalizedTargetPath,
                    out IntPtr pidl))
            {
                try
                {
                    link.SetIDList(pidl);
                    usedIdList = true;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            if (!usedIdList)
            {
                link.SetPath(normalizedTargetPath);
            }

            link.SetWorkingDirectory(
                ShellLinkVtableInterop.NormalizeWorkingDirectoryForShortcut(normalizedTargetPath));
            link.SetDescription(description);
            if (normalizedTargetPath.Length >= 258)
            {
                _ = TryStampLongTargetParsingPropertyOnComObject(
                    link,
                    normalizedTargetPath);
            }

            SaveShellLinkFileWithLongPathFallback(file, normalizedShortcutPath);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(link);
        }
    }

    internal static void CreateShellNamespaceShortcut(
        string normalizedShortcutPath,
        string parsingName,
        string description)
    {
        IntPtr pidl = IntPtr.Zero;
        int hresult = Shell32NativeMethods.SHParseDisplayName(
            parsingName,
            IntPtr.Zero,
            out pidl,
            0,
            out _);
        if (hresult < 0 || pidl == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hresult < 0 ? hresult : unchecked((int)0x80004005));
        }

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            link.SetIDList(pidl);
            link.SetDescription(description);
            SaveShellLinkFileWithLongPathFallback(file, normalizedShortcutPath);
        }
        finally
        {
            if (link is not null)
            {
                try
                {
                    _ = Marshal.FinalReleaseComObject(link);
                }
                catch
                {
                }
            }

            Marshal.FreeCoTaskMem(pidl);
        }
    }

    internal static bool TryReadShortcut(
        string shortcutPath,
        out string targetPath,
        out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;

        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)new ShellLink();
            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(shortcutPath, 0);

            var pathBuffer = new StringBuilder(260);
            var findData = new WIN32_FIND_DATAW();
            shellLink.GetPath(pathBuffer, pathBuffer.Capacity, ref findData, 0);

            var argumentBuffer = new StringBuilder(ArgumentBufferCapacity);
            shellLink.GetArguments(argumentBuffer, argumentBuffer.Capacity);
            targetPath = pathBuffer.ToString().Trim();
            arguments = argumentBuffer.ToString().Trim();
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shellLink is not null)
            {
                try
                {
                    _ = Marshal.FinalReleaseComObject(shellLink);
                }
                catch
                {
                }
            }
        }
    }

    internal static void CreateOrUpdateShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string? iconPath)
    {
        string normalizedShortcutPath = NormalizeShortcutPathOrRaw(shortcutPath);
        string normalizedTargetPath = NormalizeShortcutPathOrRaw(targetPath);
        var shellLink = (IShellLinkW)new ShellLink();
        try
        {
            bool targetAppliedViaPidl = false;
            if (normalizedTargetPath.Length >= 258 &&
                ShellLinkVtableInterop.TryParseDisplayNameWithLongPathFallback(
                    normalizedTargetPath,
                    out IntPtr pidl))
            {
                try
                {
                    shellLink.SetIDList(pidl);
                    _ = TryStampLongTargetParsingPropertyOnComObject(
                        shellLink,
                        normalizedTargetPath);
                    targetAppliedViaPidl = true;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }

            if (!targetAppliedViaPidl)
            {
                shellLink.SetPath(normalizedTargetPath);
            }

            shellLink.SetArguments(arguments);
            shellLink.SetWorkingDirectory(
                ShellLinkVtableInterop.NormalizeWorkingDirectoryForShortcut(workingDirectory));
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                shellLink.SetIconLocation(iconPath, 0);
            }

            var persistFile = (IPersistFile)shellLink;
            SaveShellLinkFileWithLongPathFallback(persistFile, normalizedShortcutPath);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(shellLink);
        }
    }

    internal static void SaveShellLinkFileWithLongPathFallback(
        IPersistFile file,
        string normalizedShortcutPath)
    {
        try
        {
            file.Save(normalizedShortcutPath, true);
        }
        catch when (!normalizedShortcutPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            file.Save(StorageMigrationNativeMethods.Extended(normalizedShortcutPath), true);
        }
    }

    private static bool TryStampLongTargetParsingPropertyOnComObject(
        object shellLink,
        string normalizedTargetPath)
    {
        IntPtr unknownPtr = Marshal.GetIUnknownForObject(shellLink);
        try
        {
            return ShellLinkVtableInterop.TryStampLongTargetParsingPropertyOnLink(
                unknownPtr,
                normalizedTargetPath);
        }
        finally
        {
            _ = Marshal.Release(unknownPtr);
        }
    }

    private static ShortcutInfo ReadShellLinkMetadata(IShellLinkW link)
    {
        var targetBuilder = new StringBuilder(LongPathBufferCapacity);
        var findData = new WIN32_FIND_DATAW();
        link.GetPath(targetBuilder, LongPathBufferCapacity, ref findData, SLGP_FLAGS.SLGP_RAWPATH);

        var descriptionBuilder = new StringBuilder(LongPathBufferCapacity);
        link.GetDescription(descriptionBuilder, LongPathBufferCapacity);

        var argsBuilder = new StringBuilder(ArgumentBufferCapacity);
        link.GetArguments(argsBuilder, ArgumentBufferCapacity);

        var workDirBuilder = new StringBuilder(LongPathBufferCapacity);
        link.GetWorkingDirectory(workDirBuilder, LongPathBufferCapacity);

        var iconBuilder = new StringBuilder(LongPathBufferCapacity);
        link.GetIconLocation(iconBuilder, LongPathBufferCapacity, out int iconIndex);

        return new ShortcutInfo(
            TargetPath: targetBuilder.ToString(),
            Description: descriptionBuilder.ToString(),
            Arguments: argsBuilder.ToString(),
            WorkingDirectory: workDirBuilder.ToString(),
            IconLocation: iconBuilder.ToString(),
            IconIndex: iconIndex);
    }

    private static string NormalizeShortcutPathOrRaw(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            ref WIN32_FIND_DATAW pfd,
            SLGP_FLAGS fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cch);

        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cch);

        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cch);

        void SetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath,
            int iIcon);

        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPathRel,
            uint dwReserved);

        void Resolve(IntPtr hwnd, SLR_FLAGS fFlags);

        void SetPath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [Flags]
    private enum SLR_FLAGS : uint
    {
        SLR_NO_UI = 0x0001,
        SLR_UPDATE = 0x0004,
        SLR_NOSEARCH = 0x0010,
        SLR_OFFER_DELETE_WITHOUT_FILE = 0x0200,
    }

    [Flags]
    private enum SLGP_FLAGS : uint
    {
        SLGP_SHORTPATH = 0x0001,
        SLGP_UNCPRIORITY = 0x0002,
        SLGP_RAWPATH = 0x0004,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
