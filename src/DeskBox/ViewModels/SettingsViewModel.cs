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
    private string _selectedWidgetCollapseBehavior = SettingsService.WidgetCollapseBehaviorClick;
    private string _selectedWidgetCompactContentMode = SettingsService.WidgetCompactContentModeSmart;
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
    private string[]? _cachedWidgetCollapseBehaviorDisplayNames;
    private string[]? _cachedWidgetCompactContentModeDisplayNames;
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
    [ObservableProperty] private bool _quickCaptureShowTabBar = true;
    [ObservableProperty] private bool _quickCaptureShowRecordsTab = true;
    [ObservableProperty] private bool _quickCaptureShowPinnedTab = true;
    [ObservableProperty] private bool _quickCaptureShowRecentTab = true;
    [ObservableProperty] private bool _todoEnabled;
    [ObservableProperty] private bool _todoShowTabBar = true;
    [ObservableProperty] private bool _todoShowAllTab = true;
    [ObservableProperty] private bool _todoShowActiveTab;
    [ObservableProperty] private bool _todoShowTodayTab = true;
    [ObservableProperty] private bool _todoShowThisWeekTab;
    [ObservableProperty] private bool _todoShowThisMonthTab;
    [ObservableProperty] private bool _todoShowImportantTab = true;
    [ObservableProperty] private bool _todoShowCompletedTab = true;
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
        InitializeFileStackSettings(settings);
        InitializeContentEditorSettings(settings);
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
        _widgetCapsuleModeEnabled = settings.WidgetCapsuleModeEnabled;
        _widgetCompactHideSensitiveContent = settings.WidgetCompactHideSensitiveContent;
        _selectedWidgetCollapseBehavior = SettingsService.NormalizeWidgetCollapseBehavior(settings.WidgetCollapseBehavior) == SettingsService.WidgetCollapseBehaviorSmart
            ? SettingsService.WidgetCollapseBehaviorSmart
            : SettingsService.WidgetCollapseBehaviorClick;
        _selectedWidgetCompactContentMode = SettingsService.NormalizeWidgetCompactContentMode(
            settings.WidgetCompactContentMode);
        _selectedWidgetCompactAnimationEffect = SettingsService.NormalizeWidgetCompactAnimationEffect(settings.WidgetCompactAnimationEffect);
        _widgetCompactAnimationDurationMs = SettingsService.NormalizeWidgetCompactAnimationDurationMs(settings.WidgetCompactAnimationDurationMs);
        _widgetCompactExpandDelayMs = SettingsService.NormalizeWidgetCompactExpandDelayMs(settings.WidgetCompactExpandDelayMs);
        _widgetCompactCollapseDelayMs = SettingsService.NormalizeWidgetCompactCollapseDelayMs(settings.WidgetCompactCollapseDelayMs);
        _selectedWidgetCompactMediaCornerMode = SettingsService.NormalizeWidgetCompactMediaCornerMode(settings.WidgetCompactMediaCornerMode);
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
        _selectedQuickCaptureTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
        _quickCaptureShowTabBar = settings.QuickCaptureShowTabBar;
        _quickCaptureShowRecordsTab = settings.QuickCaptureShowRecordsTab;
        _quickCaptureShowPinnedTab = settings.QuickCaptureShowPinnedTab;
        _quickCaptureShowRecentTab = settings.QuickCaptureShowRecentTab;
        _todoEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
        _todoShowTabBar = settings.TodoShowTabBar;
        _todoShowAllTab = settings.TodoShowAllTab;
        _todoShowActiveTab = settings.TodoShowActiveTab;
        _todoShowTodayTab = settings.TodoShowTodayTab;
        _todoShowThisWeekTab = settings.TodoShowThisWeekTab;
        _todoShowThisMonthTab = settings.TodoShowThisMonthTab;
        _todoShowImportantTab = settings.TodoShowImportantTab;
        _todoShowCompletedTab = settings.TodoShowCompletedTab;
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
        _selectedTodoTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.TodoTabStyle);
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
        DisposeFileStackSettings();
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
        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectNone)
        {
            return AnimationPresetNone;
        }

        if (_selectedWidgetAnimationEffect == SettingsService.WidgetAnimationEffectFade &&
            _selectedWidgetAnimationSpeed == SettingsService.WidgetAnimationSpeedRelaxed &&
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
