using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using DeskBox.Helpers;
using DeskBox.Models;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox.Services;

/// <summary>
/// Manages application settings persistence using JSON files stored in the application directory.
/// </summary>
public sealed class SettingsService
{
    public const double DefaultWidgetOpacity = 0.30;
    public const double MinWidgetOpacity = 0.0;
    public const double MaxWidgetOpacity = 1.0;
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
    public const string WidgetAnimationSpeedVeryFast = "VeryFast";
    public const string WidgetAnimationSpeedFast = "Fast";
    public const string WidgetAnimationSpeedStandard = "Standard";
    public const string WidgetAnimationSpeedRelaxed = "Relaxed";
    public const string WidgetAnimationSpeedSlow = "Slow";
    public const string ManagedDropActionMove = "Move";
    public const string ManagedDropActionCopy = "Copy";
    public const string LanguageSystem = "System";
    public const string LanguageChinese = "zh-CN";
    public const string LanguageEnglish = "en-US";
    public const double DefaultWidgetWidth = 280;
    public const double DefaultWidgetHeight = 400;
    public const bool DefaultGlobalHotkeyEnabled = false;
    public const int DefaultGlobalHotkeyModifiers = (int)Models.HotkeyModifierKeys.None;
    public const int DefaultGlobalHotkeyKey = (int)Windows.System.VirtualKey.F7;
    public const double MinWidgetWidth = 200;
    public const double MinWidgetHeight = 200;
    public const double DefaultIconSize = 30;
    public const double MinIconSize = 24;
    public const double MaxIconSize = 56;
    public const double DefaultTextSize = 11;
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
    public const int MaxRecentOrganizationHistoryCount = 24;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _appearancePreviewCts;

    public event Action? SettingsChanged;
    public event Action? AppearancePreviewChanged;

    public AppSettings Settings
    {
        get { lock (_lock) return _settings; }
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

            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
                if (loaded is not null)
                {
                    lock (_lock) _settings = loaded;
                }
            }

            bool changed;
            lock (_lock)
            {
                changed = NormalizePresentationSettings(_settings);
                changed |= NormalizeAppearanceSettings(_settings);
                changed |= NormalizeWidgetContentSettings(_settings);
                changed |= NormalizeOrganizerSettings(_settings);
                changed |= NormalizeHotkeySettings(_settings);
                changed |= NormalizeQuickCaptureSettings(_settings);
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
    public async Task SaveAsync()
    {
        await SaveToFileOnlyAsync();
        SettingsChanged?.Invoke();
    }

    private async Task SaveToFileOnlyAsync()
    {
        try
        {
            string json;
            lock (_lock)
            {
                NormalizePresentationSettings(_settings);
                NormalizeOrganizerSettings(_settings);
                NormalizeHotkeySettings(_settings);
                NormalizeQuickCaptureSettings(_settings);
                json = JsonSerializer.Serialize(_settings, s_jsonOptions);
            }
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
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

        _debounceCts?.Cancel();
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
        });
    }

    public void RequestAppearancePreview()
    {
        _appearancePreviewCts?.Cancel();
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
            settings.WidgetCornerPreference = WidgetCornerPreferenceSmall;
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
            WidgetAnimationEffectSlideFade))
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
            legacyLayoutDensityScale = string.Equals(settings.LayoutDensity, "Compact", StringComparison.OrdinalIgnoreCase)
                ? DefaultLayoutDensityScale
                : DefaultLayoutDensityScale;
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

        if (settings.LayoutDensity is not ("Comfortable" or "Compact"))
        {
            settings.LayoutDensity = "Comfortable";
            changed = true;
        }
        else
        {
            string normalizedLegacyDensity = settings.LayoutDensityScale <= 0.78 ? "Compact" : "Comfortable";
            if (!string.Equals(settings.LayoutDensity, normalizedLegacyDensity, StringComparison.Ordinal))
            {
                settings.LayoutDensity = normalizedLegacyDensity;
                changed = true;
            }
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
            ? Math.Clamp(settings.DefaultWidgetHeight, DefaultWidgetHeight, 1200)
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
            if (widget.WidgetKind is not (WidgetKind.File or WidgetKind.QuickCapture))
            {
                widget.WidgetKind = WidgetKind.File;
                changed = true;
            }

            if (widget.IsDisabled)
            {
                widget.IsDisabled = false;
                changed = true;
            }
        }

        return changed;
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

        if (settings.ManagedDropAction is not (ManagedDropActionMove or ManagedDropActionCopy))
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
        if (widget.IsVisible ||
            widget.IsDisabled ||
            !string.IsNullOrEmpty(widget.MappedFolderPath))
        {
            return false;
        }

        bool hasGenericName =
            string.Equals(widget.Name, "New Widget", StringComparison.Ordinal) ||
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
