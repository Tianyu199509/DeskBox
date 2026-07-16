using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using DeskBox.Helpers;
using DeskBox.Models;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox.Services;

public readonly record struct LayoutDensityPresetValues(
    double IconSize,
    double TextSize,
    double DensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    double FileNameWidthScale);

/// <summary>
/// Manages application settings persistence using JSON files stored in the application directory.
/// </summary>
public sealed class SettingsService
{
    public const double DefaultWidgetOpacity = 0.80;
    public const double MinWidgetOpacity = 0.0;
    public const double MaxWidgetOpacity = 1.0;
    public const double DefaultWidgetMaterialIntensity = 0.65;
    public const double MinWidgetMaterialIntensity = 0.0;
    public const double MaxWidgetMaterialIntensity = 1.0;
    public const string WidgetMaterialTypeMica = "Mica";
    public const string WidgetMaterialTypeMicaAlt = "MicaAlt";
    public const string WidgetMaterialTypeAcrylic = "Acrylic";
    public const string WidgetMaterialTypeAcrylicBase = "AcrylicBase";
    public const string WidgetMaterialTypeSolid = "Solid";
    public const string WidgetBorderColorModeNeutral = "Neutral";
    public const string WidgetBorderColorModeAccent = "Accent";
    public const string WidgetBorderColorModeNone = "None";
    public const string WidgetBorderStyleNone = "None";
    public const string WidgetBorderStyleThin = "Thin";
    public const string WidgetBorderStyleMedium = "Medium";
    public const string WidgetBorderStyleThick = "Thick";
    public const string WidgetCornerPreferenceDefault = "Default";
    public const string WidgetCornerPreferenceSquare = "Square";
    public const string WidgetCornerPreferenceSmall = "Small";
    public const string WidgetCornerPreferenceRound = "Round";
    public const string WidgetAnimationEffectNone = "None";
    public const string WidgetAnimationEffectFade = "Fade";
    public const string WidgetAnimationEffectSlideRight = "SlideRight";
    public const string WidgetAnimationEffectSlideLeft = "SlideLeft";
    public const string WidgetAnimationEffectSlideUp = "SlideUp";
    public const string WidgetAnimationEffectSlideDown = "SlideDown";
    public const string WidgetAnimationEffectScaleFade = "ScaleFade";
    public const string WidgetAnimationEffectSlideFade = "SlideFade";
    public const string WidgetAnimationEffectZoom = "Zoom";
    public const string WidgetAnimationEffectSlideUpFade = "SlideUpFade";
    public const string WidgetAnimationEffectSlideDownFade = "SlideDownFade";
    public const string WidgetAnimationEffectSlideLeftFade = "SlideLeftFade";
    public const string WidgetAnimationEffectSlideRightFade = "SlideRightFade";
    public const string WidgetAnimationEffectScaleSlide = "ScaleSlide";
    public const string WidgetAnimationSpeedVeryFast = "VeryFast";
    public const string WidgetAnimationSpeedFast = "Fast";
    public const string WidgetAnimationSpeedStandard = "Standard";
    public const string WidgetAnimationSpeedRelaxed = "Relaxed";
    public const string WidgetAnimationSpeedSlow = "Slow";
    public const string WidgetAnimationSlideDirectionNone = "None";
    public const string WidgetAnimationSlideDirectionLeft = "Left";
    public const string WidgetAnimationSlideDirectionRight = "Right";
    public const string WidgetAnimationSlideDirectionUp = "Up";
    public const string WidgetAnimationSlideDirectionDown = "Down";
    public const string WidgetAnimationEasingNone = "None";
    public const string WidgetAnimationEasingLight = "Light";
    public const string WidgetAnimationEasingStandard = "Standard";
    public const string WidgetAnimationEasingStrong = "Strong";

    public static bool IsMicaMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeMica or WidgetMaterialTypeMicaAlt;

    public static bool IsAcrylicMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeAcrylic or WidgetMaterialTypeAcrylicBase;

    public static bool SupportsWidgetOpacity(string? materialType) =>
        IsAcrylicMaterial(materialType);

