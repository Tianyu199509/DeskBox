using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.UI;

namespace DeskBox.Services;

/// <summary>
/// Manages application theme (Light/Dark/System) and accent color, and applies them to all windows.
/// </summary>
public sealed class ThemeService
{
    public const string AccentModeSystem = "System";
    public const string AccentModeCustom = "Custom";

    private readonly SettingsService _settingsService;
    private readonly List<Window> _trackedWindows = new();
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    public event Action? AppearanceChanged;

    public ThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _uiSettings.ColorValuesChanged += (_, _) =>
        {
            App.MainWindowDispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                RefreshAppearance);
        };
    }

    /// <summary>
    /// Current effective theme based on settings.
    /// </summary>
    public ElementTheme CurrentTheme => _settingsService.Settings.Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public bool UsesSystemAccentColor =>
        !string.Equals(_settingsService.Settings.AccentColorMode, AccentModeCustom, StringComparison.OrdinalIgnoreCase);

    public Color GetSystemAccentColor()
    {
        try
        {
            return _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return AccentColorHelper.DefaultAccentColor;
        }
    }

    public Color GetEffectiveAccentColor()
    {
        if (UsesSystemAccentColor)
        {
            return GetSystemAccentColor();
        }

        return AccentColorHelper.FromHex(_settingsService.Settings.CustomAccentColor);
    }

    /// <summary>
    /// Set the theme and apply it to all tracked windows.
    /// </summary>
    public void SetTheme(string theme)
    {
        if (string.Equals(_settingsService.Settings.Theme, theme, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.Theme = theme;
        _settingsService.SaveDebounced();
        RefreshAppearance();
    }

    public void SetAccentMode(string mode)
    {
        string normalizedMode = mode == AccentModeCustom ? AccentModeCustom : AccentModeSystem;
        if (string.Equals(_settingsService.Settings.AccentColorMode, normalizedMode, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.AccentColorMode = normalizedMode;
        _settingsService.SaveDebounced();
        RefreshAppearance();
    }

    public void SetCustomAccentColor(Color color)
    {
        string hex = AccentColorHelper.ToHex(color);
        bool changed =
            !string.Equals(_settingsService.Settings.CustomAccentColor, hex, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_settingsService.Settings.AccentColorMode, AccentModeCustom, StringComparison.Ordinal);

        if (!changed)
        {
            return;
        }

        _settingsService.Settings.CustomAccentColor = hex;
        _settingsService.Settings.AccentColorMode = AccentModeCustom;
        _settingsService.SaveDebounced();
        RefreshAppearance();
    }

    /// <summary>
    /// Register a window so appearance changes are applied to it.
    /// </summary>
    public void TrackWindow(Window window)
    {
        _trackedWindows.Add(window);
        ApplyToWindow(window);

        window.Closed += (_, _) => _trackedWindows.Remove(window);
    }

    /// <summary>
    /// Apply the current theme to a specific window.
    /// </summary>
    public void ApplyToWindow(Window window)
    {
        if (window.Content is not FrameworkElement rootElement)
        {
            return;
        }

        var theme = CurrentTheme;
        if (theme == ElementTheme.Default)
        {
            theme = Win32Helper.IsSystemDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
        }

        rootElement.RequestedTheme = theme;

        var hWnd = WindowNative.GetWindowHandle(window);
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        bool isDark = theme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };

        Win32Helper.SetWindowTheme(hWnd, isDark);
    }

    /// <summary>
    /// Apply the current theme to all tracked windows.
    /// </summary>
    public void ApplyToAllWindows()
    {
        foreach (var window in _trackedWindows)
        {
            ApplyToWindow(window);
        }
    }

    public void RefreshAppearance()
    {
        ApplyToAllWindows();
        AppearanceChanged?.Invoke();
    }
}
