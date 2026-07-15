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

    public static bool IsShortcutPath(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsShellLinkPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a .lnk shortcut file and return its target information.
    /// </summary>
    /// <param name="lnkPath">Absolute path to a .lnk file.</param>
    /// <returns>A <see cref="ShortcutInfo"/> with resolved data, or <c>null</c> on failure.</returns>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
            return null;

        if (Path.GetExtension(shortcutPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveInternetShortcut(shortcutPath);
        }

        if (!IsShellLinkPath(shortcutPath))
        {
            return null;
        }

        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;

            file.Load(shortcutPath, 0); // STGM_READ
            try
            {
                link.Resolve(IntPtr.Zero, SLR_FLAGS.SLR_NO_UI | SLR_FLAGS.SLR_NOSEARCH);
            }
            catch
            {
                // Keep reading the stored shortcut metadata even if the target is unavailable.
            }

            var targetBuilder = new StringBuilder(MAX_PATH);
            var findData = new WIN32_FIND_DATAW();
            link.GetPath(targetBuilder, MAX_PATH, ref findData, SLGP_FLAGS.SLGP_RAWPATH);

            var descriptionBuilder = new StringBuilder(MAX_PATH);
            link.GetDescription(descriptionBuilder, MAX_PATH);

            var argsBuilder = new StringBuilder(MAX_PATH);
            link.GetArguments(argsBuilder, MAX_PATH);

            var workDirBuilder = new StringBuilder(MAX_PATH);
            link.GetWorkingDirectory(workDirBuilder, MAX_PATH);

            var iconBuilder = new StringBuilder(MAX_PATH);
            link.GetIconLocation(iconBuilder, MAX_PATH, out var iconIndex);

            return new ShortcutInfo(
                TargetPath: targetBuilder.ToString(),
                Description: descriptionBuilder.ToString(),
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

    private static ShortcutInfo? ResolveInternetShortcut(string shortcutPath)
    {
        try
        {
            string target = string.Empty;
            string iconLocation = string.Empty;
            int iconIndex = 0;
            bool inInternetShortcutSection = false;

            foreach (string rawLine in File.ReadLines(shortcutPath))
            {
                string line = rawLine.Trim();
                if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
                {
                    inInternetShortcutSection = line.Equals(
                        "[InternetShortcut]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inInternetShortcutSection)
                {
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line[..separatorIndex].Trim();
                string value = line[(separatorIndex + 1)..].Trim().Trim('"');
                if (key.Equals("URL", StringComparison.OrdinalIgnoreCase))
                {
                    target = value;
                }
                else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase))
                {
                    iconLocation = value;
                }
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(value, out iconIndex);
                }
            }

            return string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(iconLocation)
                ? null
                : new ShortcutInfo(
                    TargetPath: target,
                    Description: string.Empty,
                    Arguments: string.Empty,
                    WorkingDirectory: string.Empty,
                    IconLocation: iconLocation,
                    IconIndex: iconIndex);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Let Windows resolve a broken shortcut with its native UI, including the delete offer.
    /// </summary>
    public static BrokenShortcutResolution ResolveBrokenShortcutWithShellUi(string lnkPath, IntPtr ownerHwnd)
    {
        if (!File.Exists(lnkPath))
        {
            return BrokenShortcutResolution.ShortcutDeleted;
        }

        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0); // STGM_READ

            link.Resolve(
                ownerHwnd,
                SLR_FLAGS.SLR_UPDATE |
                SLR_FLAGS.SLR_OFFER_DELETE_WITHOUT_FILE);

            return File.Exists(lnkPath)
                ? BrokenShortcutResolution.ResolvedOrKept
                : BrokenShortcutResolution.ShortcutDeleted;
        }
        catch (COMException)
        {
            return File.Exists(lnkPath)
                ? BrokenShortcutResolution.ResolvedOrKept
                : BrokenShortcutResolution.ShortcutDeleted;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  COM definitions
    // ────────────────────────────────────────────────────────────────

    public static void CreateOrUpdateFolderShortcut(
        string shortcutPath,
        string targetFolderPath,
        string description)
    {
        string normalizedShortcutPath = Path.GetFullPath(shortcutPath);
        string normalizedTargetPath = Path.GetFullPath(targetFolderPath);
        string? shortcutDirectory = Path.GetDirectoryName(normalizedShortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            throw new ArgumentException("Shortcut path must include a directory.", nameof(shortcutPath));
        }

        Directory.CreateDirectory(shortcutDirectory);

        var link = (IShellLinkW)new ShellLink();
        var file = (IPersistFile)link;
        link.SetPath(normalizedTargetPath);
        link.SetWorkingDirectory(normalizedTargetPath);
        link.SetDescription(description);
        file.Save(normalizedShortcutPath, true);
    }

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

public enum BrokenShortcutResolution
{
    ResolvedOrKept,
    ShortcutDeleted
}

/// <summary>
/// Immutable record holding the resolved information from a .lnk shortcut.
/// </summary>
/// <param name="TargetPath">Absolute path to the shortcut's target.</param>
/// <param name="Description">Description stored in the shortcut.</param>
/// <param name="Arguments">Command-line arguments stored in the shortcut.</param>
/// <param name="WorkingDirectory">Working directory for the target process.</param>
/// <param name="IconLocation">Path to the file containing the shortcut's icon.</param>
/// <param name="IconIndex">Zero-based icon index within <paramref name="IconLocation"/>.</param>
public record ShortcutInfo(
    string TargetPath,
    string Description,
    string Arguments,
    string WorkingDirectory,
    string IconLocation,
    int IconIndex);