    public static bool SupportsMaterialIntensity(string? materialType) =>
        IsMicaMaterial(materialType) || IsAcrylicMaterial(materialType);
    public const string WidgetLayerModeDynamic = "Dynamic";
    public const string WidgetLayerModeDesktopPinned = "DesktopPinned";
    public const string WidgetChromeModeStandard = WidgetChromeModeNames.Standard;
    public const string WidgetChromeModeCompact = WidgetChromeModeNames.Compact;
    public const string WidgetChromeModeOverlay = WidgetChromeModeNames.Overlay;
    public const string WidgetChromeModeHidden = WidgetChromeModeNames.Hidden;
    public const string WidgetCollapseBehaviorExpanded = WidgetCollapseBehaviorNames.Expanded;
    public const string WidgetCollapseBehaviorClick = WidgetCollapseBehaviorNames.Click;
    public const string WidgetCollapseBehaviorSmart = WidgetCollapseBehaviorNames.Smart;
    public const string WidgetCollapseBehaviorManual = WidgetCollapseBehaviorClick;
    public const string WidgetCollapseBehaviorAuto = WidgetCollapseBehaviorSmart;
    public const string WidgetCollapsedStyleMinimal = "Minimal";
    public const string WidgetCollapsedStyleSummary = "Summary";
    public const string WidgetCollapsedStyleSmart = "Smart";
    public const string WidgetCollapsedStylePill = "Pill";
    public const string WidgetCompactContentModeMinimal = "Minimal";
    public const string WidgetCompactContentModeSummary = "Summary";
    public const string WidgetCompactContentModeSmart = "Smart";
    public const int CurrentWidgetCompactSettingsVersion = 1;
    public const string WidgetCompactAnimationSmooth = "Smooth";
    public const string WidgetCompactAnimationSlow = "Slow";
    public const string WidgetCompactAnimationSnappy = "Snappy";
    public const string WidgetCompactAnimationNone = "None";
    public const string WidgetCompactMediaCornerFollowWidget = "FollowWidget";
    public const string WidgetCompactMediaCornerSquare = "Square";
    public const string WidgetCompactMediaCornerSmall = "Small";
    public const string WidgetCompactMediaCornerRound = "Round";
    public const int DefaultWidgetCompactAnimationDurationMs = 220;
    public const int MinWidgetCompactAnimationDurationMs = 120;
    public const int MaxWidgetCompactAnimationDurationMs = 400;
    public const int DefaultWidgetCompactExpandDelayMs = 360;
    public const int MinWidgetCompactExpandDelayMs = 100;
    public const int MaxWidgetCompactExpandDelayMs = 1000;
    public const int DefaultWidgetCompactCollapseDelayMs = 620;
    public const int MinWidgetCompactCollapseDelayMs = 200;
    public const int MaxWidgetCompactCollapseDelayMs = 1500;
    public const string WidgetTitleIconModeFilledMono = WidgetTitleIconModeNames.FilledMono;
    public const string WidgetTitleIconModeLineMono = WidgetTitleIconModeNames.LineMono;
    public const string WidgetTitleIconModeColor = WidgetTitleIconModeNames.Color;
    public const string WidgetTitleIconModeHidden = WidgetTitleIconModeNames.Hidden;
    public const string WidgetTitleIconModeTextLabel = WidgetTitleIconModeNames.TextLabel;
    public const string WidgetHoverActionLockPosition = "LockPosition";
    public const string WidgetHoverActionLockSize = "LockSize";
    public const string WidgetHoverActionAdd = "Add";
    public const string WidgetHoverActionMore = "More";
    public const string WidgetHoverActionDelete = "Delete";
    public const string DefaultWidgetHoverButtonActions =
        WidgetHoverActionMore;
    public const string ManagedDropActionMove = "Move";
    public const string ManagedDropActionCopy = "Copy";
    public const string AttachmentStorageModeLink = "Link";
    public const string AttachmentStorageModeCopy = "Copy";
    public const string LanguageSystem = "System";
    public const string LanguageChinese = "zh-CN";
    public const string LanguageEnglish = "en-US";
    public const double DefaultWidgetWidth = 280;
    public const double DefaultWidgetHeight = 400;
    public const bool DefaultGlobalHotkeyEnabled = true;
    public const int DefaultGlobalHotkeyModifiers = (int)Models.HotkeyModifierKeys.None;
    public const int DefaultGlobalHotkeyKey = (int)Windows.System.VirtualKey.F7;
    public const double MinWidgetWidth = 150;
    public const double MinWidgetHeight = 150;
    public const double DefaultIconSize = 30;
    public const double MinIconSize = 24;
    public const double MaxIconSize = 56;
    public const double DefaultTextSize = 11.5;
    public const double MinTextSize = 10;
    public const double MaxTextSize = 16;
    public const double DefaultLayoutDensityScale = 0.56;
    public const double MinLayoutDensityScale = 0.0;
    public const double MaxLayoutDensityScale = 1.0;
    public const double DefaultHorizontalSpacingScale = 0.40;
    public const double DefaultVerticalSpacingScale = 0.60;
    public const double DefaultFileNameWidthScale = 0.36;
    public const double MinSpacingScale = 0.0;
    public const double MaxSpacingScale = 1.0;
    public const string LayoutDensityCompact = "Compact";
    public const string LayoutDensityStandard = "Standard";
    public const string LayoutDensityRelaxed = "Relaxed";
    public const string LayoutDensityCustom = "Custom";
    public const string MusicDisplayModeAuto = "Auto";
    public const string MusicDisplayModeCover = "Cover";
    public const string MusicDisplayModeControls = "Controls";
    public const int MaxRecentOrganizationHistoryCount = 24;
    public const string TodoNewTaskPositionTop = "Top";
    public const string TodoNewTaskPositionBottom = "Bottom";
    public const string TodoDefaultFilterAll = "All";
    public const string TodoDefaultFilterToday = "Today";
    public const string TodoDefaultFilterImportant = "Important";
    public const string TodoDefaultFilterCompleted = "Completed";
    public const int DefaultTodoReminderOffsetMinutes = 5;
    public const int MinTodoReminderOffsetMinutes = 0;
    public const int MaxTodoReminderOffsetMinutes = 1440;
    public const string QuickCaptureDefaultViewRecords = "Records";
    public const string QuickCaptureDefaultViewPinned = "Pinned";
    public const string QuickCaptureDefaultViewRecent = "Recent";
    public const string WidgetTabStylePivot = "Pivot";
    public const string WidgetTabStyleButton = "Button";
public const string WeatherTemperatureUnitCelsius = "Celsius";
public const string WeatherTemperatureUnitFahrenheit = "Fahrenheit";
public const string WeatherWindSpeedUnitKmh = "kmh";
public const string WeatherWindSpeedUnitMs = "ms";
public const string WeatherWindSpeedUnitMph = "mph";
public const string WeatherDefaultViewToday = "Today";
public const string WeatherDefaultViewWeek = "Week";
public const string WeatherSkinStandard = "Standard";
public const string WeatherSkinRich = "Rich";
public const int WeatherRefreshMinMinutes = 15;
public const int WeatherRefreshMaxMinutes = 180;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fileWriteLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _appearancePreviewCts;

    public event Action? SettingsChanged;
    public event Action? AppearancePreviewChanged;

    public AppSettings Settings
    {
        get { lock (_lock) return _settings; }
    }

    /// <summary>
    /// Restores user preference defaults without touching user data, widget instances, or storage paths.
    /// </summary>
    public static void ApplyDefaultPreferences(AppSettings settings)
    {
        settings.Theme = "System";
        settings.TrayIconStyle = "Colorful";
        settings.AccentColorMode = "System";
        settings.DefaultWidgetWidth = DefaultWidgetWidth;
        settings.DefaultWidgetHeight = DefaultWidgetHeight;
        settings.WidgetCornerPreference = WidgetCornerPreferenceRound;
        settings.WidgetMaterialType = WidgetMaterialTypeMica;
        settings.WidgetMaterialIntensity = DefaultWidgetMaterialIntensity;
        settings.WidgetBorderColorMode = WidgetBorderColorModeNeutral;
        settings.WidgetBorderStyle = WidgetBorderStyleThin;
        settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
        settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
        settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
        settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
        settings.WidgetLayerMode = WidgetLayerModeDynamic;
        settings.DisplayWidgetChromeMode = WidgetChromeModeOverlay;
        settings.InteractiveWidgetChromeMode = WidgetChromeModeStandard;
        settings.WidgetCollapseBehavior = WidgetCollapseBehaviorClick;
        settings.WidgetCapsuleModeEnabled = false;
        settings.WidgetCollapsedStyle = WidgetCollapsedStyleSmart;
        settings.WidgetCompactContentMode = WidgetCompactContentModeSmart;
        settings.WidgetCompactHideSensitiveContent = false;
        settings.WidgetCompactSettingsVersion = CurrentWidgetCompactSettingsVersion;
        settings.WidgetCompactAnimationEffect = WidgetCompactAnimationSmooth;
        settings.WidgetCompactAnimationDurationMs = DefaultWidgetCompactAnimationDurationMs;
        settings.WidgetCompactExpandDelayMs = DefaultWidgetCompactExpandDelayMs;
        settings.WidgetCompactCollapseDelayMs = DefaultWidgetCompactCollapseDelayMs;
        settings.WidgetCompactMediaCornerMode = WidgetCompactMediaCornerFollowWidget;
        settings.WidgetTitleIconMode = WidgetTitleIconModeColor;
        settings.WidgetOpacity = DefaultWidgetOpacity;
        settings.IconSize = DefaultIconSize;
        settings.TextSize = DefaultTextSize;
        settings.LayoutDensityScale = DefaultLayoutDensityScale;
        settings.LayoutDensity = LayoutDensityStandard;
        settings.HorizontalSpacingScale = DefaultHorizontalSpacingScale;
        settings.VerticalSpacingScale = DefaultVerticalSpacingScale;
        settings.FileNameWidthScale = DefaultFileNameWidthScale;
        settings.ShowFileExtensions = false;
        settings.ShowImageFilesAsIcons = false;
        settings.HideShortcutExtensionWhenShowingFileExtensions = true;
        settings.ShowHoverButtons = true;
        settings.WidgetHoverButtonActions = DefaultWidgetHoverButtonActions;
        settings.AutoCheckForUpdates = true;
        settings.QuickCaptureClipboardEnabled = false;
        settings.QuickCaptureImageClipboardEnabled = false;
        settings.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
        settings.QuickCaptureShowCreatedTime = true;
        settings.AttachmentStorageMode = AttachmentStorageModeLink;
        settings.QuickCaptureDefaultView = QuickCaptureDefaultViewRecords;
        settings.QuickCaptureTabStyle = WidgetTabStyleButton;
        settings.TodoShowCompletedTasks = true;
        settings.TodoShowFooterStats = false;
        settings.TodoShowClearCompletedButton = true;
        settings.TodoConfirmBeforeDelete = false;
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = DefaultTodoReminderOffsetMinutes;
        settings.MusicUseArtworkBackdrop = true;
        settings.MusicEnableCoverHoverMotion = true;
        settings.MusicDisplayMode = MusicDisplayModeAuto;
settings.WeatherAutoLocation = true;
settings.WeatherCityName = string.Empty;
settings.WeatherLatitude = 0;
settings.WeatherLongitude = 0;
settings.WeatherTemperatureUnit = WeatherTemperatureUnitCelsius;
settings.WeatherWindSpeedUnit = WeatherWindSpeedUnitKmh;
settings.WeatherDefaultView = WeatherDefaultViewToday;
settings.WeatherSkin = WeatherSkinStandard;
settings.WeatherShowForecast = true;
settings.WeatherShowSunrise = true;
settings.WeatherShowUvIndex = true;
settings.WeatherShowPrecipitation = true;
settings.WeatherShowHumidity = true;
settings.WeatherShowWind = true;
settings.WeatherShowPressure = false;
settings.WeatherRefreshIntervalMinutes = 60;
        settings.TodoNewTaskPosition = TodoNewTaskPositionTop;
        settings.TodoDefaultFilter = TodoDefaultFilterAll;
        settings.TodoTabStyle = WidgetTabStyleButton;
        settings.ManagedDropAction = ManagedDropActionMove;
        settings.GlobalHotkeyEnabled = DefaultGlobalHotkeyEnabled;
        settings.GlobalHotkeyModifiers = DefaultGlobalHotkeyModifiers;
        settings.GlobalHotkeyKey = DefaultGlobalHotkeyKey;
        settings.DoubleClickToOpen = true;
        settings.HideShortcutArrowOverlay = true;
        settings.ResizeSnapEnabled = true;
settings.ShowListItemDetails = false;
settings.ShowFileItemPathTooltips = true;
settings.CustomAccentColor = "#0078D4";
settings.FocusClickedWidgetOnRaise = false;
    }

