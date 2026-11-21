using Microsoft.Win32;

namespace DeskBox.Platform;

/// <summary>
/// Non-locking reader for the current Windows desktop wallpaper image path, used by
/// the Widget Workshop stage to preview glass/acrylic widgets against the user's real desktop.
/// </summary>
public static class DesktopWallpaperReadOps
{
    public static string? TryResolveCurrentWallpaperImagePath()
    {
        try
        {
            using RegistryKey? desktopKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: false);
            if (desktopKey?.GetValue("WallPaper") is string regPath &&
                !string.IsNullOrWhiteSpace(regPath) &&
                File.Exists(regPath))
            {
                return regPath;
            }
        }
        catch
        {
        }

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                string transcoded = Path.Combine(appData, "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (File.Exists(transcoded))
                {
                    return transcoded;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static FileStream? TryOpenSharedReadStream(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            return null;
        }
    }
}
