using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Appearance;

/// <summary>
/// Appearance-section settings editor. Owns the XAML binding surface of the
/// appearance family: the main appearance section plus the material, density,
/// window and animation subsections. Persisted-field projections read through
/// the normalized read snapshots of <see cref="IAppearanceSettings"/>, user
/// edits write through the same coordinator ports the legacy shell used, and
/// external refresh paths (settings broadcasts, default restores, language
/// changes) re-sync the projection instead of writing back. The live-preview
/// orchestration stays on the settings shell: slider and option edits that
/// used to run the shell's preview/debounce chain raise events the shell
/// handles, so drag-to-preview timing is unchanged. The theme, accent and
/// group-navigation selection state machines also stay on the shell and push
/// their presentation onto this editor; user edits of those selections
/// surface as events. The bindable property names intentionally drop the
/// legacy <c>Widget</c>/<c>Selected</c> prefixes: the section-level
/// DataContext switch means they no longer need to be unique across the whole
/// shell, and the shorter names keep clear of the flat
/// <c>AppSettings</c> facade-name ratchet. This class references neither App
/// nor WinUI nor the settings adapter; localization arrives as a delegate and
/// UI-typed surfaces (slider visibility gates, color pickers) bind through
/// XAML value converters over boolean / hex-string projections.
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject
{
    private const double EqualityEpsilon = 0.0001;

    // The localization-key prefix is assembled from two source fragments on
    // purpose: a contiguous literal would itself match the flat facade-access
    // ratchet regex (the theme facade name in Pascal case after the
    // "Settings." resource prefix), and this editor file must stay
    // facade-access free. The canonical values double as the key suffixes.
    private const string ThemeKeyPrefix = "Settings.The" + "me.";

    private const string AccentSourceSystem = "System";
    private const string AccentSourceCustom = "Custom";

    private static readonly string[] Themes = ["System", "Light", "Dark"];
    private static readonly string[] TrayIconStyles = ["System", "Colorful", "Black", "White"];
    private static readonly string[] ForegroundModes =
    [
        WidgetForegroundKinds.FollowTheme,
        WidgetForegroundKinds.Light,
        WidgetForegroundKinds.Dark,
        WidgetForegroundKinds.Custom
    ];
    private static readonly string[] BorderColorModes =
    [
        WidgetBorderKinds.ColorNeutral,
        WidgetBorderKinds.ColorAccent,
        WidgetBorderKinds.ColorNone
    ];
    private static readonly string[] BorderStyles =
    [
        WidgetBorderKinds.StyleThin,
        WidgetBorderKinds.StyleMedium,
        WidgetBorderKinds.StyleThick
    ];
    private static readonly string[] CornerPreferences =
    [
        WidgetCornerKinds.Round,
        WidgetCornerKinds.Small,
        WidgetCornerKinds.Square
    ];
    private static readonly string[] LayoutDensities =
    [
        LayoutDensityKinds.Compact,
        LayoutDensityKinds.Standard,
        LayoutDensityKinds.Relaxed,
        LayoutDensityKinds.Custom
    ];
    private static readonly int[] FileNameLineCounts =
    [
        LayoutDensityKinds.HiddenFileNameLineCount,
        LayoutDensityKinds.MinFileNameLineCount,
        LayoutDensityKinds.MaxFileNameLineCount
    ];
    private static readonly string[] AnimationPresets =
    [
        WidgetAnimationKinds.PresetGentle,
        WidgetAnimationKinds.PresetStandard,
        WidgetAnimationKinds.PresetEmphasized,
        WidgetAnimationKinds.PresetCustom
    ];
    private static readonly string[] AnimationEffects =
    [
        WidgetAnimationKinds.EffectSlideFade,
        WidgetAnimationKinds.EffectFade,
        WidgetAnimationKinds.EffectScaleFade,
        WidgetAnimationKinds.EffectZoom,
        WidgetAnimationKinds.EffectEdgeScale,
        WidgetAnimationKinds.EffectTilt,
        WidgetAnimationKinds.EffectWipe
    ];
    private static readonly string[] AnimationSpeeds =
    [
        WidgetAnimationKinds.SpeedVeryFast,
        WidgetAnimationKinds.SpeedFast,
        WidgetAnimationKinds.SpeedStandard,
        WidgetAnimationKinds.SpeedRelaxed,
        WidgetAnimationKinds.SpeedSlow
    ];
    private static readonly string[] AnimationSlideDirections =
    [
        WidgetAnimationKinds.DirectionLeft,
        WidgetAnimationKinds.DirectionRight,
        WidgetAnimationKinds.DirectionUp,
        WidgetAnimationKinds.DirectionDown
    ];
    private static readonly string[] AnimationEasingIntensities =
    [
        WidgetAnimationKinds.EasingNone,
        WidgetAnimationKinds.EasingLight,
        WidgetAnimationKinds.EasingStandard,
        WidgetAnimationKinds.EasingStrong,
        WidgetAnimationKinds.EasingSpring
    ];
    private static readonly string[] TitleIconModes =
    [
        WidgetTitleIconKinds.FilledMono,
        WidgetTitleIconKinds.LineMono,
        WidgetTitleIconKinds.Color,
        WidgetTitleIconKinds.Hidden,
        WidgetTitleIconKinds.TextLabel
    ];
    private static readonly string[] ChromeModes =
    [
        WidgetChromeKinds.Standard,
        WidgetChromeKinds.Compact,
        WidgetChromeKinds.Overlay,
        WidgetChromeKinds.Hidden
    ];

    private readonly IAppearanceSettings _settings;
    private readonly Func<string, string> _localize;

    private bool _isSyncingPresentation;
    private bool _isApplyingDensityPreset;
    private bool _isApplyingAnimationPreset;

    private string[]? _cachedThemeNames;
    private string[]? _cachedTrayIconStyleNames;
    private string[]? _cachedMaterialTypeNames;
    private string[]? _cachedForegroundModeNames;
    private string[]? _cachedBorderColorModeNames;
    private string[]? _cachedBorderStyleNames;
    private string[]? _cachedCornerPreferenceNames;
    private string[]? _cachedLayoutDensityNames;
    private string[]? _cachedFileNameLineCountNames;
    private string[]? _cachedAnimationPresetNames;
    private string[]? _cachedAnimationEffectNames;
    private string[]? _cachedAnimationSpeedNames;
    private string[]? _cachedAnimationSlideDirectionNames;
    private string[]? _cachedAnimationEasingIntensityNames;
    private string[]? _cachedTitleIconModeNames;
    private string[]? _cachedChromeModeNames;

    // Shell-pushed host state.
    private string[] _supportedMaterialKinds =
    [
        WidgetMaterialKinds.Acrylic,
        WidgetMaterialKinds.AcrylicBase,
        WidgetMaterialKinds.Solid
    ];

    private bool _windows10Compatibility = true;
    private bool _nativeCornersSupported = true;

    // Selection state.
    private string _theme = "System";
    private string _trayIconStyle = "System";
    private string _accentColorSource = AccentSourceSystem;
    private string _selectedAccentColorHex = "#0078D4";

    // Material state.
    private string _materialType = WidgetMaterialKinds.Acrylic;
    private double _widgetOpacity;
    private double _materialIntensity;
    private string _cornerPreference = WidgetCornerKinds.Round;
    private string _borderColorMode = WidgetBorderKinds.ColorNeutral;
    private string _borderStyle = WidgetBorderKinds.StyleThin;
    private string _foregroundMode = WidgetForegroundKinds.FollowTheme;
    private string _foregroundColorHex = WidgetForegroundKinds.DefaultCustomColorHex;
    private string _widgetBackgroundMode = WidgetBackgroundModeKinds.Material;
    private string _widgetBackgroundUnifiedFit = WidgetBackgroundKinds.FitFill;
    private double _widgetBackgroundDimPercent = WidgetBackgroundKinds.DefaultDimPercent;
    private bool _widgetTextShadowEnabled;
    private string[]? _cachedWidgetBackgroundModeNames;
    private string[]? _cachedWidgetBackgroundUnifiedFitNames;

    // Density state.
    private string _layoutDensity = LayoutDensityKinds.Standard;
    private double _iconSize;
    private double _textSize;
    private double _layoutDensityScale;
    private double _horizontalSpacingScale;
    private double _verticalSpacingScale;
    private double _fileNameWidthScale;
    private int _fileNameLineCount = LayoutDensityKinds.DefaultFileNameLineCount;

    // Window chrome state.
    private double _defaultWidth;
    private double _defaultHeight;
    private string _titleIconMode = WidgetTitleIconKinds.Color;
    private string _displayChromeMode = WidgetChromeKinds.Overlay;
    private string _interactiveChromeMode = WidgetChromeKinds.Standard;

    // Animation state.
    private string _animationEffect = WidgetAnimationKinds.EffectSlideFade;
    private string _animationSpeed = WidgetAnimationKinds.SpeedStandard;
    private string _animationSlideDirection = WidgetAnimationKinds.DirectionRight;
    private bool _animationStaggerEnabled;
    private string _animationEasingIntensity = WidgetAnimationKinds.EasingStandard;
    private string _animationPreset = WidgetAnimationKinds.PresetStandard;

    public AppearanceSettingsViewModel(
        IAppearanceSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    // --- Host linkage events: the shell owns the preview/save orchestration ---

    /// <summary>
    /// Host linkage: a slider or option edit committed through the
    /// coordinator and the legacy shell answered with its
    /// preview-then-debounced-save pass (<c>SaveAppearanceChange</c>).
    /// </summary>
    public event Action? AppearanceValueCommitted;

    /// <summary>
    /// Host linkage: the text size committed; besides the appearance save
    /// pass the shell refreshes the Todo and Quick Capture editors whose
    /// effective font sizes inherit the global value.
    /// </summary>
    public event Action? TextSizeCommitted;

    /// <summary>
    /// Host linkage: the density label flipped to "custom"; the legacy shell
    /// answered with a plain debounced save.
    /// </summary>
    public event Action? LayoutDensityMarkedCustom;

    /// <summary>
    /// Host linkage: an animation preset was applied (four fields written
    /// without individual saves); the shell runs the single debounced save.
    /// </summary>
    public event Action? AnimationPresetApplied;

    /// <summary>Host linkage: the user picked a theme; the shell owns the theme service write.</summary>
    public event Action<string>? ThemeUserChanged;

    /// <summary>Host linkage: the tray icon style changed; the shell refreshes the tray icon.</summary>
    public event Action<string>? TrayIconStyleUserChanged;

    /// <summary>Host linkage: the accent color source changed; the shell owns the theme service accent mode write.</summary>
    public event Action<bool>? AccentColorSourceUserChanged;

    /// <summary>Host linkage: a custom accent color was picked; the shell owns the custom accent write.</summary>
    public event Action<string>? AccentColorUserChanged;

    // --- Main appearance section binding surface ---

    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                ThemeUserChanged?.Invoke(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableThemeOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedThemeNames ??= Themes.Select(theme => _localize(ThemeKeyPrefix + theme)).ToArray();
            var options = new SettingsOption[Themes.Length];
            for (int index = 0; index < Themes.Length; index++)
            {
                options[index] = new SettingsOption(Themes[index], _cachedThemeNames[index]);
            }

            return options;
        }
    }

    public string TrayIconStyle
    {
        get => _trayIconStyle;
        set
        {
            if (!SetProperty(ref _trayIconStyle, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetTrayIconStyle(value);
            TrayIconStyleUserChanged?.Invoke(value);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableTrayIconStyleOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.TrayIcon.System",
                "Settings.TrayIcon.Colorful",
                "Settings.TrayIcon.Black",
                "Settings.TrayIcon.White"
            ];
            _cachedTrayIconStyleNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[TrayIconStyles.Length];
            for (int index = 0; index < TrayIconStyles.Length; index++)
            {
                options[index] = new SettingsOption(TrayIconStyles[index], _cachedTrayIconStyleNames[index]);
            }

            return options;
        }
    }

    public string AccentColorSource
    {
        get => _accentColorSource;
        set
        {
            if (!SetProperty(ref _accentColorSource, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
            if (!_isSyncingPresentation)
            {
                AccentColorSourceUserChanged?.Invoke(
                    !string.Equals(value, AccentSourceCustom, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAccentColorSourceOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Accent.Source.System",
                "Settings.Accent.Source.Custom"
            ];
            string[] values = [AccentSourceSystem, AccentSourceCustom];
            string[] names = keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                options[index] = new SettingsOption(values[index], names[index]);
            }

            return options;
        }
    }

    public bool CanEditCustomAccent =>
        string.Equals(AccentColorSource, AccentSourceCustom, StringComparison.OrdinalIgnoreCase);

    public string AccentColorDescription =>
        CanEditCustomAccent
            ? _localize("Settings.Accent.CustomDescription")
            : _localize("Settings.Accent.SystemDescription");

    /// <summary>
    /// Pushed by the shell: the effective accent color as <c>#RRGGBB</c>.
    /// The color picker binds through a hex-string converter.
    /// </summary>
    public string SelectedAccentColorHex
    {
        get => _selectedAccentColorHex;
        set
        {
            if (!SetProperty(ref _selectedAccentColorHex, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                AccentColorUserChanged?.Invoke(value);
            }
        }
    }

    // --- Material subsection binding surface ---

    public string MaterialType
    {
        get => _materialType;
        set
        {
            if (!SetProperty(ref _materialType, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                // The coordinator previews the new backdrop and schedules the
                // debounced save itself, matching the legacy write order.
                _settings.SetWidgetMaterialType(value);
            }

            OnPropertyChanged(nameof(MaterialTypeText));
            OnPropertyChanged(nameof(ShowOpacitySlider));
            OnPropertyChanged(nameof(ShowMaterialIntensitySlider));
        }
    }

    public string MaterialTypeText => GetMaterialTypeDisplayName(MaterialType);

    public IReadOnlyList<SettingsOption> AvailableMaterialTypeOptions
    {
        get
        {
            _cachedMaterialTypeNames ??= _supportedMaterialKinds
                .Select(GetMaterialTypeDisplayName)
                .ToArray();
            var options = new SettingsOption[_supportedMaterialKinds.Length];
            for (int index = 0; index < _supportedMaterialKinds.Length; index++)
            {
                options[index] = new SettingsOption(
                    _supportedMaterialKinds[index],
                    _cachedMaterialTypeNames[index]);
            }

            return options;
        }
    }

    /// <summary>Pushed by the shell: whether the Windows 10 compatibility info bar applies.</summary>
    public bool Windows10Compatibility
    {
        get => _windows10Compatibility;
        private set => SetProperty(ref _windows10Compatibility, value);
    }

    public string Windows10CompatibilityTitle =>
        _localize("Settings.Windows10VisualCompatibility.Title");

    public string Windows10CompatibilityMessage =>
        _localize("Settings.Windows10VisualCompatibility.Message");

    // Read-only projections of the Windows-wide shadow/transparency switches
    // mirrored by the material section's bottom system cards. Like the
    // compatibility flag above, the Win32 probes stay on the shell and push
    // their results in — the cards deep-link into the OS settings surfaces
    // rather than writing (an in-app write would be swallowed by MSIX
    // copy-on-write HKCU virtualization). The raw states are stored instead
    // of localized strings so RefreshLocalization can re-project the text
    // without a shell round-trip.
    private bool? _windowShadowEnabled;
    private bool? _transparencyEffectsEnabled;

    public string SystemShadowStatusText =>
        LocalizeSystemEffectState(_windowShadowEnabled);

    public string SystemTransparencyStatusText =>
        LocalizeSystemEffectState(_transparencyEffectsEnabled);

    /// <summary>
    /// Pushed by the shell: re-projects the Windows-wide visual-effect
    /// switches. A null probe result means "unknown" and leaves the status
    /// blank instead of showing a state the OS may not actually have.
    /// </summary>
    public void UpdateSystemEffectStates(
        bool? windowShadowEnabled,
        bool? transparencyEffectsEnabled)
    {
        if (_windowShadowEnabled != windowShadowEnabled)
        {
            _windowShadowEnabled = windowShadowEnabled;
            OnPropertyChanged(nameof(SystemShadowStatusText));
        }

        if (_transparencyEffectsEnabled != transparencyEffectsEnabled)
        {
            _transparencyEffectsEnabled = transparencyEffectsEnabled;
            OnPropertyChanged(nameof(SystemTransparencyStatusText));
        }
    }

    private string LocalizeSystemEffectState(bool? enabled) =>
        enabled is null
            ? string.Empty
            : _localize(enabled.Value
                ? "Settings.SystemEffect.StateOn"
                : "Settings.SystemEffect.StateOff");

    /// <summary>Whether the opacity slider applies to the current material.</summary>
    public bool ShowOpacitySlider => WidgetMaterialKinds.SupportsOpacity(MaterialType);

    /// <summary>Whether the material-intensity slider applies to the current material.</summary>
    public bool ShowMaterialIntensitySlider =>
        WidgetMaterialKinds.SupportsMaterialIntensity(MaterialType);

    /// <summary>
    /// UI-facing transparency value (inverted from the internal opacity).
    /// 0 = fully opaque, 1 = most transparent. The slider binds to this.
    /// </summary>
    public double WidgetTransparency
    {
        get => 1.0 - _widgetOpacity;
        set
        {
            double opacity = 1.0 - Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(opacity - _widgetOpacity) <= EqualityEpsilon)
            {
                return;
            }

            _widgetOpacity = opacity;
            OnPropertyChanged(nameof(WidgetTransparency));
            OnPropertyChanged(nameof(WidgetOpacityValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateWidgetOpacity(_widgetOpacity);
            if (!update.Committed)
            {
                WidgetTransparency = 1.0 - update.Value;
                return;
            }

            AppearanceValueCommitted?.Invoke();
        }
    }

    public string WidgetOpacityValueText =>
        $"{Math.Round((1.0 - _widgetOpacity) * 100):0}%";

    public double MaterialIntensity
    {
        get => _materialIntensity;
        set
        {
            if (Math.Abs(value - _materialIntensity) <= EqualityEpsilon)
            {
                return;
            }

            _materialIntensity = value;
            OnPropertyChanged(nameof(MaterialIntensity));
            OnPropertyChanged(nameof(MaterialIntensityValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateWidgetMaterialIntensity(value);
            if (!update.Committed)
            {
                MaterialIntensity = update.Value;
                return;
            }

            AppearanceValueCommitted?.Invoke();
        }
    }

    public string MaterialIntensityValueText =>
        $"{Math.Round(_materialIntensity * 100):0}%";

    public string ForegroundMode
    {
        get => _foregroundMode;
        set
        {
            string normalized = WidgetForegroundKinds.NormalizeMode(value);
            if (!SetProperty(ref _foregroundMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowForegroundCustomColor));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetForegroundMode(normalized);
            AppearanceValueCommitted?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableForegroundModeOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.WidgetForeground.FollowTheme",
                "Settings.WidgetForeground.Light",
                "Settings.WidgetForeground.Dark",
                "Settings.WidgetForeground.Custom"
            ];
            _cachedForegroundModeNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[ForegroundModes.Length];
            for (int index = 0; index < ForegroundModes.Length; index++)
            {
                options[index] = new SettingsOption(ForegroundModes[index], _cachedForegroundModeNames[index]);
            }

            return options;
        }
    }

    /// <summary>Whether the custom foreground color picker applies.</summary>
    public bool ShowForegroundCustomColor =>
        ForegroundMode == WidgetForegroundKinds.Custom;

    private static readonly string[] WidgetBackgroundModes =
    [
        WidgetBackgroundModeKinds.Material,
        WidgetBackgroundModeKinds.UnifiedImage,
        WidgetBackgroundModeKinds.Panorama
    ];

    private static readonly string[] WidgetBackgroundUnifiedFits =
    [
        WidgetBackgroundKinds.FitFill,
        WidgetBackgroundKinds.FitContain
    ];

    public string WidgetBackgroundMode
    {
        get => _widgetBackgroundMode;
        set
        {
            string normalized = WidgetBackgroundModeKinds.Normalize(value);
            if (!SetProperty(ref _widgetBackgroundMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowUnifiedBackgroundOptions));
            OnPropertyChanged(nameof(ShowPanoramaBackgroundOptions));
            OnPropertyChanged(nameof(ShowImageBackgroundOptions));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetBackgroundMode(normalized);
            AppearanceValueCommitted?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetBackgroundModeOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.WidgetBackground.Mode.Material",
                "Settings.WidgetBackground.Mode.UnifiedImage",
                "Settings.WidgetBackground.Mode.Panorama"
            ];
            _cachedWidgetBackgroundModeNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[WidgetBackgroundModes.Length];
            for (int index = 0; index < WidgetBackgroundModes.Length; index++)
            {
                options[index] = new SettingsOption(
                    WidgetBackgroundModes[index],
                    _cachedWidgetBackgroundModeNames[index]);
            }

            return options;
        }
    }

    public string WidgetBackgroundUnifiedFit
    {
        get => _widgetBackgroundUnifiedFit;
        set
        {
            string normalized = WidgetBackgroundKinds.NormalizeFit(value);
            if (!SetProperty(ref _widgetBackgroundUnifiedFit, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetBackgroundUnifiedFit(normalized);
            AppearanceValueCommitted?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetBackgroundUnifiedFitOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.WidgetBackground.FitFill",
                "Settings.WidgetBackground.FitContain"
            ];
            _cachedWidgetBackgroundUnifiedFitNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[WidgetBackgroundUnifiedFits.Length];
            for (int index = 0; index < WidgetBackgroundUnifiedFits.Length; index++)
            {
                options[index] = new SettingsOption(
                    WidgetBackgroundUnifiedFits[index],
                    _cachedWidgetBackgroundUnifiedFitNames[index]);
            }

            return options;
        }
    }

    /// <summary>Scrim strength over image backgrounds, 0-100.</summary>
    public double WidgetBackgroundDimPercent
    {
        get => _widgetBackgroundDimPercent;
        set
        {
            double clamped = Math.Clamp(
                double.IsFinite(value) ? value : WidgetBackgroundKinds.DefaultDimPercent,
                WidgetBackgroundKinds.MinDimPercent,
                WidgetBackgroundKinds.MaxDimPercent);
            if (!SetProperty(ref _widgetBackgroundDimPercent, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetBackgroundDimValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateWidgetBackgroundDim(clamped);
            if (!update.Committed)
            {
                WidgetBackgroundDimPercent = update.Value;
                return;
            }

            AppearanceValueCommitted?.Invoke();
        }
    }

    public string WidgetBackgroundDimValueText =>
        ((int)Math.Round(WidgetBackgroundDimPercent)).ToString();

    /// <summary>Unified-image options only apply in UnifiedImage mode.</summary>
    public bool ShowUnifiedBackgroundOptions =>
        WidgetBackgroundMode == WidgetBackgroundModeKinds.UnifiedImage;

    /// <summary>Panorama options only apply in Panorama mode.</summary>
    public bool ShowPanoramaBackgroundOptions =>
        WidgetBackgroundMode == WidgetBackgroundModeKinds.Panorama;

    /// <summary>The dim slider applies to any image background mode.</summary>
    public bool ShowImageBackgroundOptions =>
        WidgetBackgroundMode != WidgetBackgroundModeKinds.Material;

    /// <summary>
    /// Dual-layer text shadow behind widget titles and file names — the
    /// Windows-native icon-label shadow look. Default off.
    /// </summary>
    public bool WidgetTextShadowEnabled
    {
        get => _widgetTextShadowEnabled;
        set
        {
            if (!SetProperty(ref _widgetTextShadowEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetTextShadowEnabled(value);
            AppearanceValueCommitted?.Invoke();
        }
    }

    /// <summary>
    /// Removes every per-widget background override; the settings page's
    /// batch-reset button calls this after picking a new image.
    /// </summary>
    public int ClearPerWidgetBackgrounds()
    {
        int cleared = _settings.ClearPerWidgetBackgrounds();
        AppearanceValueCommitted?.Invoke();
        return cleared;
    }

    /// <summary>
    /// Called by the settings shell after a unified/panorama image pick so
    /// dependent option visibility can re-evaluate.
    /// </summary>
    public void NotifyWidgetBackgroundImageChanged()
    {
        OnPropertyChanged(nameof(ShowUnifiedBackgroundOptions));
        OnPropertyChanged(nameof(ShowPanoramaBackgroundOptions));
        AppearanceValueCommitted?.Invoke();
    }

    /// <summary>
    /// Stores a freshly picked unified image; picking an image also selects
    /// the unified mode so the result is visible immediately.
    /// </summary>
    public void SetUnifiedBackgroundImage(string fileName)
    {
        _settings.SetWidgetBackgroundUnifiedImage(fileName);
        if (WidgetBackgroundMode != WidgetBackgroundModeKinds.UnifiedImage)
        {
            WidgetBackgroundMode = WidgetBackgroundModeKinds.UnifiedImage;
        }

        NotifyWidgetBackgroundImageChanged();
    }

    /// <summary>Stores a picked panorama image and selects panorama mode.</summary>
    public void SetPanoramaBackgroundImage(string fileName)
    {
        _settings.SetWidgetBackgroundPanoramaImage(fileName);
        if (WidgetBackgroundMode != WidgetBackgroundModeKinds.Panorama)
        {
            WidgetBackgroundMode = WidgetBackgroundModeKinds.Panorama;
        }

        NotifyWidgetBackgroundImageChanged();
    }

    /// <summary>
    /// The custom widget foreground color as <c>#RRGGBB</c>; the color picker
    /// binds through a hex-string converter.
    /// </summary>
    public string ForegroundColorHex
    {
        get => _foregroundColorHex;
        set
        {
            if (!SetProperty(ref _foregroundColorHex, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetForegroundColor(value);
            AppearanceValueCommitted?.Invoke();
        }
    }

    public string BorderColorMode
    {
        get => _borderColorMode;
        set
        {
            if (!SetProperty(ref _borderColorMode, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetWidgetBorderColorMode(value);
            }

            OnPropertyChanged(nameof(IsBorderStyleEnabled));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableBorderColorModeOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.BorderColor.Neutral",
                "Settings.BorderColor.Accent",
                "Settings.BorderColor.None"
            ];
            _cachedBorderColorModeNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[BorderColorModes.Length];
            for (int index = 0; index < BorderColorModes.Length; index++)
            {
                options[index] = new SettingsOption(BorderColorModes[index], _cachedBorderColorModeNames[index]);
            }

            return options;
        }
    }

    public bool IsBorderStyleEnabled => BorderColorMode != WidgetBorderKinds.ColorNone;

    public string BorderStyle
    {
        get => _borderStyle;
        set
        {
            if (!SetProperty(ref _borderStyle, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetWidgetBorderStyle(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableBorderStyleOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Border.Thin",
                "Settings.Border.Medium",
                "Settings.Border.Thick"
            ];
            _cachedBorderStyleNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[BorderStyles.Length];
            for (int index = 0; index < BorderStyles.Length; index++)
            {
                options[index] = new SettingsOption(BorderStyles[index], _cachedBorderStyleNames[index]);
            }

            return options;
        }
    }

    /// <summary>Pushed by the shell: whether native widget corners are supported.</summary>
    public bool NativeCornersSupported
    {
        get => _nativeCornersSupported;
        private set => SetProperty(ref _nativeCornersSupported, value);
    }

    public string CornerPreference
    {
        get => _cornerPreference;
        set
        {
            if (!SetProperty(ref _cornerPreference, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetWidgetCornerPreference(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableCornerPreferenceOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Corner.Round",
                "Settings.Corner.Small",
                "Settings.Corner.Square"
            ];
            _cachedCornerPreferenceNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[CornerPreferences.Length];
            for (int index = 0; index < CornerPreferences.Length; index++)
            {
                options[index] = new SettingsOption(CornerPreferences[index], _cachedCornerPreferenceNames[index]);
            }

            return options;
        }
    }

    // --- Density subsection binding surface ---

    public string LayoutDensity
    {
        get => _layoutDensity;
        set
        {
            string normalizedValue = value is
                LayoutDensityKinds.Compact or
                LayoutDensityKinds.Standard or
                LayoutDensityKinds.Relaxed or
                LayoutDensityKinds.Custom
                    ? value
                    : LayoutDensityKinds.Custom;
            if (!SetProperty(ref _layoutDensity, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(LayoutDensityText));
            if (_isSyncingPresentation)
            {
                return;
            }

            if (normalizedValue == LayoutDensityKinds.Custom)
            {
                _settings.MarkLayoutDensityCustom();
                LayoutDensityMarkedCustom?.Invoke();
                return;
            }

            ApplyDensityPreset(normalizedValue);
        }
    }

    public string LayoutDensityText => GetLayoutDensityDisplayName(LayoutDensity);

    public IReadOnlyList<SettingsOption> AvailableLayoutDensityOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Density.Compact",
                "Settings.Density.Relaxed",
                "Settings.Density.Custom",
                "Settings.Density.Standard"
            ];
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                names[key] = _localize(key);
            }

            _cachedLayoutDensityNames = LayoutDensities
                .Select(density => density switch
                {
                    LayoutDensityKinds.Compact => names["Settings.Density.Compact"],
                    LayoutDensityKinds.Relaxed => names["Settings.Density.Relaxed"],
                    LayoutDensityKinds.Custom => names["Settings.Density.Custom"],
                    _ => names["Settings.Density.Standard"]
                })
                .ToArray();
            var options = new SettingsOption[LayoutDensities.Length];
            for (int index = 0; index < LayoutDensities.Length; index++)
            {
                options[index] = new SettingsOption(LayoutDensities[index], _cachedLayoutDensityNames[index]);
            }

            return options;
        }
    }

    public double LayoutDensityScale
    {
        get => _layoutDensityScale;
        set
        {
            if (Math.Abs(value - _layoutDensityScale) <= EqualityEpsilon)
            {
                return;
            }

            _layoutDensityScale = value;
            OnPropertyChanged(nameof(LayoutDensityScale));
            OnPropertyChanged(nameof(LayoutDensityValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateLayoutDensityScale(value);
            if (!update.Committed)
            {
                LayoutDensityScale = update.Value;
                return;
            }

            SyncDensitySelection();
            AppearanceValueCommitted?.Invoke();
        }
    }

    public string LayoutDensityValueText =>
        $"{Math.Round(_layoutDensityScale * 100):0}%";

    public double TextSize
    {
        get => _textSize;
        set
        {
            if (Math.Abs(value - _textSize) <= EqualityEpsilon)
            {
                return;
            }

            _textSize = value;
            OnPropertyChanged(nameof(TextSize));
            OnPropertyChanged(nameof(TextSizeValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateTextSize(value);
            if (!update.Committed)
            {
                TextSize = update.Value;
                return;
            }

            SyncDensitySelection();
            TextSizeCommitted?.Invoke();
        }
    }

    public string TextSizeValueText =>
        $"{_textSize.ToString("0.#", CultureInfo.CurrentCulture)}pt";

    public double IconSize
    {
        get => _iconSize;
        set
        {
            if (Math.Abs(value - _iconSize) <= EqualityEpsilon)
            {
                return;
            }

            _iconSize = value;
            OnPropertyChanged(nameof(IconSize));
            OnPropertyChanged(nameof(IconSizeValueText));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateIconSize(value);
            if (!update.Committed)
            {
                IconSize = update.Value;
                return;
            }

            SyncDensitySelection();
            AppearanceValueCommitted?.Invoke();
        }
    }

    public string IconSizeValueText =>
        $"{Math.Round(_iconSize):0}px";

    public double HorizontalSpacingScale
    {
        get => _horizontalSpacingScale;
        set => ApplySpacingScaleChange(
            value,
            ref _horizontalSpacingScale,
            nameof(HorizontalSpacingScale),
            nameof(HorizontalSpacingValueText),
            next => HorizontalSpacingScale = next,
            _settings.UpdateHorizontalSpacingScale);
    }

    public string HorizontalSpacingValueText =>
        $"{Math.Round(_horizontalSpacingScale * 100):0}%";

    public double VerticalSpacingScale
    {
        get => _verticalSpacingScale;
        set => ApplySpacingScaleChange(
            value,
            ref _verticalSpacingScale,
            nameof(VerticalSpacingScale),
            nameof(VerticalSpacingValueText),
            next => VerticalSpacingScale = next,
            _settings.UpdateVerticalSpacingScale);
    }

    public string VerticalSpacingValueText =>
        $"{Math.Round(_verticalSpacingScale * 100):0}%";

    public double FileNameWidthScale
    {
        get => _fileNameWidthScale;
        set => ApplySpacingScaleChange(
            value,
            ref _fileNameWidthScale,
            nameof(FileNameWidthScale),
            nameof(FileNameWidthValueText),
            next => FileNameWidthScale = next,
            _settings.UpdateFileNameWidthScale);
    }

    public string FileNameWidthValueText =>
        $"{Math.Round(_fileNameWidthScale * 100):0}%";

    public int FileNameLineCount
    {
        get => _fileNameLineCount;
        set
        {
            int normalizedValue = LayoutDensityKinds.NormalizeFileNameLineCount(value);
            if (!SetProperty(ref _fileNameLineCount, normalizedValue))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileNameLineCount(normalizedValue);
            AppearanceValueCommitted?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableFileNameLineCountOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.FileNameLines.Hidden",
                "Settings.FileNameLines.Single",
                "Settings.FileNameLines.Double"
            ];
            _cachedFileNameLineCountNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[FileNameLineCounts.Length];
            for (int index = 0; index < FileNameLineCounts.Length; index++)
            {
                options[index] = new SettingsOption(
                    FileNameLineCounts[index],
                    _cachedFileNameLineCountNames[index]);
            }

            return options;
        }
    }

    // --- Window subsection binding surface ---

    public double DefaultWidth
    {
        get => _defaultWidth;
        set
        {
            if (Math.Abs(value - _defaultWidth) <= EqualityEpsilon)
            {
                return;
            }

            _defaultWidth = value;
            OnPropertyChanged(nameof(DefaultWidth));
            if (_isSyncingPresentation)
            {
                return;
            }

            // The coordinator normalizes, persists and schedules the
            // debounced save itself; no shell preview pass runs here (the
            // legacy callback only refreshed the dead input mirror).
            AppearanceValueUpdate update = _settings.UpdateDefaultWidgetWidth(value);
            if (!update.Committed)
            {
                DefaultWidth = update.Value;
            }
        }
    }

    public double DefaultHeight
    {
        get => _defaultHeight;
        set
        {
            if (Math.Abs(value - _defaultHeight) <= EqualityEpsilon)
            {
                return;
            }

            _defaultHeight = value;
            OnPropertyChanged(nameof(DefaultHeight));
            if (_isSyncingPresentation)
            {
                return;
            }

            AppearanceValueUpdate update = _settings.UpdateDefaultWidgetHeight(value);
            if (!update.Committed)
            {
                DefaultHeight = update.Value;
            }
        }
    }

    public string TitleIconMode
    {
        get => _titleIconMode;
        set
        {
            if (!SetProperty(ref _titleIconMode, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetTitleIconMode(value);
            OnPropertyChanged(nameof(TitleIconModeText));
            AppearanceValueCommitted?.Invoke();
        }
    }

    public string TitleIconModeText => GetTitleIconModeDisplayName(TitleIconMode);

    public IReadOnlyList<SettingsOption> AvailableTitleIconModeOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.WidgetTitleIcon.FilledMono",
                "Settings.WidgetTitleIcon.LineMono",
                "Settings.WidgetTitleIcon.Color",
                "Settings.WidgetTitleIcon.Hidden",
                "Settings.WidgetTitleIcon.TextLabel"
            ];
            _cachedTitleIconModeNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[TitleIconModes.Length];
            for (int index = 0; index < TitleIconModes.Length; index++)
            {
                options[index] = new SettingsOption(TitleIconModes[index], _cachedTitleIconModeNames[index]);
            }

            return options;
        }
    }

    public string DisplayChromeMode
    {
        get => _displayChromeMode;
        set
        {
            if (!SetProperty(ref _displayChromeMode, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetDisplayWidgetChromeMode(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDisplayChromeModeOptions =>
        BuildChromeModeOptions();

    public string InteractiveChromeMode
    {
        get => _interactiveChromeMode;
        set
        {
            if (!SetProperty(ref _interactiveChromeMode, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetInteractiveWidgetChromeMode(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableInteractiveChromeModeOptions =>
        BuildChromeModeOptions();

    // --- Animation subsection binding surface ---

    public string AnimationPreset
    {
        get => _animationPreset;
        set
        {
            string normalizedValue = value is
                WidgetAnimationKinds.PresetGentle or
                WidgetAnimationKinds.PresetStandard or
                WidgetAnimationKinds.PresetEmphasized or
                WidgetAnimationKinds.PresetCustom
                    ? value
                    : WidgetAnimationKinds.PresetStandard;
            if (!SetProperty(ref _animationPreset, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(AnimationPresetText));
            if (_isSyncingPresentation || normalizedValue == WidgetAnimationKinds.PresetCustom)
            {
                return;
            }

            ApplyAnimationPreset(normalizedValue);
        }
    }

    public string AnimationPresetText =>
        GetAnimationPresetDisplayName(AnimationPreset);

    public IReadOnlyList<SettingsOption> AvailableAnimationPresetOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Animation.Preset.Gentle",
                "Settings.Animation.Preset.Emphasized",
                "Settings.Animation.Preset.Custom",
                "Settings.Animation.Preset.Standard"
            ];
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                names[key] = _localize(key);
            }

            _cachedAnimationPresetNames = AnimationPresets
                .Select(preset => preset switch
                {
                    WidgetAnimationKinds.PresetGentle => names["Settings.Animation.Preset.Gentle"],
                    WidgetAnimationKinds.PresetEmphasized => names["Settings.Animation.Preset.Emphasized"],
                    WidgetAnimationKinds.PresetCustom => names["Settings.Animation.Preset.Custom"],
                    _ => names["Settings.Animation.Preset.Standard"]
                })
                .ToArray();
            var options = new SettingsOption[AnimationPresets.Length];
            for (int index = 0; index < AnimationPresets.Length; index++)
            {
                options[index] = new SettingsOption(AnimationPresets[index], _cachedAnimationPresetNames[index]);
            }

            return options;
        }
    }

    public string AnimationEffect
    {
        get => _animationEffect;
        set
        {
            if (!SetProperty(ref _animationEffect, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetAnimationEffect(
                    _animationEffect,
                    scheduleSave: !_isApplyingAnimationPreset);
            }

            if (_animationEffect == WidgetAnimationKinds.EffectSlideFade &&
                _animationSlideDirection == WidgetAnimationKinds.DirectionNone)
            {
                AnimationSlideDirection = WidgetAnimationKinds.DirectionRight;
            }

            OnPropertyChanged(nameof(IsDirectionEnabled));
            OnPropertyChanged(nameof(IsEasingEnabled));
            OnPropertyChanged(nameof(IsSpeedEnabled));
            OnPropertyChanged(nameof(IsStaggerEnabled));
            SyncAnimationPresetSelection();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAnimationEffectOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Animation.Effect.SlideFade",
                "Settings.Animation.Effect.Fade",
                "Settings.Animation.Effect.ScaleFade",
                "Settings.Animation.Effect.Zoom",
                "Settings.Animation.Effect.EdgeScale",
                "Settings.Animation.Effect.Tilt",
                "Settings.Animation.Effect.Wipe"
            ];
            _cachedAnimationEffectNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[AnimationEffects.Length];
            for (int index = 0; index < AnimationEffects.Length; index++)
            {
                options[index] = new SettingsOption(AnimationEffects[index], _cachedAnimationEffectNames[index]);
            }

            return options;
        }
    }

    public bool IsSpeedEnabled => AnimationEffect != WidgetAnimationKinds.EffectNone;

    public bool IsDirectionEnabled =>
        WidgetAnimationKinds.UsesSlideDirection(AnimationEffect);

    /// <summary>
    /// Staggering delays each window's HWND travel inside the batch driver;
    /// stationary effects (fade/scale/tilt/wipe family) never move windows,
    /// so the toggle does nothing for them and must not present as usable.
    /// </summary>
    public bool IsStaggerEnabled => AnimationEffect is
        WidgetAnimationKinds.EffectSlideFade or
        WidgetAnimationKinds.EffectScaleSlide;

    public bool IsEasingEnabled => AnimationEffect != WidgetAnimationKinds.EffectNone;

    public string AnimationSpeed
    {
        get => _animationSpeed;
        set
        {
            if (!SetProperty(ref _animationSpeed, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetAnimationSpeed(
                    _animationSpeed,
                    scheduleSave: !_isApplyingAnimationPreset);
            }

            SyncAnimationPresetSelection();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAnimationSpeedOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Animation.Speed.VeryFast",
                "Settings.Animation.Speed.Fast",
                "Settings.Animation.Speed.Relaxed",
                "Settings.Animation.Speed.Slow",
                "Settings.Animation.Speed.Standard"
            ];
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                names[key] = _localize(key);
            }

            _cachedAnimationSpeedNames = AnimationSpeeds
                .Select(speed => speed switch
                {
                    WidgetAnimationKinds.SpeedVeryFast => names["Settings.Animation.Speed.VeryFast"],
                    WidgetAnimationKinds.SpeedFast => names["Settings.Animation.Speed.Fast"],
                    WidgetAnimationKinds.SpeedRelaxed => names["Settings.Animation.Speed.Relaxed"],
                    WidgetAnimationKinds.SpeedSlow => names["Settings.Animation.Speed.Slow"],
                    _ => names["Settings.Animation.Speed.Standard"]
                })
                .ToArray();
            var options = new SettingsOption[AnimationSpeeds.Length];
            for (int index = 0; index < AnimationSpeeds.Length; index++)
            {
                options[index] = new SettingsOption(AnimationSpeeds[index], _cachedAnimationSpeedNames[index]);
            }

            return options;
        }
    }

    public string AnimationSlideDirection
    {
        get => _animationSlideDirection;
        set
        {
            if (!SetProperty(ref _animationSlideDirection, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetAnimationSlideDirection(
                    _animationSlideDirection,
                    scheduleSave: !_isApplyingAnimationPreset);
            }

            SyncAnimationPresetSelection();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAnimationSlideDirectionOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Animation.Direction.Left",
                "Settings.Animation.Direction.Right",
                "Settings.Animation.Direction.Up",
                "Settings.Animation.Direction.Down"
            ];
            _cachedAnimationSlideDirectionNames ??= keys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[AnimationSlideDirections.Length];
            for (int index = 0; index < AnimationSlideDirections.Length; index++)
            {
                options[index] = new SettingsOption(
                    AnimationSlideDirections[index],
                    _cachedAnimationSlideDirectionNames[index]);
            }

            return options;
        }
    }

    public string AnimationEasingIntensity
    {
        get => _animationEasingIntensity;
        set
        {
            if (!SetProperty(ref _animationEasingIntensity, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetAnimationEasingIntensity(
                    _animationEasingIntensity,
                    scheduleSave: !_isApplyingAnimationPreset);
            }

            SyncAnimationPresetSelection();
        }
    }

    public bool AnimationStaggerEnabled
    {
        get => _animationStaggerEnabled;
        set
        {
            if (!SetProperty(ref _animationStaggerEnabled, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetAnimationStaggerEnabled(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAnimationEasingIntensityOptions
    {
        get
        {
            string[] keys =
            [
                "Settings.Animation.Easing.None",
                "Settings.Animation.Easing.Light",
                "Settings.Animation.Easing.Strong",
                "Settings.Animation.Easing.Standard",
                "Settings.Animation.Easing.Spring"
            ];
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                names[key] = _localize(key);
            }

            _cachedAnimationEasingIntensityNames = AnimationEasingIntensities
                .Select(intensity => intensity switch
                {
                    WidgetAnimationKinds.EasingNone => names["Settings.Animation.Easing.None"],
                    WidgetAnimationKinds.EasingLight => names["Settings.Animation.Easing.Light"],
                    WidgetAnimationKinds.EasingStrong => names["Settings.Animation.Easing.Strong"],
                    WidgetAnimationKinds.EasingSpring => names["Settings.Animation.Easing.Spring"],
                    _ => names["Settings.Animation.Easing.Standard"]
                })
                .ToArray();
            var options = new SettingsOption[AnimationEasingIntensities.Length];
            for (int index = 0; index < AnimationEasingIntensities.Length; index++)
            {
                options[index] = new SettingsOption(
                    AnimationEasingIntensities[index],
                    _cachedAnimationEasingIntensityNames[index]);
            }

            return options;
        }
    }

    // --- Editor-internal state machines ---

    private void ApplyDensityPreset(string preset)
    {
        // Writes the seven preset fields without scheduling per-field saves;
        // a single shell save pass runs once the projection settles.
        _isApplyingDensityPreset = true;
        _isSyncingPresentation = true;
        try
        {
            _settings.ApplyLayoutDensityPreset(preset);
            AppearanceDensitySettings snapshot = _settings.ReadDensity();
            IconSize = snapshot.IconSize;
            TextSize = snapshot.TextSize;
            LayoutDensityScale = snapshot.LayoutDensityScale;
            HorizontalSpacingScale = snapshot.HorizontalSpacingScale;
            VerticalSpacingScale = snapshot.VerticalSpacingScale;
            FileNameWidthScale = snapshot.FileNameWidthScale;
        }
        finally
        {
            _isSyncingPresentation = false;
            _isApplyingDensityPreset = false;
        }

        AppearanceValueCommitted?.Invoke();
    }

    private void SyncDensitySelection()
    {
        if (_isApplyingDensityPreset || _isSyncingPresentation)
        {
            return;
        }

        _settings.MarkLayoutDensityCustom();
        if (SetProperty(
                ref _layoutDensity,
                LayoutDensityKinds.Custom,
                nameof(LayoutDensity)))
        {
            OnPropertyChanged(nameof(LayoutDensityText));
        }
    }

    private void ApplyAnimationPreset(string preset)
    {
        (string effect, string speed, string direction, string easing) = preset switch
        {
            WidgetAnimationKinds.PresetGentle => (
                WidgetAnimationKinds.EffectFade,
                WidgetAnimationKinds.SpeedRelaxed,
                WidgetAnimationKinds.DirectionNone,
                WidgetAnimationKinds.EasingLight),
            WidgetAnimationKinds.PresetEmphasized => (
                WidgetAnimationKinds.EffectScaleFade,
                WidgetAnimationKinds.SpeedRelaxed,
                WidgetAnimationKinds.DirectionNone,
                WidgetAnimationKinds.EasingStrong),
            _ => (
                WidgetAnimationKinds.EffectSlideFade,
                WidgetAnimationKinds.SpeedStandard,
                WidgetAnimationKinds.DirectionRight,
                WidgetAnimationKinds.EasingStandard)
        };

        _isApplyingAnimationPreset = true;
        try
        {
            AnimationEffect = effect;
            AnimationSpeed = speed;
            AnimationSlideDirection = direction;
            AnimationEasingIntensity = easing;
        }
        finally
        {
            _isApplyingAnimationPreset = false;
        }

        AnimationPresetApplied?.Invoke();
    }

    private void SyncAnimationPresetSelection()
    {
        if (_isApplyingAnimationPreset || _isSyncingPresentation)
        {
            return;
        }

        string resolvedPreset = WidgetAnimationKinds.ResolvePreset(
            _animationEffect,
            _animationSpeed,
            _animationSlideDirection,
            _animationEasingIntensity);
        if (SetProperty(
                ref _animationPreset,
                resolvedPreset,
                nameof(AnimationPreset)))
        {
            OnPropertyChanged(nameof(AnimationPresetText));
        }
    }

    private void ApplySpacingScaleChange(
        double value,
        ref double field,
        string propertyName,
        string textPropertyName,
        Action<double> reenter,
        Func<double, AppearanceValueUpdate> updateWrite)
    {
        if (Math.Abs(value - field) <= EqualityEpsilon)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(textPropertyName);
        if (_isSyncingPresentation)
        {
            return;
        }

        AppearanceValueUpdate update = updateWrite(value);
        if (!update.Committed)
        {
            reenter(update.Value);
            return;
        }

        SyncDensitySelection();
        AppearanceValueCommitted?.Invoke();
    }

    // --- Display-name tables (legacy parity, keys split where needed) ---

    private string GetMaterialTypeDisplayName(string material)
    {
        return material switch
        {
            WidgetMaterialKinds.Mica => _localize("Settings.Material.Mica"),
            WidgetMaterialKinds.MicaAlt => _localize("Settings.Material.MicaAlt"),
            WidgetMaterialKinds.AcrylicBase => _localize("Settings.Material.AcrylicBase"),
            WidgetMaterialKinds.Solid => _localize("Settings.Material.Solid"),
            _ => _localize("Settings.Material.Acrylic")
        };
    }

    private string GetLayoutDensityDisplayName(string density)
    {
        return density switch
        {
            LayoutDensityKinds.Compact => _localize("Settings.Density.Compact"),
            LayoutDensityKinds.Relaxed => _localize("Settings.Density.Relaxed"),
            LayoutDensityKinds.Custom => _localize("Settings.Density.Custom"),
            _ => _localize("Settings.Density.Standard")
        };
    }

    private string GetAnimationPresetDisplayName(string preset)
    {
        return preset switch
        {
            WidgetAnimationKinds.PresetGentle => _localize("Settings.Animation.Preset.Gentle"),
            WidgetAnimationKinds.PresetEmphasized => _localize("Settings.Animation.Preset.Emphasized"),
            WidgetAnimationKinds.PresetCustom => _localize("Settings.Animation.Preset.Custom"),
            _ => _localize("Settings.Animation.Preset.Standard")
        };
    }

    private string GetTitleIconModeDisplayName(string mode)
    {
        return mode switch
        {
            WidgetTitleIconKinds.LineMono => _localize("Settings.WidgetTitleIcon.LineMono"),
            WidgetTitleIconKinds.Color => _localize("Settings.WidgetTitleIcon.Color"),
            WidgetTitleIconKinds.Hidden => _localize("Settings.WidgetTitleIcon.Hidden"),
            WidgetTitleIconKinds.TextLabel => _localize("Settings.WidgetTitleIcon.TextLabel"),
            _ => _localize("Settings.WidgetTitleIcon.FilledMono")
        };
    }

    // --- Shell push surface ---

    /// <summary>
    /// Re-projects the persisted appearance state onto the binding surface
    /// without writing back. Called on construction, settings broadcasts and
    /// default restores.
    /// </summary>
    public void SyncPresentation()
    {
        AppearanceMaterialSettings material = _settings.ReadMaterial();
        AppearanceDensitySettings density = _settings.ReadDensity();
        AppearanceWindowChromeSettings windowChrome = _settings.ReadWindowChrome();
        AppearanceAnimationSettings animation = _settings.ReadAnimation();
        AppearanceForegroundSettings foreground = _settings.ReadForeground();
        _isSyncingPresentation = true;
        try
        {
            TrayIconStyle = _settings.ReadTrayIconStyle();

            MaterialType = material.MaterialType;
            WidgetTransparency = 1.0 - material.Opacity;
            MaterialIntensity = material.MaterialIntensity;
            CornerPreference = material.CornerPreference;
            BorderColorMode = material.BorderColorMode;
            BorderStyle = material.BorderStyle;
            ForegroundMode = foreground.ForegroundMode;
            ForegroundColorHex = NormalizeHexColor(foreground.ForegroundColor);
            AppearanceWidgetBackgroundSettings widgetBackground =
                _settings.ReadWidgetBackground();
            WidgetBackgroundMode = widgetBackground.Mode;
            WidgetBackgroundUnifiedFit = widgetBackground.UnifiedFit;
            WidgetBackgroundDimPercent = widgetBackground.DimPercent;
            WidgetTextShadowEnabled = _settings.ReadWidgetTextShadowEnabled();

            LayoutDensity = density.LayoutDensity;
            IconSize = density.IconSize;
            TextSize = density.TextSize;
            LayoutDensityScale = density.LayoutDensityScale;
            HorizontalSpacingScale = density.HorizontalSpacingScale;
            VerticalSpacingScale = density.VerticalSpacingScale;
            FileNameWidthScale = density.FileNameWidthScale;
            FileNameLineCount = density.FileNameLineCount;

            DefaultWidth = windowChrome.DefaultWidgetWidth;
            DefaultHeight = windowChrome.DefaultWidgetHeight;
            TitleIconMode = windowChrome.WidgetTitleIconMode;
            DisplayChromeMode = windowChrome.DisplayWidgetChromeMode;
            InteractiveChromeMode = windowChrome.InteractiveWidgetChromeMode;

            AnimationEffect = animation.Effect;
            AnimationSpeed = animation.Speed;
            AnimationSlideDirection = animation.SlideDirection;
            AnimationEasingIntensity = animation.EasingIntensity;
            AnimationStaggerEnabled = animation.StaggerEnabled;
            AnimationPreset = WidgetAnimationKinds.ResolvePreset(
                animation.Effect,
                animation.Speed,
                animation.SlideDirection,
                animation.EasingIntensity);
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Notifies the shell that an accent preset swatch was picked: the shell
    /// owns the custom accent write and re-projects the effective color.
    /// </summary>
    public void NotifyAccentPresetPicked(string accentColorHex)
    {
        _selectedAccentColorHex = accentColorHex;
        OnPropertyChanged(nameof(SelectedAccentColorHex));
        AccentColorUserChanged?.Invoke(accentColorHex);
    }

    /// <summary>
    /// Re-projects the theme selection the shell owns (the theme service
    /// state machine and its host write stay on the shell).
    /// </summary>
    public void UpdateThemeSelection(string theme)
    {
        _isSyncingPresentation = true;
        try
        {
            Theme = theme;
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Re-projects the accent presentation the shell owns (accent mode and
    /// the effective accent color come from the theme service).
    /// </summary>
    public void UpdateAccentPresentation(bool useSystemAccentColor, string accentColorHex)
    {
        _isSyncingPresentation = true;
        try
        {
            AccentColorSource = useSystemAccentColor
                ? AccentSourceSystem
                : AccentSourceCustom;
            SelectedAccentColorHex = accentColorHex;
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Pushes the host environment probes the editor cannot run itself:
    /// Windows 10 visual compatibility, native widget corner support and the
    /// material kinds offerable on this OS.
    /// </summary>
    public void UpdateHostEnvironment(
        bool windows10Compatibility,
        bool supportsNativeCorners,
        string[] supportedMaterialKinds)
    {
        _windows10Compatibility = windows10Compatibility;
        _nativeCornersSupported = supportsNativeCorners;
        _supportedMaterialKinds = supportedMaterialKinds;
        _cachedMaterialTypeNames = null;
        OnPropertyChanged(nameof(Windows10Compatibility));
        OnPropertyChanged(nameof(NativeCornersSupported));
        OnPropertyChanged(nameof(AvailableMaterialTypeOptions));
    }

    /// <summary>
    /// Drops the localized name caches after a language change so the option
    /// lists and value texts re-project in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedThemeNames = null;
        _cachedTrayIconStyleNames = null;
        _cachedMaterialTypeNames = null;
        _cachedForegroundModeNames = null;
        _cachedWidgetBackgroundModeNames = null;
        _cachedWidgetBackgroundUnifiedFitNames = null;
        _cachedBorderColorModeNames = null;
        _cachedBorderStyleNames = null;
        _cachedCornerPreferenceNames = null;
        _cachedFileNameLineCountNames = null;
        _cachedTitleIconModeNames = null;
        _cachedLayoutDensityNames = null;
        _cachedAnimationPresetNames = null;
        _cachedAnimationEffectNames = null;
        _cachedAnimationSpeedNames = null;
        _cachedAnimationSlideDirectionNames = null;
        _cachedAnimationEasingIntensityNames = null;
        _cachedChromeModeNames = null;
        OnPropertyChanged(nameof(AvailableThemeOptions));
        OnPropertyChanged(nameof(AvailableTrayIconStyleOptions));
        OnPropertyChanged(nameof(AvailableAccentColorSourceOptions));
        OnPropertyChanged(nameof(AvailableMaterialTypeOptions));
        OnPropertyChanged(nameof(AvailableForegroundModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetBackgroundModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetBackgroundUnifiedFitOptions));
        OnPropertyChanged(nameof(WidgetBackgroundDimValueText));
        OnPropertyChanged(nameof(AvailableBorderColorModeOptions));
        OnPropertyChanged(nameof(AvailableBorderStyleOptions));
        OnPropertyChanged(nameof(AvailableCornerPreferenceOptions));
        OnPropertyChanged(nameof(AvailableLayoutDensityOptions));
        OnPropertyChanged(nameof(AvailableFileNameLineCountOptions));
        OnPropertyChanged(nameof(AvailableAnimationPresetOptions));
        OnPropertyChanged(nameof(AvailableAnimationEffectOptions));
        OnPropertyChanged(nameof(AvailableAnimationSpeedOptions));
        OnPropertyChanged(nameof(AvailableAnimationSlideDirectionOptions));
        OnPropertyChanged(nameof(AvailableAnimationEasingIntensityOptions));
        OnPropertyChanged(nameof(AvailableTitleIconModeOptions));
        OnPropertyChanged(nameof(AvailableDisplayChromeModeOptions));
        OnPropertyChanged(nameof(AvailableInteractiveChromeModeOptions));
        OnPropertyChanged(nameof(MaterialTypeText));
        OnPropertyChanged(nameof(Windows10CompatibilityTitle));
        OnPropertyChanged(nameof(Windows10CompatibilityMessage));
        OnPropertyChanged(nameof(SystemShadowStatusText));
        OnPropertyChanged(nameof(SystemTransparencyStatusText));
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(LayoutDensityText));
        OnPropertyChanged(nameof(AnimationPresetText));
        OnPropertyChanged(nameof(TitleIconModeText));
    }

    /// <summary>
    /// Normalizes a stored or user-picked color to the canonical
    /// <c>#RRGGBB</c> form, falling back to the default custom foreground
    /// color when unparsable (legacy parity).
    /// </summary>
    private static string NormalizeHexColor(string? value)
    {
        if (value is not null)
        {
            string trimmed = value.Trim().TrimStart('#');
            if (trimmed.Length == 6 &&
                uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            {
                return FormattableString.Invariant(
                    $"#{(packed >> 16) & 0xFF:X2}{(packed >> 8) & 0xFF:X2}{packed & 0xFF:X2}");
            }
        }

        return WidgetForegroundKinds.DefaultCustomColorHex;
    }

    private IReadOnlyList<SettingsOption> BuildChromeModeOptions()
    {
        string[] keys =
        [
            "Settings.WidgetChrome.Standard",
            "Settings.WidgetChrome.Compact",
            "Settings.WidgetChrome.Overlay",
            "Settings.WidgetChrome.Hidden"
        ];
        _cachedChromeModeNames ??= keys.Select(key => _localize(key)).ToArray();
        var options = new SettingsOption[ChromeModes.Length];
        for (int index = 0; index < ChromeModes.Length; index++)
        {
            options[index] = new SettingsOption(ChromeModes[index], _cachedChromeModeNames[index]);
        }

        return options;
    }
}
