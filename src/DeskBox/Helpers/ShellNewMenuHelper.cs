using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Helpers;

public sealed record ShellNewEntry(
    string Extension,
    string DisplayName,
    string? TemplatePath,
    byte[]? TemplateData = null,
    string? FileNameBase = null)
{
    /// <summary>Null template means "create an empty file".</summary>
    public bool CreatesEmptyFile => TemplatePath is null;
}

/// <summary>
/// Enumerates the registered "New" file types — every registration layout
/// Windows and application installers actually use — and materializes new
/// files inside the grid's own creation pipeline. Registry-backed
/// association data only; no shell extension is loaded.
///
/// Native-parity rules, each verified against the Explorer New submenu on
/// Win11 26100: registrations live under <c>HKCR\.ext\ShellNew</c> (direct),
/// <c>HKCR\.ext\ProgId\ShellNew</c> (the Office layout) and
/// <c>HKCR\ProgId\ShellNew</c> (the extension's default ProgId); the first
/// usable shape per extension wins; entries sort by extension, not display
/// name; the menu text is the ShellNew <c>MenuText</c> value when present,
/// otherwise the ProgId's <c>FriendlyTypeName</c> (falling back to the
/// ProgId's default value), each resolving <c>@dll,-id</c> indirect strings;
/// <c>ItemName</c> is the created file's ready-made base name
/// ("新快捷方式", "新建压缩(zipped)文件夹"), not a menu label.
/// </summary>
public static class ShellNewMenuHelper
{
    /// <summary>
    /// One registration's resolved inputs, fed to <see cref="BuildEntries"/>
    /// so the filtering and naming rules stay testable without touching
    /// HKCR. Name fields arrive already indirect-resolved.
    /// </summary>
    public readonly record struct ShellNewKeyData(
        string Extension,
        string? MenuText,
        string? TypeName,
        string? ItemName,
        string? NullFile,
        string? FileName,
        string? Command,
        string? TemplateFullPath,
        byte[]? Data = null);

    private const int MaxEntries = 32;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static IReadOnlyList<ShellNewEntry>? _cachedEntries;
    private static long _cacheReadAt;

