using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves Windows .lnk shortcut files to extract target path, arguments,
/// working directory, and icon location via COM shell interfaces.
/// </summary>
public static class ShortcutHelper
{
    private const int MAX_PATH = 260;

    /// <summary>
    /// Resolve a .lnk shortcut file and return its target information.
    /// </summary>
    /// <param name="lnkPath">Absolute path to a .lnk file.</param>
    /// <returns>A <see cref="ShortcutInfo"/> with resolved data, or <c>null</c> on failure.</returns>
    public static ShortcutInfo? Resolve(string lnkPath)
    {
        if (!File.Exists(lnkPath))
            return null;

        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;

            file.Load(lnkPath, 0); // STGM_READ
            link.Resolve(IntPtr.Zero, SLR_FLAGS.SLR_NO_UI | SLR_FLAGS.SLR_NOSEARCH);

            var targetBuilder = new StringBuilder(MAX_PATH);
            var findData = new WIN32_FIND_DATAW();
            link.GetPath(targetBuilder, MAX_PATH, ref findData, SLGP_FLAGS.SLGP_RAWPATH);

            var argsBuilder = new StringBuilder(MAX_PATH);
            link.GetArguments(argsBuilder, MAX_PATH);

            var workDirBuilder = new StringBuilder(MAX_PATH);
            link.GetWorkingDirectory(workDirBuilder, MAX_PATH);

            var iconBuilder = new StringBuilder(MAX_PATH);
            link.GetIconLocation(iconBuilder, MAX_PATH, out var iconIndex);

            return new ShortcutInfo(
                TargetPath: targetBuilder.ToString(),
                Arguments: argsBuilder.ToString(),
                WorkingDirectory: workDirBuilder.ToString(),
                IconLocation: iconBuilder.ToString(),
                IconIndex: iconIndex);
        }
        catch
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  COM definitions
    // ────────────────────────────────────────────────────────────────

    /// <summary>Shell Link CoClass (CLSID_ShellLink).</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    /// <summary>IShellLinkW COM interface.</summary>
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
        SLR_NOSEARCH = 0x0010,
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

/// <summary>
/// Immutable record holding the resolved information from a .lnk shortcut.
/// </summary>
/// <param name="TargetPath">Absolute path to the shortcut's target.</param>
/// <param name="Arguments">Command-line arguments stored in the shortcut.</param>
/// <param name="WorkingDirectory">Working directory for the target process.</param>
/// <param name="IconLocation">Path to the file containing the shortcut's icon.</param>
/// <param name="IconIndex">Zero-based icon index within <paramref name="IconLocation"/>.</param>
public record ShortcutInfo(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string IconLocation,
    int IconIndex);
