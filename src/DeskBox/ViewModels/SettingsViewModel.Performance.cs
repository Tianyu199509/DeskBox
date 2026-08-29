using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _selectedPerformanceMode =
        PerformanceSettingsPolicy.DefaultMode;
    private int _selectedHiddenCacheCleanupDelaySeconds =
        PerformanceSettingsPolicy.DefaultHiddenCacheCleanupDelaySeconds;
    private int _selectedVisibleIdleCacheCleanupDelaySeconds =
        PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds;
    private int _selectedTransientWindowReleaseDelaySeconds =
        PerformanceSettingsPolicy.DefaultTransientWindowReleaseDelaySeconds;
    private string _selectedPerformanceCacheBudget =
        PerformanceSettingsPolicy.DefaultCacheBudget;
    private bool _enableTextMarqueeAnimations =
        PerformanceSettingsPolicy.DefaultTextMarqueeAnimationsEnabled;
    private bool _enableVinylRotationAnimations =
        PerformanceSettingsPolicy.DefaultVinylRotationAnimationsEnabled;
    private bool _enableGlanceImageAutoRotation =
        PerformanceSettingsPolicy.DefaultGlanceImageAutoRotationEnabled;
    private bool _enableCompactAmbientAnimations =
        PerformanceSettingsPolicy.DefaultCompactAmbientAnimationsEnabled;

    public IReadOnlyList<SettingsOption> AvailablePerformanceModeOptions
    {
        get
        {
            var options = new List<SettingsOption>
            {
                new(
                    PerformanceSettingsPolicy.ModeBalanced,
                    _localizationService.T("Settings.Performance.Mode.Balanced")),
                new(
                    PerformanceSettingsPolicy.ModeResourceSaver,
                    _localizationService.T("Settings.Performance.Mode.ResourceSaver"))
            };
            if (string.Equals(
                    _selectedPerformanceMode,
                    PerformanceSettingsPolicy.ModeCustom,
                    StringComparison.Ordinal))
            {
                options.Add(new(
                    PerformanceSettingsPolicy.ModeCustom,
                    _localizationService.T("Settings.Performance.Mode.Custom")));
            }

            return options;
        }
    }

    public IReadOnlyList<SettingsOption>
        AvailableHiddenCacheCleanupDelayOptions =>
    [
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            "Settings.Performance.HiddenCleanup.30Seconds"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter1Minute,
            "Settings.Performance.HiddenCleanup.1Minute"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter5Minutes,
            "Settings.Performance.HiddenCleanup.5Minutes")
    ];

    public IReadOnlyList<SettingsOption>
        AvailableVisibleIdleCacheCleanupDelayOptions =>
    [
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            "Settings.Performance.HiddenCleanup.30Seconds"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter1Minute,
            "Settings.Performance.HiddenCleanup.1Minute"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter5Minutes,
            "Settings.Performance.HiddenCleanup.5Minutes"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            "Settings.Performance.HiddenCleanup.10Minutes"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter15Minutes,
            "Settings.Performance.HiddenCleanup.15Minutes")
    ];

    public IReadOnlyList<SettingsOption>
        AvailableTransientWindowReleaseDelayOptions =>
    [
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            "Settings.Performance.HiddenCleanup.30Seconds"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter1Minute,
            "Settings.Performance.HiddenCleanup.1Minute"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter2Minutes,
            "Settings.Performance.HiddenCleanup.2Minutes"),
        CreateCleanupDelayOption(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            "Settings.Performance.HiddenCleanup.10Minutes")
    ];

    public IReadOnlyList<SettingsOption> AvailablePerformanceCacheBudgetOptions =>
    [
        new(
            PerformanceSettingsPolicy.CacheBudgetSmall,
            _localizationService.T("Settings.Performance.CacheBudget.Small")),
        new(
            PerformanceSettingsPolicy.CacheBudgetBalanced,
            _localizationService.T("Settings.Performance.CacheBudget.Balanced")),
        new(
            PerformanceSettingsPolicy.CacheBudgetLarge,
            _localizationService.T("Settings.Performance.CacheBudget.Large"))
    ];

    public string SelectedPerformanceMode
    {
        get => _selectedPerformanceMode;
        set
        {
            string normalized = PerformanceSettingsPolicy.NormalizeMode(value);
            if (!SetProperty(ref _selectedPerformanceMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AvailablePerformanceModeOptions));

            if (_isRestoringDefaults)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            PerformanceSettingsPolicy.ApplyPreset(settings, normalized);
            SynchronizePerformanceDetailSelection(settings);
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedHiddenCacheCleanupDelaySeconds
    {
        get => _selectedHiddenCacheCleanupDelaySeconds;
        set
        {
            int normalized =
                PerformanceSettingsPolicy
                    .NormalizeHiddenCacheCleanupDelaySeconds(value);
            if (!SetProperty(
                    ref _selectedHiddenCacheCleanupDelaySeconds,
                    normalized))
            {
                return;
            }

            UpdateCustomPerformanceSetting(settings =>
                settings.HiddenCacheCleanupDelaySeconds = normalized);
        }
    }

    public int SelectedVisibleIdleCacheCleanupDelaySeconds
    {
        get => _selectedVisibleIdleCacheCleanupDelaySeconds;
        set
        {
            int normalized = PerformanceSettingsPolicy
                .NormalizeVisibleIdleCacheCleanupDelaySeconds(value);
            if (!SetProperty(
                    ref _selectedVisibleIdleCacheCleanupDelaySeconds,
                    normalized))
            {
                return;
            }

            UpdateCustomPerformanceSetting(settings =>
                settings.VisibleIdleCacheCleanupDelaySeconds = normalized);
        }
    }

    public int SelectedTransientWindowReleaseDelaySeconds
    {
        get => _selectedTransientWindowReleaseDelaySeconds;
        set
        {
            int normalized = PerformanceSettingsPolicy
                .NormalizeTransientWindowReleaseDelaySeconds(value);
            if (!SetProperty(
                    ref _selectedTransientWindowReleaseDelaySeconds,
                    normalized))
            {
                return;
            }

            UpdateCustomPerformanceSetting(settings =>
                settings.TransientWindowReleaseDelaySeconds = normalized);
        }
    }

    public string SelectedPerformanceCacheBudget
    {
        get => _selectedPerformanceCacheBudget;
        set
        {
            string normalized =
                PerformanceSettingsPolicy.NormalizeCacheBudget(value);
            if (!SetProperty(ref _selectedPerformanceCacheBudget, normalized))
            {
                return;
            }

            UpdateCustomPerformanceSetting(settings =>
                settings.PerformanceCacheBudget = normalized);
        }
    }

    public IReadOnlyList<string> AvailableContinuousDecorativeAnimationOptions =>
        PerformanceSettingsPolicy.SupportedDecorativeAnimationOptions;

    public string ContinuousDecorativeAnimationsSummaryText
    {
        get
        {
            string[] selected = AvailableContinuousDecorativeAnimationOptions
                .Where(IsContinuousDecorativeAnimationSelected)
                .ToArray();
            if (selected.Length == 0)
            {
                return _localizationService.T("Common.Off");
            }

            if (selected.Length == AvailableContinuousDecorativeAnimationOptions.Count)
            {
                return _localizationService.T(
                    "Settings.Performance.DecorativeAnimations.All");
            }

            return string.Join(
                _localizationService.IsChinese ? "、" : ", ",
                selected.Select(GetContinuousDecorativeAnimationDisplayName));
        }
    }

    public bool IsContinuousDecorativeAnimationSelected(string option) =>
        option switch
        {
            PerformanceSettingsPolicy.DecorativeAnimationTextMarquee =>
                _enableTextMarqueeAnimations,
            PerformanceSettingsPolicy.DecorativeAnimationVinylRotation =>
                _enableVinylRotationAnimations,
            PerformanceSettingsPolicy.DecorativeAnimationGlanceRotation =>
                _enableGlanceImageAutoRotation,
            PerformanceSettingsPolicy.DecorativeAnimationCompactAmbient =>
                _enableCompactAmbientAnimations,
            _ => false
        };

    public string GetContinuousDecorativeAnimationDisplayName(string option) =>
        option switch
        {
            PerformanceSettingsPolicy.DecorativeAnimationTextMarquee =>
                _localizationService.T(
                    "Settings.Performance.DecorativeAnimations.Marquee"),
            PerformanceSettingsPolicy.DecorativeAnimationVinylRotation =>
                _localizationService.T(
                    "Settings.Performance.DecorativeAnimations.Vinyl"),
            PerformanceSettingsPolicy.DecorativeAnimationGlanceRotation =>
                _localizationService.T(
                    "Settings.Performance.DecorativeAnimations.GlanceRotation"),
            PerformanceSettingsPolicy.DecorativeAnimationCompactAmbient =>
                _localizationService.T(
                    "Settings.Performance.DecorativeAnimations.Ambient"),
            _ => option
        };

    public void ToggleContinuousDecorativeAnimation(string option)
    {
        switch (option)
        {
            case PerformanceSettingsPolicy.DecorativeAnimationTextMarquee:
                _enableTextMarqueeAnimations = !_enableTextMarqueeAnimations;
                break;
            case PerformanceSettingsPolicy.DecorativeAnimationVinylRotation:
                _enableVinylRotationAnimations = !_enableVinylRotationAnimations;
                break;
            case PerformanceSettingsPolicy.DecorativeAnimationGlanceRotation:
                _enableGlanceImageAutoRotation = !_enableGlanceImageAutoRotation;
                break;
            case PerformanceSettingsPolicy.DecorativeAnimationCompactAmbient:
                _enableCompactAmbientAnimations = !_enableCompactAmbientAnimations;
                break;
            default:
                return;
        }

        OnPropertyChanged(nameof(ContinuousDecorativeAnimationsSummaryText));
        UpdateCustomPerformanceSetting(settings =>
        {
            settings.EnableTextMarqueeAnimations = _enableTextMarqueeAnimations;
            settings.EnableVinylRotationAnimations = _enableVinylRotationAnimations;
            settings.EnableGlanceImageAutoRotation = _enableGlanceImageAutoRotation;
            settings.EnableCompactAmbientAnimations = _enableCompactAmbientAnimations;
            settings.EnableContinuousDecorativeAnimations =
                _enableTextMarqueeAnimations &&
                _enableVinylRotationAnimations &&
                _enableGlanceImageAutoRotation &&
                _enableCompactAmbientAnimations;
        });
    }

    private void InitializePerformanceSettings(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        _selectedPerformanceMode = effective.Mode;
        _selectedHiddenCacheCleanupDelaySeconds =
            effective.HiddenCacheCleanupDelaySeconds;
        _selectedVisibleIdleCacheCleanupDelaySeconds =
            effective.VisibleIdleCacheCleanupDelaySeconds;
        _selectedTransientWindowReleaseDelaySeconds =
            effective.TransientWindowReleaseDelaySeconds;
        _selectedPerformanceCacheBudget = effective.CacheBudget;
        ApplyContinuousDecorativeAnimationSelection(effective);
    }

    private void ApplyPerformanceSettingsSnapshot(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        SelectedPerformanceMode = effective.Mode;
        SelectedHiddenCacheCleanupDelaySeconds =
            effective.HiddenCacheCleanupDelaySeconds;
        SelectedVisibleIdleCacheCleanupDelaySeconds =
            effective.VisibleIdleCacheCleanupDelaySeconds;
        SelectedTransientWindowReleaseDelaySeconds =
            effective.TransientWindowReleaseDelaySeconds;
        SelectedPerformanceCacheBudget = effective.CacheBudget;
        ApplyContinuousDecorativeAnimationSelection(effective);
    }

    private void RefreshPerformanceSelectionProperties(
        bool refreshLocalizedOptions)
    {
        if (!refreshLocalizedOptions)
        {
            return;
        }

        OnPropertyChanged(nameof(AvailablePerformanceModeOptions));
        OnPropertyChanged(nameof(AvailableHiddenCacheCleanupDelayOptions));
        OnPropertyChanged(nameof(AvailableVisibleIdleCacheCleanupDelayOptions));
        OnPropertyChanged(nameof(AvailableTransientWindowReleaseDelayOptions));
        OnPropertyChanged(nameof(AvailablePerformanceCacheBudgetOptions));
        OnPropertyChanged(nameof(ContinuousDecorativeAnimationsSummaryText));
    }

    private void SynchronizePerformanceDetailSelection(AppSettings settings)
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);
        SynchronizePerformanceProperty(
            ref _selectedHiddenCacheCleanupDelaySeconds,
            effective.HiddenCacheCleanupDelaySeconds,
            nameof(SelectedHiddenCacheCleanupDelaySeconds));
        SynchronizePerformanceProperty(
            ref _selectedVisibleIdleCacheCleanupDelaySeconds,
            effective.VisibleIdleCacheCleanupDelaySeconds,
            nameof(SelectedVisibleIdleCacheCleanupDelaySeconds));
        SynchronizePerformanceProperty(
            ref _selectedTransientWindowReleaseDelaySeconds,
            effective.TransientWindowReleaseDelaySeconds,
            nameof(SelectedTransientWindowReleaseDelaySeconds));
        SynchronizePerformanceProperty(
            ref _selectedPerformanceCacheBudget,
            effective.CacheBudget,
            nameof(SelectedPerformanceCacheBudget));
        ApplyContinuousDecorativeAnimationSelection(effective);
    }

    private void UpdateCustomPerformanceSetting(Action<AppSettings> update)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        AppSettings settings = _settingsService.Settings;
        update(settings);
        SwitchPerformanceModeToCustom(settings);
        _settingsService.SaveDebounced();
    }

    private void SwitchPerformanceModeToCustom(AppSettings settings)
    {
        settings.PerformanceMode = PerformanceSettingsPolicy.ModeCustom;
        if (string.Equals(
                _selectedPerformanceMode,
                PerformanceSettingsPolicy.ModeCustom,
                StringComparison.Ordinal))
        {
            return;
        }

        _selectedPerformanceMode = PerformanceSettingsPolicy.ModeCustom;
        OnPropertyChanged(nameof(AvailablePerformanceModeOptions));
        OnPropertyChanged(nameof(SelectedPerformanceMode));
    }

    private void SynchronizePerformanceProperty<T>(
        ref T field,
        T value,
        string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private SettingsOption CreateCleanupDelayOption(int value, string key) =>
        new(value, _localizationService.T(key));

    private void ApplyContinuousDecorativeAnimationSelection(
        EffectivePerformanceSettings effective)
    {
        bool changed =
            _enableTextMarqueeAnimations != effective.AllowTextMarqueeAnimations ||
            _enableVinylRotationAnimations != effective.AllowVinylRotationAnimations ||
            _enableGlanceImageAutoRotation != effective.AllowGlanceImageAutoRotation ||
            _enableCompactAmbientAnimations != effective.AllowCompactAmbientAnimations;
        _enableTextMarqueeAnimations = effective.AllowTextMarqueeAnimations;
        _enableVinylRotationAnimations = effective.AllowVinylRotationAnimations;
        _enableGlanceImageAutoRotation = effective.AllowGlanceImageAutoRotation;
        _enableCompactAmbientAnimations = effective.AllowCompactAmbientAnimations;
        if (changed)
        {
            OnPropertyChanged(nameof(ContinuousDecorativeAnimationsSummaryText));
        }
    }
}
