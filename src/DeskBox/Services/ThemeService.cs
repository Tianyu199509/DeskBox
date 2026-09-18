using DeskBox.Helpers;
using DeskBox.Platform;
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _appearanceDebounceTimer;

    public event Action? AppearanceChanged;

    public ThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    private void OnColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        App.UiDispatcherQueue?.TryEnqueue(() =>
        {
            if (_appearanceDebounceTimer is null)
            {
                _appearanceDebounceTimer = App.UiDispatcherQueue.CreateTimer();
                _appearanceDebounceTimer.Interval = TimeSpan.FromMilliseconds(200);
                _appearanceDebounceTimer.IsRepeating = false;
                _appearanceDebounceTimer.Tick += (_, _) => RefreshAppearance();
            }

            _appearanceDebounceTimer.Stop();
            _appearanceDebounceTimer.Start();
        });
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

    /// <summary>
    /// The theme applied to window roots: the explicit override, or the
    /// resolved system theme when following it. Themed lookups without an
    /// element scope (for example a window that owns no tree yet) resolve
    /// against this instead of the application theme, which tracks the system.
    /// </summary>
    public ElementTheme EffectiveTheme
    {
        get
        {
            var theme = CurrentTheme;
            return theme == ElementTheme.Default
                ? (Win32Helper.IsSystemDarkMode() ? ElementTheme.Dark : ElementTheme.Light)
                : theme;
        }
    }

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
        _settingsService.SaveDebounced(notifySubscribers: false);
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
        _settingsService.SaveDebounced(notifySubscribers: false);
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
        _settingsService.SaveDebounced(notifySubscribers: false);
        RefreshAppearance();
    }

    /// <summary>
    /// Register a window so appearance changes are applied to it.
    /// </summary>
    public void TrackWindow(Window window)
    {
        if (_trackedWindows.Contains(window))
        {
            ApplyToWindow(window);
            return;
        }

        _trackedWindows.Add(window);
        ApplyToWindow(window);
        window.Closed += OnTrackedWindowClosed;
    }

    private void OnTrackedWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Closed -= OnTrackedWindowClosed;
        _trackedWindows.Remove(window);
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

        if (!rootElement.DispatcherQueue.HasThreadAccess)
        {
            _ = rootElement.DispatcherQueue.TryEnqueue(() => ApplyToWindow(window));
            return;
        }

        var theme = EffectiveTheme;

        rootElement.RequestedTheme = theme;
        AccentResourceScope.Apply(rootElement, GetEffectiveAccentColor());

        var hWnd = WindowNative.GetWindowHandle(window);
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.SetWindowTheme(hWnd, theme == ElementTheme.Dark);
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
        App.ScheduleLightMemoryCleanup();
    }
}
