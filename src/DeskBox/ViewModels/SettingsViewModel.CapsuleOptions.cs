using CommunityToolkit.Mvvm.Input;
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
        SettingsService.WidgetCompactAnimationCustom,
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
            int? presetDuration = normalized switch
            {
                SettingsService.WidgetCompactAnimationSmooth =>
                    SettingsService.DefaultWidgetCompactAnimationDurationMs,
                SettingsService.WidgetCompactAnimationSlow =>
                    SettingsService.SlowWidgetCompactAnimationDurationMs,
                SettingsService.WidgetCompactAnimationSnappy =>
                    SettingsService.SnappyWidgetCompactAnimationDurationMs,
                _ => null
            };
            if (presetDuration is { } duration &&
                SetProperty(
                    ref _widgetCompactAnimationDurationMs,
                    duration,
                    nameof(WidgetCompactAnimationDurationMs)))
            {
                _settingsService.Settings.WidgetCompactAnimationDurationMs = duration;
                OnPropertyChanged(nameof(WidgetCompactAnimationDurationText));
            }
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

            if (SelectedWidgetCompactAnimationEffect is not
                (SettingsService.WidgetCompactAnimationCustom or
                 SettingsService.WidgetCompactAnimationNone))
            {
                _selectedWidgetCompactAnimationEffect = SettingsService.WidgetCompactAnimationCustom;
                _settingsService.Settings.WidgetCompactAnimationEffect =
                    SettingsService.WidgetCompactAnimationCustom;
                OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffect));
                OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectText));
                OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectIndex));
                OnPropertyChanged(nameof(IsWidgetCompactAnimationEnabled));
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

    public int CapsuleCustomRuleCount => _settingsService.Settings.Widgets.Count(widget =>
        widget.Metadata?.ContainsKey(WidgetCollapseBehaviorNames.MetadataKey) == true);

    public int CapsuleCustomWidthCount =>
        _settingsService.Settings.Widgets.Count(widget => widget.CompactWidth is not null);

    public int CapsuleSavedPlacementCount =>
        _settingsService.Settings.Widgets.Count(widget => widget.CompactPlacement is not null);

    public bool HasCapsuleBehaviorOverrides => CapsuleCustomRuleCount > 0;

    public bool HasCapsuleGeometryOverrides =>
        CapsuleCustomWidthCount > 0 || CapsuleSavedPlacementCount > 0;

    public string CapsuleOverrideSummaryText => _localizationService.Format(
        "Settings.Capsule.Overrides.Summary",
        CapsuleCustomRuleCount,
        CapsuleCustomWidthCount,
        CapsuleSavedPlacementCount);

    public string CapsuleBehaviorOverrideSummaryText => _localizationService.Format(
        "Settings.Capsule.Overrides.Behavior.Summary",
        CapsuleCustomRuleCount);

    public string CapsuleGeometryOverrideSummaryText => _localizationService.Format(
        "Settings.Capsule.Overrides.Geometry.Summary",
        CapsuleCustomWidthCount,
        CapsuleSavedPlacementCount);

    [RelayCommand]
    private void ResetCapsuleBehaviorOverrides()
    {
        int changed = 0;
        foreach (var widget in _settingsService.Settings.Widgets)
        {
            if (widget.Metadata?.Remove(WidgetCollapseBehaviorNames.MetadataKey) == true)
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
            NotifyCapsuleOverridePropertiesChanged();
        }
    }

    [RelayCommand]
    private void ResetCapsuleGeometryOverrides()
    {
        int changed = 0;
        foreach (var widget in _settingsService.Settings.Widgets)
        {
            if (widget.CompactWidth is not null)
            {
                widget.CompactWidth = null;
                changed++;
            }

            if (widget.CompactPlacement is not null)
            {
                widget.CompactPlacement = null;
                changed++;
            }
        }

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
            NotifyCapsuleOverridePropertiesChanged();
        }
    }

    private void NotifyCapsuleOverridePropertiesChanged()
    {
        OnPropertyChanged(nameof(CapsuleCustomRuleCount));
        OnPropertyChanged(nameof(CapsuleCustomWidthCount));
        OnPropertyChanged(nameof(CapsuleSavedPlacementCount));
        OnPropertyChanged(nameof(HasCapsuleBehaviorOverrides));
        OnPropertyChanged(nameof(HasCapsuleGeometryOverrides));
        OnPropertyChanged(nameof(CapsuleOverrideSummaryText));
        OnPropertyChanged(nameof(CapsuleBehaviorOverrideSummaryText));
        OnPropertyChanged(nameof(CapsuleGeometryOverrideSummaryText));
        ResetCapsuleBehaviorOverridesCommand.NotifyCanExecuteChanged();
        ResetCapsuleGeometryOverridesCommand.NotifyCanExecuteChanged();
    }

    private string GetWidgetCompactAnimationEffectDisplayName(string effect) =>
        SettingsService.NormalizeWidgetCompactAnimationEffect(effect) switch
        {
            SettingsService.WidgetCompactAnimationSnappy => _localizationService.T("Settings.Capsule.Animation.Snappy"),
            SettingsService.WidgetCompactAnimationSlow => _localizationService.T("Settings.Capsule.Animation.Slow"),
            SettingsService.WidgetCompactAnimationCustom => _localizationService.T("Settings.Capsule.Animation.Custom"),
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
