using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

/// <summary>
/// ViewModel for the settings window.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string ThemeSystem = "\u8ddf\u968f\u7cfb\u7edf";
    private const string ThemeLight = "\u6d45\u8272";
    private const string ThemeDark = "\u6df1\u8272";
    private const string ManagedActionMove = "\u79fb\u52a8";
    private const string ManagedActionCopy = "\u590d\u5236";

    private readonly SettingsService _settingsService;
    private readonly OrganizerService _organizerService;
    private readonly ThemeService _themeService;
    private readonly DispatcherQueue _dispatcherQueue;
    private Color _currentAccentColor;
    private string _selectedTheme = ThemeSystem;
    private string _selectedManagedDropAction = ManagedActionMove;
    private bool _useSystemAccentColor;
    private string _accentColorHex = AccentColorHelper.DefaultAccentColorHex;
    private string _managedStorageRootPath = SettingsService.GetDefaultManagedStorageRootPath();
    private string _latestOrganizationSummary = "\u6700\u8fd1\u6ca1\u6709\u65b0\u7684\u6536\u7eb3\u8bb0\u5f55";
    private string _undoLatestActionText = "\u64a4\u9500\u4e0a\u6b21\u79fb\u52a8";

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _doubleClickToOpen;
    [ObservableProperty] private double _defaultWidth;
    [ObservableProperty] private double _defaultHeight;
    [ObservableProperty] private bool _hideShortcutArrowOverlay;
    [ObservableProperty] private double _widgetOpacity = SettingsService.DefaultWidgetOpacity;
    [ObservableProperty] private double _iconSize = SettingsService.DefaultIconSize;
    [ObservableProperty] private double _textSize = SettingsService.DefaultTextSize;
    [ObservableProperty] private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    [ObservableProperty] private bool _hasUndoableOrganization;

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value))
            {
                return;
            }

            string themeValue = value switch
            {
                ThemeLight => "Light",
                ThemeDark => "Dark",
                _ => "System"
            };

            _themeService.SetTheme(themeValue);
        }
    }

    public bool UseSystemAccentColor
    {
        get => _useSystemAccentColor;
        set
        {
            if (!SetProperty(ref _useSystemAccentColor, value))
            {
                return;
            }

            _themeService.SetAccentMode(value ? ThemeService.AccentModeSystem : ThemeService.AccentModeCustom);
            RefreshAccentPreview();
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
        }
    }

    public bool CanEditCustomAccent => !UseSystemAccentColor;

    public string SelectedManagedDropAction
    {
        get => _selectedManagedDropAction;
        set
        {
            if (!SetProperty(ref _selectedManagedDropAction, value))
            {
                return;
            }

            _settingsService.Settings.ManagedDropAction = value == ManagedActionCopy
                ? SettingsService.ManagedDropActionCopy
                : SettingsService.ManagedDropActionMove;
            _settingsService.SaveDebounced();
        }
    }

    public string AccentColorHex
    {
        get => _accentColorHex;
        private set => SetProperty(ref _accentColorHex, value);
    }

    public string ManagedStorageRootPath
    {
        get => _managedStorageRootPath;
        private set => SetProperty(ref _managedStorageRootPath, value);
    }

    public string IconSizeValueText => $"{Math.Round(IconSize):0}px";
    public string WidgetOpacityValueText => $"{Math.Round(WidgetOpacity * 100):0}%";
    public string TextSizeValueText => $"{TextSize:0.#}pt";
    public string LayoutDensityValueText => $"{Math.Round(LayoutDensityScale * 100):0}%";
    public string DefaultWidthInput
    {
        get => FormatNumber(DefaultWidth, 0);
        set => ApplyNumberInput(value, () => DefaultWidth, next => DefaultWidth = next, SettingsService.MinWidgetWidth, 1200d, 0);
    }

    public string DefaultHeightInput
    {
        get => FormatNumber(DefaultHeight, 0);
        set => ApplyNumberInput(value, () => DefaultHeight, next => DefaultHeight = next, SettingsService.DefaultWidgetHeight, 1200d, 0);
    }

    public string WidgetOpacityPercentInput
    {
        get => FormatNumber(WidgetOpacityPercent, 0);
        set => ApplyNumberInput(value, () => WidgetOpacityPercent, next => WidgetOpacityPercent = next, 0d, 100d, 0);
    }

    public string IconSizeInput
    {
        get => FormatNumber(IconSize, 0);
        set => ApplyNumberInput(value, () => IconSize, next => IconSize = next, SettingsService.MinIconSize, SettingsService.MaxIconSize, 0);
    }

    public string TextSizeInput
    {
        get => FormatNumber(TextSize, 1);
        set => ApplyNumberInput(value, () => TextSize, next => TextSize = next, SettingsService.MinTextSize, SettingsService.MaxTextSize, 1);
    }

    public string LayoutDensityPercentInput
    {
        get => FormatNumber(LayoutDensityPercent, 0);
        set => ApplyNumberInput(value, () => LayoutDensityPercent, next => LayoutDensityPercent = next, 55d, 100d, 0);
    }

    public double WidgetOpacityPercent
    {
        get => Math.Round(WidgetOpacity * 100);
        set => WidgetOpacity = Math.Clamp(value / 100d, SettingsService.MinWidgetOpacity, SettingsService.MaxWidgetOpacity);
    }

    public double LayoutDensityPercent
    {
        get => Math.Round(LayoutDensityScale * 100);
        set => LayoutDensityScale = Math.Clamp(value / 100d, SettingsService.MinLayoutDensityScale, SettingsService.MaxLayoutDensityScale);
    }

    public string AccentColorDescription => UseSystemAccentColor
        ? "\u4f7f\u7528 Windows \u5f53\u524d\u7684\u4e3b\u9898\u8272\u3002"
        : "\u70b9\u51fb\u53f3\u4fa7\u989c\u8272\u6309\u94ae\uff0c\u6216\u76f4\u63a5\u4f7f\u7528\u9884\u8bbe\u989c\u8272\u3002";

    public SolidColorBrush AccentPreviewBrush { get; } = new(AccentColorHelper.DefaultAccentColor);
    public ObservableCollection<OrganizationHistoryEntry> RecentOrganizationEntries { get; } = [];

    public string LatestOrganizationSummary
    {
        get => _latestOrganizationSummary;
        private set => SetProperty(ref _latestOrganizationSummary, value);
    }

    public string UndoLatestActionText
    {
        get => _undoLatestActionText;
        private set => SetProperty(ref _undoLatestActionText, value);
    }

    public string[] AvailableThemes { get; } = [ThemeSystem, ThemeLight, ThemeDark];
    public string[] AvailableManagedDropActions { get; } = [ManagedActionMove, ManagedActionCopy];

    public string AppVersion => System.Reflection.Assembly
        .GetExecutingAssembly()
        .GetName()
        .Version?.ToString() ?? "1.0.0";
    public string AboutVersionText => $"版本 {AppVersion}";

    public SettingsViewModel(SettingsService settingsService, OrganizerService organizerService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _organizerService = organizerService;
        _themeService = themeService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var settings = settingsService.Settings;
        _selectedTheme = settings.Theme switch
        {
            "Light" => ThemeLight,
            "Dark" => ThemeDark,
            _ => ThemeSystem
        };

        _useSystemAccentColor = !string.Equals(settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        _autoStart = StartupService.IsEnabled();
        _doubleClickToOpen = settings.DoubleClickToOpen;
        _defaultWidth = settings.DefaultWidgetWidth;
        _defaultHeight = settings.DefaultWidgetHeight;
        _hideShortcutArrowOverlay = settings.HideShortcutArrowOverlay;
        _widgetOpacity = settings.WidgetOpacity;
        _iconSize = settings.IconSize;
        _textSize = settings.TextSize;
        _layoutDensityScale = settings.LayoutDensityScale;
        _selectedManagedDropAction = string.Equals(settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? ManagedActionCopy
            : ManagedActionMove;
        _managedStorageRootPath = settings.DefaultManagedStorageRootPath;

        RefreshAccentPreview();
        RefreshOrganizationState();
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public Color GetCurrentAccentColor() => _currentAccentColor;

    public void SetCustomAccentColor(Color color)
    {
        _themeService.SetCustomAccentColor(color);

        if (UseSystemAccentColor)
        {
            _useSystemAccentColor = false;
            OnPropertyChanged(nameof(UseSystemAccentColor));
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
        }

        RefreshAccentPreview();
    }

    public void UpdateManagedStorageRootPath(string path)
    {
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(path);
        ManagedStorageRootPath = normalizedPath;
        _settingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
        _settingsService.SaveDebounced();
    }

    partial void OnAutoStartChanged(bool value)
    {
        StartupService.SetEnabled(value);
        _settingsService.Settings.AutoStart = value;
        _settingsService.SaveDebounced();
    }

    partial void OnDoubleClickToOpenChanged(bool value)
    {
        _settingsService.Settings.DoubleClickToOpen = value;
        _settingsService.SaveDebounced();
    }

    partial void OnDefaultWidthChanged(double value)
    {
        if (double.IsNaN(value))
        {
            DefaultWidth = _settingsService.Settings.DefaultWidgetWidth;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 10d, MidpointRounding.AwayFromZero) * 10d,
            SettingsService.MinWidgetWidth,
            1200d);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            DefaultWidth = normalizedValue;
            return;
        }

        _settingsService.Settings.DefaultWidgetWidth = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(DefaultWidthInput));
    }

    partial void OnDefaultHeightChanged(double value)
    {
        if (double.IsNaN(value))
        {
            DefaultHeight = _settingsService.Settings.DefaultWidgetHeight;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 10d, MidpointRounding.AwayFromZero) * 10d,
            SettingsService.DefaultWidgetHeight,
            1200d);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            DefaultHeight = normalizedValue;
            return;
        }

        _settingsService.Settings.DefaultWidgetHeight = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(DefaultHeightInput));
    }

    partial void OnHideShortcutArrowOverlayChanged(bool value)
    {
        _settingsService.Settings.HideShortcutArrowOverlay = value;
        _settingsService.SaveDebounced();
    }

    partial void OnWidgetOpacityChanged(double value)
    {
        if (double.IsNaN(value))
        {
            WidgetOpacity = _settingsService.Settings.WidgetOpacity;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            WidgetOpacity = normalizedValue;
            return;
        }

        _settingsService.Settings.WidgetOpacity = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(WidgetOpacityValueText));
        OnPropertyChanged(nameof(WidgetOpacityPercent));
        OnPropertyChanged(nameof(WidgetOpacityPercentInput));
    }

    partial void OnIconSizeChanged(double value)
    {
        if (double.IsNaN(value))
        {
            IconSize = _settingsService.Settings.IconSize;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2d,
            SettingsService.MinIconSize,
            SettingsService.MaxIconSize);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            IconSize = normalizedValue;
            return;
        }

        _settingsService.Settings.IconSize = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(IconSizeValueText));
        OnPropertyChanged(nameof(IconSizeInput));
    }

    partial void OnTextSizeChanged(double value)
    {
        if (double.IsNaN(value))
        {
            TextSize = _settingsService.Settings.TextSize;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value * 2d, MidpointRounding.AwayFromZero) / 2d,
            SettingsService.MinTextSize,
            SettingsService.MaxTextSize);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            TextSize = normalizedValue;
            return;
        }

        _settingsService.Settings.TextSize = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(TextSizeValueText));
        OnPropertyChanged(nameof(TextSizeInput));
    }

    partial void OnLayoutDensityScaleChanged(double value)
    {
        if (double.IsNaN(value))
        {
            LayoutDensityScale = _settingsService.Settings.LayoutDensityScale;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            LayoutDensityScale = normalizedValue;
            return;
        }

        _settingsService.Settings.LayoutDensityScale = normalizedValue;
        _settingsService.Settings.LayoutDensity = normalizedValue <= 0.78 ? "Compact" : "Comfortable";
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(LayoutDensityValueText));
        OnPropertyChanged(nameof(LayoutDensityPercent));
        OnPropertyChanged(nameof(LayoutDensityPercentInput));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settingsService.SaveAsync();
    }

    public void Dispose()
    {
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }

    private void OnAppearanceChanged()
    {
        RefreshAccentPreview();
    }

    private void OnSettingsChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            RefreshOrganizationState();
            return;
        }

        _dispatcherQueue.TryEnqueue(RefreshOrganizationState);
    }

    private void RefreshAccentPreview()
    {
        _currentAccentColor = _themeService.GetEffectiveAccentColor();
        AccentPreviewBrush.Color = _currentAccentColor;
        AccentColorHex = AccentColorHelper.ToHex(_currentAccentColor);
    }

    private void RefreshOrganizationState()
    {
        RecentOrganizationEntries.Clear();
        foreach (var entry in _organizerService.GetRecentHistory())
        {
            RecentOrganizationEntries.Add(entry);
        }

        var latestEntry = RecentOrganizationEntries.FirstOrDefault();
        LatestOrganizationSummary = latestEntry is null
            ? "\u6700\u8fd1\u6ca1\u6709\u65b0\u7684\u6536\u7eb3\u8bb0\u5f55"
            : $"{latestEntry.DisplayTitle} - {latestEntry.DisplayDetail}";

        var undoableEntry = _organizerService.GetLatestUndoableEntry();
        HasUndoableOrganization = undoableEntry is not null;
        UndoLatestActionText = undoableEntry?.UndoButtonText ?? "\u64a4\u9500\u4e0a\u6b21\u79fb\u52a8";
    }

    private static string FormatNumber(double value, int decimals)
    {
        string format = decimals <= 0 ? "0" : $"0.{new string('#', decimals)}";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private void ApplyNumberInput(
        string? value,
        Func<double> getCurrentValue,
        Action<double> setValue,
        double min,
        double max,
        int decimals)
    {
        if (!TryParseNumberInput(value, out double parsedValue))
        {
            RefreshNumberInputs();
            return;
        }

        double multiplier = Math.Pow(10, Math.Max(0, decimals));
        double normalizedValue = Math.Clamp(Math.Round(parsedValue * multiplier, MidpointRounding.AwayFromZero) / multiplier, min, max);
        if (Math.Abs(normalizedValue - getCurrentValue()) > 0.0001)
        {
            setValue(normalizedValue);
        }

        RefreshNumberInputs();
    }

    private static bool TryParseNumberInput(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
               double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private void RefreshNumberInputs()
    {
        OnPropertyChanged(nameof(DefaultWidthInput));
        OnPropertyChanged(nameof(DefaultHeightInput));
        OnPropertyChanged(nameof(WidgetOpacityPercentInput));
        OnPropertyChanged(nameof(IconSizeInput));
        OnPropertyChanged(nameof(TextSizeInput));
        OnPropertyChanged(nameof(LayoutDensityPercentInput));
    }
}
