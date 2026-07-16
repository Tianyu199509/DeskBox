using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
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
    private const string TrayIconStyleSystem = "System";
    private const string TrayIconStyleColorful = "Colorful";
    private const string TrayIconStyleBlack = "Black";
    private const string TrayIconStyleWhite = "White";
    private const string CornerDefault = SettingsService.WidgetCornerPreferenceDefault;
    private const string CornerSquare = SettingsService.WidgetCornerPreferenceSquare;
    private const string CornerSmall = SettingsService.WidgetCornerPreferenceSmall;
    private const string CornerRound = SettingsService.WidgetCornerPreferenceRound;
    private const string MaterialMica = SettingsService.WidgetMaterialTypeMica;
    private const string MaterialMicaAlt = SettingsService.WidgetMaterialTypeMicaAlt;
    private const string MaterialAcrylic = SettingsService.WidgetMaterialTypeAcrylic;
    private const string MaterialAcrylicBase = SettingsService.WidgetMaterialTypeAcrylicBase;
    private const string MaterialSolid = SettingsService.WidgetMaterialTypeSolid;
    private const string BorderColorNeutral = SettingsService.WidgetBorderColorModeNeutral;
    private const string BorderColorAccent = SettingsService.WidgetBorderColorModeAccent;
    private const string BorderColorNone = SettingsService.WidgetBorderColorModeNone;
    private const string BorderNone = SettingsService.WidgetBorderStyleNone;
    private const string BorderThin = SettingsService.WidgetBorderStyleThin;
    private const string BorderMedium = SettingsService.WidgetBorderStyleMedium;
    private const string BorderThick = SettingsService.WidgetBorderStyleThick;
    private const string AnimationPresetNone = "None";
    private const string AnimationPresetGentle = "Gentle";
    private const string AnimationPresetStandard = "Standard";
    private const string AnimationPresetEmphasized = "Emphasized";
    private const string AnimationPresetCustom = "Custom";
    private const string RepositoryUrl = "https://github.com/Tianyu199509/DeskBox";
    private const string OfficialWebsiteUrl = "https://deskbox.fun";

    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly WidgetContentFactory _widgetContentFactory;
    private readonly IAppUpdateService _appUpdateService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isDisposed;
    private CancellationTokenSource? _updateOperationCts;
    private AppUpdateManifest? _availableUpdateManifest;
    private string? _downloadedUpdateInstallerPath;
    private bool _showManualUpdateFallback;
    private ImageSource? _donationWechatImageSource;
    private ImageSource? _donationAlipayImageSource;
    private Color _currentAccentColor;
    private string _selectedTheme = ThemeSystem;
    private string _selectedTrayIconStyle = TrayIconStyleSystem;
    private string _selectedLanguage = SettingsService.LanguageSystem;
    private string _selectedWidgetCornerPreference = CornerRound;
    private string _selectedWidgetMaterialType = MaterialMica;
    private string _selectedWidgetBorderColorMode = BorderColorNeutral;
    private string _selectedWidgetBorderStyle = BorderThin;
    private string _selectedLayoutDensity = SettingsService.LayoutDensityStandard;
    private string _selectedAnimationPreset = AnimationPresetStandard;
    private string _selectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectFade;
    private string _selectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
    private string _selectedWidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
    private string _selectedWidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
    private string _selectedDisplayWidgetChromeMode = SettingsService.WidgetChromeModeOverlay;
    private string _selectedInteractiveWidgetChromeMode = SettingsService.WidgetChromeModeStandard;
    private string _selectedWidgetTitleIconMode = SettingsService.WidgetTitleIconModeColor;
    private string _selectedWidgetLayerMode = SettingsService.WidgetLayerModeDynamic;
    private string _selectedQuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
    private string _selectedQuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
    private string _selectedTodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
    private string _selectedAttachmentStorageMode = SettingsService.AttachmentStorageModeLink;
    private string _selectedTodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
    private string _selectedTodoTabStyle = SettingsService.WidgetTabStyleButton;
    private int _selectedTodoReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
    private string _selectedMusicDisplayMode = SettingsService.MusicDisplayModeAuto;
private string _selectedWeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
private string _selectedWeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
private string _selectedWeatherDefaultView = SettingsService.WeatherDefaultViewToday;
private string _selectedWeatherSkin = SettingsService.WeatherSkinStandard;
private int _selectedWeatherRefreshInterval = 60;
    private bool _useSystemAccentColor;
    private string _accentColorHex = AccentColorHelper.DefaultAccentColorHex;
    private string _managedStorageRootPath = SettingsService.GetDefaultManagedStorageRootPath();
    private QuickAccessPinState _quickAccessPinState = QuickAccessPinState.Unknown;
    private bool _isQuickAccessBusy;
    private bool _globalHotkeyEnabled;
    private string _globalHotkeyText = string.Empty;
    private string _globalHotkeyStatusText = string.Empty;
    private string _globalHotkeyStatusKind = "Normal";
    private string _quickCaptureImageCacheText = string.Empty;
    private string _quickCaptureClipboardDiagnosticsText = string.Empty;
    private DragDropPermissionDiagnostic? _dragDropPermissionDiagnostic;
    private string _dragDropPermissionRepairStatusText = string.Empty;
    private bool _isDragDropPermissionRepairing;
    private bool _canClearQuickCaptureImageCache;
    private bool _isRestoringDefaults;
    private bool _isApplyingSettingsSnapshot;
    private bool _isApplyingLayoutDensityPreset;
    private bool _isApplyingAnimationPreset;
    private bool _isUpdatingHoverButtonActionSelection;

    private string[]? _cachedTrayIconStyleDisplayNames;
    private string[]? _cachedThemeDisplayNames;
    private string[]? _cachedLanguageDisplayNames;
    private string[]? _cachedWidgetCornerPreferenceDisplayNames;
    private string[]? _cachedWidgetMaterialTypeDisplayNames;
    private string[]? _cachedWidgetBorderColorModeDisplayNames;
    private string[]? _cachedWidgetBorderStyleDisplayNames;
    private string[]? _cachedLayoutDensityDisplayNames;
    private string[]? _cachedAnimationPresetDisplayNames;
    private string[]? _cachedWidgetAnimationEffectDisplayNames;
    private string[]? _cachedWidgetAnimationSpeedDisplayNames;
    private string[]? _cachedWidgetAnimationSlideDirectionDisplayNames;
    private string[]? _cachedWidgetAnimationEasingIntensityDisplayNames;
    private string[]? _cachedDisplayWidgetChromeModeDisplayNames;
    private string[]? _cachedInteractiveWidgetChromeModeDisplayNames;
    private string[]? _cachedWidgetTitleIconModeDisplayNames;
    private string[]? _cachedWidgetLayerModeDisplayNames;
    private string[]? _cachedQuickCaptureDefaultViewDisplayNames;
    private string[]? _cachedQuickCaptureTabStyleDisplayNames;
    private string[]? _cachedTodoNewTaskPositionDisplayNames;
    private string[]? _cachedAttachmentStorageModeDisplayNames;
    private string[]? _cachedTodoDefaultFilterDisplayNames;
    private string[]? _cachedTodoTabStyleDisplayNames;
    private string[]? _cachedTodoReminderOffsetDisplayNames;
    private string[]? _cachedMusicDisplayModeDisplayNames;
private string[]? _cachedWeatherTempUnitDisplayNames;
private string[]? _cachedWeatherWindUnitDisplayNames;
private string[]? _cachedWeatherDefaultViewDisplayNames;
private string[]? _cachedWeatherSkinDisplayNames;
private string[]? _cachedWeatherRefreshIntervalDisplayNames;

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _autoCheckForUpdates = true;
    [ObservableProperty] private bool _doubleClickToOpen;
    [ObservableProperty] private double _defaultWidth;
    [ObservableProperty] private double _defaultHeight;
    [ObservableProperty] private bool _hideShortcutArrowOverlay;
    [ObservableProperty] private bool _showImageFilesAsIcons;
    [ObservableProperty] private bool _showHoverButtons = true;
    [ObservableProperty] private bool _resizeSnapEnabled = true;
    [ObservableProperty] private bool _showHoverActionLockPosition;
    [ObservableProperty] private bool _showHoverActionLockSize;
    [ObservableProperty] private bool _showHoverActionAdd;
    [ObservableProperty] private bool _showHoverActionMore = true;
    [ObservableProperty] private bool _showHoverActionDelete = true;
    [ObservableProperty] private bool _showListItemDetails;
    [ObservableProperty] private bool _showFileItemPathTooltips = true;
    [ObservableProperty] private double _widgetOpacity = SettingsService.DefaultWidgetOpacity;
    [ObservableProperty] private double _widgetMaterialIntensity = SettingsService.DefaultWidgetMaterialIntensity;
    [ObservableProperty] private double _iconSize = SettingsService.DefaultIconSize;
    [ObservableProperty] private double _textSize = SettingsService.DefaultTextSize;
    [ObservableProperty] private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    [ObservableProperty] private double _horizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
    [ObservableProperty] private double _verticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
    [ObservableProperty] private double _fileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
    [ObservableProperty] private bool _showFileExtensions;
    [ObservableProperty] private bool _hideShortcutExtensionWhenShowingFileExtensions = true;
    [ObservableProperty] private bool _quickCaptureEnabled;
    [ObservableProperty] private bool _todoEnabled;
    [ObservableProperty] private bool _todoShowCompletedTasks = true;
    [ObservableProperty] private bool _todoShowFooterStats;
    [ObservableProperty] private bool _todoShowClearCompletedButton = true;
    [ObservableProperty] private bool _todoConfirmBeforeDelete;
    [ObservableProperty] private bool _todoReminderEnabled = true;
    [ObservableProperty] private bool _musicUseArtworkBackdrop = true;
    [ObservableProperty] private bool _musicEnableCoverHoverMotion = true;