    public SettingsService()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "data");
        _settingsPath = InitializeSettingsPath(dataDir);
    }

    internal SettingsService(string dataDir)
    {
        _settingsPath = InitializeSettingsPath(dataDir);
    }

    private static string InitializeSettingsPath(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "settings.json");
    }

    /// <summary>
    /// Load settings from disk. Creates default settings if file doesn't exist.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            await MigrateLegacySettingsIfNeededAsync();

            bool loadedFromDisk = false;

            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
                if (loaded is not null)
                {
                    lock (_lock) _settings = loaded;
                    loadedFromDisk = true;
                }
            }

            bool changed;
            lock (_lock)
            {
                if (!loadedFromDisk)
                {
                    ApplyDefaultPreferences(_settings);
                }

                changed = NormalizePresentationSettings(_settings);
                changed |= NormalizeAppearanceSettings(_settings);
                changed |= NormalizeFeatureWidgetSettings(_settings);
                changed |= NormalizeWidgetContentSettings(_settings);
                changed |= NormalizeOrganizerSettings(_settings);
                changed |= NormalizeHotkeySettings(_settings);
                changed |= NormalizeQuickCaptureSettings(_settings);
                changed |= NormalizeTodoSettings(_settings);
changed |= NormalizeWeatherSettings(_settings);
changed |= NormalizeDeletionSettings(_settings);
            }

            if (changed)
            {
                await SaveToFileOnlyAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
            lock (_lock) _settings = new AppSettings();
        }
    }

    private async Task MigrateLegacySettingsIfNeededAsync()
    {
        if (File.Exists(_settingsPath))
        {
            return;
        }

        var legacyPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(legacyPath);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to migrate legacy settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Save settings to disk immediately.
    /// </summary>
    public async Task SaveAsync(bool notifySubscribers = true)
    {
        await SaveToFileOnlyAsync();
        if (notifySubscribers)
        {
            SettingsChanged?.Invoke();
        }
    }

    private async Task SaveToFileOnlyAsync()
    {
        await _fileWriteLock.WaitAsync();
        try
        {
            string json;
            lock (_lock)
            {
                NormalizePresentationSettings(_settings);
                NormalizeAppearanceSettings(_settings);
                NormalizeFeatureWidgetSettings(_settings);
                NormalizeWidgetContentSettings(_settings);
                NormalizeOrganizerSettings(_settings);
                NormalizeHotkeySettings(_settings);
                NormalizeQuickCaptureSettings(_settings);
                NormalizeTodoSettings(_settings);
                NormalizeWeatherSettings(_settings);
                json = JsonSerializer.Serialize(_settings, s_jsonOptions);
            }

            // Atomic write: serialize to a temp file, then rename to the target path.
            // This prevents corruption if the process crashes or power is lost mid-write.
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
        }
        finally
        {
            _fileWriteLock.Release();
        }
    }

    /// <summary>
    /// Save settings with debouncing (waits 1 second after last call before actually saving).
    /// Use this for frequent changes like window drag/resize.
    /// </summary>
    public void SaveDebounced(bool notifySubscribers = true)
    {
        if (notifySubscribers)
        {
            SettingsChanged?.Invoke();
        }

        // Cancel and dispose the previous CTS to avoid leaking native
        // kernel event handles.  Each undisposed CTS holds a native handle
        // that is only reclaimed by the GC finalizer, which may not run
        // for a long time in a large-heap app.
        //
        // Note: The CTS may have already been disposed by a completed
        // Task.Run lambda's finally block (see below).  Catch
        // ObjectDisposedException defensively to handle this race.
        try
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                if (!token.IsCancellationRequested)
                {
                    await SaveToFileOnlyAsync();
                }
            }
            catch (TaskCanceledException) { }
            // Do NOT dispose the CTS here — _debounceCts may still
            // reference it, and disposing it here would cause the next
            // SaveDebounced call to throw ObjectDisposedException when
            // it tries to Cancel/Dispose the (already-disposed) CTS.
            // The CTS will be disposed by the next SaveDebounced call
            // or by the GC finalizer.
        });
    }

    public void RequestAppearancePreview()
    {
        // Dispose the previous CTS to avoid leaking native handles.
        try
        {
            _appearancePreviewCts?.Cancel();
            _appearancePreviewCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        _appearancePreviewCts = new CancellationTokenSource();
        var token = _appearancePreviewCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(66, token);
                if (!token.IsCancellationRequested)
                {
                    AppearancePreviewChanged?.Invoke();
                }
            }
            catch (TaskCanceledException) { }
            // Do NOT dispose the CTS here — same rationale as SaveDebounced.
        });
    }

    public void NotifyAppearancePreviewNow()
    {
        _appearancePreviewCts?.Cancel();
        AppearancePreviewChanged?.Invoke();
    }

    /// <summary>
    /// Update a widget's configuration. If the widget doesn't exist, it will be added.
    /// </summary>
    public void UpdateWidget(WidgetConfig config, bool notifySubscribers = true)
    {
        lock (_lock)
        {
            if (_settings.DeletedWidgetIds.Contains(config.Id))
            {
                return;
            }

            var existing = _settings.Widgets.FindIndex(w => w.Id == config.Id);
            if (existing >= 0)
                _settings.Widgets[existing] = config;
            else
                _settings.Widgets.Add(config);
        }
        SaveDebounced(notifySubscribers);
    }

    /// <summary>
    /// Remove a widget configuration.
    /// </summary>
    public void RemoveWidget(string widgetId)
    {
        lock (_lock)
        {
            if (!_settings.DeletedWidgetIds.Contains(widgetId))
            {
                _settings.DeletedWidgetIds.Add(widgetId);
            }

            _settings.Widgets.RemoveAll(w => w.Id == widgetId);
        }
        SaveDebounced();
    }

    public void RemoveWidgetImmediate(string widgetId)
    {
        lock (_lock)
        {
            if (!_settings.DeletedWidgetIds.Contains(widgetId))
            {
                _settings.DeletedWidgetIds.Add(widgetId);
            }

            _settings.Widgets.RemoveAll(w => w.Id == widgetId);
        }
    }

    private static bool NormalizePresentationSettings(AppSettings settings)
    {
        bool changed = false;

        double normalizedWidgetOpacity = double.IsFinite(settings.WidgetOpacity)
            ? Math.Clamp(settings.WidgetOpacity, MinWidgetOpacity, MaxWidgetOpacity)
            : DefaultWidgetOpacity;
        if (Math.Abs(settings.WidgetOpacity - normalizedWidgetOpacity) > 0.0001)
        {
            settings.WidgetOpacity = normalizedWidgetOpacity;
            changed = true;
        }

        if (settings.WidgetCornerPreference is not (
            WidgetCornerPreferenceDefault or
            WidgetCornerPreferenceSquare or
            WidgetCornerPreferenceSmall or
            WidgetCornerPreferenceRound))
        {
            settings.WidgetCornerPreference = WidgetCornerPreferenceRound;
            changed = true;
        }

        if (settings.WidgetMaterialType is not (
            WidgetMaterialTypeMica or
            WidgetMaterialTypeMicaAlt or
            WidgetMaterialTypeAcrylic or
            WidgetMaterialTypeAcrylicBase or
            WidgetMaterialTypeSolid))
        {
            // Migrate legacy "Auto" to "Acrylic"
            if (settings.WidgetMaterialType == "Auto")
            {
                settings.WidgetMaterialType = WidgetMaterialTypeAcrylic;
            }
            else
            {
                settings.WidgetMaterialType = WidgetMaterialTypeAcrylic;
            }
            changed = true;
        }

        if (settings.WidgetMaterialType == WidgetMaterialTypeSolid &&
            Math.Abs(settings.WidgetOpacity - MaxWidgetOpacity) > 0.0001)
        {
            settings.WidgetOpacity = MaxWidgetOpacity;
            changed = true;
        }

        double normalizedMaterialIntensity = double.IsFinite(settings.WidgetMaterialIntensity)
            ? Math.Clamp(
                settings.WidgetMaterialIntensity,
                MinWidgetMaterialIntensity,
                MaxWidgetMaterialIntensity)
            : DefaultWidgetMaterialIntensity;
        if (Math.Abs(settings.WidgetMaterialIntensity - normalizedMaterialIntensity) > 0.0001)
        {
            settings.WidgetMaterialIntensity = normalizedMaterialIntensity;
            changed = true;
        }

        if (settings.WidgetBorderColorMode is not (
            WidgetBorderColorModeNeutral or
            WidgetBorderColorModeAccent or
            WidgetBorderColorModeNone))
        {
            settings.WidgetBorderColorMode = WidgetBorderColorModeNeutral;
            changed = true;
        }

        if (settings.WidgetBorderStyle is not (
            WidgetBorderStyleThin or
            WidgetBorderStyleMedium or
            WidgetBorderStyleThick))
        {
            if (settings.WidgetBorderStyle == WidgetBorderStyleNone)
            {
                settings.WidgetBorderColorMode = WidgetBorderColorModeNone;
            }

            settings.WidgetBorderStyle = WidgetBorderStyleThin;
            changed = true;
        }

        if (settings.WidgetAnimationEffect is not (
            WidgetAnimationEffectNone or
            WidgetAnimationEffectFade or
            WidgetAnimationEffectSlideRight or
            WidgetAnimationEffectSlideLeft or
            WidgetAnimationEffectSlideUp or
            WidgetAnimationEffectSlideDown or
            WidgetAnimationEffectScaleFade or
            WidgetAnimationEffectSlideFade or
            WidgetAnimationEffectZoom or
            WidgetAnimationEffectSlideUpFade or
            WidgetAnimationEffectSlideDownFade or
            WidgetAnimationEffectSlideLeftFade or
            WidgetAnimationEffectSlideRightFade or
            WidgetAnimationEffectScaleSlide))
        {
            settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
            changed = true;
        }

        if (settings.WidgetAnimationSpeed is not (
            WidgetAnimationSpeedVeryFast or
            WidgetAnimationSpeedFast or
            WidgetAnimationSpeedStandard or
            WidgetAnimationSpeedRelaxed or
            WidgetAnimationSpeedSlow))
        {
            settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
            changed = true;
        }

        string normalizedLayerMode = NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);
        if (!string.Equals(settings.WidgetLayerMode, normalizedLayerMode, StringComparison.Ordinal))
        {
            settings.WidgetLayerMode = normalizedLayerMode;
            changed = true;
        }

        string normalizedDisplayChrome = NormalizeWidgetChromeModeSetting(
            settings.DisplayWidgetChromeMode,
            WidgetChromeMode.Overlay);
        if (!string.Equals(settings.DisplayWidgetChromeMode, normalizedDisplayChrome, StringComparison.Ordinal))
        {
            settings.DisplayWidgetChromeMode = normalizedDisplayChrome;
            changed = true;
        }

        string normalizedInteractiveChrome = NormalizeWidgetChromeModeSetting(
            settings.InteractiveWidgetChromeMode,
            WidgetChromeMode.Standard);
        if (!string.Equals(settings.InteractiveWidgetChromeMode, normalizedInteractiveChrome, StringComparison.Ordinal))
        {
            settings.InteractiveWidgetChromeMode = normalizedInteractiveChrome;
            changed = true;
        }

        string normalizedCollapseBehavior = NormalizeWidgetCollapseBehavior(settings.WidgetCollapseBehavior);
        if (normalizedCollapseBehavior == WidgetCollapseBehaviorExpanded)
        {
            normalizedCollapseBehavior = WidgetCollapseBehaviorClick;
        }
        if (!string.Equals(settings.WidgetCollapseBehavior, normalizedCollapseBehavior, StringComparison.Ordinal))
        {
            settings.WidgetCollapseBehavior = normalizedCollapseBehavior;
            changed = true;
        }

        string normalizedCollapsedStyle = NormalizeWidgetCollapsedStyle(settings.WidgetCollapsedStyle);
        if (!string.Equals(settings.WidgetCollapsedStyle, normalizedCollapsedStyle, StringComparison.Ordinal))
        {
            settings.WidgetCollapsedStyle = normalizedCollapsedStyle;
            changed = true;
        }

        if (settings.WidgetCompactSettingsVersion < CurrentWidgetCompactSettingsVersion)
        {
            settings.WidgetCompactContentMode = normalizedCollapsedStyle switch
            {
                WidgetCollapsedStyleMinimal => WidgetCompactContentModeMinimal,
                WidgetCollapsedStyleSmart => WidgetCompactContentModeSmart,
                _ => WidgetCompactContentModeSummary
            };
            settings.WidgetCompactSettingsVersion = CurrentWidgetCompactSettingsVersion;
            changed = true;
        }

        string normalizedCompactContentMode = NormalizeWidgetCompactContentMode(
            settings.WidgetCompactContentMode);
        if (!string.Equals(settings.WidgetCompactContentMode, normalizedCompactContentMode, StringComparison.Ordinal))
        {
            settings.WidgetCompactContentMode = normalizedCompactContentMode;
            changed = true;
        }

        string normalizedCompactAnimation = NormalizeWidgetCompactAnimationEffect(settings.WidgetCompactAnimationEffect);
        if (!string.Equals(settings.WidgetCompactAnimationEffect, normalizedCompactAnimation, StringComparison.Ordinal))
        {
            settings.WidgetCompactAnimationEffect = normalizedCompactAnimation;
            changed = true;
        }

        int normalizedCompactDuration = NormalizeWidgetCompactAnimationDurationMs(settings.WidgetCompactAnimationDurationMs);
        if (settings.WidgetCompactAnimationDurationMs != normalizedCompactDuration)
        {
            settings.WidgetCompactAnimationDurationMs = normalizedCompactDuration;
            changed = true;
        }

        int normalizedCompactExpandDelay = NormalizeWidgetCompactExpandDelayMs(settings.WidgetCompactExpandDelayMs);
        if (settings.WidgetCompactExpandDelayMs != normalizedCompactExpandDelay)
        {
            settings.WidgetCompactExpandDelayMs = normalizedCompactExpandDelay;
            changed = true;
        }

        int normalizedCompactCollapseDelay = NormalizeWidgetCompactCollapseDelayMs(settings.WidgetCompactCollapseDelayMs);
        if (settings.WidgetCompactCollapseDelayMs != normalizedCompactCollapseDelay)
        {
            settings.WidgetCompactCollapseDelayMs = normalizedCompactCollapseDelay;
            changed = true;
        }

        string normalizedCompactMediaCorner = NormalizeWidgetCompactMediaCornerMode(settings.WidgetCompactMediaCornerMode);
        if (!string.Equals(settings.WidgetCompactMediaCornerMode, normalizedCompactMediaCorner, StringComparison.Ordinal))
        {
            settings.WidgetCompactMediaCornerMode = normalizedCompactMediaCorner;
            changed = true;
        }

        string normalizedTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
        if (!string.Equals(settings.WidgetTitleIconMode, normalizedTitleIconMode, StringComparison.Ordinal))
        {
            settings.WidgetTitleIconMode = normalizedTitleIconMode;
            changed = true;
        }

        string normalizedHoverActions = NormalizeWidgetHoverButtonActions(settings.WidgetHoverButtonActions);
        if (!string.Equals(settings.WidgetHoverButtonActions, normalizedHoverActions, StringComparison.Ordinal))
        {
            settings.WidgetHoverButtonActions = normalizedHoverActions;
            changed = true;
        }

        double normalizedIconSize = NormalizeIconSize(settings.IconSize);
        if (Math.Abs(settings.IconSize - normalizedIconSize) > 0.0001)
        {
            settings.IconSize = normalizedIconSize;
            changed = true;
        }

        double normalizedTextSize = NormalizeTextSize(settings.TextSize);
        if (Math.Abs(settings.TextSize - normalizedTextSize) > 0.0001)
        {
            settings.TextSize = normalizedTextSize;
            changed = true;
        }

        double legacyLayoutDensityScale = settings.LayoutDensityScale;
        if (!double.IsFinite(legacyLayoutDensityScale))
        {
            legacyLayoutDensityScale = DefaultLayoutDensityScale;
        }

        double normalizedLayoutDensityScale = Math.Clamp(legacyLayoutDensityScale, MinLayoutDensityScale, MaxLayoutDensityScale);
        if (Math.Abs(settings.LayoutDensityScale - normalizedLayoutDensityScale) > 0.0001)
        {
            settings.LayoutDensityScale = normalizedLayoutDensityScale;
            changed = true;
        }

        double normalizedHorizontalSpacingScale = NormalizeScale(
            settings.HorizontalSpacingScale,
            DefaultHorizontalSpacingScale,
            MinSpacingScale,
            MaxSpacingScale);
        double normalizedVerticalSpacingScale = NormalizeScale(
            settings.VerticalSpacingScale,
            DefaultVerticalSpacingScale,
            MinSpacingScale,
            MaxSpacingScale);
        double normalizedFileNameWidthScale = NormalizeScale(
            settings.FileNameWidthScale,
            DefaultFileNameWidthScale,
            MinSpacingScale,
            MaxSpacingScale);

        if (Math.Abs(settings.HorizontalSpacingScale - normalizedHorizontalSpacingScale) > 0.0001)
        {
            settings.HorizontalSpacingScale = normalizedHorizontalSpacingScale;
            changed = true;
        }

        if (Math.Abs(settings.VerticalSpacingScale - normalizedVerticalSpacingScale) > 0.0001)
        {
            settings.VerticalSpacingScale = normalizedVerticalSpacingScale;
            changed = true;
        }

        if (Math.Abs(settings.FileNameWidthScale - normalizedFileNameWidthScale) > 0.0001)
        {
            settings.FileNameWidthScale = normalizedFileNameWidthScale;
            changed = true;
        }

        string resolvedLayoutDensity = settings.LayoutDensity == LayoutDensityCustom
            ? LayoutDensityCustom
            : ResolveLayoutDensityPreset(settings);
        if (!string.Equals(settings.LayoutDensity, resolvedLayoutDensity, StringComparison.Ordinal))
        {
            settings.LayoutDensity = resolvedLayoutDensity;
            changed = true;
        }

        string normalizedMusicDisplayMode = NormalizeMusicDisplayMode(settings.MusicDisplayMode);
        if (!string.Equals(settings.MusicDisplayMode, normalizedMusicDisplayMode, StringComparison.Ordinal))
        {
            settings.MusicDisplayMode = normalizedMusicDisplayMode;
            changed = true;
        }

        double normalizedWidgetWidth = double.IsFinite(settings.DefaultWidgetWidth)
            ? Math.Clamp(settings.DefaultWidgetWidth, MinWidgetWidth, 1200)
            : DefaultWidgetWidth;
        if (Math.Abs(settings.DefaultWidgetWidth - normalizedWidgetWidth) > 0.0001)
        {
            settings.DefaultWidgetWidth = normalizedWidgetWidth;
            changed = true;
        }

        double normalizedWidgetHeight = double.IsFinite(settings.DefaultWidgetHeight)
            ? Math.Clamp(settings.DefaultWidgetHeight, MinWidgetHeight, 1200)
            : DefaultWidgetHeight;
        if (Math.Abs(settings.DefaultWidgetHeight - normalizedWidgetHeight) > 0.0001)
        {
            settings.DefaultWidgetHeight = normalizedWidgetHeight;
            changed = true;
        }

        return changed;
    }

    private static double NormalizeScale(double value, double defaultValue, double min, double max)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    public static double NormalizeIconSize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinIconSize, MaxIconSize)
            : DefaultIconSize;
    }

    public static double NormalizeTextSize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinTextSize, MaxTextSize)
            : DefaultTextSize;
    }

    public static string NormalizeMusicDisplayMode(string? mode)
    {
        return mode switch
        {
            MusicDisplayModeCover => MusicDisplayModeCover,
            MusicDisplayModeControls => MusicDisplayModeControls,
            _ => MusicDisplayModeAuto
        };
    }

    public static bool TryGetLayoutDensityPresetValues(
        string? preset,
        out LayoutDensityPresetValues values)
    {
        values = preset switch
        {
            LayoutDensityCompact => new LayoutDensityPresetValues(
                IconSize: 26,
                TextSize: 10.5,
                DensityScale: 0.20,
                HorizontalSpacingScale: 0.20,
                VerticalSpacingScale: 0.28,
                FileNameWidthScale: 0.30),
            LayoutDensityStandard => new LayoutDensityPresetValues(
                IconSize: DefaultIconSize,
                TextSize: DefaultTextSize,
                DensityScale: DefaultLayoutDensityScale,
                HorizontalSpacingScale: DefaultHorizontalSpacingScale,
                VerticalSpacingScale: DefaultVerticalSpacingScale,
                FileNameWidthScale: DefaultFileNameWidthScale),
            LayoutDensityRelaxed => new LayoutDensityPresetValues(
                IconSize: 36,
                TextSize: 13,
                DensityScale: 0.84,
                HorizontalSpacingScale: 0.68,
                VerticalSpacingScale: 0.82,
                FileNameWidthScale: 0.50),
            _ => default
        };

        return preset is LayoutDensityCompact or LayoutDensityStandard or LayoutDensityRelaxed;
    }

    public static void ApplyLayoutDensityPreset(AppSettings settings, string preset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues values))
        {
            return;
        }

        settings.IconSize = values.IconSize;
        settings.TextSize = values.TextSize;
        settings.LayoutDensityScale = values.DensityScale;
        settings.HorizontalSpacingScale = values.HorizontalSpacingScale;
        settings.VerticalSpacingScale = values.VerticalSpacingScale;
        settings.FileNameWidthScale = values.FileNameWidthScale;
        settings.LayoutDensity = preset;
    }

    public static string ResolveLayoutDensityPreset(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (string preset in new[] { LayoutDensityCompact, LayoutDensityStandard, LayoutDensityRelaxed })
        {
            TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues values);
            if (NearlyEqual(settings.IconSize, values.IconSize) &&
                NearlyEqual(settings.TextSize, values.TextSize) &&
                NearlyEqual(settings.LayoutDensityScale, values.DensityScale) &&
                NearlyEqual(settings.HorizontalSpacingScale, values.HorizontalSpacingScale) &&
                NearlyEqual(settings.VerticalSpacingScale, values.VerticalSpacingScale) &&
                NearlyEqual(settings.FileNameWidthScale, values.FileNameWidthScale))
            {
                return preset;
            }
        }

        return LayoutDensityCustom;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.0001;

    public static string NormalizeWidgetChromeModeSetting(string? value, WidgetChromeMode fallback)
    {
        return WidgetChromeModeNames.NormalizeSettingValue(value, fallback);
    }

    public static string NormalizeWidgetCollapseBehavior(string? value)
    {
        return WidgetCollapseBehaviorNames.ToSettingValue(
            WidgetCollapseBehaviorNames.Normalize(value));
    }

    public static string NormalizeWidgetCollapsedStyle(string? value)
    {
        if (string.Equals(value, WidgetCollapsedStylePill, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapsedStylePill;
        }

        if (string.Equals(value, WidgetCollapsedStyleSmart, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapsedStyleSmart;
        }

        return string.Equals(value, WidgetCollapsedStyleMinimal, StringComparison.OrdinalIgnoreCase)
            ? WidgetCollapsedStyleMinimal
            : WidgetCollapsedStyleSummary;
    }

    public static string NormalizeWidgetCompactContentMode(string? value)
    {
        if (string.Equals(value, WidgetCompactContentModeMinimal, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactContentModeMinimal;
        }

        return string.Equals(value, WidgetCompactContentModeSummary, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactContentModeSummary
            : WidgetCompactContentModeSmart;
    }

    public static string NormalizeWidgetCompactAnimationEffect(string? value)
    {
        if (string.Equals(value, WidgetCompactAnimationSlow, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactAnimationSlow;
        }

        if (string.Equals(value, WidgetCompactAnimationSnappy, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactAnimationSnappy;
        }

        return string.Equals(value, WidgetCompactAnimationNone, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactAnimationNone
            : WidgetCompactAnimationSmooth;
    }

    public static int NormalizeWidgetCompactAnimationDurationMs(int value) =>
        Math.Clamp(value, MinWidgetCompactAnimationDurationMs, MaxWidgetCompactAnimationDurationMs);

    public static int NormalizeWidgetCompactExpandDelayMs(int value) =>
        Math.Clamp(value, MinWidgetCompactExpandDelayMs, MaxWidgetCompactExpandDelayMs);

    public static int NormalizeWidgetCompactCollapseDelayMs(int value) =>
        Math.Clamp(value, MinWidgetCompactCollapseDelayMs, MaxWidgetCompactCollapseDelayMs);

    public static string NormalizeWidgetCompactMediaCornerMode(string? value)
    {
        if (string.Equals(value, WidgetCompactMediaCornerSquare, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactMediaCornerSquare;
        }

        if (string.Equals(value, WidgetCompactMediaCornerSmall, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactMediaCornerSmall;
        }

        return string.Equals(value, WidgetCompactMediaCornerRound, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactMediaCornerRound
            : WidgetCompactMediaCornerFollowWidget;
    }

    public static string NormalizeWidgetTitleIconModeSetting(string? value)
    {
        return WidgetTitleIconModeNames.NormalizeSettingValue(value);
    }

    public static string NormalizeWidgetHoverButtonActions(string? value)
    {
        var normalized = ParseWidgetHoverButtonActions(value);
        return normalized.Count == 0
            ? DefaultWidgetHoverButtonActions
            : string.Join(",", normalized);
    }

    public static IReadOnlyList<string> ParseWidgetHoverButtonActions(string? value)
    {
        string[] allowed =
        [
            WidgetHoverActionLockPosition,
            WidgetHoverActionLockSize,
            WidgetHoverActionAdd,
            WidgetHoverActionMore,
            WidgetHoverActionDelete
        ];

        if (string.IsNullOrWhiteSpace(value))
        {
            return [WidgetHoverActionMore];
        }

        var selected = new List<string>();
        foreach (string rawPart in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string? normalized = allowed.FirstOrDefault(action =>
                string.Equals(action, rawPart, StringComparison.OrdinalIgnoreCase));
            if (normalized is null || selected.Contains(normalized))
            {
                continue;
            }

            selected.Add(normalized);
            if (selected.Count == 3)
            {
                break;
            }
        }

        return selected.Count == 0
            ? [WidgetHoverActionMore]
            : selected;
    }

    public static string NormalizeWidgetLayerModeSetting(string? value)
    {
        return string.Equals(value, WidgetLayerModeDesktopPinned, StringComparison.Ordinal)
            ? WidgetLayerModeDesktopPinned
            : WidgetLayerModeDynamic;
    }

    private static bool NormalizeAppearanceSettings(AppSettings settings)
    {
        bool changed = false;

        if (settings.Theme is not ("System" or "Light" or "Dark"))
        {
            settings.Theme = "System";
            changed = true;
        }

        if (settings.Language is not (LanguageSystem or LanguageChinese or LanguageEnglish))
        {
            settings.Language = LanguageSystem;
            changed = true;
        }

        if (settings.AccentColorMode is not ("System" or "Custom"))
        {
            settings.AccentColorMode = "System";
            changed = true;
        }

        if (!AccentColorHelper.TryParseHex(settings.CustomAccentColor, out _))
        {
            settings.CustomAccentColor = AccentColorHelper.DefaultAccentColorHex;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeWidgetContentSettings(AppSettings settings)
    {
        bool changed = false;

        int removedProductivityWidgets = settings.Widgets.RemoveAll(widget => widget.WidgetKind == WidgetKind.Productivity);
        if (removedProductivityWidgets > 0)
        {
            changed = true;
        }

        foreach (var widget in settings.Widgets)
        {
            if (widget.WidgetKind is WidgetKind.Productivity)
            {
                widget.WidgetKind = WidgetKind.File;
                changed = true;
            }

            if (!WidgetRegistry.Default.IsKnown(widget.WidgetKind))
            {
                widget.WidgetKind = WidgetKind.File;
                changed = true;
            }

            widget.Metadata ??= [];

            if (widget.CompactWidth is { } compactWidth)
            {
                double normalizedCompactWidth = WidgetCompactBoundsCalculator.ClampLogicalWidth(compactWidth);
                if (Math.Abs(compactWidth - normalizedCompactWidth) > 0.0001)
                {
                    widget.CompactWidth = normalizedCompactWidth;
                    changed = true;
                }
            }

            if (widget.Metadata.TryGetValue(WidgetChromeModeNames.MetadataKey, out string? chromeModeValue))
            {
                var normalizedChromeMode = WidgetChromeModeNames.NormalizeMode(
                    chromeModeValue,
                    WidgetChromeMode.System,
                    allowSystem: true);
                if (normalizedChromeMode == WidgetChromeMode.System)
                {
                    widget.Metadata.Remove(WidgetChromeModeNames.MetadataKey);
                    changed = true;
                }
                else
                {
                    string normalizedChromeModeValue = WidgetChromeModeNames.ToSettingValue(normalizedChromeMode);
                    if (!string.Equals(chromeModeValue, normalizedChromeModeValue, StringComparison.Ordinal))
                    {
                        widget.Metadata[WidgetChromeModeNames.MetadataKey] = normalizedChromeModeValue;
                        changed = true;
                    }
                }
            }

            if (widget.Metadata.TryGetValue(WidgetCollapseBehaviorNames.MetadataKey, out string? collapseBehaviorValue))
            {
                WidgetCollapseBehavior normalizedBehavior = WidgetCollapseBehaviorNames.Normalize(
                    collapseBehaviorValue,
                    WidgetCollapseBehavior.System,
                    allowSystem: true);
                if (normalizedBehavior == WidgetCollapseBehavior.System)
                {
                    widget.Metadata.Remove(WidgetCollapseBehaviorNames.MetadataKey);
                    changed = true;
                }
                else
                {
                    string normalizedValue = WidgetCollapseBehaviorNames.ToSettingValue(normalizedBehavior);
                    if (!string.Equals(collapseBehaviorValue, normalizedValue, StringComparison.Ordinal))
                    {
                        widget.Metadata[WidgetCollapseBehaviorNames.MetadataKey] = normalizedValue;
                        changed = true;
                    }
                }
            }

            if (widget.IsDisabled)
            {
                widget.IsDisabled = false;
                changed = true;
            }
        }

        return changed;
    }

    internal static bool NormalizeFeatureWidgetSettings(AppSettings settings)
    {
        return FeatureWidgetSettings.Normalize(settings);
    }

    public static string GetDefaultManagedStorageRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "DeskBox");
    }

    public static string NormalizeManagedStorageRootPath(string? path)
    {
        string candidate = string.IsNullOrWhiteSpace(path)
            ? GetDefaultManagedStorageRootPath()
            : Environment.ExpandEnvironmentVariables(path.Trim());

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return GetDefaultManagedStorageRootPath();
        }
    }

    private static bool NormalizeOrganizerSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedAttachmentStorageMode = NormalizeAttachmentStorageMode(settings.AttachmentStorageMode);
        if (!string.Equals(settings.AttachmentStorageMode, normalizedAttachmentStorageMode, StringComparison.Ordinal))
        {
            settings.AttachmentStorageMode = normalizedAttachmentStorageMode;
            changed = true;
        }

        if (!string.Equals(settings.ManagedDropAction, ManagedDropActionMove, StringComparison.Ordinal))
        {
            settings.ManagedDropAction = ManagedDropActionMove;
            changed = true;
        }

        string normalizedRootPath = NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);
        if (!string.Equals(settings.DefaultManagedStorageRootPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultManagedStorageRootPath = normalizedRootPath;
            changed = true;
        }

        settings.RecentOrganizationHistory ??= [];
        int originalHistoryCount = settings.RecentOrganizationHistory.Count;
        settings.RecentOrganizationHistory = settings.RecentOrganizationHistory
            .Where(entry => entry is not null)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(MaxRecentOrganizationHistoryCount)
            .ToList();
        if (settings.RecentOrganizationHistory.Count != originalHistoryCount)
        {
            changed = true;
        }

        foreach (var entry in settings.RecentOrganizationHistory)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString();
                changed = true;
            }

            entry.WidgetId ??= string.Empty;
            entry.WidgetName ??= string.Empty;
            entry.ActionType = string.IsNullOrWhiteSpace(entry.ActionType)
                ? OrganizationActionType.ManagedDrop
                : entry.ActionType;
            entry.TransferMode = entry.TransferMode is "Move" or "Copy"
                ? entry.TransferMode
                : ManagedDropActionMove;
            entry.Items ??= [];
        }

        foreach (var widget in settings.Widgets)
        {
            if (!widget.FollowsDefaultStoragePath)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(widget.ManagedFolderName) && !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                widget.ManagedFolderName = Path.GetFileName(widget.MappedFolderPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(widget.ManagedFolderName))
            {
                string normalizedWidgetPath = string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                    ? Path.Combine(normalizedRootPath, widget.ManagedFolderName)
                    : NormalizeManagedStorageRootPath(widget.MappedFolderPath);
                if (!string.Equals(widget.MappedFolderPath, normalizedWidgetPath, StringComparison.OrdinalIgnoreCase))
                {
                    widget.MappedFolderPath = normalizedWidgetPath;
                    changed = true;
                }
            }
        }

        return changed;
    }

    public static string NormalizeAttachmentStorageMode(string? storageMode)
    {
        return string.Equals(storageMode, AttachmentStorageModeCopy, StringComparison.OrdinalIgnoreCase)
            ? AttachmentStorageModeCopy
            : AttachmentStorageModeLink;
    }

    private static bool NormalizeHotkeySettings(AppSettings settings)
    {
        bool changed = false;
        int normalizedModifiers = (int)((Models.HotkeyModifierKeys)settings.GlobalHotkeyModifiers &
            (Models.HotkeyModifierKeys.Alt | Models.HotkeyModifierKeys.Control | Models.HotkeyModifierKeys.Shift));

        if (settings.GlobalHotkeyModifiers != normalizedModifiers)
        {
            settings.GlobalHotkeyModifiers = normalizedModifiers;
            changed = true;
        }

        var gesture = GlobalHotkeyService.NormalizeGesture(settings.GlobalHotkeyModifiers, settings.GlobalHotkeyKey);
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            settings.GlobalHotkeyModifiers = DefaultGlobalHotkeyModifiers;
            settings.GlobalHotkeyKey = DefaultGlobalHotkeyKey;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeQuickCaptureSettings(AppSettings settings)
    {
        bool changed = false;

        int normalizedLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        if (settings.QuickCaptureRecentLimit != normalizedLimit)
        {
            settings.QuickCaptureRecentLimit = normalizedLimit;
            changed = true;
        }

        string normalizedLastFileWidgetId = string.IsNullOrWhiteSpace(settings.LastQuickCaptureFileWidgetId)
            ? string.Empty
            : settings.LastQuickCaptureFileWidgetId.Trim();
        if (!string.Equals(settings.LastQuickCaptureFileWidgetId, normalizedLastFileWidgetId, StringComparison.Ordinal))
        {
            settings.LastQuickCaptureFileWidgetId = normalizedLastFileWidgetId;
            changed = true;
        }

        if (settings.QuickCaptureDefaultView is not (
            QuickCaptureDefaultViewRecords or
            QuickCaptureDefaultViewPinned or
            QuickCaptureDefaultViewRecent))
        {
            settings.QuickCaptureDefaultView = QuickCaptureDefaultViewRecords;
            changed = true;
        }

        string normalizedTabStyle = NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
        if (!string.Equals(settings.QuickCaptureTabStyle, normalizedTabStyle, StringComparison.Ordinal))
        {
            settings.QuickCaptureTabStyle = normalizedTabStyle;
            changed = true;
        }

        if (!FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture))
        {
            if (settings.QuickCaptureClipboardEnabled)
            {
                settings.QuickCaptureClipboardEnabled = false;
                changed = true;
            }

            if (settings.QuickCaptureImageClipboardEnabled)
            {
                settings.QuickCaptureImageClipboardEnabled = false;
                changed = true;
            }
        }
        else if (!settings.QuickCaptureClipboardEnabled && settings.QuickCaptureImageClipboardEnabled)
        {
            settings.QuickCaptureImageClipboardEnabled = false;
            changed = true;
        }

        return changed;
    }

    internal static bool NormalizeTodoSettings(AppSettings settings)
    {
        bool changed = false;

        if (settings.TodoNewTaskPosition is not (TodoNewTaskPositionTop or TodoNewTaskPositionBottom))
        {
            settings.TodoNewTaskPosition = TodoNewTaskPositionTop;
            changed = true;
        }

        if (settings.TodoDefaultFilter is not (
            TodoDefaultFilterAll or
            TodoDefaultFilterToday or
            TodoDefaultFilterImportant or
            TodoDefaultFilterCompleted))
        {
            settings.TodoDefaultFilter = TodoDefaultFilterAll;
            changed = true;
        }

        int normalizedReminderOffset = NormalizeTodoReminderOffsetMinutes(settings.TodoDefaultReminderOffsetMinutes);
        if (settings.TodoDefaultReminderOffsetMinutes != normalizedReminderOffset)
        {
            settings.TodoDefaultReminderOffsetMinutes = normalizedReminderOffset;
            changed = true;
        }

        string normalizedTabStyle = NormalizeWidgetTabStyle(settings.TodoTabStyle);
        if (!string.Equals(settings.TodoTabStyle, normalizedTabStyle, StringComparison.Ordinal))
        {
            settings.TodoTabStyle = normalizedTabStyle;
            changed = true;
        }

        return changed;
    }

    public static string NormalizeWidgetTabStyle(string? style)
    {
        return style == WidgetTabStylePivot
            ? WidgetTabStylePivot
            : WidgetTabStyleButton;
    }

    public static int NormalizeTodoReminderOffsetMinutes(int minutes)
    {
        return minutes is 0 or 5 or 10 or 15 or 30 or 60 or 1440
            ? minutes
            : DefaultTodoReminderOffsetMinutes;
    }

    internal static bool NormalizeWeatherSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedTempUnit = settings.WeatherTemperatureUnit is WeatherTemperatureUnitFahrenheit
            ? WeatherTemperatureUnitFahrenheit
            : WeatherTemperatureUnitCelsius;
        if (!string.Equals(settings.WeatherTemperatureUnit, normalizedTempUnit, StringComparison.Ordinal))
        {
            settings.WeatherTemperatureUnit = normalizedTempUnit;
            changed = true;
        }

        string normalizedWindUnit = settings.WeatherWindSpeedUnit is WeatherWindSpeedUnitMs or WeatherWindSpeedUnitMph
            ? settings.WeatherWindSpeedUnit
            : WeatherWindSpeedUnitKmh;
        if (!string.Equals(settings.WeatherWindSpeedUnit, normalizedWindUnit, StringComparison.Ordinal))
        {
            settings.WeatherWindSpeedUnit = normalizedWindUnit;
            changed = true;
        }

        string normalizedView = settings.WeatherDefaultView is WeatherDefaultViewWeek
            ? WeatherDefaultViewWeek
            : WeatherDefaultViewToday;
        if (!string.Equals(settings.WeatherDefaultView, normalizedView, StringComparison.Ordinal))
        {
            settings.WeatherDefaultView = normalizedView;
            changed = true;
        }

        string normalizedSkin = settings.WeatherSkin is WeatherSkinRich
            ? WeatherSkinRich
            : WeatherSkinStandard;
        if (!string.Equals(settings.WeatherSkin, normalizedSkin, StringComparison.Ordinal))
        {
            settings.WeatherSkin = normalizedSkin;
            changed = true;
        }

        int clampedRefresh = Math.Clamp(
            settings.WeatherRefreshIntervalMinutes,
            WeatherRefreshMinMinutes,
            WeatherRefreshMaxMinutes);
        if (settings.WeatherRefreshIntervalMinutes != clampedRefresh)
        {
            settings.WeatherRefreshIntervalMinutes = clampedRefresh;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeDeletionSettings(AppSettings settings)
    {
        int beforeIds = settings.DeletedWidgetIds.Count;
        settings.DeletedWidgetIds = settings.DeletedWidgetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool changed = settings.DeletedWidgetIds.Count != beforeIds;

        int removed = settings.Widgets.RemoveAll(widget => settings.DeletedWidgetIds.Contains(widget.Id));
        if (removed > 0)
        {
            changed = true;
        }

        int staleRemoved = settings.Widgets.RemoveAll(widget => IsStaleHiddenWidget(settings, widget));
        if (staleRemoved > 0)
        {
            changed = true;
        }

        return changed;
    }

    private static bool IsStaleHiddenWidget(AppSettings settings, WidgetConfig widget)
    {
        if (widget.WidgetKind != WidgetKind.File ||
            widget.IsVisible ||
            widget.IsDisabled ||
            !string.IsNullOrEmpty(widget.MappedFolderPath))
        {
            return false;
        }

        bool hasGenericName =
            string.Equals(widget.Name, "New Widget", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "Deskbox", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "\u65B0\u5EFA\u7EC4\u4EF6", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "\u65B0\u5EFA\u5C0F\u7EC4\u4EF6", StringComparison.Ordinal);

        if (!hasGenericName)
        {
            return false;
        }

        return Math.Abs(widget.X - 100) < 0.01 &&
               Math.Abs(widget.Y - 100) < 0.01 &&
               Math.Abs(widget.Width - settings.DefaultWidgetWidth) < 0.01 &&
               Math.Abs(widget.Height - settings.DefaultWidgetHeight) < 0.01;
    }
}
