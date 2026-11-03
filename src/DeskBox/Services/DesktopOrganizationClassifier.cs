using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class DesktopOrganizationClassifier
{
    private static readonly HashSet<string> ShortcutExtensions =
        CreateSet(".lnk", ".url", ".appref-ms");

    private static readonly HashSet<string> DesignExtensions =
        CreateSet(
            ".rp", ".rplib",
            ".xmind", ".mmap", ".emmx", ".pos",
            ".fig", ".sketch", ".xd",
            ".psd", ".psb", ".ai", ".eps", ".cdr", ".indd", ".idml",
            ".afdesign", ".afphoto", ".afpub",
            ".dwg", ".dxf",
            ".blend", ".fbx", ".obj", ".stl", ".3ds", ".step", ".stp", ".iges", ".igs");

    private static readonly HashSet<string> ImageExtensions =
        CreateSet(
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg",
            ".heic", ".heif", ".tif", ".tiff", ".raw", ".avif", ".jxl",
            ".ico", ".icns", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2");

    private static readonly HashSet<string> AudioExtensions =
        CreateSet(".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".ape", ".alac", ".aiff");

    private static readonly HashSet<string> VideoExtensions =
        CreateSet(".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp");

    private static readonly HashSet<string> PackageExtensions =
        CreateSet(
            ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
            ".iso", ".img", ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle");

    private static readonly HashSet<string> ExtraDocumentExtensions =
        CreateSet(".epub", ".mobi", ".azw3", ".chm", ".one");

    private static readonly Dictionary<string, string> DocumentSubtypeByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = DesktopOrganizationSubtypeIds.Pdf,
            [".ofd"] = DesktopOrganizationSubtypeIds.Pdf,
            [".xps"] = DesktopOrganizationSubtypeIds.Pdf,
            [".doc"] = DesktopOrganizationSubtypeIds.Word,
            [".docx"] = DesktopOrganizationSubtypeIds.Word,
            [".rtf"] = DesktopOrganizationSubtypeIds.Word,
            [".odt"] = DesktopOrganizationSubtypeIds.Word,
            [".wps"] = DesktopOrganizationSubtypeIds.Word,
            [".wpt"] = DesktopOrganizationSubtypeIds.Word,
            [".pages"] = DesktopOrganizationSubtypeIds.Word,
            [".xls"] = DesktopOrganizationSubtypeIds.Excel,
            [".xlsx"] = DesktopOrganizationSubtypeIds.Excel,
            [".xlsm"] = DesktopOrganizationSubtypeIds.Excel,
            [".xlsb"] = DesktopOrganizationSubtypeIds.Excel,
            [".csv"] = DesktopOrganizationSubtypeIds.Excel,
            [".tsv"] = DesktopOrganizationSubtypeIds.Excel,
            [".ods"] = DesktopOrganizationSubtypeIds.Excel,
            [".et"] = DesktopOrganizationSubtypeIds.Excel,
            [".ett"] = DesktopOrganizationSubtypeIds.Excel,
            [".numbers"] = DesktopOrganizationSubtypeIds.Excel,
            [".ppt"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".pptx"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".pptm"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".odp"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".dps"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".dpt"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".key"] = DesktopOrganizationSubtypeIds.PowerPoint,
            [".txt"] = DesktopOrganizationSubtypeIds.Text,
            [".md"] = DesktopOrganizationSubtypeIds.Text,
            [".markdown"] = DesktopOrganizationSubtypeIds.Text,
            [".log"] = DesktopOrganizationSubtypeIds.Text,
            [".json"] = DesktopOrganizationSubtypeIds.Text,
            [".yaml"] = DesktopOrganizationSubtypeIds.Text,
            [".yml"] = DesktopOrganizationSubtypeIds.Text,
            [".xml"] = DesktopOrganizationSubtypeIds.Text,
            [".ini"] = DesktopOrganizationSubtypeIds.Text,
            [".toml"] = DesktopOrganizationSubtypeIds.Text,
            [".cfg"] = DesktopOrganizationSubtypeIds.Text,
            [".conf"] = DesktopOrganizationSubtypeIds.Text,
            [".sql"] = DesktopOrganizationSubtypeIds.Text
        };

    private static readonly string[] InstallerExecutableKeywords =
    [
        "setup", "install", "unins", "update", "upgrade", "patch", "hotfix",
        "driver", "redist", "runtime", "bootstrapper", "websetup",
        "安装", "升级", "更新", "驱动", "补丁", "卸载"
    ];

    private static readonly string[] PortableExecutableKeywords =
    [
        "portable", "绿色", "免安装", "单文件"
    ];

    [GeneratedRegex(@"(?:^|[\s._-])v?\d+\.\d+(?:[\s._-]|\d|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerVersionPattern();

    public DesktopOrganizationClassification Classify(string path)
    {
        string extension = NormalizeExtension(Path.GetExtension(path));

        if (ShortcutExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Shortcuts, null, extension);
        }

        if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            return LooksLikeInstallerExecutable(stem)
                ? new(DesktopOrganizationCategoryIds.Packages, null, extension)
                : new(DesktopOrganizationCategoryIds.Shortcuts, null, extension);
        }

        if (DesignExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Design, null, extension);
        }

        if (DocumentSubtypeByExtension.TryGetValue(extension, out string? documentSubtype))
        {
            return new(DesktopOrganizationCategoryIds.Documents, documentSubtype, extension);
        }

        if (ExtraDocumentExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Documents, null, extension);
        }

        if (ImageExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Images, null, extension);
        }

        if (AudioExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Audio, extension);
        }

        if (VideoExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Video, extension);
        }

        if (PackageExtensions.Contains(extension))
        {
            return new(DesktopOrganizationCategoryIds.Packages, null, extension);
        }

        return new(DesktopOrganizationCategoryIds.Other, null, extension);
    }

    public static bool LooksLikeInstallerExecutable(string? fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return false;
        }

        foreach (string keyword in InstallerExecutableKeywords)
        {
            if (fileNameWithoutExtension.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string portable in PortableExecutableKeywords)
        {
            if (fileNameWithoutExtension.Contains(portable, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return InstallerVersionPattern().IsMatch(fileNameWithoutExtension);
    }

    public static IReadOnlyList<string> GetCategoryExtensions(string categoryId) =>
        categoryId switch
        {
            DesktopOrganizationCategoryIds.Shortcuts => ShortcutExtensions.OrderBy(value => value).ToArray(),
            DesktopOrganizationCategoryIds.Design => DesignExtensions.OrderBy(value => value).ToArray(),
            DesktopOrganizationCategoryIds.Documents => DocumentSubtypeByExtension.Keys
                .Concat(ExtraDocumentExtensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToArray(),
            DesktopOrganizationCategoryIds.Images => ImageExtensions.OrderBy(value => value).ToArray(),
            DesktopOrganizationCategoryIds.Media => AudioExtensions.Concat(VideoExtensions).OrderBy(value => value).ToArray(),
            DesktopOrganizationCategoryIds.Packages => PackageExtensions.OrderBy(value => value).ToArray(),
            _ => []
        };

    public static IReadOnlyList<string> GetSubtypeExtensions(string subtypeId) =>
        subtypeId switch
        {
            DesktopOrganizationSubtypeIds.Audio => AudioExtensions.OrderBy(value => value).ToArray(),
            DesktopOrganizationSubtypeIds.Video => VideoExtensions.OrderBy(value => value).ToArray(),
            _ => DocumentSubtypeByExtension
                .Where(pair => string.Equals(pair.Value, subtypeId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .OrderBy(value => value)
                .ToArray()
        };

    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        string trimmed = extension.Trim();
        return (trimmed.StartsWith('.') ? trimmed : $".{trimmed}").ToLowerInvariant();
    }

    private static HashSet<string> CreateSet(params string[] extensions) =>
        new(extensions, StringComparer.OrdinalIgnoreCase);
}

public sealed record DesktopOrganizationClassification(
    string CategoryId,
    string? SubtypeId,
    string Extension);
