using Microsoft.UI.Windowing;

namespace DeskBox.Helpers;

public static class AppBranding
{
    private const string LogoIconFileName = "deskbox.ico";
    private const string LogoSvgUriString = "ms-appx:///Assets/deskbox.svg";
    private const string TrayIconLightFileName = "deskbox-tray-light.ico";
    private const string TrayIconDarkFileName = "deskbox-tray-dark.ico";

    public static string LogoIconPath => Path.Combine(AppContext.BaseDirectory, "Assets", LogoIconFileName);
    public static string TrayIconLightPath => Path.Combine(AppContext.BaseDirectory, "Assets", TrayIconLightFileName);
    public static string TrayIconDarkPath => Path.Combine(AppContext.BaseDirectory, "Assets", TrayIconDarkFileName);

    public static Uri LogoSvgUri { get; } = new(LogoSvgUriString);

    public static string? ResolveTrayIconPath(string? style, bool isDarkTheme)
    {
        string trayIconPath = style switch
        {
            "Colorful" => LogoIconPath,
            "Black" => TrayIconDarkPath,
            "White" => TrayIconLightPath,
            _ => isDarkTheme ? TrayIconDarkPath : TrayIconLightPath
        };

        return File.Exists(trayIconPath) ? trayIconPath : null;
    }

    public static void ApplyWindowIcon(AppWindow? appWindow)
    {
        if (appWindow is null || !File.Exists(LogoIconPath))
        {
            return;
        }

        try
        {
            appWindow.SetIcon(LogoIconPath);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Branding] Failed to apply window icon: {ex.Message}");
        }
    }
}
