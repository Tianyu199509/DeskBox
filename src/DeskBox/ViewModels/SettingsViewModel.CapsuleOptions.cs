using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private bool _widgetCapsuleModeEnabled;
    private bool _widgetCompactHideSensitiveContent;
    private string _selectedWidgetCompactAnimationEffect = SettingsService.WidgetCompactAnimationSmooth;
    private string _selectedWidgetCompactMediaCornerMode = SettingsService.WidgetCompactMediaCornerFollowWidget;
    private double _widgetCompactAnimationDurationMs = SettingsService.DefaultWidgetCompactAnimationDurationMs;
    private double _widgetCompactExpandDelayMs = SettingsService.DefaultWidgetCompactExpandDelayMs;
    private double _widgetCompactCollapseDelayMs = SettingsService.DefaultWidgetCompactCollapseDelayMs;
    private string[]? _cachedWidgetCompactAnimationEffectDisplayNames;
    private string[]? _cachedWidgetCompactMediaCornerDisplayNames;

    public bool WidgetCapsuleModeEnabled
    {
        get => _widgetCapsuleModeEnabled;
        set
        {
            if (!SetProperty(ref _widgetCapsuleModeEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSmartWidgetCollapseBehavior));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCapsuleModeEnabled = value;
            if (value &&
                SettingsService.NormalizeWidgetCollapseBehavior(_settingsService.Settings.WidgetCollapseBehavior) ==
                SettingsService.WidgetCollapseBehaviorExpanded)
            {
                _settingsService.Settings.WidgetCollapseBehavior = SelectedWidgetCollapseBehavior;
            }
            _settingsService.SaveDebounced();
        }
    }

    public bool IsSmartWidgetCollapseBehavior =>
        WidgetCapsuleModeEnabled &&
        SelectedWidgetCollapseBehavior == SettingsService.WidgetCollapseBehaviorSmart;

    public bool WidgetCompactHideSensitiveContent
    {
        get => _widgetCompactHideSensitiveContent;
        set
        {
            if (!SetProperty(ref _widgetCompactHideSensitiveContent, value))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactHideSensitiveContent = value;
            _settingsService.SaveDebounced();
        }
    }

    public string[] AvailableWidgetCompactAnimationEffects { get; } =
    [
        SettingsService.WidgetCompactAnimationSmooth,
        SettingsService.WidgetCompactAnimationSlow,
        SettingsService.WidgetCompactAnimationSnappy,
        SettingsService.WidgetCompactAnimationNone
    ];

    public string[] AvailableWidgetCompactAnimationEffectDisplayNames =>
        _cachedWidgetCompactAnimationEffectDisplayNames ??=
            AvailableWidgetCompactAnimationEffects.Select(GetWidgetCompactAnimationEffectDisplayName).ToArray();

    public string SelectedWidgetCompactAnimationEffect
    {
        get => _selectedWidgetCompactAnimationEffect;
        set
        {
            string normalized = SettingsService.NormalizeWidgetCompactAnimationEffect(value);
            if (!SetProperty(ref _selectedWidgetCompactAnimationEffect, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectText));
            OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectIndex));
            OnPropertyChanged(nameof(IsWidgetCompactAnimationEnabled));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactAnimationEffect = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedWidgetCompactAnimationEffectText =>
        GetWidgetCompactAnimationEffectDisplayName(SelectedWidgetCompactAnimationEffect);

    public int SelectedWidgetCompactAnimationEffectIndex =>
        Array.IndexOf(AvailableWidgetCompactAnimationEffects, _selectedWidgetCompactAnimationEffect);

    public bool IsWidgetCompactAnimationEnabled =>
        SelectedWidgetCompactAnimationEffect != SettingsService.WidgetCompactAnimationNone;

    public double WidgetCompactAnimationDurationMs
    {
        get => _widgetCompactAnimationDurationMs;
        set
        {
            int normalized = SettingsService.NormalizeWidgetCompactAnimationDurationMs((int)Math.Round(value));
            if (!SetProperty(ref _widgetCompactAnimationDurationMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetCompactAnimationDurationText));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactAnimationDurationMs = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string WidgetCompactAnimationDurationText => $"{Math.Round(WidgetCompactAnimationDurationMs):0} ms";

    public double WidgetCompactExpandDelayMs
    {
        get => _widgetCompactExpandDelayMs;
        set
        {
            int normalized = SettingsService.NormalizeWidgetCompactExpandDelayMs((int)Math.Round(value));
            if (!SetProperty(ref _widgetCompactExpandDelayMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetCompactExpandDelayText));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactExpandDelayMs = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string WidgetCompactExpandDelayText => $"{Math.Round(WidgetCompactExpandDelayMs):0} ms";

    public double WidgetCompactCollapseDelayMs
    {
        get => _widgetCompactCollapseDelayMs;
        set
        {
            int normalized = SettingsService.NormalizeWidgetCompactCollapseDelayMs((int)Math.Round(value));
            if (!SetProperty(ref _widgetCompactCollapseDelayMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetCompactCollapseDelayText));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactCollapseDelayMs = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string WidgetCompactCollapseDelayText => $"{Math.Round(WidgetCompactCollapseDelayMs):0} ms";

    public string[] AvailableWidgetCompactMediaCornerModes { get; } =
    [
        SettingsService.WidgetCompactMediaCornerFollowWidget,
        SettingsService.WidgetCompactMediaCornerSquare,
        SettingsService.WidgetCompactMediaCornerSmall,
        SettingsService.WidgetCompactMediaCornerRound
    ];

    public string[] AvailableWidgetCompactMediaCornerDisplayNames =>
        _cachedWidgetCompactMediaCornerDisplayNames ??=
            AvailableWidgetCompactMediaCornerModes.Select(GetWidgetCompactMediaCornerDisplayName).ToArray();

    public string SelectedWidgetCompactMediaCornerMode
    {
        get => _selectedWidgetCompactMediaCornerMode;
        set
        {
            string normalized = SettingsService.NormalizeWidgetCompactMediaCornerMode(value);
            if (!SetProperty(ref _selectedWidgetCompactMediaCornerMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedWidgetCompactMediaCornerText));
            OnPropertyChanged(nameof(SelectedWidgetCompactMediaCornerIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.WidgetCompactMediaCornerMode = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedWidgetCompactMediaCornerText =>
        GetWidgetCompactMediaCornerDisplayName(SelectedWidgetCompactMediaCornerMode);

    public int SelectedWidgetCompactMediaCornerIndex =>
        Array.IndexOf(AvailableWidgetCompactMediaCornerModes, _selectedWidgetCompactMediaCornerMode);

    private string GetWidgetCompactAnimationEffectDisplayName(string effect) =>
        SettingsService.NormalizeWidgetCompactAnimationEffect(effect) switch
        {
            SettingsService.WidgetCompactAnimationSnappy => _localizationService.T("Settings.Capsule.Animation.Snappy"),
            SettingsService.WidgetCompactAnimationSlow => _localizationService.T("Settings.Capsule.Animation.Slow"),
            SettingsService.WidgetCompactAnimationNone => _localizationService.T("Settings.Capsule.Animation.None"),
            _ => _localizationService.T("Settings.Capsule.Animation.Smooth")
        };

    private string GetWidgetCompactMediaCornerDisplayName(string mode) =>
        SettingsService.NormalizeWidgetCompactMediaCornerMode(mode) switch
        {
            SettingsService.WidgetCompactMediaCornerSquare => _localizationService.T("Settings.Capsule.MediaCorner.Square"),
            SettingsService.WidgetCompactMediaCornerSmall => _localizationService.T("Settings.Capsule.MediaCorner.Small"),
            SettingsService.WidgetCompactMediaCornerRound => _localizationService.T("Settings.Capsule.MediaCorner.Round"),
            _ => _localizationService.T("Settings.Capsule.MediaCorner.FollowWidget")
        };
}
