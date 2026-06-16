using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

/// <summary>
/// ViewModel for the settings window.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string ThemeSystem = "System";
    private const string ThemeLight = "Light";
    private const string ThemeDark = "Dark";
    private const string CornerDefault = SettingsService.WidgetCornerPreferenceDefault;
    private const string CornerSquare = SettingsService.WidgetCornerPreferenceSquare;
    private const string CornerSmall = SettingsService.WidgetCornerPreferenceSmall;
    private const string CornerRound = SettingsService.WidgetCornerPreferenceRound;
    private const string RepositoryUrl = "https://github.com/Tianyu199509/DeskBox";

    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private Color _currentAccentColor;
    private string _selectedTheme = ThemeSystem;
    private string _selectedLanguage = SettingsService.LanguageSystem;
    private string _selectedManagedDropAction = SettingsService.ManagedDropActionMove;
    private string _selectedWidgetCornerPreference = CornerSmall;
    private string _selectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
    private string _selectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
    private bool _useSystemAccentColor;
    private string _accentColorHex = AccentColorHelper.DefaultAccentColorHex;
    private string _managedStorageRootPath = SettingsService.GetDefaultManagedStorageRootPath();
    private bool _isRestoringDefaults;

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _doubleClickToOpen;
    [ObservableProperty] private double _defaultWidth;
    [ObservableProperty] private double _defaultHeight;
    [ObservableProperty] private bool _hideShortcutArrowOverlay;
    [ObservableProperty] private bool _showListItemDetails;
    [ObservableProperty] private double _widgetOpacity = SettingsService.DefaultWidgetOpacity;
    [ObservableProperty] private double _iconSize = SettingsService.DefaultIconSize;
    [ObservableProperty] private double _textSize = SettingsService.DefaultTextSize;
    [ObservableProperty] private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    [ObservableProperty] private double _horizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
    [ObservableProperty] private double _verticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
    [ObservableProperty] private double _fileNameWidthScale = SettingsService.DefaultFileNameWidthScale;

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value))
            {
                return;
            }

            string themeValue = value is ThemeLight or ThemeDark ? value : ThemeSystem;

            if (_isRestoringDefaults)
            {
                return;
            }

            _themeService.SetTheme(themeValue);
            OnPropertyChanged(nameof(SelectedThemeText));
        }
    }

    public string SelectedThemeText => GetThemeDisplayName(SelectedTheme);

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            string normalizedValue = LocalizationService.NormalizeLanguageSetting(value);
            if (!SetProperty(ref _selectedLanguage, normalizedValue))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _localizationService.SetLanguage(normalizedValue);
            RefreshLocalizedProperties();
        }
    }

    public string SelectedLanguageText => _localizationService.GetLanguageDisplayName(SelectedLanguage);

    public bool UseSystemAccentColor
    {
        get => _useSystemAccentColor;
        set
        {
            if (!SetProperty(ref _useSystemAccentColor, value))
            {
                return;
            }

            if (_isRestoringDefaults)
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

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.ManagedDropAction = value == SettingsService.ManagedDropActionCopy
                ? SettingsService.ManagedDropActionCopy
                : SettingsService.ManagedDropActionMove;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedManagedDropActionText));
        }
    }

    public string SelectedManagedDropActionText => GetManagedDropActionDisplayName(SelectedManagedDropAction);

    public string SelectedWidgetCornerPreference
    {
        get => _selectedWidgetCornerPreference;
        set
        {
            if (!SetProperty(ref _selectedWidgetCornerPreference, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetCornerPreference = value is CornerDefault or CornerSquare or CornerSmall or CornerRound
                ? value
                : SettingsService.WidgetCornerPreferenceSmall;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceText));
        }
    }

    public string SelectedWidgetCornerPreferenceText => GetCornerDisplayName(SelectedWidgetCornerPreference);

    public string SelectedWidgetAnimationEffect
    {
        get => _selectedWidgetAnimationEffect;
        set
        {
            if (!SetProperty(ref _selectedWidgetAnimationEffect, NormalizeWidgetAnimationEffect(value)))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetAnimationEffect = _selectedWidgetAnimationEffect;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
        }
    }

    public string SelectedWidgetAnimationEffectText => GetWidgetAnimationEffectDisplayName(SelectedWidgetAnimationEffect);

    public string SelectedWidgetAnimationSpeed
    {
        get => _selectedWidgetAnimationSpeed;
        set
        {
            if (!SetProperty(ref _selectedWidgetAnimationSpeed, NormalizeWidgetAnimationSpeed(value)))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetAnimationSpeed = _selectedWidgetAnimationSpeed;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
        }
    }

    public string SelectedWidgetAnimationSpeedText => GetWidgetAnimationSpeedDisplayName(SelectedWidgetAnimationSpeed);

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
    public string HorizontalSpacingValueText => $"{Math.Round(HorizontalSpacingScale * 100):0}%";
    public string VerticalSpacingValueText => $"{Math.Round(VerticalSpacingScale * 100):0}%";
    public string FileNameWidthValueText => $"{Math.Round(FileNameWidthScale * 100):0}%";
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
        set => ApplyNumberInput(value, () => LayoutDensityPercent, next => LayoutDensityPercent = next, 0d, 100d, 0);
    }

    public string HorizontalSpacingPercentInput
    {
        get => FormatNumber(HorizontalSpacingPercent, 0);
        set => ApplyNumberInput(value, () => HorizontalSpacingPercent, next => HorizontalSpacingPercent = next, 0d, 100d, 0);
    }

    public string VerticalSpacingPercentInput
    {
        get => FormatNumber(VerticalSpacingPercent, 0);
        set => ApplyNumberInput(value, () => VerticalSpacingPercent, next => VerticalSpacingPercent = next, 0d, 100d, 0);
    }

    public string FileNameWidthPercentInput
    {
        get => FormatNumber(FileNameWidthPercent, 0);
        set => ApplyNumberInput(value, () => FileNameWidthPercent, next => FileNameWidthPercent = next, 0d, 100d, 0);
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

    public double HorizontalSpacingPercent
    {
        get => Math.Round(HorizontalSpacingScale * 100);
        set => HorizontalSpacingScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public double VerticalSpacingPercent
    {
        get => Math.Round(VerticalSpacingScale * 100);
        set => VerticalSpacingScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public double FileNameWidthPercent
    {
        get => Math.Round(FileNameWidthScale * 100);
        set => FileNameWidthScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public string AccentColorDescription => UseSystemAccentColor
        ? _localizationService.T("Settings.Accent.SystemDescription")
        : _localizationService.T("Settings.Accent.CustomDescription");

    public SolidColorBrush AccentPreviewBrush { get; } = new(AccentColorHelper.DefaultAccentColor);

    public string[] AvailableThemes { get; } = [ThemeSystem, ThemeLight, ThemeDark];
    public string[] AvailableLanguages { get; } = [SettingsService.LanguageSystem, SettingsService.LanguageChinese, SettingsService.LanguageEnglish];
    public string[] AvailableManagedDropActions { get; } = [SettingsService.ManagedDropActionMove, SettingsService.ManagedDropActionCopy];
    public string[] AvailableWidgetCornerPreferences { get; } = [CornerSmall, CornerRound, CornerSquare, CornerDefault];
    public string[] AvailableWidgetAnimationEffects { get; } =
    [
        SettingsService.WidgetAnimationEffectSlideFade,
        SettingsService.WidgetAnimationEffectSlideRight,
        SettingsService.WidgetAnimationEffectSlideLeft,
        SettingsService.WidgetAnimationEffectSlideUp,
        SettingsService.WidgetAnimationEffectSlideDown,
        SettingsService.WidgetAnimationEffectFade,
        SettingsService.WidgetAnimationEffectScaleFade,
        SettingsService.WidgetAnimationEffectNone
    ];
    public string[] AvailableWidgetAnimationSpeeds { get; } =
    [
        SettingsService.WidgetAnimationSpeedVeryFast,
        SettingsService.WidgetAnimationSpeedFast,
        SettingsService.WidgetAnimationSpeedStandard,
        SettingsService.WidgetAnimationSpeedRelaxed,
        SettingsService.WidgetAnimationSpeedSlow
    ];

    public string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
        "1.0.2";
    public string AboutVersionText => _localizationService.Format("Settings.About.Version", AppVersion);
    public string OpenSourceRepositoryUrl => RepositoryUrl;
    public string OpenSourceRepositoryDisplayText =>
        _localizationService.Format(
            "Settings.About.Developer",
            RepositoryUrl.Replace("https://", string.Empty).Replace("http://", string.Empty).TrimEnd('/'));

    public SettingsViewModel(SettingsService settingsService, ThemeService themeService, LocalizationService? localizationService = null)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);

        var settings = settingsService.Settings;
        _selectedTheme = settings.Theme is ThemeLight or ThemeDark ? settings.Theme : ThemeSystem;
        _selectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);

        _useSystemAccentColor = !string.Equals(settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        _autoStart = StartupService.IsEnabled();
        _doubleClickToOpen = settings.DoubleClickToOpen;
        _defaultWidth = settings.DefaultWidgetWidth;
        _defaultHeight = settings.DefaultWidgetHeight;
        _hideShortcutArrowOverlay = settings.HideShortcutArrowOverlay;
        _showListItemDetails = settings.ShowListItemDetails;
        _widgetOpacity = settings.WidgetOpacity;
        _selectedWidgetCornerPreference = settings.WidgetCornerPreference is CornerDefault or CornerSquare or CornerSmall or CornerRound
            ? settings.WidgetCornerPreference
            : CornerSmall;
        _selectedWidgetAnimationEffect = NormalizeWidgetAnimationEffect(settings.WidgetAnimationEffect);
        _selectedWidgetAnimationSpeed = NormalizeWidgetAnimationSpeed(settings.WidgetAnimationSpeed);
        _iconSize = settings.IconSize;
        _textSize = settings.TextSize;
        _layoutDensityScale = settings.LayoutDensityScale;
        _horizontalSpacingScale = settings.HorizontalSpacingScale;
        _verticalSpacingScale = settings.VerticalSpacingScale;
        _fileNameWidthScale = settings.FileNameWidthScale;
        _selectedManagedDropAction = string.Equals(settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? SettingsService.ManagedDropActionCopy
            : SettingsService.ManagedDropActionMove;
        _managedStorageRootPath = settings.DefaultManagedStorageRootPath;

        RefreshAccentPreview();
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public Color GetCurrentAccentColor() => _currentAccentColor;

    public bool SuppressAppearanceNotifications { get; set; }
    public bool DeferAppearancePersistence { get; set; }

    public void CommitAppearanceChanges()
    {
        _settingsService.NotifyAppearancePreviewNow();
        _settingsService.SaveDebounced();
    }

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

    public async Task RestoreDefaultPreferencesAsync()
    {
        _isRestoringDefaults = true;
        SuppressAppearanceNotifications = true;
        DeferAppearancePersistence = false;

        try
        {
            SelectedTheme = ThemeSystem;
            UseSystemAccentColor = true;
            DefaultWidth = SettingsService.DefaultWidgetWidth;
            DefaultHeight = SettingsService.DefaultWidgetHeight;
            SelectedWidgetCornerPreference = CornerSmall;
            SelectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            SelectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            IconSize = SettingsService.DefaultIconSize;
            TextSize = SettingsService.DefaultTextSize;
            LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            SelectedManagedDropAction = SettingsService.ManagedDropActionMove;
            DoubleClickToOpen = true;
            HideShortcutArrowOverlay = true;
            ShowListItemDetails = false;

            var settings = _settingsService.Settings;
            settings.Theme = "System";
            settings.AccentColorMode = ThemeService.AccentModeSystem;
            settings.DefaultWidgetWidth = SettingsService.DefaultWidgetWidth;
            settings.DefaultWidgetHeight = SettingsService.DefaultWidgetHeight;
            settings.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceSmall;
            settings.WidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            settings.WidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            settings.UseNativeBackdropBlur = SettingsService.DefaultNativeBackdropBlur;
            settings.WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            settings.IconSize = SettingsService.DefaultIconSize;
            settings.TextSize = SettingsService.DefaultTextSize;
            settings.LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            settings.LayoutDensity = SettingsService.DefaultLayoutDensityScale <= 0.78 ? "Compact" : "Comfortable";
            settings.HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            settings.VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            settings.FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            settings.ManagedDropAction = SettingsService.ManagedDropActionMove;
            settings.DoubleClickToOpen = true;
            settings.HideShortcutArrowOverlay = true;
            settings.ShowListItemDetails = false;

            RefreshAccentPreview();
            RefreshNumberInputs();
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
            RefreshLocalizedProperties();
            _themeService.RefreshAppearance();
            await _settingsService.SaveAsync();
            _settingsService.NotifyAppearancePreviewNow();
        }
        finally
        {
            SuppressAppearanceNotifications = false;
            _isRestoringDefaults = false;
        }
    }

    public string GetThemeDisplayName(string theme)
    {
        return theme switch
        {
            ThemeLight => _localizationService.T("Settings.Theme.Light"),
            ThemeDark => _localizationService.T("Settings.Theme.Dark"),
            _ => _localizationService.T("Settings.Theme.System")
        };
    }

    public string GetManagedDropActionDisplayName(string action)
    {
        return string.Equals(action, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? _localizationService.T("Settings.DropAction.Copy")
            : _localizationService.T("Settings.DropAction.Move");
    }

    public string GetCornerDisplayName(string corner)
    {
        return corner switch
        {
            CornerDefault => _localizationService.T("Settings.Corner.Default"),
            CornerSquare => _localizationService.T("Settings.Corner.Square"),
            CornerRound => _localizationService.T("Settings.Corner.Round"),
            _ => _localizationService.T("Settings.Corner.Small")
        };
    }

    public string GetWidgetAnimationEffectDisplayName(string effect)
    {
        return NormalizeWidgetAnimationEffect(effect) switch
        {
            SettingsService.WidgetAnimationEffectNone => _localizationService.T("Settings.Animation.Effect.None"),
            SettingsService.WidgetAnimationEffectFade => _localizationService.T("Settings.Animation.Effect.Fade"),
            SettingsService.WidgetAnimationEffectSlideRight => _localizationService.T("Settings.Animation.Effect.SlideRight"),
            SettingsService.WidgetAnimationEffectSlideLeft => _localizationService.T("Settings.Animation.Effect.SlideLeft"),
            SettingsService.WidgetAnimationEffectSlideUp => _localizationService.T("Settings.Animation.Effect.SlideUp"),
            SettingsService.WidgetAnimationEffectSlideDown => _localizationService.T("Settings.Animation.Effect.SlideDown"),
            SettingsService.WidgetAnimationEffectScaleFade => _localizationService.T("Settings.Animation.Effect.ScaleFade"),
            _ => _localizationService.T("Settings.Animation.Effect.SlideFade")
        };
    }

    public string GetWidgetAnimationSpeedDisplayName(string speed)
    {
        return NormalizeWidgetAnimationSpeed(speed) switch
        {
            SettingsService.WidgetAnimationSpeedVeryFast => _localizationService.T("Settings.Animation.Speed.VeryFast"),
            SettingsService.WidgetAnimationSpeedFast => _localizationService.T("Settings.Animation.Speed.Fast"),
            SettingsService.WidgetAnimationSpeedRelaxed => _localizationService.T("Settings.Animation.Speed.Relaxed"),
            SettingsService.WidgetAnimationSpeedSlow => _localizationService.T("Settings.Animation.Speed.Slow"),
            _ => _localizationService.T("Settings.Animation.Speed.Standard")
        };
    }

    public string GetLanguageDisplayName(string language)
    {
        return _localizationService.GetLanguageDisplayName(language);
    }

    private void OnLanguageChanged()
    {
        RefreshLocalizedProperties();
    }

    private void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(SelectedThemeText));
        OnPropertyChanged(nameof(SelectedLanguageText));
        OnPropertyChanged(nameof(SelectedManagedDropActionText));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(OpenSourceRepositoryDisplayText));
    }

    private static string NormalizeWidgetAnimationEffect(string? effect)
    {
        return effect is
            SettingsService.WidgetAnimationEffectNone or
            SettingsService.WidgetAnimationEffectFade or
            SettingsService.WidgetAnimationEffectSlideRight or
            SettingsService.WidgetAnimationEffectSlideLeft or
            SettingsService.WidgetAnimationEffectSlideUp or
            SettingsService.WidgetAnimationEffectSlideDown or
            SettingsService.WidgetAnimationEffectScaleFade or
            SettingsService.WidgetAnimationEffectSlideFade
            ? effect
            : SettingsService.WidgetAnimationEffectSlideFade;
    }

    private static string NormalizeWidgetAnimationSpeed(string? speed)
    {
        return speed is
            SettingsService.WidgetAnimationSpeedVeryFast or
            SettingsService.WidgetAnimationSpeedFast or
            SettingsService.WidgetAnimationSpeedStandard or
            SettingsService.WidgetAnimationSpeedRelaxed or
            SettingsService.WidgetAnimationSpeedSlow
            ? speed
            : SettingsService.WidgetAnimationSpeedStandard;
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        StartupService.SetEnabled(value);
        _settingsService.Settings.AutoStart = value;
        _settingsService.SaveDebounced();
    }

    partial void OnDoubleClickToOpenChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.DoubleClickToOpen = value;
        _settingsService.SaveDebounced();
    }

    partial void OnDefaultWidthChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultWidthInput));
            return;
        }

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
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultHeightInput));
            return;
        }

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
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.HideShortcutArrowOverlay = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowListItemDetailsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowListItemDetails = value;
        _settingsService.SaveDebounced();
    }

    partial void OnWidgetOpacityChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(WidgetOpacityValueText));
            OnPropertyChanged(nameof(WidgetOpacityPercent));
            OnPropertyChanged(nameof(WidgetOpacityPercentInput));
            return;
        }

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
        SaveAppearanceChange();
        OnPropertyChanged(nameof(WidgetOpacityValueText));
        OnPropertyChanged(nameof(WidgetOpacityPercent));
        OnPropertyChanged(nameof(WidgetOpacityPercentInput));
    }

    partial void OnIconSizeChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(IconSizeValueText));
            OnPropertyChanged(nameof(IconSizeInput));
            return;
        }

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
        SaveAppearanceChange();
        OnPropertyChanged(nameof(IconSizeValueText));
        OnPropertyChanged(nameof(IconSizeInput));
    }

    partial void OnTextSizeChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(TextSizeValueText));
            OnPropertyChanged(nameof(TextSizeInput));
            return;
        }

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
        SaveAppearanceChange();
        OnPropertyChanged(nameof(TextSizeValueText));
        OnPropertyChanged(nameof(TextSizeInput));
    }

    partial void OnLayoutDensityScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(LayoutDensityValueText));
            OnPropertyChanged(nameof(LayoutDensityPercent));
            OnPropertyChanged(nameof(LayoutDensityPercentInput));
            return;
        }

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
        SaveAppearanceChange();
        OnPropertyChanged(nameof(LayoutDensityValueText));
        OnPropertyChanged(nameof(LayoutDensityPercent));
        OnPropertyChanged(nameof(LayoutDensityPercentInput));
    }

    partial void OnHorizontalSpacingScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(HorizontalSpacingValueText));
            OnPropertyChanged(nameof(HorizontalSpacingPercent));
            OnPropertyChanged(nameof(HorizontalSpacingPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.HorizontalSpacingScale,
            next => HorizontalSpacingScale = next,
            next => _settingsService.Settings.HorizontalSpacingScale = next,
            nameof(HorizontalSpacingValueText),
            nameof(HorizontalSpacingPercent),
            nameof(HorizontalSpacingPercentInput));
    }

    partial void OnVerticalSpacingScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(VerticalSpacingValueText));
            OnPropertyChanged(nameof(VerticalSpacingPercent));
            OnPropertyChanged(nameof(VerticalSpacingPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.VerticalSpacingScale,
            next => VerticalSpacingScale = next,
            next => _settingsService.Settings.VerticalSpacingScale = next,
            nameof(VerticalSpacingValueText),
            nameof(VerticalSpacingPercent),
            nameof(VerticalSpacingPercentInput));
    }

    partial void OnFileNameWidthScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(FileNameWidthValueText));
            OnPropertyChanged(nameof(FileNameWidthPercent));
            OnPropertyChanged(nameof(FileNameWidthPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.FileNameWidthScale,
            next => FileNameWidthScale = next,
            next => _settingsService.Settings.FileNameWidthScale = next,
            nameof(FileNameWidthValueText),
            nameof(FileNameWidthPercent),
            nameof(FileNameWidthPercentInput));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settingsService.SaveAsync();
    }

    public void Dispose()
    {
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnAppearanceChanged()
    {
        RefreshAccentPreview();
    }

    private void RefreshAccentPreview()
    {
        _currentAccentColor = _themeService.GetEffectiveAccentColor();
        AccentPreviewBrush.Color = _currentAccentColor;
        AccentColorHex = AccentColorHelper.ToHex(_currentAccentColor);
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
        OnPropertyChanged(nameof(HorizontalSpacingPercentInput));
        OnPropertyChanged(nameof(VerticalSpacingPercentInput));
        OnPropertyChanged(nameof(FileNameWidthPercentInput));
    }

    private void ApplySpacingScaleChange(
        double value,
        double currentStoredValue,
        Action<double> setViewModelValue,
        Action<double> setStoredValue,
        params string[] dependentPropertyNames)
    {
        if (double.IsNaN(value))
        {
            setViewModelValue(currentStoredValue);
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            setViewModelValue(normalizedValue);
            return;
        }

        setStoredValue(normalizedValue);
        SaveAppearanceChange();
        foreach (string propertyName in dependentPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void SaveAppearanceChange()
    {
        if (DeferAppearancePersistence)
        {
            _settingsService.RequestAppearancePreview();
            return;
        }

        _settingsService.SaveDebounced(notifySubscribers: !SuppressAppearanceNotifications);
    }
}
