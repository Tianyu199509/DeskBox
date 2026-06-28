using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
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
    private const string TrayIconStyleSystem = "System";
    private const string TrayIconStyleColorful = "Colorful";
    private const string TrayIconStyleBlack = "Black";
    private const string TrayIconStyleWhite = "White";
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
    private string _selectedTrayIconStyle = TrayIconStyleSystem;
    private string _selectedLanguage = SettingsService.LanguageSystem;
    private string _selectedManagedDropAction = SettingsService.ManagedDropActionMove;
    private string _selectedWidgetCornerPreference = CornerSmall;
    private string _selectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectFade;
    private string _selectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
    private string _selectedWidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
    private string _selectedWidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
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

    private string[]? _cachedTrayIconStyleDisplayNames;
    private string[]? _cachedThemeDisplayNames;
    private string[]? _cachedLanguageDisplayNames;
    private string[]? _cachedManagedDropActionDisplayNames;
    private string[]? _cachedWidgetCornerPreferenceDisplayNames;
    private string[]? _cachedWidgetAnimationEffectDisplayNames;
    private string[]? _cachedWidgetAnimationSpeedDisplayNames;
    private string[]? _cachedWidgetAnimationSlideDirectionDisplayNames;
    private string[]? _cachedWidgetAnimationEasingIntensityDisplayNames;

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _doubleClickToOpen;
    [ObservableProperty] private double _defaultWidth;
    [ObservableProperty] private double _defaultHeight;
    [ObservableProperty] private bool _hideShortcutArrowOverlay;
    [ObservableProperty] private bool _showHoverButtons = true;
    [ObservableProperty] private bool _showListItemDetails;
    [ObservableProperty] private double _widgetOpacity = SettingsService.DefaultWidgetOpacity;
    [ObservableProperty] private double _iconSize = SettingsService.DefaultIconSize;
    [ObservableProperty] private double _textSize = SettingsService.DefaultTextSize;
    [ObservableProperty] private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    [ObservableProperty] private double _horizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
    [ObservableProperty] private double _verticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
    [ObservableProperty] private double _fileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
    [ObservableProperty] private bool _showFileExtensions;
    [ObservableProperty] private bool _hideShortcutExtensionWhenShowingFileExtensions = true;
    [ObservableProperty] private bool _quickCaptureEnabled;
    [ObservableProperty] private bool _quickCaptureClipboardEnabled;
    [ObservableProperty] private bool _quickCaptureImageClipboardEnabled = true;
    [ObservableProperty] private int _quickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;

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
            RefreshLocalizedProperties();
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
    public int SelectedManagedDropActionIndex => Array.IndexOf(AvailableManagedDropActions, _selectedManagedDropAction);

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
            OnPropertyChanged(nameof(IsDirectionEnabled));
            OnPropertyChanged(nameof(IsEasingEnabled));
            OnPropertyChanged(nameof(IsSpeedEnabled));
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
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
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
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionText));
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
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityText));
        }
    }

    public string SelectedWidgetAnimationEasingIntensityText => GetWidgetAnimationEasingIntensityDisplayName(SelectedWidgetAnimationEasingIntensity);
    public int SelectedWidgetAnimationEasingIntensityIndex => Array.IndexOf(AvailableWidgetAnimationEasingIntensities, _selectedWidgetAnimationEasingIntensity);

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

    public string GlobalHotkeyDescription => _localizationService.T("Settings.GlobalHotkey.Description");
    public bool CanShowGlobalHotkeyWarning => GlobalHotkeyEnabled && GlobalHotkeyService.IsRiskyGesture(GetCurrentGlobalHotkeyGesture());
    public string DragDropPermissionSummaryText => GetDragDropPermissionSummaryText();
    public string DragDropPermissionDetailText => GetDragDropPermissionDetailText();
    public string DragDropPermissionSeverityKind => _dragDropPermissionDiagnostic?.Severity switch
    {
        DragDropDiagnosticSeverity.Warning => "Warning",
        DragDropDiagnosticSeverity.Error => "Error",
        _ => "Normal"
    };
    public string DragDropPermissionProcessText => _dragDropPermissionDiagnostic?.CurrentProcessIntegrity ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionExplorerText => _dragDropPermissionDiagnostic?.ExplorerIntegrity ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionUacText => _dragDropPermissionDiagnostic?.UacStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionAppCompatText => _dragDropPermissionDiagnostic?.AppCompatStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionStartupText => _dragDropPermissionDiagnostic?.StartupStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionShortcutText => _dragDropPermissionDiagnostic?.ShortcutStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionRepairStatusText
    {
        get => _dragDropPermissionRepairStatusText;
        private set => SetProperty(ref _dragDropPermissionRepairStatusText, value);
    }
    public bool IsDragDropPermissionRepairing
    {
        get => _isDragDropPermissionRepairing;
        private set
        {
            if (!SetProperty(ref _isDragDropPermissionRepairing, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRepairDragDropPermission));
        }
    }
    public bool CanRepairDragDropPermission =>
        !IsDragDropPermissionRepairing &&
        _dragDropPermissionDiagnostic is not null &&
        (_dragDropPermissionDiagnostic.HasAppCompatIssue ||
         _dragDropPermissionDiagnostic.HasStartupIssue ||
         _dragDropPermissionDiagnostic.HasShortcutIssue ||
         _dragDropPermissionDiagnostic.NeedsRelaunch);
    public string QuickCaptureStatusText => QuickCaptureEnabled
        ? _localizationService.T("Settings.QuickCapture.Status.Enabled")
        : _localizationService.T("Settings.QuickCapture.Status.Disabled");
    public string QuickCaptureDependencyStatusText => QuickCaptureEnabled
        ? _localizationService.T("Settings.QuickCapture.Dependency.Enabled")
        : _localizationService.T("Settings.QuickCapture.Dependency.Disabled");
    public string QuickCaptureRecentLimitText => _localizationService.Format("Settings.QuickCapture.RecentLimitValue", QuickCaptureRecentLimit);
    public string QuickCaptureClipboardDiagnosticsText
    {
        get => _quickCaptureClipboardDiagnosticsText;
        private set => SetProperty(ref _quickCaptureClipboardDiagnosticsText, value);
    }

    public string QuickCaptureRecentLimitInput
    {
        get => QuickCaptureRecentLimit.ToString(CultureInfo.CurrentCulture);
        set => ApplyQuickCaptureRecentLimitInput(value);
    }
    public string QuickCaptureImageCacheText
    {
        get => _quickCaptureImageCacheText;
        private set => SetProperty(ref _quickCaptureImageCacheText, value);
    }

    public bool CanClearQuickCaptureImageCache
    {
        get => _canClearQuickCaptureImageCache;
        private set => SetProperty(ref _canClearQuickCaptureImageCache, value);
    }

    public SolidColorBrush AccentPreviewBrush { get; } = new(AccentColorHelper.DefaultAccentColor);

    public string[] AvailableThemes { get; } = [ThemeSystem, ThemeLight, ThemeDark];
    public string[] AvailableThemeDisplayNames => _cachedThemeDisplayNames ??= AvailableThemes.Select(GetThemeDisplayName).ToArray();
    public string[] AvailableLanguages { get; } = [SettingsService.LanguageSystem, SettingsService.LanguageChinese, SettingsService.LanguageEnglish];
    public string[] AvailableLanguageDisplayNames => _cachedLanguageDisplayNames ??= AvailableLanguages.Select(_localizationService.GetLanguageDisplayName).ToArray();
    public string[] AvailableManagedDropActions { get; } = [SettingsService.ManagedDropActionMove, SettingsService.ManagedDropActionCopy];
    public string[] AvailableManagedDropActionDisplayNames => _cachedManagedDropActionDisplayNames ??= AvailableManagedDropActions.Select(GetManagedDropActionDisplayName).ToArray();
    public string[] AvailableWidgetCornerPreferences { get; } = [CornerSmall, CornerRound, CornerSquare];
    public string[] AvailableWidgetCornerPreferenceDisplayNames => _cachedWidgetCornerPreferenceDisplayNames ??= AvailableWidgetCornerPreferences.Select(GetCornerDisplayName).ToArray();
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

    public void RefreshDragDropPermissionDiagnostic()
    {
        try
        {
            _dragDropPermissionDiagnostic = DragDropPermissionService.Diagnose();
            App.Log(
                "[DragDropPermission] " +
                $"issue={_dragDropPermissionDiagnostic.Issue} severity={_dragDropPermissionDiagnostic.Severity} " +
                $"process='{_dragDropPermissionDiagnostic.CurrentProcessIntegrity}' " +
                $"explorer='{_dragDropPermissionDiagnostic.ExplorerIntegrity}' " +
                $"uac='{_dragDropPermissionDiagnostic.UacStatus}' " +
                $"appCompat='{_dragDropPermissionDiagnostic.AppCompatStatus}' " +
                $"startup='{_dragDropPermissionDiagnostic.StartupStatus}'");
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Diagnose failed: {ex}");
            _dragDropPermissionDiagnostic = new DragDropPermissionDiagnostic(
                DragDropDiagnosticSeverity.Error,
                DragDropDiagnosticIssue.None,
                _localizationService.T("Settings.DragDropPermission.DiagnoseFailedSummary"),
                ex.Message,
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }

        NotifyDragDropPermissionPropertiesChanged();
    }

    public DragDropPermissionRepairResult RepairDragDropPermission()
    {
        IsDragDropPermissionRepairing = true;
        try
        {
            var result = DragDropPermissionService.Repair(_settingsService);
            DragDropPermissionRepairStatusText = result.Success
                ? _localizationService.Format("Settings.DragDropPermission.RepairStatus", result.RepairedCount)
                : _localizationService.Format("Settings.DragDropPermission.RepairFailedStatus", result.FailureMessage);
            RefreshDragDropPermissionDiagnostic();
            return result;
        }
        finally
        {
            IsDragDropPermissionRepairing = false;
        }
    }

    public void RefreshQuickAccessState()
    {
        ManagedStorageQuickAccessPinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(ManagedStorageRootPath, out _);
    }

    public async Task RefreshQuickAccessStateAsync(bool showBusy = false)
    {
        string path = ManagedStorageRootPath;
        if (showBusy)
        {
            IsQuickAccessBusy = true;
        }

        try
        {
            QuickAccessStateResult result = await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(path);
            if (string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = result.State;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to refresh Quick Access state: {ex}");
            if (string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = QuickAccessPinState.Unknown;
            }
        }
        finally
        {
            if (showBusy)
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

    public async Task RefreshQuickCaptureImageCacheInfoAsync()
    {
        if (App.Current?.QuickCaptureService is not { } quickCaptureService)
        {
            QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable");
            CanClearQuickCaptureImageCache = false;
            return;
        }

        try
        {
            var info = await quickCaptureService.GetImageCacheInfoAsync();
            if (info.TotalFileCount == 0)
            {
                QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheEmpty");
                CanClearQuickCaptureImageCache = false;
                return;
            }

            QuickCaptureImageCacheText = _localizationService.Format(
                "Settings.QuickCapture.ImageCacheValue",
                info.TotalFileCount,
                FormatBytes(info.TotalBytes),
                info.UnusedFileCount,
                FormatBytes(info.UnusedBytes));
            CanClearQuickCaptureImageCache = info.UnusedFileCount > 0;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to refresh image cache info: {ex}");
            QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable");
            CanClearQuickCaptureImageCache = false;
        }
    }

    public void RefreshQuickCaptureClipboardDiagnostics()
    {
        if (App.Current?.QuickCaptureClipboardService is not { } clipboardService)
        {
            QuickCaptureClipboardDiagnosticsText = _localizationService.T("Settings.QuickCapture.ClipboardDiagnosticsUnavailable");
            return;
        }

        var diagnostics = clipboardService.GetDiagnostics();
        string reasonText = GetQuickCaptureClipboardReasonText(diagnostics.LastReason);
        if (diagnostics.LastCapturedAt is { } capturedAt)
        {
            QuickCaptureClipboardDiagnosticsText = _localizationService.Format(
                diagnostics.IsRecording && diagnostics.IsListening
                    ? "Settings.QuickCapture.ClipboardDiagnosticsRecording"
                    : "Settings.QuickCapture.ClipboardDiagnosticsNotRecording",
                capturedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                reasonText);
            return;
        }

        QuickCaptureClipboardDiagnosticsText = _localizationService.Format(
            diagnostics.IsRecording && diagnostics.IsListening
                ? "Settings.QuickCapture.ClipboardDiagnosticsNoCapture"
                : "Settings.QuickCapture.ClipboardDiagnosticsNotRecordingNoCapture",
            reasonText);
    }

    public SettingsViewModel(SettingsService settingsService, ThemeService themeService, LocalizationService? localizationService = null)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _quickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheLoading");
        _quickCaptureClipboardDiagnosticsText = _localizationService.T("Settings.QuickCapture.ClipboardDiagnosticsUnavailable");
        _dragDropPermissionRepairStatusText = string.Empty;

        var settings = settingsService.Settings;
        _selectedTheme = settings.Theme is ThemeLight or ThemeDark ? settings.Theme : ThemeSystem;
        _selectedTrayIconStyle = settings.TrayIconStyle is TrayIconStyleColorful or TrayIconStyleBlack or TrayIconStyleWhite
            ? settings.TrayIconStyle
            : TrayIconStyleSystem;
        _selectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);

        _useSystemAccentColor = !string.Equals(settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        _autoStart = StartupService.IsEnabled();
        _doubleClickToOpen = settings.DoubleClickToOpen;
        _defaultWidth = settings.DefaultWidgetWidth;
        _defaultHeight = settings.DefaultWidgetHeight;
        _hideShortcutArrowOverlay = settings.HideShortcutArrowOverlay;
        _showHoverButtons = settings.ShowHoverButtons;
        _showListItemDetails = settings.ShowListItemDetails;
        _widgetOpacity = settings.WidgetOpacity;
        _selectedWidgetCornerPreference = settings.WidgetCornerPreference is CornerDefault or CornerSquare or CornerSmall or CornerRound
            ? settings.WidgetCornerPreference
            : CornerSmall;
        _selectedWidgetAnimationEffect = NormalizeWidgetAnimationEffect(settings.WidgetAnimationEffect);
        _selectedWidgetAnimationSpeed = NormalizeWidgetAnimationSpeed(settings.WidgetAnimationSpeed);
        _selectedWidgetAnimationSlideDirection = NormalizeWidgetAnimationSlideDirection(settings.WidgetAnimationSlideDirection);
        _selectedWidgetAnimationEasingIntensity = NormalizeWidgetAnimationEasingIntensity(settings.WidgetAnimationEasingIntensity);
        _iconSize = settings.IconSize;
        _textSize = settings.TextSize;
        _layoutDensityScale = settings.LayoutDensityScale;
        _horizontalSpacingScale = settings.HorizontalSpacingScale;
        _verticalSpacingScale = settings.VerticalSpacingScale;
        _fileNameWidthScale = settings.FileNameWidthScale;
        _showFileExtensions = settings.ShowFileExtensions;
        _hideShortcutExtensionWhenShowingFileExtensions = settings.HideShortcutExtensionWhenShowingFileExtensions;
        _quickCaptureEnabled = settings.QuickCaptureEnabled;
        _quickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
        _quickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
        _quickCaptureRecentLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        _selectedManagedDropAction = string.Equals(settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? SettingsService.ManagedDropActionCopy
            : SettingsService.ManagedDropActionMove;
        _managedStorageRootPath = settings.DefaultManagedStorageRootPath;

        RefreshAccentPreview();
        RefreshDragDropPermissionDiagnostic();
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
        _ = RefreshQuickAccessStateAsync(showBusy: true);
    }

    public async Task RestoreDefaultPreferencesAsync()
    {
        _isRestoringDefaults = true;
        SuppressAppearanceNotifications = true;
        DeferAppearancePersistence = false;

        try
        {
            SelectedTheme = ThemeSystem;
            SelectedTrayIconStyle = TrayIconStyleColorful;
            UseSystemAccentColor = true;
            DefaultWidth = SettingsService.DefaultWidgetWidth;
            DefaultHeight = SettingsService.DefaultWidgetHeight;
            SelectedWidgetCornerPreference = CornerSmall;
            SelectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            SelectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            SelectedWidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
            SelectedWidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
            WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            IconSize = SettingsService.DefaultIconSize;
            TextSize = SettingsService.DefaultTextSize;
            LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            ShowFileExtensions = false;
            HideShortcutExtensionWhenShowingFileExtensions = true;
            QuickCaptureImageClipboardEnabled = true;
            SelectedManagedDropAction = SettingsService.ManagedDropActionMove;
            DoubleClickToOpen = true;
            HideShortcutArrowOverlay = true;
            ShowListItemDetails = false;

            var settings = _settingsService.Settings;
            settings.Theme = "System";
            settings.TrayIconStyle = TrayIconStyleColorful;
            settings.AccentColorMode = ThemeService.AccentModeSystem;
            settings.DefaultWidgetWidth = SettingsService.DefaultWidgetWidth;
            settings.DefaultWidgetHeight = SettingsService.DefaultWidgetHeight;
            settings.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceSmall;
            settings.WidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            settings.WidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            settings.WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
            settings.WidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
            settings.WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            settings.IconSize = SettingsService.DefaultIconSize;
            settings.TextSize = SettingsService.DefaultTextSize;
            settings.LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            settings.LayoutDensity = SettingsService.DefaultLayoutDensityScale <= 0.78 ? "Compact" : "Comfortable";
            settings.HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            settings.VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            settings.FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            settings.ShowFileExtensions = false;
            settings.HideShortcutExtensionWhenShowingFileExtensions = true;
            settings.QuickCaptureImageClipboardEnabled = true;
            settings.ManagedDropAction = SettingsService.ManagedDropActionMove;
            settings.GlobalHotkeyEnabled = SettingsService.DefaultGlobalHotkeyEnabled;
            settings.GlobalHotkeyModifiers = SettingsService.DefaultGlobalHotkeyModifiers;
            settings.GlobalHotkeyKey = SettingsService.DefaultGlobalHotkeyKey;
            settings.DoubleClickToOpen = true;
            settings.HideShortcutArrowOverlay = true;
            settings.ShowListItemDetails = false;

            RefreshAccentPreview();
            RefreshNumberInputs();
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
            App.Current?.GlobalHotkeyService?.RefreshRegistration();
            RefreshGlobalHotkeyState();
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
        bool quickCaptureEnabled = settings.QuickCaptureEnabled;
        bool quickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
        bool quickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
        string managedDropAction = string.Equals(settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? SettingsService.ManagedDropActionCopy
            : SettingsService.ManagedDropActionMove;
        string managedStorageRootPath = SettingsService.NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);

        _isApplyingSettingsSnapshot = true;
        try
        {
            if (!string.Equals(SelectedManagedDropAction, managedDropAction, StringComparison.OrdinalIgnoreCase))
            {
                SelectedManagedDropAction = managedDropAction;
            }

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
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
        }

        OnPropertyChanged(nameof(SelectedManagedDropActionText));
        OnPropertyChanged(nameof(SelectedManagedDropActionIndex));
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshLocalizedProperties()
    {
        _cachedThemeDisplayNames = null;
        _cachedTrayIconStyleDisplayNames = null;
        _cachedLanguageDisplayNames = null;
        _cachedWidgetCornerPreferenceDisplayNames = null;
        _cachedWidgetAnimationEffectDisplayNames = null;
        _cachedWidgetAnimationSpeedDisplayNames = null;
        _cachedWidgetAnimationSlideDirectionDisplayNames = null;
        _cachedWidgetAnimationEasingIntensityDisplayNames = null;
        _cachedManagedDropActionDisplayNames = null;

        OnPropertyChanged(nameof(AvailableThemeDisplayNames));
        OnPropertyChanged(nameof(AvailableTrayIconStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableLanguageDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCornerPreferenceDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEffectDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSpeedDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSlideDirectionDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEasingIntensityDisplayNames));
        OnPropertyChanged(nameof(AvailableManagedDropActionDisplayNames));
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(SelectedTrayIconStyleIndex));
        OnPropertyChanged(nameof(SelectedLanguageIndex));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityIndex));
        OnPropertyChanged(nameof(SelectedManagedDropActionIndex));
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(OpenSourceRepositoryDisplayText));
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
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private string GetDragDropPermissionSummaryText()
    {
        if (_dragDropPermissionDiagnostic is null)
        {
            return _localizationService.T("Settings.DragDropPermission.NotChecked");
        }

        return _dragDropPermissionDiagnostic.Issue switch
        {
            DragDropDiagnosticIssue.UacDisabled => _localizationService.T("Settings.DragDropPermission.Summary.UacDisabled"),
            DragDropDiagnosticIssue.PermissionMismatch => _localizationService.T("Settings.DragDropPermission.Summary.PermissionMismatch"),
            DragDropDiagnosticIssue.AppCompatIssue => _localizationService.T("Settings.DragDropPermission.Summary.AppCompatIssue"),
            DragDropDiagnosticIssue.StartupShortcutIssue => _localizationService.T("Settings.DragDropPermission.Summary.StartupShortcutIssue"),
            _ => _localizationService.T("Settings.DragDropPermission.Summary.Ok")
        };
    }

    private string GetDragDropPermissionDetailText()
    {
        if (_dragDropPermissionDiagnostic is null)
        {
            return _localizationService.T("Settings.DragDropPermission.NotCheckedDetail");
        }

        return _dragDropPermissionDiagnostic.Issue switch
        {
            DragDropDiagnosticIssue.UacDisabled => _localizationService.T("Settings.DragDropPermission.Detail.UacDisabled"),
            DragDropDiagnosticIssue.PermissionMismatch => _localizationService.T("Settings.DragDropPermission.Detail.PermissionMismatch"),
            DragDropDiagnosticIssue.AppCompatIssue => _localizationService.T("Settings.DragDropPermission.Detail.AppCompatIssue"),
            DragDropDiagnosticIssue.StartupShortcutIssue => _localizationService.T("Settings.DragDropPermission.Detail.StartupShortcutIssue"),
            _ => _localizationService.T("Settings.DragDropPermission.Detail.Ok")
        };
    }

    private void NotifyDragDropPermissionPropertiesChanged()
    {
        OnPropertyChanged(nameof(DragDropPermissionSummaryText));
        OnPropertyChanged(nameof(DragDropPermissionDetailText));
        OnPropertyChanged(nameof(DragDropPermissionSeverityKind));
        OnPropertyChanged(nameof(DragDropPermissionProcessText));
        OnPropertyChanged(nameof(DragDropPermissionExplorerText));
        OnPropertyChanged(nameof(DragDropPermissionUacText));
        OnPropertyChanged(nameof(DragDropPermissionAppCompatText));
        OnPropertyChanged(nameof(DragDropPermissionStartupText));
        OnPropertyChanged(nameof(DragDropPermissionShortcutText));
        OnPropertyChanged(nameof(CanRepairDragDropPermission));
    }

    private string GetQuickCaptureClipboardReasonText(string reason)
    {
        string key = reason switch
        {
            "enabled" => "Settings.QuickCapture.ClipboardReason.Enabled",
            "disabled:quick-capture-off" => "Settings.QuickCapture.ClipboardReason.QuickCaptureOff",
            "disabled:clipboard-off" => "Settings.QuickCapture.ClipboardReason.ClipboardOff",
            "disabled:notice-unconfirmed" => "Settings.QuickCapture.ClipboardReason.NoticeUnconfirmed",
            "ignored:empty-or-unsupported" => "Settings.QuickCapture.ClipboardReason.EmptyOrUnsupported",
            "ignored:deskbox-write" => "Settings.QuickCapture.ClipboardReason.DeskBoxWrite",
            "ignored:image-recording-off" => "Settings.QuickCapture.ClipboardReason.ImageOff",
            "ignored:image-too-large" => "Settings.QuickCapture.ClipboardReason.ImageTooLarge",
            "ignored:text-too-large" => "Settings.QuickCapture.ClipboardReason.TextTooLarge",
            "ignored:duplicate-or-app-write" => "Settings.QuickCapture.ClipboardReason.Duplicate",
            "failed:read-or-save" => "Settings.QuickCapture.ClipboardReason.Failed",
            _ when reason.StartsWith("captured:", StringComparison.Ordinal) => "Settings.QuickCapture.ClipboardReason.Captured",
            _ => "Settings.QuickCapture.ClipboardReason.Unknown"
        };

        return _localizationService.T(key);
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

    partial void OnShowHoverButtonsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowHoverButtons = value;
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

        _settingsService.Settings.QuickCaptureEnabled = value;
        _settingsService.SaveDebounced();
        _ = SyncQuickCaptureEnabledAsync(value);
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private static async Task SyncQuickCaptureEnabledAsync(bool value)
    {
        try
        {
            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetQuickCaptureEnabledAsync(value, reveal: value);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync Quick Capture enabled state: {ex}");
        }
    }

    partial void OnQuickCaptureClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.QuickCaptureClipboardEnabled = value;
        _settingsService.SaveDebounced();
        RefreshQuickCaptureClipboardDiagnostics();
    }

    partial void OnQuickCaptureImageClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.QuickCaptureImageClipboardEnabled = value;
        _settingsService.SaveDebounced();
        RefreshQuickCaptureClipboardDiagnostics();
        if (value)
        {
            App.Current.QuickCaptureClipboardService?.CaptureCurrent();
        }
    }

    partial void OnQuickCaptureRecentLimitChanged(int value)
    {
        if (_isRestoringDefaults)
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
        if (App.Current?.QuickCaptureClipboardService is { } clipboardService)
        {
            clipboardService.DiagnosticsChanged -= OnQuickCaptureClipboardDiagnosticsChanged;
        }

        _settingsService.SettingsChanged -= OnSettingsChanged;
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnQuickCaptureClipboardDiagnosticsChanged()
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(RefreshQuickCaptureClipboardDiagnostics);
            return;
        }

        RefreshQuickCaptureClipboardDiagnostics();
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