    private static readonly HashSet<string> ExcludedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Offered as a dedicated wizard entry sourced from the same key.
            ".lnk",
            // Folder-like or non-file registrations.
            ".library-ms",
            ".searchConnector-ms"
        };

    public static IReadOnlyList<ShellNewEntry> GetEntries()
    {
        long now = Environment.TickCount64;
        IReadOnlyList<ShellNewEntry>? cached = _cachedEntries;
        if (cached is not null &&
            now - _cacheReadAt < CacheLifetime.TotalMilliseconds)
        {
            return cached;
        }

        List<ShellNewEntry> entries = EnumerateRegistryEntries();
        _cachedEntries = entries;
        _cacheReadAt = now;
        return entries;
    }

    internal static void InvalidateCacheForTests()
    {
        _cachedEntries = null;
        _cacheReadAt = 0;
    }

    /// <summary>
    /// The New→Shortcut entry, sourced from <c>HKCR\.lnk\ShellNew</c> so the
    /// label ("快捷方式" / "Shortcut") is the OS-localized one. Null when the
    /// OS itself does not register the entry.
    /// </summary>
    public static bool TryGetShortcutNewTemplate(
        out string menuText,
        out string? fileNameBase)
    {
        menuText = string.Empty;
        fileNameBase = null;
        try
        {
            using Microsoft.Win32.RegistryKey? shellNew =
                Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(".lnk\\ShellNew");
            if (shellNew is null || shellNew.GetValue("NullFile") is null)
            {
                return false;
            }

            string? text = StripMenuMnemonic(
                Win32Helper.TryResolveIndirectString(
                    shellNew.GetValue("MenuText") as string));
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            menuText = text;
            fileNameBase = Win32Helper.TryResolveIndirectString(
                shellNew.GetValue("ItemName") as string);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the native naming, filtering and ordering rules to resolved
    /// registrations. Pure so tests can drive it without touching HKCR.
    /// </summary>
    internal static IReadOnlyList<ShellNewEntry> BuildEntries(
        IEnumerable<ShellNewKeyData> registrations)
    {
        var entries = new List<ShellNewEntry>();
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ShellNewKeyData data in registrations)
        {
            if (!data.Extension.StartsWith('.'))
            {
                continue;
            }

            if (ExcludedExtensions.Contains(data.Extension))
            {
                continue;
            }

            // Command-only registrations launch an application instead of
            // creating a file, and value-less ShellNew keys (the VMware
            // family) create nothing; neither belongs in the menu.
            bool createsEmptyFile = data.NullFile is not null;
            bool carriesData = data.Data is { Length: > 0 };
            bool copiesTemplate =
                data.FileName is not null &&
                data.TemplateFullPath is not null &&
                File.Exists(data.TemplateFullPath);
            if (!createsEmptyFile && !carriesData && !copiesTemplate)
            {
                continue;
            }

            // One row per file type, like the native menu; the first usable
            // shape for an extension wins.
            if (!seenExtensions.Add(data.Extension))
            {
                continue;
            }

            string? displayName =
                string.IsNullOrWhiteSpace(data.MenuText) ? data.TypeName : data.MenuText;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            entries.Add(new ShellNewEntry(
                data.Extension,
                displayName!,
                copiesTemplate ? data.TemplateFullPath : null,
                data.Data,
                string.IsNullOrWhiteSpace(data.ItemName) ? null : data.ItemName));
        }

        return entries
            .OrderBy(entry => entry.Extension, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToList();
    }

    /// <summary>
    /// Creates the new file inside <paramref name="destinationFolderPath"/>
    /// using the same collision naming as folder creation, and returns its
    /// full path (null when the template vanished or the write failed).
    /// A registration's <c>Data</c> blob (the .zip empty-archive header)
    /// seeds the file content; a registration's <c>ItemName</c> is used
    /// verbatim as the base name because it already carries the OS-local
    /// "新建…" wording.
    /// </summary>
    public static string? CreateFile(
        ShellNewEntry entry,
        string destinationFolderPath,
        Func<string, string> baseNameFormatter)
    {
        if (string.IsNullOrWhiteSpace(destinationFolderPath) ||
            !Directory.Exists(destinationFolderPath))
        {
            return null;
        }

        try
        {
            string baseName = entry.FileNameBase ??
                baseNameFormatter(entry.DisplayName);
            string targetPath = FileService.GetAvailablePath(Path.Combine(
                destinationFolderPath,
                baseName + entry.Extension));
            if (entry.TemplateData is { Length: > 0 } data)
            {
                File.WriteAllBytes(targetPath, data);
                return targetPath;
            }

            if (entry.CreatesEmptyFile)
            {
                using FileStream stream = new(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write);
                return targetPath;
            }

            if (entry.TemplatePath is { } template &&
                File.Exists(template))
            {
                // Stream copy keeps this helper clear of the destructive
                // file-operation boundary (routed through FileService).
                using FileStream source = new(
                    template,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                using FileStream target = new(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write);
                source.CopyTo(target);
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellNew] create failed ext='{entry.Extension}': " +
                ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Removes Windows menu accelerators: trailing "(&amp;X)" groups first,
    /// then any remaining marker, so "快捷方式(&amp;S)" becomes "快捷方式".
    /// </summary>
    internal static string? StripMenuMnemonic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string current = text;
        while (true)
        {
            int paren = current.IndexOf("(&", StringComparison.Ordinal);
            if (paren < 0)
            {
                break;
            }

            int close = current.IndexOf(')', paren + 2);
            // Only a short accelerator group, not stray prose parentheses.
            if (close > paren && close - paren <= 4)
            {
                current = current.Remove(paren, close - paren + 1);
            }
            else
            {
                break;
            }
        }

        return current.Replace("&", string.Empty);
    }

    private static List<ShellNewEntry> EnumerateRegistryEntries()
    {
        var registrations = new List<ShellNewKeyData>();
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using Microsoft.Win32.RegistryKey classesRoot =
                Microsoft.Win32.Registry.ClassesRoot;
            foreach (string keyName in classesRoot.GetSubKeyNames())
            {
                if (!keyName.StartsWith('.') ||
                    keyName.Length <= 1)
                {
                    continue;
                }

                try
                {
                    CollectExtensionRegistrations(
                        classesRoot,
                        keyName,
                        registrations,
                        seenExtensions);
                }
                catch
                {
                    // One unreadable association key must not break the
                    // whole New submenu.
                }
            }
        }
        catch
        {
            // Registry unavailable (for example a locked-down session):
            // fall through with whatever was collected.
        }

        // Recent Windows builds ship no ShellNew registration for .txt at
        // all (Notepad's is gone with the txtfilelegacy rename), yet every
        // native New menu still shows a localized "Text Document". Seed it
        // from the shell's own type name when the registry scan found none.
        if (!seenExtensions.Contains(".txt") &&
            GetShellTypeName("new.txt") is { } txtTypeName)
        {
            registrations.Add(new ShellNewKeyData(
                ".txt",
                null,
                txtTypeName,
                null,
                NullFile: string.Empty,
                null,
                null,
                null));
        }

        return BuildEntries(registrations).ToList();
    }

    /// <summary>
    /// Harvests every ShellNew shape an extension may carry: directly under
    /// <c>HKCR\.ext\ShellNew</c>, under a ProgId subkey of the extension
    /// (the Office layout, <c>HKCR\.docx\Word.Document.12\ShellNew</c>),
    /// and under the extension's default ProgId
    /// (<c>HKCR\ProgId\ShellNew</c>). First usable shape per extension
    /// wins, like the native menu's one-row-per-type.
    /// </summary>
    private static void CollectExtensionRegistrations(
        Microsoft.Win32.RegistryKey classesRoot,
        string extensionKeyName,
        List<ShellNewKeyData> registrations,
        HashSet<string> seenExtensions)
    {
        string? extensionProgId =
            ReadDefaultValue(classesRoot, extensionKeyName);

        // 1) HKCR\.ext\ShellNew
        if (TryReadShellNew(
                classesRoot,
                extensionKeyName + "\\ShellNew",
                extensionKeyName,
                extensionProgId) is { } direct)
        {
            AddRegistration(direct, registrations, seenExtensions);
        }

        using (Microsoft.Win32.RegistryKey? extensionKey =
                   classesRoot.OpenSubKey(extensionKeyName))
        {
            // 2) HKCR\.ext\ProgId\ShellNew (Office layout); the subkey name
            // is the ProgId that carries the display name.
            if (extensionKey is not null)
            {
                foreach (string subKeyName in extensionKey.GetSubKeyNames())
                {
                    if (seenExtensions.Contains(extensionKeyName))
                    {
                        break;
                    }

                    if (TryReadShellNew(
                            classesRoot,
                            extensionKeyName + "\\" + subKeyName +
                                "\\ShellNew",
                            extensionKeyName,
                            subKeyName) is not { } nested)
                    {
                        continue;
                    }

                    AddRegistration(nested, registrations, seenExtensions);
                }
            }
        }

        // 3) HKCR\ProgId\ShellNew where ProgId is the extension default
        if (!seenExtensions.Contains(extensionKeyName) &&
            !string.IsNullOrEmpty(extensionProgId) &&
            TryReadShellNew(
                classesRoot,
                extensionProgId + "\\ShellNew",
                extensionKeyName,
                extensionProgId) is { } classic)
        {
            AddRegistration(classic, registrations, seenExtensions);
        }
    }

    private static ShellNewKeyData? TryReadShellNew(
        Microsoft.Win32.RegistryKey root,
        string shellNewKeyPath,
        string extension,
        string? progId)
    {
        using Microsoft.Win32.RegistryKey? shellNew =
            root.OpenSubKey(shellNewKeyPath);
        if (shellNew is null)
        {
            return null;
        }

        string? fileName = shellNew.GetValue("FileName") as string;
        return new ShellNewKeyData(
            extension,
            StripMenuMnemonic(Win32Helper.TryResolveIndirectString(
                shellNew.GetValue("MenuText") as string)),
            ResolveTypeName(root, progId),
            Win32Helper.TryResolveIndirectString(
                shellNew.GetValue("ItemName") as string),
            shellNew.GetValue("NullFile") as string,
            fileName,
            shellNew.GetValue("Command") as string,
            ResolveTemplatePath(fileName),
            shellNew.GetValue("Data") as byte[]);
    }

    /// <summary>
    /// The type's display name exactly as the native menu derives it:
    /// the ProgId's <c>FriendlyTypeName</c> first, then the ProgId's
    /// legacy default value, both indirect-resolved. Deliberately not
    /// SHGetFileInfo's type name — that one follows the per-user
    /// UserChoice association and diverges from the New menu's
    /// registry-derived labels (observed: .md shows "WorkBuddy Document"
    /// in Explorer's menu while SHGetFileInfo reports the UserChoice
    /// handler's "MD 文件").
    /// </summary>
    private static string? ResolveTypeName(
        Microsoft.Win32.RegistryKey root,
        string? progId)
    {
        if (string.IsNullOrEmpty(progId))
        {
            return null;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? progIdKey =
                root.OpenSubKey(progId);
            if (progIdKey is null)
            {
                return null;
            }

            string? friendly = Win32Helper.TryResolveIndirectString(
                progIdKey.GetValue("FriendlyTypeName") as string);
            if (!string.IsNullOrWhiteSpace(friendly))
            {
                return friendly;
            }

            return Win32Helper.TryResolveIndirectString(
                ReadDefaultValue(root, progId));
        }
        catch
        {
            return null;
        }
    }

    private static void AddRegistration(
        ShellNewKeyData data,
        List<ShellNewKeyData> registrations,
        HashSet<string> seenExtensions)
    {
        // Only count an extension as claimed once a BuildEntries-valid
        // shape exists for it, so an unsupported first shape (a Command-only
        // or value-less key) cannot shadow a usable later shape.
        bool createsEmptyFile = data.NullFile is not null;
        bool carriesData = data.Data is { Length: > 0 };
        bool copiesTemplate =
            data.FileName is not null &&
            data.TemplateFullPath is not null &&
            File.Exists(data.TemplateFullPath);
        if (!createsEmptyFile && !carriesData && !copiesTemplate)
        {
            return;
        }

        if (!seenExtensions.Add(data.Extension))
        {
            return;
        }

        registrations.Add(data);
    }

    /// <summary>
    /// The shell's own localized type label for a (possibly nonexistent)
    /// file name, used only where the registry chain yields nothing.
    /// </summary>
    private static string? GetShellTypeName(string fileNameWithExtension)
    {
        try
        {
            var info = new ShellIconNativeMethods.SHFILEINFO();
            IntPtr result = ShellIconNativeMethods.SHGetFileInfo(
                fileNameWithExtension,
                FileAttributeNormal,
                ref info,
                (uint)Marshal.SizeOf<ShellIconNativeMethods.SHFILEINFO>(),
                ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES |
                ShellIconNativeMethods.SHGFI_TYPENAME);
            return result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szTypeName)
                ? null
                : info.szTypeName;
        }
        catch
        {
            return null;
        }
    }

    private const uint FileAttributeNormal = 0x80;

    private static string? ReadDefaultValue(
        Microsoft.Win32.RegistryKey root,
        string subKeyName)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                root.OpenSubKey(subKeyName);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveTemplatePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.Contains('\\') || fileName.Contains('/'))
        {
            return fileName;
        }

        // Bare template names resolve against the classic ShellNew folder
        // and the Office click-to-run virtualization of it, where Office's
        // own templates live.
        string? windir = Environment.GetEnvironmentVariable("windir");
        string? programFiles =
            Environment.GetEnvironmentVariable("ProgramFiles");
        string? programFilesX86 =
            Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        foreach (string? baseDir in new[]
                 {
                     string.IsNullOrEmpty(windir)
                         ? null
                         : Path.Combine(windir, "ShellNew"),
                     string.IsNullOrEmpty(programFiles)
                         ? null
                         : Path.Combine(
                             programFiles,
                             "Microsoft Office", "Root", "VFS", "Windows",
                             "ShellNew"),
                     string.IsNullOrEmpty(programFilesX86)
                         ? null
                         : Path.Combine(
                             programFilesX86,
                             "Microsoft Office", "Root", "VFS", "Windows",
                             "ShellNew")
                 })
        {
            if (baseDir is null)
            {
                continue;
            }

            string candidate = Path.Combine(baseDir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return windir is null
            ? null
            : Path.Combine(windir, "ShellNew", fileName);
    }
}