[ObservableProperty] private bool _weatherAutoLocation = true;
[ObservableProperty] private string _weatherCityName = string.Empty;
[ObservableProperty] private bool _weatherShowForecast = true;
[ObservableProperty] private bool _weatherShowSunrise = true;
[ObservableProperty] private bool _weatherShowUvIndex = true;
[ObservableProperty] private bool _weatherShowPrecipitation = true;
[ObservableProperty] private bool _weatherShowHumidity = true;
[ObservableProperty] private bool _weatherShowWind = true;
[ObservableProperty] private bool _weatherShowPressure;

    [ObservableProperty] private bool _quickCaptureClipboardEnabled;
    [ObservableProperty] private bool _quickCaptureImageClipboardEnabled;
    [ObservableProperty] private int _quickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
    [ObservableProperty] private bool _quickCaptureShowCreatedTime = true;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private string _updateDetailText = string.Empty;
    [ObservableProperty] private double _updateProgressValue;

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
    public int SelectedThemeIndex => Array.IndexOf(AvailableThemes, _selectedTheme);

    public string SelectedTrayIconStyle
    {
        get => _selectedTrayIconStyle;
        set
        {
            if (!SetProperty(ref _selectedTrayIconStyle, value))
            {
                return;
            }

            string styleValue = value is TrayIconStyleColorful or TrayIconStyleBlack or TrayIconStyleWhite
                ? value
                : TrayIconStyleSystem;

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.TrayIconStyle = styleValue;
            _settingsService.SaveDebounced();
            App.Current.UpdateTrayIcon();
            OnPropertyChanged(nameof(SelectedTrayIconStyleText));
        }
    }

    public string SelectedTrayIconStyleText => GetTrayIconStyleDisplayName(SelectedTrayIconStyle);
    public int SelectedTrayIconStyleIndex => Array.IndexOf(AvailableTrayIconStyles, _selectedTrayIconStyle);

    public string[] AvailableTrayIconStyles { get; } =
    [
        TrayIconStyleSystem,
        TrayIconStyleColorful,
        TrayIconStyleBlack,
        TrayIconStyleWhite
    ];

    public string[] AvailableTrayIconStyleDisplayNames => _cachedTrayIconStyleDisplayNames ??= AvailableTrayIconStyles.Select(GetTrayIconStyleDisplayName).ToArray();

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
        }
    }

    public string SelectedLanguageText => _localizationService.GetLanguageDisplayName(SelectedLanguage);
    public int SelectedLanguageIndex => Array.IndexOf(AvailableLanguages, _selectedLanguage);

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
    public int SelectedWidgetCornerPreferenceIndex => Array.IndexOf(AvailableWidgetCornerPreferences, _selectedWidgetCornerPreference);

    public string SelectedWidgetMaterialType
    {
        get => _selectedWidgetMaterialType;
        set
        {
            if (!SetProperty(ref _selectedWidgetMaterialType, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetMaterialType = value is
                MaterialMica or MaterialMicaAlt or MaterialAcrylic or MaterialAcrylicBase or MaterialSolid
                ? value
                : SettingsService.WidgetMaterialTypeAcrylic;

            bool forcedSolidOpacity =
                _settingsService.Settings.WidgetMaterialType == MaterialSolid &&
                Math.Abs(WidgetOpacity - SettingsService.MaxWidgetOpacity) > 0.0001;
            if (forcedSolidOpacity)
            {
                WidgetOpacity = SettingsService.MaxWidgetOpacity;
            }
            else
            {
                _settingsService.RequestAppearancePreview();
            }

            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetMaterialTypeText));
            OnPropertyChanged(nameof(IsOpacitySliderEnabled));
            OnPropertyChanged(nameof(WidgetOpacityVisibility));
            OnPropertyChanged(nameof(MaterialIntensityVisibility));
        }
    }

    public string SelectedWidgetMaterialTypeText => GetMaterialTypeDisplayName(SelectedWidgetMaterialType);
    public int SelectedWidgetMaterialTypeIndex => Array.IndexOf(AvailableWidgetMaterialTypes, _selectedWidgetMaterialType);

    public bool IsOpacitySliderEnabled =>
        SettingsService.SupportsWidgetOpacity(_selectedWidgetMaterialType);

    public Visibility WidgetOpacityVisibility => IsOpacitySliderEnabled
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MaterialIntensityVisibility =>
        SettingsService.SupportsMaterialIntensity(_selectedWidgetMaterialType)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string SelectedWidgetBorderColorMode
    {
        get => _selectedWidgetBorderColorMode;
        set
        {
            if (!SetProperty(ref _selectedWidgetBorderColorMode, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetBorderColorMode = value is
                BorderColorNeutral or BorderColorAccent or BorderColorNone
                    ? value
                    : BorderColorNeutral;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetBorderColorModeText));
            OnPropertyChanged(nameof(IsWidgetBorderStyleEnabled));
        }
    }

    public string SelectedWidgetBorderColorModeText =>
        GetBorderColorModeDisplayName(SelectedWidgetBorderColorMode);

    public int SelectedWidgetBorderColorModeIndex =>
        Array.IndexOf(AvailableWidgetBorderColorModes, _selectedWidgetBorderColorMode);

    public bool IsWidgetBorderStyleEnabled =>
        _selectedWidgetBorderColorMode != BorderColorNone;

    public string SelectedWidgetBorderStyle
    {
        get => _selectedWidgetBorderStyle;
        set
        {
            if (!SetProperty(ref _selectedWidgetBorderStyle, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetBorderStyle = value is BorderThin or BorderMedium or BorderThick
                ? value
                : SettingsService.WidgetBorderStyleThin;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetBorderStyleText));
        }
    }

    public string SelectedWidgetBorderStyleText => GetBorderStyleDisplayName(SelectedWidgetBorderStyle);
    public int SelectedWidgetBorderStyleIndex => Array.IndexOf(AvailableWidgetBorderStyles, _selectedWidgetBorderStyle);

    public string SelectedLayoutDensity
    {
        get => _selectedLayoutDensity;
        set
        {
            string normalizedValue = value is
                SettingsService.LayoutDensityCompact or
                SettingsService.LayoutDensityStandard or
                SettingsService.LayoutDensityRelaxed or
                SettingsService.LayoutDensityCustom
                    ? value
                    : SettingsService.LayoutDensityCustom;
            if (!SetProperty(ref _selectedLayoutDensity, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedLayoutDensityText));
            OnPropertyChanged(nameof(SelectedLayoutDensityIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            if (normalizedValue == SettingsService.LayoutDensityCustom)
            {
                _settingsService.Settings.LayoutDensity = normalizedValue;
                _settingsService.SaveDebounced();
                return;
            }

            ApplyLayoutDensityPreset(normalizedValue);
        }
    }

    public string SelectedLayoutDensityText => GetLayoutDensityDisplayName(SelectedLayoutDensity);
    public int SelectedLayoutDensityIndex => Array.IndexOf(AvailableLayoutDensities, _selectedLayoutDensity);

    public string SelectedAnimationPreset
    {
        get => _selectedAnimationPreset;
        set
        {
            string normalizedValue = value is
                AnimationPresetNone or
                AnimationPresetGentle or
                AnimationPresetStandard or
                AnimationPresetEmphasized or
                AnimationPresetCustom
                    ? value
                    : AnimationPresetCustom;
            if (!SetProperty(ref _selectedAnimationPreset, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedAnimationPresetText));
            OnPropertyChanged(nameof(SelectedAnimationPresetIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot || normalizedValue == AnimationPresetCustom)
            {
                return;
            }

            ApplyAnimationPreset(normalizedValue);
        }
    }

    public string SelectedAnimationPresetText => GetAnimationPresetDisplayName(SelectedAnimationPreset);
    public int SelectedAnimationPresetIndex => Array.IndexOf(AvailableAnimationPresets, _selectedAnimationPreset);

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
            if (!_isApplyingAnimationPreset)
            {
                _settingsService.SaveDebounced();
            }
            OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
            OnPropertyChanged(nameof(IsDirectionEnabled));
            OnPropertyChanged(nameof(IsEasingEnabled));
            OnPropertyChanged(nameof(IsSpeedEnabled));
            SyncAnimationPresetSelection();
        }
    }

    public string SelectedWidgetAnimationEffectText => GetWidgetAnimationEffectDisplayName(SelectedWidgetAnimationEffect);
    public int SelectedWidgetAnimationEffectIndex => Array.IndexOf(AvailableWidgetAnimationEffects, _selectedWidgetAnimationEffect);

    public bool IsDirectionEnabled => _selectedWidgetAnimationEffect is
        SettingsService.WidgetAnimationEffectSlideFade or
        SettingsService.WidgetAnimationEffectScaleSlide;

    public bool IsEasingEnabled => _selectedWidgetAnimationEffect != SettingsService.WidgetAnimationEffectNone;

    public bool IsSpeedEnabled => _selectedWidgetAnimationEffect != SettingsService.WidgetAnimationEffectNone;

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
            if (!_isApplyingAnimationPreset)
            {
                _settingsService.SaveDebounced();
            }
            OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
            SyncAnimationPresetSelection();
        }
    }

    public string SelectedWidgetAnimationSpeedText => GetWidgetAnimationSpeedDisplayName(SelectedWidgetAnimationSpeed);
    public int SelectedWidgetAnimationSpeedIndex => Array.IndexOf(AvailableWidgetAnimationSpeeds, _selectedWidgetAnimationSpeed);

    public string SelectedWidgetAnimationSlideDirection
    {
        get => _selectedWidgetAnimationSlideDirection;
        set
        {
            if (!SetProperty(ref _selectedWidgetAnimationSlideDirection, NormalizeWidgetAnimationSlideDirection(value)))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetAnimationSlideDirection = _selectedWidgetAnimationSlideDirection;
            if (!_isApplyingAnimationPreset)
            {
                _settingsService.SaveDebounced();
            }
            OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionText));
            SyncAnimationPresetSelection();
        }
    }

    public string SelectedWidgetAnimationSlideDirectionText => GetWidgetAnimationSlideDirectionDisplayName(SelectedWidgetAnimationSlideDirection);
    public int SelectedWidgetAnimationSlideDirectionIndex => Array.IndexOf(AvailableWidgetAnimationSlideDirections, _selectedWidgetAnimationSlideDirection);

    public string SelectedWidgetAnimationEasingIntensity
    {
        get => _selectedWidgetAnimationEasingIntensity;
        set
        {
            if (!SetProperty(ref _selectedWidgetAnimationEasingIntensity, NormalizeWidgetAnimationEasingIntensity(value)))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetAnimationEasingIntensity = _selectedWidgetAnimationEasingIntensity;
            if (!_isApplyingAnimationPreset)
            {
                _settingsService.SaveDebounced();
            }
            OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityText));
            SyncAnimationPresetSelection();
        }
    }

    public string SelectedWidgetAnimationEasingIntensityText => GetWidgetAnimationEasingIntensityDisplayName(SelectedWidgetAnimationEasingIntensity);
    public int SelectedWidgetAnimationEasingIntensityIndex => Array.IndexOf(AvailableWidgetAnimationEasingIntensities, _selectedWidgetAnimationEasingIntensity);

    public string SelectedDisplayWidgetChromeMode
    {
        get => _selectedDisplayWidgetChromeMode;
        set
        {
            if (!SetProperty(ref _selectedDisplayWidgetChromeMode, NormalizeWidgetChromeModeSetting(value, WidgetChromeMode.Overlay)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.DisplayWidgetChromeMode = _selectedDisplayWidgetChromeMode;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        }
    }

    public string SelectedDisplayWidgetChromeModeText => GetWidgetChromeModeDisplayName(SelectedDisplayWidgetChromeMode);
    public int SelectedDisplayWidgetChromeModeIndex => Array.IndexOf(AvailableDisplayWidgetChromeModes, _selectedDisplayWidgetChromeMode);

    public string SelectedInteractiveWidgetChromeMode
    {
        get => _selectedInteractiveWidgetChromeMode;
        set
        {
            if (!SetProperty(ref _selectedInteractiveWidgetChromeMode, NormalizeWidgetChromeModeSetting(value, WidgetChromeMode.Standard)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.InteractiveWidgetChromeMode = _selectedInteractiveWidgetChromeMode;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        }
    }

    public string SelectedInteractiveWidgetChromeModeText => GetWidgetChromeModeDisplayName(SelectedInteractiveWidgetChromeMode);
    public int SelectedInteractiveWidgetChromeModeIndex => Array.IndexOf(AvailableInteractiveWidgetChromeModes, _selectedInteractiveWidgetChromeMode);

    public string SelectedWidgetTitleIconMode
    {
        get => _selectedWidgetTitleIconMode;
        set
        {
            if (!SetProperty(ref _selectedWidgetTitleIconMode, NormalizeWidgetTitleIconModeSetting(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetTitleIconMode = _selectedWidgetTitleIconMode;
            SaveAppearanceChange();
            OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
            OnPropertyChanged(nameof(SelectedWidgetTitleIconModeIndex));
        }
    }

    public string SelectedWidgetTitleIconModeText => GetWidgetTitleIconModeDisplayName(SelectedWidgetTitleIconMode);
    public int SelectedWidgetTitleIconModeIndex => Array.IndexOf(AvailableWidgetTitleIconModes, _selectedWidgetTitleIconMode);

    public bool CanToggleHoverActionLockPosition => CanToggleHoverButtonAction(ShowHoverActionLockPosition);
    public bool CanToggleHoverActionLockSize => CanToggleHoverButtonAction(ShowHoverActionLockSize);
    public bool CanToggleHoverActionAdd => CanToggleHoverButtonAction(ShowHoverActionAdd);
    public bool CanToggleHoverActionMore => CanToggleHoverButtonAction(ShowHoverActionMore);
    public bool CanToggleHoverActionDelete => CanToggleHoverButtonAction(ShowHoverActionDelete);
    public string HoverButtonActionsSummaryText => string.Join(
        _localizationService.IsEnglish ? ", " : "、",
        AvailableWidgetHoverButtonActions
            .Where(IsHoverButtonActionSelected)
            .Select(GetHoverButtonActionDisplayName));

    public string[] AvailableWidgetHoverButtonActions { get; } =
    [
        SettingsService.WidgetHoverActionLockPosition,
        SettingsService.WidgetHoverActionLockSize,
        SettingsService.WidgetHoverActionAdd,
        SettingsService.WidgetHoverActionMore,
        SettingsService.WidgetHoverActionDelete
    ];

    public string SelectedWidgetLayerMode
    {
        get => _selectedWidgetLayerMode;
        set
        {
            string normalizedValue = SettingsService.NormalizeWidgetLayerModeSetting(value);
            if (!SetProperty(ref _selectedWidgetLayerMode, normalizedValue))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetLayerMode = normalizedValue;
            _settingsService.SaveDebounced();
            App.Current?.WidgetManager?.RefreshVisibleWidgetDesktopLayers("settings-layer-mode");
            OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
            OnPropertyChanged(nameof(SelectedWidgetLayerModeIndex));
        }
    }

    public string SelectedWidgetLayerModeText => GetWidgetLayerModeDisplayName(SelectedWidgetLayerMode);
    public int SelectedWidgetLayerModeIndex => Array.IndexOf(AvailableWidgetLayerModes, _selectedWidgetLayerMode);

    [RelayCommand]
    public void ResetDisplayWidgetChromeOverrides()
    {
        ResetWidgetChromeOverrides(WidgetChromeCategory.Display, SelectedDisplayWidgetChromeMode);
    }

    [RelayCommand]
    public void ResetInteractiveWidgetChromeOverrides()
    {
        ResetWidgetChromeOverrides(WidgetChromeCategory.Interactive, SelectedInteractiveWidgetChromeMode);
    }

    internal int ResetWidgetChromeOverrides(WidgetChromeCategory category, string mode)
    {
        int changed = ResetWidgetChromeOverrides(
            _settingsService.Settings,
            _widgetContentFactory,
            category);

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
        }

        return changed;
    }

    internal static int ResetWidgetChromeOverrides(
        AppSettings settings,
        WidgetContentFactory widgetContentFactory,
        WidgetChromeCategory category,
        string? mode = null)
    {
        int changed = 0;

        foreach (var widget in settings.Widgets)
        {
            WidgetContentDescriptor descriptor;
            try
            {
                descriptor = widgetContentFactory.GetDescriptor(widget.WidgetKind);
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (descriptor.ChromeCategory != category)
            {
                continue;
            }

            if (widget.Metadata is null ||
                !widget.Metadata.ContainsKey(WidgetChromeModeNames.MetadataKey))
            {
                continue;
            }

            WidgetChromeModeNames.SetOverrideMode(widget, WidgetChromeMode.System);
            changed++;
        }

        return changed;
    }

    public string SelectedTodoNewTaskPosition
    {
        get => _selectedTodoNewTaskPosition;
        set
        {
            if (!SetProperty(ref _selectedTodoNewTaskPosition, NormalizeTodoNewTaskPosition(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoNewTaskPosition = _selectedTodoNewTaskPosition;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        }
    }

    public string SelectedTodoNewTaskPositionText => GetTodoNewTaskPositionDisplayName(SelectedTodoNewTaskPosition);
    public int SelectedTodoNewTaskPositionIndex => Array.IndexOf(AvailableTodoNewTaskPositions, _selectedTodoNewTaskPosition);

    public string SelectedAttachmentStorageMode
    {
        get => _selectedAttachmentStorageMode;
        set
        {
            string normalized = SettingsService.NormalizeAttachmentStorageMode(value);
            if (!SetProperty(ref _selectedAttachmentStorageMode, normalized))
            {
                return;
            }

            if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
            {
                _settingsService.Settings.AttachmentStorageMode = normalized;
                _settingsService.SaveDebounced();
            }

            OnPropertyChanged(nameof(SelectedAttachmentStorageModeIndex));
        }
    }

    public int SelectedAttachmentStorageModeIndex => Array.IndexOf(
        AvailableAttachmentStorageModes,
        _selectedAttachmentStorageMode);

    public string SelectedQuickCaptureDefaultView
    {
        get => _selectedQuickCaptureDefaultView;
        set
        {
            if (!SetProperty(ref _selectedQuickCaptureDefaultView, NormalizeQuickCaptureDefaultView(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureDefaultView = _selectedQuickCaptureDefaultView;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        }
    }

    public string SelectedQuickCaptureDefaultViewText => GetQuickCaptureDefaultViewDisplayName(SelectedQuickCaptureDefaultView);
    public int SelectedQuickCaptureDefaultViewIndex => Array.IndexOf(AvailableQuickCaptureDefaultViews, _selectedQuickCaptureDefaultView);

    public string SelectedQuickCaptureTabStyle
    {
        get => _selectedQuickCaptureTabStyle;
        set
        {
            if (!SetProperty(ref _selectedQuickCaptureTabStyle, SettingsService.NormalizeWidgetTabStyle(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureTabStyle = _selectedQuickCaptureTabStyle;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
            OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        }
    }

    public string SelectedQuickCaptureTabStyleText => GetWidgetTabStyleDisplayName(SelectedQuickCaptureTabStyle);
    public int SelectedQuickCaptureTabStyleIndex => Array.IndexOf(AvailableWidgetTabStyles, _selectedQuickCaptureTabStyle);

    public string SelectedTodoDefaultFilter
    {
        get => _selectedTodoDefaultFilter;
        set
        {
            if (!SetProperty(ref _selectedTodoDefaultFilter, NormalizeTodoDefaultFilter(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoDefaultFilter = _selectedTodoDefaultFilter;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        }
    }

    public string SelectedTodoDefaultFilterText => GetTodoDefaultFilterDisplayName(SelectedTodoDefaultFilter);
    public int SelectedTodoDefaultFilterIndex => Array.IndexOf(AvailableTodoDefaultFilters, _selectedTodoDefaultFilter);

    public string SelectedTodoTabStyle
    {
        get => _selectedTodoTabStyle;
        set
        {
            if (!SetProperty(ref _selectedTodoTabStyle, SettingsService.NormalizeWidgetTabStyle(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoTabStyle = _selectedTodoTabStyle;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoTabStyleText));
            OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        }
    }

    public string SelectedTodoTabStyleText => GetWidgetTabStyleDisplayName(SelectedTodoTabStyle);
    public int SelectedTodoTabStyleIndex => Array.IndexOf(AvailableWidgetTabStyles, _selectedTodoTabStyle);

    public int SelectedTodoReminderOffsetMinutes
    {
        get => _selectedTodoReminderOffsetMinutes;
        set
        {
            int normalizedValue = SettingsService.NormalizeTodoReminderOffsetMinutes(value);
            if (!SetProperty(ref _selectedTodoReminderOffsetMinutes, normalizedValue))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoDefaultReminderOffsetMinutes = normalizedValue;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        }
    }

    public string SelectedTodoReminderOffsetMinutesText => GetTodoReminderOffsetDisplayName(SelectedTodoReminderOffsetMinutes);
    public int SelectedTodoReminderOffsetMinutesIndex => Array.IndexOf(AvailableTodoReminderOffsetMinutes, _selectedTodoReminderOffsetMinutes);

    public string SelectedMusicDisplayMode
    {
        get => _selectedMusicDisplayMode;
        set
        {
            string normalizedValue = SettingsService.NormalizeMusicDisplayMode(value);
            if (!SetProperty(ref _selectedMusicDisplayMode, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
            OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.MusicDisplayMode = normalizedValue;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedMusicDisplayModeText => GetMusicDisplayModeDisplayName(SelectedMusicDisplayMode);
    public int SelectedMusicDisplayModeIndex => Array.IndexOf(AvailableMusicDisplayModes, _selectedMusicDisplayMode);

    public string AccentColorHex
    {
        get => _accentColorHex;
        private set => SetProperty(ref _accentColorHex, value);
    }

    public Color SelectedAccentColor
    {
        get => _currentAccentColor;
        set
        {
            if (_currentAccentColor.Equals(value))
            {
                return;
            }

            SetCustomAccentColor(value);
        }
    }

    public string ManagedStorageRootPath
    {
        get => _managedStorageRootPath;
        private set => SetProperty(ref _managedStorageRootPath, value);
    }

    public QuickAccessPinState ManagedStorageQuickAccessPinState
    {
        get => _quickAccessPinState;
        private set
        {
            if (!SetProperty(ref _quickAccessPinState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(QuickAccessStatusText));
            OnPropertyChanged(nameof(PinQuickAccessButtonText));
            OnPropertyChanged(nameof(PinQuickAccessToolTipText));
            OnPropertyChanged(nameof(ShouldUnpinManagedStorageFromQuickAccess));
        }
    }

    public bool IsQuickAccessBusy
    {
        get => _isQuickAccessBusy;
        private set
        {
            if (!SetProperty(ref _isQuickAccessBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(QuickAccessStatusText));
            OnPropertyChanged(nameof(PinQuickAccessButtonText));
            OnPropertyChanged(nameof(PinQuickAccessToolTipText));
            OnPropertyChanged(nameof(CanInvokeQuickAccessAction));
        }
    }

    public bool CanInvokeQuickAccessAction => !IsQuickAccessBusy;

    public string QuickAccessStatusText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessStatusUpdating")
        : ManagedStorageQuickAccessPinState switch
    {
        QuickAccessPinState.Pinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusPinned"),
        QuickAccessPinState.NotPinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusNotPinned"),
        _ => _localizationService.T("Settings.ManagedPath.QuickAccessStatusUnknown")
    };

    public string PinQuickAccessButtonText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdating")
        : ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned
            ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccess")
            : _localizationService.T("Settings.ManagedPath.PinQuickAccess");

    public string PinQuickAccessToolTipText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdatingTooltip")
        : ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned
            ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccessTooltip")
            : _localizationService.T("Settings.ManagedPath.PinQuickAccessTooltip");

    public bool ShouldUnpinManagedStorageFromQuickAccess => ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned;

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set
        {
            if (!SetProperty(ref _globalHotkeyEnabled, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            App.Current?.GlobalHotkeyService?.SetEnabled(value);
            RefreshGlobalHotkeyStatus();
        }
    }

    public string GlobalHotkeyText
    {
        get => _globalHotkeyText;
        private set => SetProperty(ref _globalHotkeyText, value);
    }

    public string GlobalHotkeyStatusText
    {
        get => _globalHotkeyStatusText;
        private set => SetProperty(ref _globalHotkeyStatusText, value);
    }

    public string GlobalHotkeyStatusKind
    {
        get => _globalHotkeyStatusKind;
        private set => SetProperty(ref _globalHotkeyStatusKind, value);
    }

    public string IconSizeValueText => $"{Math.Round(IconSize):0}px";
    public string WidgetOpacityValueText => $"{Math.Round(WidgetOpacity * 100):0}%";
    public string WidgetMaterialIntensityValueText => $"{Math.Round(WidgetMaterialIntensity * 100):0}%";
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
        set => ApplyNumberInput(value, () => DefaultHeight, next => DefaultHeight = next, SettingsService.MinWidgetHeight, 1200d, 0);
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

    public string GlobalHotkeyDescription => _localizationService.T("Settings.GlobalHotkey.Description");
    public bool CanShowGlobalHotkeyWarning => GlobalHotkeyEnabled && GlobalHotkeyService.IsRiskyGesture(GetCurrentGlobalHotkeyGesture());
    public IEnumerable<FeatureWidgetEntry> FeatureWidgetEntries
    {
        get
        {
            var factory = new FeatureWidgetEntryFactory(
                _localizationService,
                new WidgetContentFactory(_localizationService),
                WidgetRegistry.Default,
                IsWidgetEnabled);
            return factory.CreateEntries();
        }
    }

    public bool IsWidgetEnabled(WidgetKind kind)
    {
        return App.Current?.WidgetManager?.IsFeatureWidgetEnabled(kind) ??
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
    }

    public void SetWidgetEnabled(WidgetKind kind, bool enabled)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                QuickCaptureEnabled = enabled;
                return;
            case WidgetKind.Todo:
                TodoEnabled = enabled;
                return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
        _ = SyncFeatureWidgetAsync(kind, enabled);
    }

    public async Task ResetFeatureWidgetAsync(WidgetKind kind)
    {
        if (!FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        try
        {
            await ApplyFeatureWidgetDefaultSettingsAsync(kind);

            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.ResetFeatureWidgetAsync(kind);
            }
            else
            {
                await _settingsService.SaveAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to reset feature widget kind={kind}: {ex}");
        }
        finally
        {
            RefreshFeatureWidgetViewState(kind);
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    private async Task ApplyFeatureWidgetDefaultSettingsAsync(WidgetKind kind)
    {
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            switch (kind)
            {
                case WidgetKind.QuickCapture:
                    QuickCaptureClipboardEnabled = false;
                    QuickCaptureImageClipboardEnabled = false;
                    QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
                    QuickCaptureShowCreatedTime = true;
                    SelectedQuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
                    SelectedQuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
                    _settingsService.Settings.QuickCaptureClipboardEnabled = false;
                    _settingsService.Settings.QuickCaptureImageClipboardEnabled = false;
                    _settingsService.Settings.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
                    _settingsService.Settings.QuickCaptureShowCreatedTime = true;
                    _settingsService.Settings.QuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
                    _settingsService.Settings.QuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
                    _settingsService.Settings.LastQuickCaptureFileWidgetId = string.Empty;
                    App.Current?.QuickCaptureClipboardService?.Refresh();
                    RefreshQuickCaptureClipboardDiagnostics();
                    break;
                case WidgetKind.Todo:
                    TodoShowCompletedTasks = true;
                    TodoShowFooterStats = false;
                    TodoShowClearCompletedButton = true;
                    TodoConfirmBeforeDelete = false;
                    TodoReminderEnabled = true;
                    SelectedTodoReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
                    SelectedTodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
                    SelectedTodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
                    SelectedTodoTabStyle = SettingsService.WidgetTabStyleButton;
                    _settingsService.Settings.TodoShowCompletedTasks = true;
                    _settingsService.Settings.TodoShowFooterStats = false;
                    _settingsService.Settings.TodoShowClearCompletedButton = true;
                    _settingsService.Settings.TodoConfirmBeforeDelete = false;
                    _settingsService.Settings.TodoReminderEnabled = true;
                    _settingsService.Settings.TodoDefaultReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
                    _settingsService.Settings.TodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
                    _settingsService.Settings.TodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
                    _settingsService.Settings.TodoTabStyle = SettingsService.WidgetTabStyleButton;
                    break;
                case WidgetKind.Music:
                    MusicUseArtworkBackdrop = true;
                    MusicEnableCoverHoverMotion = true;
                    SelectedMusicDisplayMode = SettingsService.MusicDisplayModeAuto;
                    _settingsService.Settings.MusicUseArtworkBackdrop = true;
                    _settingsService.Settings.MusicEnableCoverHoverMotion = true;
                    _settingsService.Settings.MusicDisplayMode = SettingsService.MusicDisplayModeAuto;
                    break;
                case WidgetKind.Weather:
                    WeatherAutoLocation = true;
                    WeatherCityName = string.Empty;
                    SelectedWeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
                    SelectedWeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
                    SelectedWeatherDefaultView = SettingsService.WeatherDefaultViewToday;
                    SelectedWeatherSkin = SettingsService.WeatherSkinStandard;
                    WeatherShowForecast = true;
                    WeatherShowSunrise = true;
                    WeatherShowUvIndex = true;
                    WeatherShowPrecipitation = true;
                    WeatherShowHumidity = true;
                    WeatherShowWind = true;
                    WeatherShowPressure = false;
                    SelectedWeatherRefreshInterval = 60;

                    _settingsService.Settings.WeatherAutoLocation = true;
                    _settingsService.Settings.WeatherCityName = string.Empty;
                    _settingsService.Settings.WeatherLatitude = 0;
                    _settingsService.Settings.WeatherLongitude = 0;
                    _settingsService.Settings.WeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
                    _settingsService.Settings.WeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
                    _settingsService.Settings.WeatherDefaultView = SettingsService.WeatherDefaultViewToday;
                    _settingsService.Settings.WeatherSkin = SettingsService.WeatherSkinStandard;
                    _settingsService.Settings.WeatherShowForecast = true;
                    _settingsService.Settings.WeatherShowSunrise = true;
                    _settingsService.Settings.WeatherShowUvIndex = true;
                    _settingsService.Settings.WeatherShowPrecipitation = true;
                    _settingsService.Settings.WeatherShowHumidity = true;
                    _settingsService.Settings.WeatherShowWind = true;
                    _settingsService.Settings.WeatherShowPressure = false;
                    _settingsService.Settings.WeatherRefreshIntervalMinutes = 60;
                    break;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }

        await _settingsService.SaveAsync();
    }

    private void RefreshFeatureWidgetViewState(WidgetKind kind)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
                OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
                OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
                OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
                OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
                OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
                OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
                break;
            case WidgetKind.Todo:
                OnPropertyChanged(nameof(TodoEnabled));
                OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
                OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
                OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
                OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
                OnPropertyChanged(nameof(SelectedTodoTabStyleText));
                OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
                break;
            case WidgetKind.Music:
                break;
            case WidgetKind.Weather:
                OnPropertyChanged(nameof(SelectedWeatherDefaultViewIndex));
                OnPropertyChanged(nameof(SelectedWeatherSkinIndex));
                OnPropertyChanged(nameof(SelectedWeatherTemperatureUnitIndex));
                OnPropertyChanged(nameof(SelectedWeatherWindSpeedUnitIndex));
                OnPropertyChanged(nameof(SelectedWeatherRefreshIntervalIndex));
                break;
        }
    }

    private async Task SyncFeatureWidgetAsync(WidgetKind kind, bool enabled)
    {
        try
        {
            if (App.Current?.WidgetManager is not { } widgetManager)
            {
                await _settingsService.SaveAsync();
                return;
            }

            await widgetManager.SetFeatureWidgetEnabledAsync(kind, enabled, reveal: enabled);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync feature widget enabled state kind={kind}: {ex}");
        }
        finally
        {
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    public SolidColorBrush AccentPreviewBrush { get; } = new(AccentColorHelper.DefaultAccentColor);

    public string[] AvailableThemes { get; } = [ThemeSystem, ThemeLight, ThemeDark];
    public string[] AvailableThemeDisplayNames => _cachedThemeDisplayNames ??= AvailableThemes.Select(GetThemeDisplayName).ToArray();
    public string[] AvailableLanguages { get; } = [SettingsService.LanguageSystem, SettingsService.LanguageChinese, SettingsService.LanguageEnglish];
    public string[] AvailableLanguageDisplayNames => _cachedLanguageDisplayNames ??= AvailableLanguages.Select(_localizationService.GetLanguageDisplayName).ToArray();
    public string[] AvailableWidgetCornerPreferences { get; } = [CornerSmall, CornerRound, CornerSquare];
    public string[] AvailableWidgetCornerPreferenceDisplayNames => _cachedWidgetCornerPreferenceDisplayNames ??= AvailableWidgetCornerPreferences.Select(GetCornerDisplayName).ToArray();

    public string[] AvailableWidgetMaterialTypes { get; } =
        [MaterialAcrylic, MaterialAcrylicBase, MaterialMica, MaterialMicaAlt, MaterialSolid];
    public string[] AvailableWidgetMaterialTypeDisplayNames => _cachedWidgetMaterialTypeDisplayNames ??= AvailableWidgetMaterialTypes.Select(GetMaterialTypeDisplayName).ToArray();

    public string[] AvailableWidgetBorderColorModes { get; } =
        [BorderColorNeutral, BorderColorAccent, BorderColorNone];
    public string[] AvailableWidgetBorderColorModeDisplayNames =>
        _cachedWidgetBorderColorModeDisplayNames ??=
            AvailableWidgetBorderColorModes.Select(GetBorderColorModeDisplayName).ToArray();

    public string[] AvailableWidgetBorderStyles { get; } = [BorderThin, BorderMedium, BorderThick];
    public string[] AvailableWidgetBorderStyleDisplayNames => _cachedWidgetBorderStyleDisplayNames ??= AvailableWidgetBorderStyles.Select(GetBorderStyleDisplayName).ToArray();
    public string[] AvailableLayoutDensities { get; } =
    [
        SettingsService.LayoutDensityCompact,
        SettingsService.LayoutDensityStandard,
        SettingsService.LayoutDensityRelaxed,
        SettingsService.LayoutDensityCustom
    ];
    public string[] AvailableLayoutDensityDisplayNames =>
        _cachedLayoutDensityDisplayNames ??= AvailableLayoutDensities.Select(GetLayoutDensityDisplayName).ToArray();
    public string[] AvailableMusicDisplayModes { get; } =
    [
        SettingsService.MusicDisplayModeAuto,
        SettingsService.MusicDisplayModeCover,
        SettingsService.MusicDisplayModeControls
    ];
    public string[] AvailableMusicDisplayModeDisplayNames =>
        _cachedMusicDisplayModeDisplayNames ??= AvailableMusicDisplayModes.Select(GetMusicDisplayModeDisplayName).ToArray();
    public string[] AvailableAnimationPresets { get; } =
    [
        AnimationPresetNone,
        AnimationPresetGentle,
        AnimationPresetStandard,
        AnimationPresetEmphasized,
        AnimationPresetCustom
    ];
    public string[] AvailableAnimationPresetDisplayNames =>
        _cachedAnimationPresetDisplayNames ??= AvailableAnimationPresets.Select(GetAnimationPresetDisplayName).ToArray();
    public string[] AvailableWidgetAnimationEffects { get; } =
    [
        SettingsService.WidgetAnimationEffectSlideFade,
        SettingsService.WidgetAnimationEffectFade,
        SettingsService.WidgetAnimationEffectScaleFade,
        SettingsService.WidgetAnimationEffectZoom,
        SettingsService.WidgetAnimationEffectNone
    ];
    public string[] AvailableWidgetAnimationEffectDisplayNames => _cachedWidgetAnimationEffectDisplayNames ??= AvailableWidgetAnimationEffects.Select(GetWidgetAnimationEffectDisplayName).ToArray();
    public string[] AvailableWidgetAnimationSpeeds { get; } =
    [
        SettingsService.WidgetAnimationSpeedVeryFast,
        SettingsService.WidgetAnimationSpeedFast,
        SettingsService.WidgetAnimationSpeedStandard,
        SettingsService.WidgetAnimationSpeedRelaxed,
        SettingsService.WidgetAnimationSpeedSlow
    ];
    public string[] AvailableWidgetAnimationSpeedDisplayNames => _cachedWidgetAnimationSpeedDisplayNames ??= AvailableWidgetAnimationSpeeds.Select(GetWidgetAnimationSpeedDisplayName).ToArray();
    public string[] AvailableWidgetAnimationSlideDirections { get; } =
    [
        SettingsService.WidgetAnimationSlideDirectionNone,
        SettingsService.WidgetAnimationSlideDirectionLeft,
        SettingsService.WidgetAnimationSlideDirectionRight,
        SettingsService.WidgetAnimationSlideDirectionUp,
        SettingsService.WidgetAnimationSlideDirectionDown
    ];
    public string[] AvailableWidgetAnimationSlideDirectionDisplayNames => _cachedWidgetAnimationSlideDirectionDisplayNames ??= AvailableWidgetAnimationSlideDirections.Select(GetWidgetAnimationSlideDirectionDisplayName).ToArray();
    public string[] AvailableWidgetAnimationEasingIntensities { get; } =
    [
        SettingsService.WidgetAnimationEasingNone,
        SettingsService.WidgetAnimationEasingLight,
        SettingsService.WidgetAnimationEasingStandard,
        SettingsService.WidgetAnimationEasingStrong
    ];
    public string[] AvailableWidgetAnimationEasingIntensityDisplayNames => _cachedWidgetAnimationEasingIntensityDisplayNames ??= AvailableWidgetAnimationEasingIntensities.Select(GetWidgetAnimationEasingIntensityDisplayName).ToArray();

    public string[] AvailableDisplayWidgetChromeModes { get; } =
    [
        SettingsService.WidgetChromeModeOverlay,
        SettingsService.WidgetChromeModeHidden
    ];

    public string[] AvailableInteractiveWidgetChromeModes { get; } =
    [
        SettingsService.WidgetChromeModeStandard,
        SettingsService.WidgetChromeModeCompact,
        SettingsService.WidgetChromeModeOverlay,
        SettingsService.WidgetChromeModeHidden
    ];

    public string[] AvailableDisplayWidgetChromeModeDisplayNames => _cachedDisplayWidgetChromeModeDisplayNames ??= AvailableDisplayWidgetChromeModes.Select(GetWidgetChromeModeDisplayName).ToArray();
    public string[] AvailableInteractiveWidgetChromeModeDisplayNames => _cachedInteractiveWidgetChromeModeDisplayNames ??= AvailableInteractiveWidgetChromeModes.Select(GetWidgetChromeModeDisplayName).ToArray();

    public string[] AvailableWidgetTitleIconModes { get; } =
    [
        SettingsService.WidgetTitleIconModeFilledMono,
        SettingsService.WidgetTitleIconModeLineMono,
        SettingsService.WidgetTitleIconModeColor,
        SettingsService.WidgetTitleIconModeHidden,
        SettingsService.WidgetTitleIconModeTextLabel
    ];

    public string[] AvailableWidgetTitleIconModeDisplayNames => _cachedWidgetTitleIconModeDisplayNames ??= AvailableWidgetTitleIconModes.Select(GetWidgetTitleIconModeDisplayName).ToArray();

    public string[] AvailableWidgetLayerModes { get; } =
    [
        SettingsService.WidgetLayerModeDynamic,
        SettingsService.WidgetLayerModeDesktopPinned
    ];

    public string[] AvailableWidgetLayerModeDisplayNames => _cachedWidgetLayerModeDisplayNames ??= AvailableWidgetLayerModes.Select(GetWidgetLayerModeDisplayName).ToArray();

    public string[] AvailableQuickCaptureDefaultViews { get; } =
    [
        SettingsService.QuickCaptureDefaultViewRecords,
        SettingsService.QuickCaptureDefaultViewPinned,
        SettingsService.QuickCaptureDefaultViewRecent
    ];

    public string[] AvailableQuickCaptureDefaultViewDisplayNames => _cachedQuickCaptureDefaultViewDisplayNames ??= AvailableQuickCaptureDefaultViews.Select(GetQuickCaptureDefaultViewDisplayName).ToArray();

    public string[] AvailableWidgetTabStyles { get; } =
    [
        SettingsService.WidgetTabStylePivot,
        SettingsService.WidgetTabStyleButton
    ];

    public string[] AvailableQuickCaptureTabStyleDisplayNames => _cachedQuickCaptureTabStyleDisplayNames ??= AvailableWidgetTabStyles.Select(GetWidgetTabStyleDisplayName).ToArray();

    public string[] AvailableTodoNewTaskPositions { get; } =
    [
        SettingsService.TodoNewTaskPositionTop,
        SettingsService.TodoNewTaskPositionBottom
    ];

    public string[] AvailableTodoNewTaskPositionDisplayNames => _cachedTodoNewTaskPositionDisplayNames ??= AvailableTodoNewTaskPositions.Select(GetTodoNewTaskPositionDisplayName).ToArray();

    public string[] AvailableAttachmentStorageModes { get; } =
    [
        SettingsService.AttachmentStorageModeLink,
        SettingsService.AttachmentStorageModeCopy
    ];

    public string[] AvailableAttachmentStorageModeDisplayNames =>
        _cachedAttachmentStorageModeDisplayNames ??=
            AvailableAttachmentStorageModes.Select(GetAttachmentStorageModeDisplayName).ToArray();

    public string GetAttachmentStorageModeDisplayName(string storageMode)
    {
        return SettingsService.NormalizeAttachmentStorageMode(storageMode) == SettingsService.AttachmentStorageModeCopy
            ? _localizationService.T("Settings.AttachmentStorageMode.Copy")
            : _localizationService.T("Settings.AttachmentStorageMode.Link");
    }

    public string[] AvailableTodoDefaultFilters { get; } =
    [
        SettingsService.TodoDefaultFilterAll,
        SettingsService.TodoDefaultFilterToday,
        SettingsService.TodoDefaultFilterImportant,
        SettingsService.TodoDefaultFilterCompleted
    ];

    public string[] AvailableTodoDefaultFilterDisplayNames => _cachedTodoDefaultFilterDisplayNames ??= AvailableTodoDefaultFilters.Select(GetTodoDefaultFilterDisplayName).ToArray();

    public string[] AvailableTodoTabStyleDisplayNames => _cachedTodoTabStyleDisplayNames ??= AvailableWidgetTabStyles.Select(GetWidgetTabStyleDisplayName).ToArray();

    public int[] AvailableTodoReminderOffsetMinutes { get; } =
    [
        0,
        5,
        10,
        15,
        30,
        60,
        1440
    ];

    public string[] AvailableTodoReminderOffsetDisplayNames => _cachedTodoReminderOffsetDisplayNames ??= AvailableTodoReminderOffsetMinutes.Select(GetTodoReminderOffsetDisplayName).ToArray();

// ─── Weather Settings Properties ──────────────────────────────

public string[] AvailableWeatherTemperatureUnits { get; } =
[
    SettingsService.WeatherTemperatureUnitCelsius,
    SettingsService.WeatherTemperatureUnitFahrenheit
];

public string[] AvailableWeatherTemperatureUnitDisplayNames =>
    _cachedWeatherTempUnitDisplayNames ??= AvailableWeatherTemperatureUnits.Select(GetWeatherTempUnitDisplayName).ToArray();

public string SelectedWeatherTemperatureUnit
{
    get => _selectedWeatherTemperatureUnit;
    set
    {
        string normalized = value == SettingsService.WeatherTemperatureUnitFahrenheit
            ? SettingsService.WeatherTemperatureUnitFahrenheit
            : SettingsService.WeatherTemperatureUnitCelsius;
        if (!SetProperty(ref _selectedWeatherTemperatureUnit, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherTemperatureUnit = _selectedWeatherTemperatureUnit;
        _settingsService.SaveDebounced();
    }
}

public int SelectedWeatherTemperatureUnitIndex => Array.IndexOf(AvailableWeatherTemperatureUnits, _selectedWeatherTemperatureUnit);

public string[] AvailableWeatherWindSpeedUnits { get; } =
[
    SettingsService.WeatherWindSpeedUnitKmh,
    SettingsService.WeatherWindSpeedUnitMs,
    SettingsService.WeatherWindSpeedUnitMph
];

public string[] AvailableWeatherWindSpeedUnitDisplayNames =>
    _cachedWeatherWindUnitDisplayNames ??= AvailableWeatherWindSpeedUnits.Select(GetWeatherWindUnitDisplayName).ToArray();

public string SelectedWeatherWindSpeedUnit
{
    get => _selectedWeatherWindSpeedUnit;
    set
    {
        string normalized = value is SettingsService.WeatherWindSpeedUnitMs or SettingsService.WeatherWindSpeedUnitMph
            ? value
            : SettingsService.WeatherWindSpeedUnitKmh;
        if (!SetProperty(ref _selectedWeatherWindSpeedUnit, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherWindSpeedUnit = _selectedWeatherWindSpeedUnit;
        _settingsService.SaveDebounced();
    }
}

public int SelectedWeatherWindSpeedUnitIndex => Array.IndexOf(AvailableWeatherWindSpeedUnits, _selectedWeatherWindSpeedUnit);

public string[] AvailableWeatherDefaultViews { get; } =
[
    SettingsService.WeatherDefaultViewToday,
    SettingsService.WeatherDefaultViewWeek
];

public string[] AvailableWeatherDefaultViewDisplayNames =>
    _cachedWeatherDefaultViewDisplayNames ??= AvailableWeatherDefaultViews.Select(GetWeatherDefaultViewDisplayName).ToArray();

public string SelectedWeatherDefaultView
{
    get => _selectedWeatherDefaultView;
    set
    {
        string normalized = value == SettingsService.WeatherDefaultViewWeek
            ? SettingsService.WeatherDefaultViewWeek
            : SettingsService.WeatherDefaultViewToday;
        if (!SetProperty(ref _selectedWeatherDefaultView, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherDefaultView = _selectedWeatherDefaultView;
        _settingsService.SaveDebounced();
    }
}

public int SelectedWeatherDefaultViewIndex => Array.IndexOf(AvailableWeatherDefaultViews, _selectedWeatherDefaultView);

public string[] AvailableWeatherSkins { get; } =
[
    SettingsService.WeatherSkinStandard,
    SettingsService.WeatherSkinRich
];

public string[] AvailableWeatherSkinDisplayNames =>
    _cachedWeatherSkinDisplayNames ??= AvailableWeatherSkins.Select(GetWeatherSkinDisplayName).ToArray();

public string SelectedWeatherSkin
{
    get => _selectedWeatherSkin;
    set
    {
        string normalized = value == SettingsService.WeatherSkinRich
            ? SettingsService.WeatherSkinRich
            : SettingsService.WeatherSkinStandard;
        if (!SetProperty(ref _selectedWeatherSkin, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherSkin = _selectedWeatherSkin;
        _settingsService.SaveDebounced();
    }
}

public int SelectedWeatherSkinIndex => Array.IndexOf(AvailableWeatherSkins, _selectedWeatherSkin);

public int[] AvailableWeatherRefreshIntervals { get; } = [15, 30, 60, 180];

public string[] AvailableWeatherRefreshIntervalDisplayNames =>
    _cachedWeatherRefreshIntervalDisplayNames ??= AvailableWeatherRefreshIntervals.Select(GetWeatherRefreshIntervalDisplayName).ToArray();

public int SelectedWeatherRefreshInterval
{
    get => _selectedWeatherRefreshInterval;
    set
    {
        int clamped = Math.Clamp(value, SettingsService.WeatherRefreshMinMinutes, SettingsService.WeatherRefreshMaxMinutes);
        if (!SetProperty(ref _selectedWeatherRefreshInterval, clamped))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherRefreshIntervalMinutes = _selectedWeatherRefreshInterval;
        _settingsService.SaveDebounced();
    }
}

public int SelectedWeatherRefreshIntervalIndex => Array.IndexOf(AvailableWeatherRefreshIntervals, _selectedWeatherRefreshInterval);

private string GetWeatherTempUnitDisplayName(string unit) => unit switch
{
    SettingsService.WeatherTemperatureUnitFahrenheit => _localizationService.T("Weather.Unit.Fahrenheit"),
    _ => _localizationService.T("Weather.Unit.Celsius")
};

private string GetWeatherWindUnitDisplayName(string unit) => unit switch
{
    SettingsService.WeatherWindSpeedUnitMs => "m/s",
    SettingsService.WeatherWindSpeedUnitMph => "mph",
    _ => "km/h"
};

private string GetWeatherDefaultViewDisplayName(string view) => view switch
{
    SettingsService.WeatherDefaultViewWeek => _localizationService.T("Weather.View.Week"),
    _ => _localizationService.T("Weather.View.Today")
};

private string GetWeatherSkinDisplayName(string skin) => skin switch
{
    SettingsService.WeatherSkinRich => _localizationService.T("Weather.Skin.Rich"),
    _ => _localizationService.T("Weather.Skin.Standard")
};

private string GetWeatherRefreshIntervalDisplayName(int minutes) => minutes switch
{
    15 => _localizationService.Format("Weather.Refresh.Minute", minutes),
    30 => _localizationService.Format("Weather.Refresh.Minute", minutes),
    60 => _localizationService.T("Weather.Refresh.Hour"),
    180 => _localizationService.Format("Weather.Refresh.Hours", 3),
    _ => $"{minutes} min"
};

partial void OnWeatherAutoLocationChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherAutoLocation = value;
    _settingsService.SaveDebounced();
    OnPropertyChanged(nameof(WeatherCityNameVisibility));
}

public Visibility WeatherCityNameVisibility => WeatherAutoLocation ? Visibility.Collapsed : Visibility.Visible;

partial void OnWeatherCityNameChanged(string value)
{
    // Don't save on text change — only save when a city is selected from suggestions.
    // See WeatherCitySuggestions / SelectWeatherCity.
}

// ─── Weather city search (AutoSuggestBox) ───

private string _weatherCitySearchText = string.Empty;
private bool _isWeatherCitySearchUpdating;

public string WeatherCitySearchText
{
    get => _weatherCitySearchText;
    set => SetProperty(ref _weatherCitySearchText, value);
}

/// <summary>
/// Suggestions shown in the AutoSuggestBox dropdown.
/// Populated with nearby popular cities when empty, or search results when typing.
/// </summary>
public ObservableCollection<WeatherCitySearchResult> WeatherCitySuggestions { get; } = [];

private bool _isWeatherCitySearching;
public bool IsWeatherCitySearching
{
    get => _isWeatherCitySearching;
    private set => SetProperty(ref _isWeatherCitySearching, value);
}

/// <summary>
/// True when a search was performed but returned no results.
/// </summary>
private bool _hasNoCitySearchResults;
public bool HasNoCitySearchResults
{
    get => _hasNoCitySearchResults;
    private set
    {
        if (SetProperty(ref _hasNoCitySearchResults, value))
        {
            OnPropertyChanged(nameof(HasNoCitySearchResultsVisibility));
        }
    }
}

public Visibility HasNoCitySearchResultsVisibility => HasNoCitySearchResults
    ? Visibility.Visible
    : Visibility.Collapsed;

public string WeatherCitySearchPlaceholder => _localizationService.T("Weather.CitySearch.Placeholder");
public string WeatherCityNoResultsText => _localizationService.T("Weather.CitySearch.NoResults");

private CancellationTokenSource? _citySearchCts;
private CitySearchService? _citySearchService;
private double? _cachedLocationLat;
private double? _cachedLocationLon;
private bool _locationInitialized;

/// <summary>
/// Called from the AutoSuggestBox TextChanged event (code-behind).
/// Populates suggestions with nearby popular cities when empty,
/// or search results when the user types.
/// </summary>
public async Task UpdateWeatherCitySuggestionsAsync(string query)
{
    if (_isWeatherCitySearchUpdating)
    {
        return;
    }

    // Cancel any pending search
    _citySearchCts?.Cancel();
    _citySearchCts = new CancellationTokenSource();
    var ct = _citySearchCts.Token;

    // Empty query → show nearby popular cities
    if (string.IsNullOrWhiteSpace(query))
    {
        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;

        await PopulateNearbyPopularCitiesAsync(ct);
        return;
    }

    // Non-empty but too short → clear and wait
    if (query.Length < 2)
    {
        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;
        return;
    }

    IsWeatherCitySearching = true;
    try
    {
        await Task.Delay(300, ct);

        // Guard: if a city was selected while we were waiting, abort.
        if (_isWeatherCitySearchUpdating || ct.IsCancellationRequested)
        {
            return;
        }

        _citySearchService ??= new CitySearchService();
        var language = _localizationService.IsEnglish ? "en" : "zh";
        var results = await _citySearchService.SearchAsync(query, language, ct);

        if (ct.IsCancellationRequested || _isWeatherCitySearchUpdating)
        {
            return;
        }

        WeatherCitySuggestions.Clear();
        foreach (var r in results)
        {
            WeatherCitySuggestions.Add(r);
        }
        HasNoCitySearchResults = WeatherCitySuggestions.Count == 0;
    }
    catch (OperationCanceledException)
    {
        // Expected when a newer search supersedes this one.
    }
    catch (Exception ex)
    {
        App.Log($"[SettingsViewModel] City search failed: {ex.Message}");
    }
    finally
    {
        IsWeatherCitySearching = false;
    }
}

/// <summary>
/// Populates the suggestions with nearby popular cities based on user location.
/// Falls back to global popular cities if location is unavailable.
/// </summary>
private async Task PopulateNearbyPopularCitiesAsync(CancellationToken cancellationToken = default)
{
    cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
    try
    {
        _citySearchService ??= new CitySearchService();
        var language = _localizationService.IsEnglish ? "en" : "zh";

        // Try to get user location (cached after first call)
        if (!_locationInitialized)
        {
            var (lat, lon, _) = await WindowsLocationHelper.GetLocationAsync(_localizationService);
            cancellationToken.ThrowIfCancellationRequested();
            _cachedLocationLat = lat;
            _cachedLocationLon = lon;
            _locationInitialized = true;
        }

        var cities = _citySearchService.GetNearbyPopularCities(
            _cachedLocationLat, _cachedLocationLon, language, maxCount: 8);
        cancellationToken.ThrowIfCancellationRequested();

        WeatherCitySuggestions.Clear();
        foreach (var c in cities)
        {
            WeatherCitySuggestions.Add(c);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        App.Log($"[SettingsViewModel] Failed to populate nearby cities: {ex.Message}");
    }
}

/// <summary>
/// Called when language changes to refresh the popular cities list.
/// Clears cached suggestions so they get repopulated on next focus.
/// </summary>
public void RefreshWeatherCityPopularCities()
{
    _citySearchCts?.Cancel();
    WeatherCitySuggestions.Clear();
    HasNoCitySearchResults = false;
}

public void SelectWeatherCity(WeatherCitySearchResult result)
{
    if (result is null)
    {
        return;
    }

    // Cancel any pending search so it can't overwrite our state.
    _citySearchCts?.Cancel();

    _isWeatherCitySearchUpdating = true;
    try
    {
        _settingsService.Settings.WeatherCityName = result.DisplayName;
        _settingsService.Settings.WeatherLatitude = result.Latitude;
        _settingsService.Settings.WeatherLongitude = result.Longitude;
        _settingsService.SaveDebounced();

        WeatherCityName = result.DisplayName;
        _weatherCitySearchText = result.DisplayName;
        OnPropertyChanged(nameof(WeatherCitySearchText));

        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;
    }
    finally
    {
        _isWeatherCitySearchUpdating = false;
    }
}

public void ClearWeatherCitySuggestions()
{
    _citySearchCts?.Cancel();
    WeatherCitySuggestions.Clear();
    HasNoCitySearchResults = false;
}

public void RestoreWeatherCitySearchText()
{
    _isWeatherCitySearchUpdating = true;
    try
    {
        _weatherCitySearchText = _settingsService.Settings.WeatherCityName;
        OnPropertyChanged(nameof(WeatherCitySearchText));
    }
    finally
    {
        _isWeatherCitySearchUpdating = false;
    }
}

partial void OnWeatherShowForecastChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowForecast = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowSunriseChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowSunrise = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowUvIndexChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowUvIndex = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowPrecipitationChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowPrecipitation = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowHumidityChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowHumidity = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowWindChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowWind = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowPressureChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowPressure = value;
    _settingsService.SaveDebounced();
}

    public GlobalHotkeyGesture GetCurrentGlobalHotkeyGesture()
    {
        var settings = _settingsService.Settings;
        return GlobalHotkeyService.NormalizeGesture(settings.GlobalHotkeyModifiers, settings.GlobalHotkeyKey);
    }

    public void RefreshGlobalHotkeyState()
    {
        var settings = _settingsService.Settings;
        _globalHotkeyEnabled = settings.GlobalHotkeyEnabled;
        GlobalHotkeyText = GlobalHotkeyService.FormatGesture(GetCurrentGlobalHotkeyGesture(), _localizationService);
        RefreshGlobalHotkeyStatus();
        OnPropertyChanged(nameof(GlobalHotkeyEnabled));
        OnPropertyChanged(nameof(CanShowGlobalHotkeyWarning));
        OnPropertyChanged(nameof(GlobalHotkeyDescription));
        OnPropertyChanged(nameof(GlobalHotkeyText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusKind));
    }

    public void RefreshGlobalHotkeyStatus()
    {
        if (!GlobalHotkeyEnabled)
        {
            GlobalHotkeyStatusKind = "Muted";
            GlobalHotkeyStatusText = _localizationService.T("Settings.GlobalHotkey.Status.Disabled");
            return;
        }

        if (App.Current?.GlobalHotkeyService is not { } hotkeyService)
        {
            GlobalHotkeyStatusKind = "Warning";
            GlobalHotkeyStatusText = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            return;
        }

        if (hotkeyService.IsRegistered)
        {
            GlobalHotkeyStatusKind = GlobalHotkeyService.IsRiskyGesture(hotkeyService.CurrentGesture) ? "Warning" : "Normal";
            GlobalHotkeyStatusText = _localizationService.Format(
                "Settings.GlobalHotkey.Status.Active",
                hotkeyService.CurrentGestureText);
            return;
        }

        GlobalHotkeyStatusKind = "Warning";
        GlobalHotkeyStatusText = string.IsNullOrWhiteSpace(hotkeyService.LastError)
            ? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered")
            : hotkeyService.LastError;
    }

    public void RefreshQuickAccessState()
    {
        ManagedStorageQuickAccessPinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(ManagedStorageRootPath, out _);
    }

    public async Task RefreshQuickAccessStateAsync(bool showBusy = false, CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        string path = ManagedStorageRootPath;
        if (showBusy)
        {
            IsQuickAccessBusy = true;
        }

        try
        {
            QuickAccessStateResult result = await ExplorerQuickAccessHelper
                .GetQuickAccessPinStateAsync(path)
                .WaitAsync(cancellationToken);
            if (!_isDisposed && string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = result.State;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to refresh Quick Access state: {ex}");
            if (!_isDisposed && string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = QuickAccessPinState.Unknown;
            }
        }
        finally
        {
            if (showBusy && !_isDisposed)
            {
                IsQuickAccessBusy = false;
            }
        }
    }

    public void SetQuickAccessBusy(bool isBusy)
    {
        IsQuickAccessBusy = isBusy;
    }

    public void SetQuickAccessPinState(QuickAccessPinState state)
    {
        ManagedStorageQuickAccessPinState = state;
    }

    public SettingsViewModel(
        SettingsService settingsService,
        ThemeService themeService,
        LocalizationService? localizationService = null,
        IAppUpdateService? appUpdateService = null)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _widgetContentFactory = new WidgetContentFactory(_localizationService);
        _appUpdateService = appUpdateService ?? new AppUpdateService();
        _quickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheLoading");
        _quickCaptureClipboardDiagnosticsText = _localizationService.T("Settings.QuickCapture.ClipboardDiagnosticsUnavailable");
        _dragDropPermissionRepairStatusText = string.Empty;
        _updateStatusText = _localizationService.T("Settings.Update.Status.Ready");
        _updateDetailText = GetReadyUpdateDetailText();

        var settings = settingsService.Settings;
        _selectedTheme = settings.Theme is ThemeLight or ThemeDark ? settings.Theme : ThemeSystem;
        _selectedTrayIconStyle = settings.TrayIconStyle is TrayIconStyleColorful or TrayIconStyleBlack or TrayIconStyleWhite
            ? settings.TrayIconStyle
            : TrayIconStyleSystem;
        _selectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);

        _useSystemAccentColor = !string.Equals(settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        _autoStart = StartupService.IsEnabled();
        _autoCheckForUpdates = settings.AutoCheckForUpdates;
        _doubleClickToOpen = settings.DoubleClickToOpen;
        _defaultWidth = settings.DefaultWidgetWidth;
        _defaultHeight = settings.DefaultWidgetHeight;
        _hideShortcutArrowOverlay = settings.HideShortcutArrowOverlay;
        _showImageFilesAsIcons = settings.ShowImageFilesAsIcons;
        _showHoverButtons = settings.ShowHoverButtons;
        _resizeSnapEnabled = settings.ResizeSnapEnabled;
        ApplyHoverButtonActionSelection(settings.WidgetHoverButtonActions);
        _showListItemDetails = settings.ShowListItemDetails;
        _showFileItemPathTooltips = settings.ShowFileItemPathTooltips;
        _widgetOpacity = settings.WidgetOpacity;
        _widgetMaterialIntensity = settings.WidgetMaterialIntensity;
        _selectedWidgetCornerPreference = settings.WidgetCornerPreference is CornerDefault or CornerSquare or CornerSmall or CornerRound
            ? settings.WidgetCornerPreference
            : CornerSmall;
        _selectedWidgetMaterialType = settings.WidgetMaterialType is
            MaterialMica or MaterialMicaAlt or MaterialAcrylic or MaterialAcrylicBase or MaterialSolid
            ? settings.WidgetMaterialType
            : MaterialAcrylic;
        _selectedWidgetBorderColorMode = settings.WidgetBorderColorMode is
            BorderColorNeutral or BorderColorAccent or BorderColorNone
                ? settings.WidgetBorderColorMode
                : BorderColorNeutral;
        _selectedWidgetBorderStyle = settings.WidgetBorderStyle is BorderThin or BorderMedium or BorderThick
            ? settings.WidgetBorderStyle
            : BorderThin;
        _selectedWidgetAnimationEffect = NormalizeWidgetAnimationEffect(settings.WidgetAnimationEffect);
        _selectedWidgetAnimationSpeed = NormalizeWidgetAnimationSpeed(settings.WidgetAnimationSpeed);
        _selectedWidgetAnimationSlideDirection = NormalizeWidgetAnimationSlideDirection(settings.WidgetAnimationSlideDirection);
        _selectedWidgetAnimationEasingIntensity = NormalizeWidgetAnimationEasingIntensity(settings.WidgetAnimationEasingIntensity);
        _selectedAnimationPreset = ResolveAnimationPreset();
        _selectedDisplayWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.DisplayWidgetChromeMode, WidgetChromeMode.Overlay);
        _selectedInteractiveWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.InteractiveWidgetChromeMode, WidgetChromeMode.Standard);
        _selectedWidgetTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
        _selectedWidgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);
        _iconSize = settings.IconSize;
        _textSize = settings.TextSize;
        _layoutDensityScale = settings.LayoutDensityScale;
        _horizontalSpacingScale = settings.HorizontalSpacingScale;
        _verticalSpacingScale = settings.VerticalSpacingScale;
        _fileNameWidthScale = settings.FileNameWidthScale;
        _selectedLayoutDensity = SettingsService.ResolveLayoutDensityPreset(settings);
        _showFileExtensions = settings.ShowFileExtensions;
        _hideShortcutExtensionWhenShowingFileExtensions = settings.HideShortcutExtensionWhenShowingFileExtensions;
        _quickCaptureEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture);
        _quickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
        _quickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
        _quickCaptureRecentLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        _quickCaptureShowCreatedTime = settings.QuickCaptureShowCreatedTime;
        _selectedAttachmentStorageMode = SettingsService.NormalizeAttachmentStorageMode(settings.AttachmentStorageMode);
        _selectedQuickCaptureDefaultView = NormalizeQuickCaptureDefaultView(settings.QuickCaptureDefaultView);
        _todoEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
        _todoShowCompletedTasks = settings.TodoShowCompletedTasks;
        _todoShowFooterStats = settings.TodoShowFooterStats;
        _todoShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        _todoConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;
        _todoReminderEnabled = settings.TodoReminderEnabled;
        _musicUseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        _musicEnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        _selectedMusicDisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.MusicDisplayMode);
_weatherAutoLocation = settings.WeatherAutoLocation;
_weatherCityName = settings.WeatherCityName;
_weatherCitySearchText = settings.WeatherCityName;
_selectedWeatherTemperatureUnit = settings.WeatherTemperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit
    ? SettingsService.WeatherTemperatureUnitFahrenheit
    : SettingsService.WeatherTemperatureUnitCelsius;
_selectedWeatherWindSpeedUnit = settings.WeatherWindSpeedUnit is SettingsService.WeatherWindSpeedUnitMs or SettingsService.WeatherWindSpeedUnitMph
    ? settings.WeatherWindSpeedUnit
    : SettingsService.WeatherWindSpeedUnitKmh;
_selectedWeatherDefaultView = settings.WeatherDefaultView == SettingsService.WeatherDefaultViewWeek
    ? SettingsService.WeatherDefaultViewWeek
    : SettingsService.WeatherDefaultViewToday;
_selectedWeatherSkin = settings.WeatherSkin == SettingsService.WeatherSkinRich
    ? SettingsService.WeatherSkinRich
    : SettingsService.WeatherSkinStandard;
_weatherShowForecast = settings.WeatherShowForecast;
_weatherShowSunrise = settings.WeatherShowSunrise;
_weatherShowUvIndex = settings.WeatherShowUvIndex;
_weatherShowPrecipitation = settings.WeatherShowPrecipitation;
_weatherShowHumidity = settings.WeatherShowHumidity;
_weatherShowWind = settings.WeatherShowWind;
_weatherShowPressure = settings.WeatherShowPressure;
_selectedWeatherRefreshInterval = Math.Clamp(
    settings.WeatherRefreshIntervalMinutes,
    SettingsService.WeatherRefreshMinMinutes,
    SettingsService.WeatherRefreshMaxMinutes);
        _selectedTodoNewTaskPosition = NormalizeTodoNewTaskPosition(settings.TodoNewTaskPosition);
        _selectedTodoDefaultFilter = NormalizeTodoDefaultFilter(settings.TodoDefaultFilter);
        _selectedTodoReminderOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(settings.TodoDefaultReminderOffsetMinutes);
        _managedStorageRootPath = settings.DefaultManagedStorageRootPath;

        ApplyCachedUpdateResult();
        RefreshAccentPreview();
        RefreshDragDropPermissionDiagnostic();
_ = PopulateNearbyPopularCitiesAsync();
_ = RefreshQuickAccessStateAsync();
        _settingsService.SettingsChanged += OnSettingsChanged;
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        if (App.Current?.QuickCaptureClipboardService is { } clipboardService)
        {
            clipboardService.DiagnosticsChanged += OnQuickCaptureClipboardDiagnosticsChanged;
            RefreshQuickCaptureClipboardDiagnostics();
        }
    }

    public Color GetCurrentAccentColor() => _currentAccentColor;

    public bool SuppressAppearanceNotifications { get; set; }
    public bool DeferAppearancePersistence { get; set; }

    public void CommitAppearanceChanges()
    {
        _settingsService.NotifyAppearancePreviewNow();
        // The preview notification has already updated every widget with the
        // final slider value. Persist without broadcasting the same appearance
        // pass a second time through SettingsChanged.
        _settingsService.SaveDebounced(notifySubscribers: false);
        App.ScheduleLightMemoryCleanup();
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
        _ = RefreshQuickAccessStateAsync(showBusy: true);
    }

    public async Task RestoreDefaultPreferencesAsync()
    {
        _isRestoringDefaults = true;
        _isApplyingSettingsSnapshot = true;
        SuppressAppearanceNotifications = true;
        DeferAppearancePersistence = false;

        try
        {
            SettingsService.ApplyDefaultPreferences(_settingsService.Settings);
            IconHelper.ClearAllThumbnailCaches();
            SelectedTheme = ThemeSystem;
            SelectedTrayIconStyle = TrayIconStyleColorful;
            UseSystemAccentColor = true;
            DefaultWidth = SettingsService.DefaultWidgetWidth;
            DefaultHeight = SettingsService.DefaultWidgetHeight;
            SelectedWidgetCornerPreference = CornerRound;
            SelectedWidgetMaterialType = MaterialMica;
            WidgetMaterialIntensity = SettingsService.DefaultWidgetMaterialIntensity;
            SelectedWidgetBorderColorMode = BorderColorNeutral;
            SelectedWidgetBorderStyle = BorderThin;
            SelectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            SelectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            SelectedWidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
            SelectedWidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
            SelectedAnimationPreset = AnimationPresetStandard;
            SelectedDisplayWidgetChromeMode = SettingsService.WidgetChromeModeOverlay;
            SelectedInteractiveWidgetChromeMode = SettingsService.WidgetChromeModeStandard;
            SelectedWidgetTitleIconMode = SettingsService.WidgetTitleIconModeColor;
            SelectedWidgetLayerMode = SettingsService.WidgetLayerModeDynamic;
            WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            IconSize = SettingsService.DefaultIconSize;
            TextSize = SettingsService.DefaultTextSize;
            LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            SelectedLayoutDensity = SettingsService.LayoutDensityStandard;
            ShowFileExtensions = false;
            ShowImageFilesAsIcons = false;
            HideShortcutExtensionWhenShowingFileExtensions = true;
            ShowHoverButtons = true;
            ApplyHoverButtonActionSelection(SettingsService.DefaultWidgetHoverButtonActions);
            AutoCheckForUpdates = true;
            QuickCaptureClipboardEnabled = false;
            QuickCaptureImageClipboardEnabled = false;
            QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
            QuickCaptureShowCreatedTime = true;
            SelectedAttachmentStorageMode = SettingsService.AttachmentStorageModeLink;
            SelectedQuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
            SelectedQuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
            TodoShowCompletedTasks = true;
            TodoShowFooterStats = false;
            TodoShowClearCompletedButton = true;
            TodoConfirmBeforeDelete = false;
            TodoReminderEnabled = true;
            SelectedTodoReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
            MusicUseArtworkBackdrop = true;
            MusicEnableCoverHoverMotion = true;
            SelectedMusicDisplayMode = SettingsService.MusicDisplayModeAuto;
WeatherAutoLocation = true;
WeatherCityName = string.Empty;
SelectedWeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
SelectedWeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
SelectedWeatherDefaultView = SettingsService.WeatherDefaultViewToday;
SelectedWeatherSkin = SettingsService.WeatherSkinStandard;
WeatherShowForecast = true;
WeatherShowSunrise = true;
WeatherShowUvIndex = true;
WeatherShowPrecipitation = true;
WeatherShowHumidity = true;
WeatherShowWind = true;
WeatherShowPressure = false;
SelectedWeatherRefreshInterval = 60;
            SelectedTodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
            SelectedTodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
            SelectedTodoTabStyle = SettingsService.WidgetTabStyleButton;
            DoubleClickToOpen = true;
            ResizeSnapEnabled = true;
            GlobalHotkeyEnabled = SettingsService.DefaultGlobalHotkeyEnabled;
            HideShortcutArrowOverlay = true;
            ShowListItemDetails = false;
            ShowFileItemPathTooltips = true;

            ResetWidgetChromeOverrides(_settingsService.Settings, _widgetContentFactory, WidgetChromeCategory.Display);
            ResetWidgetChromeOverrides(_settingsService.Settings, _widgetContentFactory, WidgetChromeCategory.Interactive);
            App.Current?.ResizeGuideOverlay.IsSnapEnabled = true;

            RefreshAccentPreview();
            RefreshNumberInputs();
            RefreshSelectionProperties();
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
            App.Current?.GlobalHotkeyService?.RefreshRegistration();
            App.Current?.UpdateTrayIcon();
            RefreshGlobalHotkeyState();
            RefreshLocalizedProperties();
            _themeService.RefreshAppearance();
            await _settingsService.SaveAsync();
            App.Current?.QuickCaptureClipboardService?.Refresh();
            _settingsService.NotifyAppearancePreviewNow();
        }
        finally
        {
            SuppressAppearanceNotifications = false;
            _isApplyingSettingsSnapshot = false;
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

    public string GetTrayIconStyleDisplayName(string style)
    {
        return style switch
        {
            TrayIconStyleColorful => _localizationService.T("Settings.TrayIcon.Colorful"),
            TrayIconStyleBlack => _localizationService.T("Settings.TrayIcon.Black"),
            TrayIconStyleWhite => _localizationService.T("Settings.TrayIcon.White"),
            _ => _localizationService.T("Settings.TrayIcon.System")
        };
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

    public string GetMaterialTypeDisplayName(string material)
    {
        return material switch
        {
            MaterialMica => _localizationService.T("Settings.Material.Mica"),
            MaterialMicaAlt => _localizationService.T("Settings.Material.MicaAlt"),
            MaterialAcrylicBase => _localizationService.T("Settings.Material.AcrylicBase"),
            MaterialSolid => _localizationService.T("Settings.Material.Solid"),
            _ => _localizationService.T("Settings.Material.Acrylic")
        };
    }

    public string GetBorderColorModeDisplayName(string mode)
    {
        return mode switch
        {
            BorderColorAccent => _localizationService.T("Settings.BorderColor.Accent"),
            BorderColorNone => _localizationService.T("Settings.BorderColor.None"),
            _ => _localizationService.T("Settings.BorderColor.Neutral")
        };
    }

    public string GetBorderStyleDisplayName(string style)
    {
        return style switch
        {
            BorderNone => _localizationService.T("Settings.Border.None"),
            BorderMedium => _localizationService.T("Settings.Border.Medium"),
            BorderThick => _localizationService.T("Settings.Border.Thick"),
            _ => _localizationService.T("Settings.Border.Thin")
        };
    }

    public string GetLayoutDensityDisplayName(string density)
    {
        return density switch
        {
            SettingsService.LayoutDensityCompact => _localizationService.T("Settings.Density.Compact"),
            SettingsService.LayoutDensityRelaxed => _localizationService.T("Settings.Density.Relaxed"),
            SettingsService.LayoutDensityCustom => _localizationService.T("Settings.Density.Custom"),
            _ => _localizationService.T("Settings.Density.Standard")
        };
    }

    public string GetAnimationPresetDisplayName(string preset)
    {
        return preset switch
        {
            AnimationPresetNone => _localizationService.T("Settings.Animation.Preset.None"),
            AnimationPresetGentle => _localizationService.T("Settings.Animation.Preset.Gentle"),
            AnimationPresetEmphasized => _localizationService.T("Settings.Animation.Preset.Emphasized"),
            AnimationPresetCustom => _localizationService.T("Settings.Animation.Preset.Custom"),
            _ => _localizationService.T("Settings.Animation.Preset.Standard")
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
            SettingsService.WidgetAnimationEffectZoom => _localizationService.T("Settings.Animation.Effect.Zoom"),
            SettingsService.WidgetAnimationEffectSlideUpFade => _localizationService.T("Settings.Animation.Effect.SlideUpFade"),
            SettingsService.WidgetAnimationEffectSlideDownFade => _localizationService.T("Settings.Animation.Effect.SlideDownFade"),
            SettingsService.WidgetAnimationEffectSlideLeftFade => _localizationService.T("Settings.Animation.Effect.SlideLeftFade"),
            SettingsService.WidgetAnimationEffectSlideRightFade => _localizationService.T("Settings.Animation.Effect.SlideRightFade"),
            SettingsService.WidgetAnimationEffectScaleSlide => _localizationService.T("Settings.Animation.Effect.ScaleSlide"),
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

    public string GetWidgetAnimationSlideDirectionDisplayName(string direction)
    {
        return NormalizeWidgetAnimationSlideDirection(direction) switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => _localizationService.T("Settings.Animation.Direction.Left"),
            SettingsService.WidgetAnimationSlideDirectionRight => _localizationService.T("Settings.Animation.Direction.Right"),
            SettingsService.WidgetAnimationSlideDirectionUp => _localizationService.T("Settings.Animation.Direction.Up"),
            SettingsService.WidgetAnimationSlideDirectionDown => _localizationService.T("Settings.Animation.Direction.Down"),
            _ => _localizationService.T("Settings.Animation.Direction.None")
        };
    }

    public string GetWidgetAnimationEasingIntensityDisplayName(string intensity)
    {
        return NormalizeWidgetAnimationEasingIntensity(intensity) switch
        {
            SettingsService.WidgetAnimationEasingLight => _localizationService.T("Settings.Animation.Easing.Light"),
            SettingsService.WidgetAnimationEasingStandard => _localizationService.T("Settings.Animation.Easing.Standard"),
            SettingsService.WidgetAnimationEasingStrong => _localizationService.T("Settings.Animation.Easing.Strong"),
            _ => _localizationService.T("Settings.Animation.Easing.None")
        };
    }

    public string GetWidgetChromeModeDisplayName(string mode)
    {
        return NormalizeWidgetChromeModeSetting(mode, WidgetChromeMode.Standard) switch
        {
            SettingsService.WidgetChromeModeCompact => _localizationService.T("Settings.WidgetChrome.Compact"),
            SettingsService.WidgetChromeModeOverlay => _localizationService.T("Settings.WidgetChrome.Overlay"),
            SettingsService.WidgetChromeModeHidden => _localizationService.T("Settings.WidgetChrome.Hidden"),
            _ => _localizationService.T("Settings.WidgetChrome.Standard")
        };
    }

    public string GetWidgetTitleIconModeDisplayName(string mode)
    {
        return NormalizeWidgetTitleIconModeSetting(mode) switch
        {
            SettingsService.WidgetTitleIconModeLineMono => _localizationService.T("Settings.WidgetTitleIcon.LineMono"),
            SettingsService.WidgetTitleIconModeColor => _localizationService.T("Settings.WidgetTitleIcon.Color"),
            SettingsService.WidgetTitleIconModeHidden => _localizationService.T("Settings.WidgetTitleIcon.Hidden"),
            SettingsService.WidgetTitleIconModeTextLabel => _localizationService.T("Settings.WidgetTitleIcon.TextLabel"),
            _ => _localizationService.T("Settings.WidgetTitleIcon.FilledMono")
        };
    }

    public string GetHoverButtonActionDisplayName(string action)
    {
        return action switch
        {
            SettingsService.WidgetHoverActionLockPosition => _localizationService.T("Settings.HoverButtonActions.LockPosition"),
            SettingsService.WidgetHoverActionLockSize => _localizationService.T("Settings.HoverButtonActions.LockSize"),
            SettingsService.WidgetHoverActionAdd => _localizationService.T("Settings.HoverButtonActions.Add"),
            SettingsService.WidgetHoverActionMore => _localizationService.T("Settings.HoverButtonActions.More"),
            SettingsService.WidgetHoverActionDelete => _localizationService.T("Settings.HoverButtonActions.Delete"),
            _ => action
        };
    }

    public string GetWidgetLayerModeDisplayName(string mode)
    {
        return SettingsService.NormalizeWidgetLayerModeSetting(mode) switch
        {
            SettingsService.WidgetLayerModeDesktopPinned => _localizationService.T("Settings.WidgetLayerMode.DesktopPinned"),
            _ => _localizationService.T("Settings.WidgetLayerMode.Dynamic")
        };
    }

    public string GetQuickCaptureDefaultViewDisplayName(string view)
    {
        return NormalizeQuickCaptureDefaultView(view) switch
        {
            SettingsService.QuickCaptureDefaultViewPinned => _localizationService.T("Settings.QuickCapture.DefaultView.Pinned"),
            SettingsService.QuickCaptureDefaultViewRecent => _localizationService.T("Settings.QuickCapture.DefaultView.Recent"),
            _ => _localizationService.T("Settings.QuickCapture.DefaultView.Records")
        };
    }

    public string GetWidgetTabStyleDisplayName(string style)
    {
        return SettingsService.NormalizeWidgetTabStyle(style) switch
        {
            SettingsService.WidgetTabStyleButton => _localizationService.T("Settings.WidgetTabStyle.Button"),
            _ => _localizationService.T("Settings.WidgetTabStyle.Pivot")
        };
    }

    public string GetTodoNewTaskPositionDisplayName(string position)
    {
        return NormalizeTodoNewTaskPosition(position) switch
        {
            SettingsService.TodoNewTaskPositionBottom => _localizationService.T("Settings.Todo.NewTaskPosition.Bottom"),
            _ => _localizationService.T("Settings.Todo.NewTaskPosition.Top")
        };
    }

    public string GetTodoDefaultFilterDisplayName(string filter)
    {
        return NormalizeTodoDefaultFilter(filter) switch
        {
            SettingsService.TodoDefaultFilterToday => _localizationService.T("Settings.Todo.DefaultFilter.Today"),
            SettingsService.TodoDefaultFilterImportant => _localizationService.T("Settings.Todo.DefaultFilter.Important"),
            SettingsService.TodoDefaultFilterCompleted => _localizationService.T("Settings.Todo.DefaultFilter.Completed"),
            _ => _localizationService.T("Settings.Todo.DefaultFilter.All")
        };
    }

    public string GetTodoReminderOffsetDisplayName(int minutes)
    {
        return SettingsService.NormalizeTodoReminderOffsetMinutes(minutes) switch
        {
            0 => _localizationService.T("Settings.Todo.ReminderOffset.AtDueTime"),
            60 => _localizationService.T("Settings.Todo.ReminderOffset.OneHour"),
            1440 => _localizationService.T("Settings.Todo.ReminderOffset.OneDay"),
            var value => _localizationService.Format("Settings.Todo.ReminderOffset.Minutes", value)
        };
    }

    public string GetMusicDisplayModeDisplayName(string mode)
    {
        return SettingsService.NormalizeMusicDisplayMode(mode) switch
        {
            SettingsService.MusicDisplayModeCover => _localizationService.T("Settings.Music.DisplayMode.Cover"),
            SettingsService.MusicDisplayModeControls => _localizationService.T("Settings.Music.DisplayMode.Controls"),
            _ => _localizationService.T("Settings.Music.DisplayMode.Auto")
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

    private void OnSettingsChanged()
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ApplySettingsSnapshot();
    }

    private void ApplySettingsSnapshot()
    {
        var settings = _settingsService.Settings;
        bool quickCaptureEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture);
        bool quickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
        bool quickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
        int quickCaptureRecentLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        bool quickCaptureShowCreatedTime = settings.QuickCaptureShowCreatedTime;
        string quickCaptureDefaultView = NormalizeQuickCaptureDefaultView(settings.QuickCaptureDefaultView);
        string quickCaptureTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
        bool todoEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
        bool todoShowCompletedTasks = settings.TodoShowCompletedTasks;
        bool todoShowFooterStats = settings.TodoShowFooterStats;
        bool todoShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        bool todoConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;
        bool todoReminderEnabled = settings.TodoReminderEnabled;
        bool musicUseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        bool musicEnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        string musicDisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.MusicDisplayMode);
        bool showImageFilesAsIcons = settings.ShowImageFilesAsIcons;
        bool showFileItemPathTooltips = settings.ShowFileItemPathTooltips;
        bool showHoverButtons = settings.ShowHoverButtons;
        string hoverButtonActions = SettingsService.NormalizeWidgetHoverButtonActions(settings.WidgetHoverButtonActions);
        bool autoCheckForUpdates = settings.AutoCheckForUpdates;
        string displayWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.DisplayWidgetChromeMode, WidgetChromeMode.Overlay);
        string interactiveWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.InteractiveWidgetChromeMode, WidgetChromeMode.Standard);
        string widgetTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
        string widgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);
        string todoNewTaskPosition = NormalizeTodoNewTaskPosition(settings.TodoNewTaskPosition);
        string todoDefaultFilter = NormalizeTodoDefaultFilter(settings.TodoDefaultFilter);
        string todoTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.TodoTabStyle);
        int todoReminderOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(settings.TodoDefaultReminderOffsetMinutes);
        string managedStorageRootPath = SettingsService.NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);

        _isApplyingSettingsSnapshot = true;
        try
        {
            if (!string.Equals(ManagedStorageRootPath, managedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageRootPath = managedStorageRootPath;
                _ = RefreshQuickAccessStateAsync();
            }

            if (QuickCaptureEnabled != quickCaptureEnabled)
            {
                QuickCaptureEnabled = quickCaptureEnabled;
            }

            if (QuickCaptureClipboardEnabled != quickCaptureClipboardEnabled)
            {
                QuickCaptureClipboardEnabled = quickCaptureClipboardEnabled;
            }

            if (QuickCaptureImageClipboardEnabled != quickCaptureImageClipboardEnabled)
            {
                QuickCaptureImageClipboardEnabled = quickCaptureImageClipboardEnabled;
            }

            if (QuickCaptureRecentLimit != quickCaptureRecentLimit)
            {
                QuickCaptureRecentLimit = quickCaptureRecentLimit;
            }

            if (QuickCaptureShowCreatedTime != quickCaptureShowCreatedTime)
            {
                QuickCaptureShowCreatedTime = quickCaptureShowCreatedTime;
            }

            if (!string.Equals(SelectedQuickCaptureDefaultView, quickCaptureDefaultView, StringComparison.Ordinal))
            {
                SelectedQuickCaptureDefaultView = quickCaptureDefaultView;
            }

            if (!string.Equals(SelectedQuickCaptureTabStyle, quickCaptureTabStyle, StringComparison.Ordinal))
            {
                SelectedQuickCaptureTabStyle = quickCaptureTabStyle;
            }

            if (TodoEnabled != todoEnabled)
            {
                TodoEnabled = todoEnabled;
            }

            if (TodoShowCompletedTasks != todoShowCompletedTasks)
            {
                TodoShowCompletedTasks = todoShowCompletedTasks;
            }

            if (TodoShowFooterStats != todoShowFooterStats)
            {
                TodoShowFooterStats = todoShowFooterStats;
            }

            if (TodoShowClearCompletedButton != todoShowClearCompletedButton)
            {
                TodoShowClearCompletedButton = todoShowClearCompletedButton;
            }

            if (TodoConfirmBeforeDelete != todoConfirmBeforeDelete)
            {
                TodoConfirmBeforeDelete = todoConfirmBeforeDelete;
            }

            if (TodoReminderEnabled != todoReminderEnabled)
            {
                TodoReminderEnabled = todoReminderEnabled;
            }

            if (MusicUseArtworkBackdrop != musicUseArtworkBackdrop)
            {
                MusicUseArtworkBackdrop = musicUseArtworkBackdrop;
            }

            if (MusicEnableCoverHoverMotion != musicEnableCoverHoverMotion)
            {
                MusicEnableCoverHoverMotion = musicEnableCoverHoverMotion;
            }

            if (!string.Equals(SelectedMusicDisplayMode, musicDisplayMode, StringComparison.Ordinal))
            {
                SelectedMusicDisplayMode = musicDisplayMode;
            }

            if (ShowImageFilesAsIcons != showImageFilesAsIcons)
            {
                ShowImageFilesAsIcons = showImageFilesAsIcons;
            }

            if (ShowFileItemPathTooltips != showFileItemPathTooltips)
            {
                ShowFileItemPathTooltips = showFileItemPathTooltips;
            }

            if (ShowHoverButtons != showHoverButtons)
            {
                ShowHoverButtons = showHoverButtons;
            }

            ApplyHoverButtonActionSelection(hoverButtonActions);

            if (AutoCheckForUpdates != autoCheckForUpdates)
            {
                AutoCheckForUpdates = autoCheckForUpdates;
            }

            if (!string.Equals(SelectedDisplayWidgetChromeMode, displayWidgetChromeMode, StringComparison.Ordinal))
            {
                SelectedDisplayWidgetChromeMode = displayWidgetChromeMode;
            }

            if (!string.Equals(SelectedInteractiveWidgetChromeMode, interactiveWidgetChromeMode, StringComparison.Ordinal))
            {
                SelectedInteractiveWidgetChromeMode = interactiveWidgetChromeMode;
            }

            if (!string.Equals(SelectedWidgetTitleIconMode, widgetTitleIconMode, StringComparison.Ordinal))
            {
                SelectedWidgetTitleIconMode = widgetTitleIconMode;
            }

            if (!string.Equals(SelectedWidgetLayerMode, widgetLayerMode, StringComparison.Ordinal))
            {
                SelectedWidgetLayerMode = widgetLayerMode;
            }

            if (!string.Equals(SelectedTodoNewTaskPosition, todoNewTaskPosition, StringComparison.Ordinal))
            {
                SelectedTodoNewTaskPosition = todoNewTaskPosition;
            }

            if (!string.Equals(SelectedTodoDefaultFilter, todoDefaultFilter, StringComparison.Ordinal))
            {
                SelectedTodoDefaultFilter = todoDefaultFilter;
            }

            if (!string.Equals(SelectedTodoTabStyle, todoTabStyle, StringComparison.Ordinal))
            {
                SelectedTodoTabStyle = todoTabStyle;
            }

            if (SelectedTodoReminderOffsetMinutes != todoReminderOffsetMinutes)
            {
                SelectedTodoReminderOffsetMinutes = todoReminderOffsetMinutes;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
        }

        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
        OnPropertyChanged(nameof(SelectedTodoTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesIndex));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeIndex));
        NotifyHoverButtonActionPropertiesChanged();
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshLocalizedProperties()
    {
        RefreshSelectionProperties();
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(DistributionChannelText));
        OnPropertyChanged(nameof(AboutDeveloperText));
        OnPropertyChanged(nameof(OfficialWebsiteDisplayText));
        OnPropertyChanged(nameof(OpenSourceRepositoryDisplayText));
        OnPropertyChanged(nameof(UpdateDownloadActionText));
        OnPropertyChanged(nameof(DonationCardVisibility));
        OnPropertyChanged(nameof(DonationWechatImageSource));
        OnPropertyChanged(nameof(DonationAlipayImageSource));
        if (!IsCheckingForUpdates && !IsDownloadingUpdate)
        {
            if (_appUpdateService.LastCheckResult is not null)
            {
                ApplyCachedUpdateResult();
            }
            else
            {
                UpdateStatusText = _localizationService.T("Settings.Update.Status.Ready");
                UpdateDetailText = GetReadyUpdateDetailText();
            }
        }
        OnPropertyChanged(nameof(QuickAccessStatusText));
        OnPropertyChanged(nameof(PinQuickAccessButtonText));
        OnPropertyChanged(nameof(PinQuickAccessToolTipText));
        OnPropertyChanged(nameof(GlobalHotkeyDescription));
        OnPropertyChanged(nameof(GlobalHotkeyText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusKind));
        OnPropertyChanged(nameof(CanShowGlobalHotkeyWarning));
        NotifyDragDropPermissionPropertiesChanged();
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
OnPropertyChanged(nameof(WeatherCitySearchPlaceholder));
OnPropertyChanged(nameof(WeatherCityNoResultsText));
RefreshWeatherCityPopularCities();
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshSelectionProperties()
    {
        _cachedThemeDisplayNames = null;
        _cachedTrayIconStyleDisplayNames = null;
        _cachedLanguageDisplayNames = null;
        _cachedWidgetCornerPreferenceDisplayNames = null;
        _cachedWidgetMaterialTypeDisplayNames = null;
        _cachedWidgetBorderColorModeDisplayNames = null;
        _cachedWidgetBorderStyleDisplayNames = null;
        _cachedLayoutDensityDisplayNames = null;
        _cachedAnimationPresetDisplayNames = null;
        _cachedWidgetAnimationEffectDisplayNames = null;
        _cachedWidgetAnimationSpeedDisplayNames = null;
        _cachedWidgetAnimationSlideDirectionDisplayNames = null;
        _cachedWidgetAnimationEasingIntensityDisplayNames = null;
        _cachedDisplayWidgetChromeModeDisplayNames = null;
        _cachedInteractiveWidgetChromeModeDisplayNames = null;
        _cachedWidgetTitleIconModeDisplayNames = null;
        _cachedWidgetLayerModeDisplayNames = null;
        _cachedQuickCaptureDefaultViewDisplayNames = null;
        _cachedQuickCaptureTabStyleDisplayNames = null;
        _cachedTodoNewTaskPositionDisplayNames = null;
        _cachedAttachmentStorageModeDisplayNames = null;
        _cachedTodoDefaultFilterDisplayNames = null;
        _cachedTodoTabStyleDisplayNames = null;
        _cachedTodoReminderOffsetDisplayNames = null;
        _cachedMusicDisplayModeDisplayNames = null;
        _cachedWeatherTempUnitDisplayNames = null;
        _cachedWeatherWindUnitDisplayNames = null;
        _cachedWeatherDefaultViewDisplayNames = null;
        _cachedWeatherSkinDisplayNames = null;
        _cachedWeatherRefreshIntervalDisplayNames = null;
        OnPropertyChanged(nameof(AvailableThemeDisplayNames));
        OnPropertyChanged(nameof(AvailableTrayIconStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableLanguageDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCornerPreferenceDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetMaterialTypeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetBorderColorModeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetBorderStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableLayoutDensityDisplayNames));
        OnPropertyChanged(nameof(AvailableAnimationPresetDisplayNames));
        OnPropertyChanged(nameof(IsOpacitySliderEnabled));
        OnPropertyChanged(nameof(WidgetOpacityVisibility));
        OnPropertyChanged(nameof(MaterialIntensityVisibility));
        OnPropertyChanged(nameof(IsWidgetBorderStyleEnabled));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEffectDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSpeedDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSlideDirectionDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEasingIntensityDisplayNames));
        OnPropertyChanged(nameof(AvailableDisplayWidgetChromeModeDisplayNames));
        OnPropertyChanged(nameof(AvailableInteractiveWidgetChromeModeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetTitleIconModeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetLayerModeDisplayNames));
        OnPropertyChanged(nameof(AvailableQuickCaptureDefaultViewDisplayNames));
        OnPropertyChanged(nameof(AvailableQuickCaptureTabStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableTodoNewTaskPositionDisplayNames));
        OnPropertyChanged(nameof(AvailableAttachmentStorageModeDisplayNames));
        OnPropertyChanged(nameof(SelectedAttachmentStorageModeIndex));
        OnPropertyChanged(nameof(AvailableTodoDefaultFilterDisplayNames));
        OnPropertyChanged(nameof(AvailableTodoTabStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableTodoReminderOffsetDisplayNames));
        OnPropertyChanged(nameof(AvailableMusicDisplayModeDisplayNames));
        OnPropertyChanged(nameof(SelectedThemeText));
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(SelectedTrayIconStyleText));
        OnPropertyChanged(nameof(SelectedTrayIconStyleIndex));
        OnPropertyChanged(nameof(SelectedLanguageText));
        OnPropertyChanged(nameof(SelectedLanguageIndex));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceText));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceIndex));
        OnPropertyChanged(nameof(SelectedWidgetMaterialTypeText));
        OnPropertyChanged(nameof(SelectedWidgetMaterialTypeIndex));
        OnPropertyChanged(nameof(SelectedWidgetBorderColorModeText));
        OnPropertyChanged(nameof(SelectedWidgetBorderColorModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetBorderStyleText));
        OnPropertyChanged(nameof(SelectedWidgetBorderStyleIndex));
        OnPropertyChanged(nameof(SelectedLayoutDensityText));
        OnPropertyChanged(nameof(SelectedLayoutDensityIndex));
        OnPropertyChanged(nameof(SelectedAnimationPresetText));
        OnPropertyChanged(nameof(SelectedAnimationPresetIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectIndex));
        OnPropertyChanged(nameof(IsDirectionEnabled));
        OnPropertyChanged(nameof(IsEasingEnabled));
        OnPropertyChanged(nameof(IsSpeedEnabled));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityIndex));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeIndex));
        NotifyHoverButtonActionPropertiesChanged();
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
        OnPropertyChanged(nameof(SelectedTodoTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesIndex));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
        OnPropertyChanged(nameof(AvailableWeatherTemperatureUnitDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherTemperatureUnitIndex));
        OnPropertyChanged(nameof(AvailableWeatherWindSpeedUnitDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherWindSpeedUnitIndex));
        OnPropertyChanged(nameof(AvailableWeatherDefaultViewDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherDefaultViewIndex));
        OnPropertyChanged(nameof(AvailableWeatherSkinDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherSkinIndex));
        OnPropertyChanged(nameof(AvailableWeatherRefreshIntervalDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherRefreshIntervalIndex));
    }

    private bool CanToggleHoverButtonAction(bool isSelected)
    {
        int selectedCount = GetSelectedHoverButtonActionCount();
        return isSelected
            ? selectedCount > 1
            : selectedCount < 3;
    }

    public bool IsHoverButtonActionSelected(string action)
    {
        return action switch
        {
            SettingsService.WidgetHoverActionLockPosition => ShowHoverActionLockPosition,
            SettingsService.WidgetHoverActionLockSize => ShowHoverActionLockSize,
            SettingsService.WidgetHoverActionAdd => ShowHoverActionAdd,
            SettingsService.WidgetHoverActionMore => ShowHoverActionMore,
            SettingsService.WidgetHoverActionDelete => ShowHoverActionDelete,
            _ => false
        };
    }

    public bool CanToggleHoverButtonAction(string action)
    {
        return CanToggleHoverButtonAction(IsHoverButtonActionSelected(action));
    }

    public void ToggleHoverButtonAction(string action)
    {
        switch (action)
        {
            case SettingsService.WidgetHoverActionLockPosition:
                ShowHoverActionLockPosition = !ShowHoverActionLockPosition;
                break;
            case SettingsService.WidgetHoverActionLockSize:
                ShowHoverActionLockSize = !ShowHoverActionLockSize;
                break;
            case SettingsService.WidgetHoverActionAdd:
                ShowHoverActionAdd = !ShowHoverActionAdd;
                break;
            case SettingsService.WidgetHoverActionMore:
                ShowHoverActionMore = !ShowHoverActionMore;
                break;
            case SettingsService.WidgetHoverActionDelete:
                ShowHoverActionDelete = !ShowHoverActionDelete;
                break;
        }
    }

    private void ApplyHoverButtonActionSelection(string? value)
    {
        var selected = SettingsService.ParseWidgetHoverButtonActions(value);
        bool previousUpdating = _isUpdatingHoverButtonActionSelection;
        _isUpdatingHoverButtonActionSelection = true;
        try
        {
            ShowHoverActionLockPosition = selected.Contains(SettingsService.WidgetHoverActionLockPosition);
            ShowHoverActionLockSize = selected.Contains(SettingsService.WidgetHoverActionLockSize);
            ShowHoverActionAdd = selected.Contains(SettingsService.WidgetHoverActionAdd);
            ShowHoverActionMore = selected.Contains(SettingsService.WidgetHoverActionMore);
            ShowHoverActionDelete = selected.Contains(SettingsService.WidgetHoverActionDelete);
        }
        finally
        {
            _isUpdatingHoverButtonActionSelection = previousUpdating;
        }

        NotifyHoverButtonActionPropertiesChanged();
    }

    private void OnHoverButtonActionSelectionChanged(string action, bool value)
    {
        if (_isUpdatingHoverButtonActionSelection)
        {
            return;
        }

        int selectedCount = GetSelectedHoverButtonActionCount();
        if ((!value && selectedCount == 0) || (value && selectedCount > 3))
        {
            RevertHoverButtonActionSelection(action, !value);
            return;
        }

        NotifyHoverButtonActionPropertiesChanged();

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WidgetHoverButtonActions = BuildHoverButtonActionSettingValue();
        SaveAppearanceChange();
    }

    private void RevertHoverButtonActionSelection(string action, bool value)
    {
        bool previousUpdating = _isUpdatingHoverButtonActionSelection;
        _isUpdatingHoverButtonActionSelection = true;
        try
        {
            switch (action)
            {
                case SettingsService.WidgetHoverActionLockPosition:
                    ShowHoverActionLockPosition = value;
                    break;
                case SettingsService.WidgetHoverActionLockSize:
                    ShowHoverActionLockSize = value;
                    break;
                case SettingsService.WidgetHoverActionAdd:
                    ShowHoverActionAdd = value;
                    break;
                case SettingsService.WidgetHoverActionMore:
                    ShowHoverActionMore = value;
                    break;
                case SettingsService.WidgetHoverActionDelete:
                    ShowHoverActionDelete = value;
                    break;
            }
        }
        finally
        {
            _isUpdatingHoverButtonActionSelection = previousUpdating;
        }

        NotifyHoverButtonActionPropertiesChanged();
    }

    private int GetSelectedHoverButtonActionCount()
    {
        int count = 0;
        if (ShowHoverActionLockPosition)
        {
            count++;
        }

        if (ShowHoverActionLockSize)
        {
            count++;
        }

        if (ShowHoverActionAdd)
        {
            count++;
        }

        if (ShowHoverActionMore)
        {
            count++;
        }

        if (ShowHoverActionDelete)
        {
            count++;
        }

        return count;
    }

    private string BuildHoverButtonActionSettingValue()
    {
        var selected = new List<string>(capacity: 3);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionLockPosition, ShowHoverActionLockPosition);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionLockSize, ShowHoverActionLockSize);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionAdd, ShowHoverActionAdd);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionMore, ShowHoverActionMore);
        AddHoverButtonActionIfSelected(selected, SettingsService.WidgetHoverActionDelete, ShowHoverActionDelete);
        return selected.Count == 0
            ? SettingsService.DefaultWidgetHoverButtonActions
            : string.Join(",", selected);
    }

    private static void AddHoverButtonActionIfSelected(ICollection<string> selected, string action, bool isSelected)
    {
        if (isSelected && selected.Count < 3)
        {
            selected.Add(action);
        }
    }

    private void NotifyHoverButtonActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(CanToggleHoverActionLockPosition));
        OnPropertyChanged(nameof(CanToggleHoverActionLockSize));
        OnPropertyChanged(nameof(CanToggleHoverActionAdd));
        OnPropertyChanged(nameof(CanToggleHoverActionMore));
        OnPropertyChanged(nameof(CanToggleHoverActionDelete));
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
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
            SettingsService.WidgetAnimationEffectSlideFade or
            SettingsService.WidgetAnimationEffectZoom or
            SettingsService.WidgetAnimationEffectSlideUpFade or
            SettingsService.WidgetAnimationEffectSlideDownFade or
            SettingsService.WidgetAnimationEffectSlideLeftFade or
            SettingsService.WidgetAnimationEffectSlideRightFade or
            SettingsService.WidgetAnimationEffectScaleSlide
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

    private static string NormalizeWidgetAnimationSlideDirection(string? direction)
    {
        return direction is
            SettingsService.WidgetAnimationSlideDirectionNone or
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight or
            SettingsService.WidgetAnimationSlideDirectionUp or
            SettingsService.WidgetAnimationSlideDirectionDown
            ? direction
            : SettingsService.WidgetAnimationSlideDirectionRight;
    }

    private static string NormalizeWidgetAnimationEasingIntensity(string? intensity)
    {
        return intensity is
            SettingsService.WidgetAnimationEasingNone or
            SettingsService.WidgetAnimationEasingLight or
            SettingsService.WidgetAnimationEasingStandard or
            SettingsService.WidgetAnimationEasingStrong
            ? intensity
            : SettingsService.WidgetAnimationEasingStandard;
    }

    private static string NormalizeWidgetChromeModeSetting(string? mode, WidgetChromeMode fallback)
    {
        return SettingsService.NormalizeWidgetChromeModeSetting(mode, fallback);
    }

    private static string NormalizeWidgetTitleIconModeSetting(string? mode)
    {
        return SettingsService.NormalizeWidgetTitleIconModeSetting(mode);
    }

    private static string NormalizeTodoNewTaskPosition(string? position)
    {
        return position == SettingsService.TodoNewTaskPositionBottom
            ? SettingsService.TodoNewTaskPositionBottom
            : SettingsService.TodoNewTaskPositionTop;
    }

    private static string NormalizeQuickCaptureDefaultView(string? view)
    {
        return view is
            SettingsService.QuickCaptureDefaultViewPinned or
            SettingsService.QuickCaptureDefaultViewRecent
            ? view
            : SettingsService.QuickCaptureDefaultViewRecords;
    }

    private static string NormalizeTodoDefaultFilter(string? filter)
    {
        return filter is
            SettingsService.TodoDefaultFilterToday or
            SettingsService.TodoDefaultFilterImportant or
            SettingsService.TodoDefaultFilterCompleted
            ? filter
            : SettingsService.TodoDefaultFilterAll;
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

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.AutoCheckForUpdates = value;
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

    partial void OnResizeSnapEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ResizeSnapEnabled = value;
        _settingsService.SaveDebounced();

        // Sync to the live overlay service
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.IsSnapEnabled = value;
        }
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
            SettingsService.MinWidgetHeight,
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

    partial void OnShowImageFilesAsIconsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.ShowImageFilesAsIcons = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowHoverButtonsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowHoverButtons = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowHoverActionLockPositionChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockPosition, value);
    }

    partial void OnShowHoverActionLockSizeChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockSize, value);
    }

    partial void OnShowHoverActionAddChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionAdd, value);
    }

    partial void OnShowHoverActionMoreChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionMore, value);
    }

    partial void OnShowHoverActionDeleteChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionDelete, value);
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

    partial void OnShowFileItemPathTooltipsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.ShowFileItemPathTooltips = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowFileExtensions = value;
        _settingsService.SaveDebounced();
    }

    partial void OnHideShortcutExtensionWhenShowingFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions = value;
        _settingsService.SaveDebounced();
    }

    partial void OnQuickCaptureEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            OnPropertyChanged(nameof(QuickCaptureStatusText));
            OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, value);
        if (!value)
        {
            ApplyQuickCaptureRecordingState(clipboardEnabled: false, imageEnabled: false);
            _settingsService.SaveDebounced();
            App.Current?.QuickCaptureClipboardService?.Refresh();
        }

        _ = SyncQuickCaptureEnabledAsync(value);
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private async Task SyncQuickCaptureEnabledAsync(bool value)
    {
        try
        {
            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetQuickCaptureEnabledAsync(value, reveal: value);
                return;
            }

            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync Quick Capture enabled state: {ex}");
        }
        finally
        {
            App.Current?.QuickCaptureClipboardService?.Refresh();
            OnPropertyChanged(nameof(FeatureWidgetEntries));
            RefreshQuickCaptureClipboardDiagnostics();
        }
    }

    partial void OnTodoEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Todo, value);
        _ = SyncTodoEnabledAsync(value);
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        App.Current?.TodoReminderService?.Refresh();
    }

    partial void OnTodoShowCompletedTasksChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowCompletedTasks = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoShowFooterStatsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowFooterStats = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoShowClearCompletedButtonChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowClearCompletedButton = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoConfirmBeforeDeleteChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoConfirmBeforeDelete = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoReminderEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoReminderEnabled = value;
        _settingsService.SaveDebounced();
        App.Current?.TodoReminderService?.Refresh();
        if (value && App.Current?.TodoReminderService is { } reminderService)
        {
            _ = reminderService.CheckNowAsync(DateTimeOffset.Now);
        }
    }

    partial void OnMusicUseArtworkBackdropChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.MusicUseArtworkBackdrop = value;
        _settingsService.SaveDebounced();
    }

    partial void OnMusicEnableCoverHoverMotionChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.MusicEnableCoverHoverMotion = value;
        _settingsService.SaveDebounced();
    }

    private async Task SyncTodoEnabledAsync(bool enabled)
    {
        try
        {
            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.Todo, enabled, reveal: enabled);
                return;
            }

            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync Todo enabled state: {ex}");
        }
        finally
        {
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    partial void OnQuickCaptureClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        ApplyQuickCaptureRecordingState(
            clipboardEnabled: value,
            imageEnabled: value && QuickCaptureImageClipboardEnabled);

        if (value)
        {
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, true);
            bool shouldSyncQuickCapture = !QuickCaptureEnabled;
            if (shouldSyncQuickCapture)
            {
                _quickCaptureEnabled = true;
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            }

            _ = SyncQuickCaptureEnabledAsync(true);
        }
        else
        {
            App.Log("[QuickCaptureClipboard] Disabled from settings");
        }

        _settingsService.SaveDebounced();
        App.Current?.QuickCaptureClipboardService?.Refresh();
        if (value)
        {
            App.Current?.QuickCaptureClipboardService?.CaptureCurrent();
        }

        RefreshQuickCaptureClipboardDiagnostics();
    }

    partial void OnQuickCaptureImageClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        if (value)
        {
            ApplyQuickCaptureRecordingState(clipboardEnabled: true, imageEnabled: true);
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, true);
            bool shouldSyncQuickCapture = !QuickCaptureEnabled;
            if (shouldSyncQuickCapture)
            {
                _quickCaptureEnabled = true;
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            }

            _ = SyncQuickCaptureEnabledAsync(true);
        }
        else
        {
            ApplyQuickCaptureRecordingState(
                clipboardEnabled: QuickCaptureClipboardEnabled,
                imageEnabled: false);
        }

        _settingsService.SaveDebounced();
        App.Current?.QuickCaptureClipboardService?.Refresh();
        RefreshQuickCaptureClipboardDiagnostics();
        if (value)
        {
            App.Current?.QuickCaptureClipboardService?.CaptureCurrent();
        }
    }

    private void ApplyQuickCaptureRecordingState(bool clipboardEnabled, bool imageEnabled)
    {
        if (!clipboardEnabled)
        {
            imageEnabled = false;
        }

        _settingsService.Settings.QuickCaptureClipboardEnabled = clipboardEnabled;
        _settingsService.Settings.QuickCaptureImageClipboardEnabled = imageEnabled;

        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            if (QuickCaptureClipboardEnabled != clipboardEnabled)
            {
                QuickCaptureClipboardEnabled = clipboardEnabled;
            }

            if (QuickCaptureImageClipboardEnabled != imageEnabled)
            {
                QuickCaptureImageClipboardEnabled = imageEnabled;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }
    }

    partial void OnQuickCaptureRecentLimitChanged(int value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
            OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
            return;
        }

        int normalizedValue = QuickCaptureService.NormalizeRecentLimit(value);
        if (normalizedValue != value)
        {
            QuickCaptureRecentLimit = normalizedValue;
            return;
        }

        _settingsService.Settings.QuickCaptureRecentLimit = normalizedValue;
        _settingsService.SaveDebounced();
        _ = App.Current.QuickCaptureService.TrimRecentItemsAsync(normalizedValue);
        OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
        OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
    }

    partial void OnQuickCaptureShowCreatedTimeChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.QuickCaptureShowCreatedTime = value;
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

    partial void OnWidgetMaterialIntensityChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(WidgetMaterialIntensityValueText));
            return;
        }

        if (!double.IsFinite(value))
        {
            WidgetMaterialIntensity = _settingsService.Settings.WidgetMaterialIntensity;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinWidgetMaterialIntensity,
            SettingsService.MaxWidgetMaterialIntensity);
        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            WidgetMaterialIntensity = normalizedValue;
            return;
        }

        _settingsService.Settings.WidgetMaterialIntensity = normalizedValue;
        SaveAppearanceChange();
        OnPropertyChanged(nameof(WidgetMaterialIntensityValueText));
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
        SyncLayoutDensitySelection();
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
        SyncLayoutDensitySelection();
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
        SyncLayoutDensitySelection();
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
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetimeCts.Cancel();
        _updateOperationCts?.Cancel();
        _updateOperationCts?.Dispose();
        if (App.Current?.QuickCaptureClipboardService is { } clipboardService)
        {
            clipboardService.DiagnosticsChanged -= OnQuickCaptureClipboardDiagnosticsChanged;
        }

        _settingsService.SettingsChanged -= OnSettingsChanged;
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _citySearchCts?.Cancel();
        _citySearchCts?.Dispose();
        _citySearchService?.Dispose();
        _lifetimeCts.Dispose();
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
        OnPropertyChanged(nameof(SelectedAccentColor));
    }

    private static string FormatNumber(double value, int decimals)
    {
        string format = decimals <= 0 ? "0" : $"0.{new string('#', decimals)}";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0} B", Math.Max(0, bytes));
        }

        string[] units = ["KB", "MB", "GB"];
        double value = bytes;
        int unitIndex = -1;
        do
        {
            value /= 1024d;
            unitIndex++;
        }
        while (value >= 1024d && unitIndex < units.Length - 1);

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unitIndex]);
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

    private void ApplyQuickCaptureRecentLimitInput(string? value)
    {
        if (!TryParseNumberInput(value, out double parsedValue))
        {
            OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
            return;
        }

        int normalizedValue = QuickCaptureService.NormalizeRecentLimit((int)Math.Round(parsedValue, MidpointRounding.AwayFromZero));
        if (normalizedValue != QuickCaptureRecentLimit)
        {
            QuickCaptureRecentLimit = normalizedValue;
        }

        OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
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
        OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
    }

    private void ApplyLayoutDensityPreset(string preset)
    {
        if (!SettingsService.TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues values))
        {
            return;
        }

        _isApplyingLayoutDensityPreset = true;
        try
        {
            IconSize = values.IconSize;
            TextSize = values.TextSize;
            LayoutDensityScale = values.DensityScale;
            HorizontalSpacingScale = values.HorizontalSpacingScale;
            VerticalSpacingScale = values.VerticalSpacingScale;
            FileNameWidthScale = values.FileNameWidthScale;
            _settingsService.Settings.LayoutDensity = preset;
        }
        finally
        {
            _isApplyingLayoutDensityPreset = false;
        }

        RefreshNumberInputs();
        SaveAppearanceChange();
    }

    private void SyncLayoutDensitySelection()
    {
        if (_isApplyingLayoutDensityPreset || _isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.LayoutDensity = SettingsService.LayoutDensityCustom;
        if (SetProperty(
            ref _selectedLayoutDensity,
            SettingsService.LayoutDensityCustom,
            nameof(SelectedLayoutDensity)))
        {
            OnPropertyChanged(nameof(SelectedLayoutDensityText));
            OnPropertyChanged(nameof(SelectedLayoutDensityIndex));
        }
    }

    private void ApplyAnimationPreset(string preset)
    {
        (string effect, string speed, string direction, string easing) = preset switch
        {
            AnimationPresetNone => (
                SettingsService.WidgetAnimationEffectNone,
                SettingsService.WidgetAnimationSpeedStandard,
                SettingsService.WidgetAnimationSlideDirectionNone,
                SettingsService.WidgetAnimationEasingNone),
            AnimationPresetGentle => (
                SettingsService.WidgetAnimationEffectFade,
                SettingsService.WidgetAnimationSpeedRelaxed,
                SettingsService.WidgetAnimationSlideDirectionNone,
                SettingsService.WidgetAnimationEasingLight),
            AnimationPresetEmphasized => (
                SettingsService.WidgetAnimationEffectScaleFade,
                SettingsService.WidgetAnimationSpeedRelaxed,
                SettingsService.WidgetAnimationSlideDirectionNone,
                SettingsService.WidgetAnimationEasingStrong),
            _ => (
                SettingsService.WidgetAnimationEffectSlideFade,
                SettingsService.WidgetAnimationSpeedStandard,
                SettingsService.WidgetAnimationSlideDirectionRight,
                SettingsService.WidgetAnimationEasingStandard)
        };

        _isApplyingAnimationPreset = true;
        try
        {
            SelectedWidgetAnimationEffect = effect;
            SelectedWidgetAnimationSpeed = speed;
            SelectedWidgetAnimationSlideDirection = direction;
            SelectedWidgetAnimationEasingIntensity = easing;
        }
        finally
        {
            _isApplyingAnimationPreset = false;
        }

        _settingsService.SaveDebounced();
    }

    private void SyncAnimationPresetSelection()
    {
        if (_isApplyingAnimationPreset || _isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        string resolvedPreset = ResolveAnimationPreset();
        if (SetProperty(ref _selectedAnimationPreset, resolvedPreset, nameof(SelectedAnimationPreset)))
        {
            OnPropertyChanged(nameof(SelectedAnimationPresetText));
            OnPropertyChanged(nameof(SelectedAnimationPresetIndex));
        }
    }

    private string ResolveAnimationPreset()
    {
        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectNone &&
            _selectedWidgetAnimationSpeed == SettingsService.WidgetAnimationSpeedStandard &&
            _selectedWidgetAnimationSlideDirection == SettingsService.WidgetAnimationSlideDirectionNone &&
            _selectedWidgetAnimationEasingIntensity == SettingsService.WidgetAnimationEasingNone)
        {
            return AnimationPresetNone;
        }

        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectFade &&
            _selectedWidgetAnimationSpeed == SettingsService.WidgetAnimationSpeedRelaxed &&
            _selectedWidgetAnimationSlideDirection == SettingsService.WidgetAnimationSlideDirectionNone &&
            _selectedWidgetAnimationEasingIntensity == SettingsService.WidgetAnimationEasingLight)
        {
            return AnimationPresetGentle;
        }

        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectSlideFade &&
            _selectedWidgetAnimationSpeed == SettingsService.WidgetAnimationSpeedStandard &&
            _selectedWidgetAnimationSlideDirection == SettingsService.WidgetAnimationSlideDirectionRight &&
            _selectedWidgetAnimationEasingIntensity == SettingsService.WidgetAnimationEasingStandard)
        {
            return AnimationPresetStandard;
        }

        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectScaleFade &&
            _selectedWidgetAnimationSpeed == SettingsService.WidgetAnimationSpeedRelaxed &&
            _selectedWidgetAnimationSlideDirection == SettingsService.WidgetAnimationSlideDirectionNone &&
            _selectedWidgetAnimationEasingIntensity == SettingsService.WidgetAnimationEasingStrong)
        {
            return AnimationPresetEmphasized;
        }

        return AnimationPresetCustom;
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
        SyncLayoutDensitySelection();
        SaveAppearanceChange();
        foreach (string propertyName in dependentPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void SaveAppearanceChange()
    {
        if (_isApplyingLayoutDensityPreset)
        {
            return;
        }

        if (DeferAppearancePersistence)
        {
            _settingsService.RequestAppearancePreview();
            return;
        }

        if (!SuppressAppearanceNotifications)
        {
            _settingsService.RequestAppearancePreview();
        }

        _settingsService.SaveDebounced(notifySubscribers: !SuppressAppearanceNotifications);
    }

}
