using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Performance;

/// <summary>
/// Performance-section settings editor (batch 50). Owns the section's XAML
/// binding surface — the preset-mode combo (also re-bound through the
/// General section's inline drill-down row), the four custom-mode detail
/// combos, the decorative-animation flyout summary and the three
/// working-set trim switches. Reads project from the performance
/// coordinator's policy-resolved snapshot; user edits write through the
/// performance coordinator (the idle and immediate-hidden trim switches
/// keep their interaction-coordinator write owner) and re-project the whole
/// surface afterwards, so the custom-mode switch and the preset's detail
/// re-resolution render exactly as the legacy shell setters did. The editor
/// references neither App nor WinUI nor any service; localization arrives
/// as delegates.
/// </summary>
public sealed partial class PerformanceSettingsViewModel : ObservableObject
{
    private readonly IPerformanceSettings _settings;
    private readonly IInteractionSettings _interactionSettings;
    private readonly Func<string, string> _localize;
    private readonly Func<bool> _isChinese;
    private bool _isSyncingPresentation;

    private string _selectedPerformanceMode = PerformanceOptionKinds.ModeResourceSaver;
    private int _selectedHiddenCacheCleanupDelaySeconds =
        PerformanceOptionKinds.CleanupAfter30Seconds;
    private int _selectedVisibleIdleCacheCleanupDelaySeconds =
        PerformanceOptionKinds.CleanupAfter5Minutes;
    private string _selectedPerformanceCacheBudget = PerformanceOptionKinds.CacheBudgetSmall;
    private string _selectedHiddenCacheCleanupScope =
        PerformanceOptionKinds.HiddenCacheCleanupScopeWarm;
    private bool _enableTextMarqueeAnimations = true;
    private bool _enableVinylRotationAnimations = true;
    private bool _enableGlanceImageAutoRotation = true;
    private bool _enableCompactAmbientAnimations = true;

    public PerformanceSettingsViewModel(
        IPerformanceSettings settings,
        IInteractionSettings interactionSettings,
        Func<string, string> localize,
        Func<bool> isChinese)
    {
        _settings = settings;
        _interactionSettings = interactionSettings;
        _localize = localize;
        _isChinese = isChinese;
        SyncPresentation();
    }

    public IReadOnlyList<SettingsOption> AvailablePerformanceModeOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            SettingsOption[] options =
            [
                new(
                    PerformanceOptionKinds.ModeBalanced,
                    _localize("Settings.Performance.Mode.Balanced")),
                new(
                    PerformanceOptionKinds.ModeResourceSaver,
                    _localize("Settings.Performance.Mode.ResourceSaver"))
            ];
            if (string.Equals(
                    _selectedPerformanceMode,
                    PerformanceOptionKinds.ModeCustom,
                    StringComparison.Ordinal))
            {
                options =
                [
                    .. options,
                    new(
                        PerformanceOptionKinds.ModeCustom,
                        _localize("Settings.Performance.Mode.Custom"))
                ];
            }

            return WrapOptions(options);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableHiddenCacheCleanupDelayOptions =>
        WrapOptions(
        [
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter30Seconds,
                "Settings.Performance.HiddenCleanup.30Seconds"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter1Minute,
                "Settings.Performance.HiddenCleanup.1Minute"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter5Minutes,
                "Settings.Performance.HiddenCleanup.5Minutes")
        ]);

    public IReadOnlyList<SettingsOption> AvailableVisibleIdleCacheCleanupDelayOptions =>
        WrapOptions(
        [
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter30Seconds,
                "Settings.Performance.HiddenCleanup.30Seconds"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter1Minute,
                "Settings.Performance.HiddenCleanup.1Minute"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter5Minutes,
                "Settings.Performance.HiddenCleanup.5Minutes"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter10Minutes,
                "Settings.Performance.HiddenCleanup.10Minutes"),
            CreateCleanupDelayOption(
                PerformanceOptionKinds.CleanupAfter15Minutes,
                "Settings.Performance.HiddenCleanup.15Minutes")
        ]);

    public IReadOnlyList<SettingsOption> AvailablePerformanceCacheBudgetOptions =>
        WrapOptions(
        [
            new(
                PerformanceOptionKinds.CacheBudgetSmall,
                _localize("Settings.Performance.CacheBudget.Small")),
            new(
                PerformanceOptionKinds.CacheBudgetBalanced,
                _localize("Settings.Performance.CacheBudget.Balanced")),
            new(
                PerformanceOptionKinds.CacheBudgetLarge,
                _localize("Settings.Performance.CacheBudget.Large"))
        ]);

    public IReadOnlyList<SettingsOption> AvailableHiddenCacheCleanupScopeOptions =>
        WrapOptions(
        [
            new(
                PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable,
                _localize(
                    "Settings.Performance.HiddenScope.AllRecreatable")),
            new(
                PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
                _localize(
                    "Settings.Performance.HiddenScope.Warm"))
        ]);

    public string SelectedPerformanceMode
    {
        get => _selectedPerformanceMode;
        set
        {
            string normalized = PerformanceOptionKinds.NormalizeMode(value);
            if (!SetProperty(ref _selectedPerformanceMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AvailablePerformanceModeOptions));

            if (_isSyncingPresentation)
            {
                return;
            }

            // The preset write re-resolves the detail fields onto the
            // settings; re-project the whole surface so the combos follow
            // (the legacy SynchronizePerformanceDetailSelection).
            _settings.SetPerformanceMode(normalized);
            SyncPresentation();
        }
    }

    public int SelectedHiddenCacheCleanupDelaySeconds
    {
        get => _selectedHiddenCacheCleanupDelaySeconds;
        set
        {
            if (!SetProperty(ref _selectedHiddenCacheCleanupDelaySeconds, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetHiddenCacheCleanupDelaySeconds(value);
            SyncPresentation();
        }
    }

    public int SelectedVisibleIdleCacheCleanupDelaySeconds
    {
        get => _selectedVisibleIdleCacheCleanupDelaySeconds;
        set
        {
            if (!SetProperty(ref _selectedVisibleIdleCacheCleanupDelaySeconds, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetVisibleIdleCacheCleanupDelaySeconds(value);
            SyncPresentation();
        }
    }

    public string SelectedPerformanceCacheBudget
    {
        get => _selectedPerformanceCacheBudget;
        set
        {
            string normalized = PerformanceOptionKinds.NormalizeCacheBudget(value);
            if (!SetProperty(ref _selectedPerformanceCacheBudget, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetPerformanceCacheBudget(normalized);
            SyncPresentation();
        }
    }

    public string SelectedHiddenCacheCleanupScope
    {
        get => _selectedHiddenCacheCleanupScope;
        set
        {
            string normalized = PerformanceOptionKinds.NormalizeHiddenCacheCleanupScope(value);
            if (!SetProperty(ref _selectedHiddenCacheCleanupScope, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetHiddenCacheCleanupScope(normalized);
            OnPropertyChanged(nameof(KeepWarmCacheWhenHidden));
            SyncPresentation();
        }
    }

    public bool KeepWarmCacheWhenHidden
    {
        get => string.Equals(
            _selectedHiddenCacheCleanupScope,
            PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
            StringComparison.Ordinal);
        set => SelectedHiddenCacheCleanupScope = value
            ? PerformanceOptionKinds.HiddenCacheCleanupScopeWarm
            : PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable;
    }

    public bool AllowTextMarquee
    {
        get => _enableTextMarqueeAnimations;
        set => SetDecorativeAnimationOption(
            PerformanceOptionKinds.DecorativeAnimationTextMarquee,
            ref _enableTextMarqueeAnimations,
            value);
    }

    public bool AllowVinylRotation
    {
        get => _enableVinylRotationAnimations;
        set => SetDecorativeAnimationOption(
            PerformanceOptionKinds.DecorativeAnimationVinylRotation,
            ref _enableVinylRotationAnimations,
            value);
    }

    public bool AllowGlanceRotation
    {
        get => _enableGlanceImageAutoRotation;
        set => SetDecorativeAnimationOption(
            PerformanceOptionKinds.DecorativeAnimationGlanceRotation,
            ref _enableGlanceImageAutoRotation,
            value);
    }

    public bool AllowCompactAmbient
    {
        get => _enableCompactAmbientAnimations;
        set => SetDecorativeAnimationOption(
            PerformanceOptionKinds.DecorativeAnimationCompactAmbient,
            ref _enableCompactAmbientAnimations,
            value);
    }

    // The three working-set trim switches. The idle and immediate-hidden
    // switches persist through the interaction coordinator (their write
    // owner since the interaction-section batch); the quiescence switch
    // persists through the performance coordinator.
    [ObservableProperty]
    public partial bool IdleWorkingSetTrimEnabled { get; set; }

    [ObservableProperty]
    public partial bool ImmediateHiddenWorkingSetTrimEnabled { get; set; }

    [ObservableProperty]
    public partial bool QuiescenceWorkingSetTrimEnabled { get; set; }

    [ObservableProperty]
    public partial bool KeepCapsuleContentWarm { get; set; }

    public IReadOnlyList<string> AvailableContinuousDecorativeAnimationOptions =>
        PerformanceOptionKinds.SupportedDecorativeAnimationOptions;

    public string ContinuousDecorativeAnimationsSummaryText
    {
        get
        {
            string[] selected = AvailableContinuousDecorativeAnimationOptions
                .Where(IsContinuousDecorativeAnimationSelected)
                .ToArray();
            if (selected.Length == 0)
            {
                return _localize("Common.Off");
            }

            if (selected.Length == AvailableContinuousDecorativeAnimationOptions.Count)
            {
                return _localize(
                    "Settings.Performance.DecorativeAnimations.All");
            }

            return string.Join(
                _isChinese() ? "、" : ", ",
                selected.Select(GetContinuousDecorativeAnimationDisplayName));
        }
    }

    public bool IsContinuousDecorativeAnimationSelected(string option) =>
        option switch
        {
            PerformanceOptionKinds.DecorativeAnimationTextMarquee =>
                _enableTextMarqueeAnimations,
            PerformanceOptionKinds.DecorativeAnimationVinylRotation =>
                _enableVinylRotationAnimations,
            PerformanceOptionKinds.DecorativeAnimationGlanceRotation =>
                _enableGlanceImageAutoRotation,
            PerformanceOptionKinds.DecorativeAnimationCompactAmbient =>
                _enableCompactAmbientAnimations,
            _ => false
        };

    public string GetContinuousDecorativeAnimationDisplayName(string option) =>
        option switch
        {
            PerformanceOptionKinds.DecorativeAnimationTextMarquee =>
                _localize(
                    "Settings.Performance.DecorativeAnimations.Marquee"),
            PerformanceOptionKinds.DecorativeAnimationVinylRotation =>
                _localize(
                    "Settings.Performance.DecorativeAnimations.Vinyl"),
            PerformanceOptionKinds.DecorativeAnimationGlanceRotation =>
                _localize(
                    "Settings.Performance.DecorativeAnimations.GlanceRotation"),
            PerformanceOptionKinds.DecorativeAnimationCompactAmbient =>
                _localize(
                    "Settings.Performance.DecorativeAnimations.Ambient"),
            _ => option
        };

    public void ToggleContinuousDecorativeAnimation(string option)
    {
        switch (option)
        {
            case PerformanceOptionKinds.DecorativeAnimationTextMarquee:
                _enableTextMarqueeAnimations = !_enableTextMarqueeAnimations;
                break;
            case PerformanceOptionKinds.DecorativeAnimationVinylRotation:
                _enableVinylRotationAnimations = !_enableVinylRotationAnimations;
                break;
            case PerformanceOptionKinds.DecorativeAnimationGlanceRotation:
                _enableGlanceImageAutoRotation = !_enableGlanceImageAutoRotation;
                break;
            case PerformanceOptionKinds.DecorativeAnimationCompactAmbient:
                _enableCompactAmbientAnimations = !_enableCompactAmbientAnimations;
                break;
            default:
                return;
        }

        // The coordinator persists the switch, re-derives the legacy combined
        // flag and flips the preset mode to Custom; the re-projection below
        // updates the mode combo and its Custom entry.
        _settings.SetDecorativeAnimationEnabled(
            option,
            IsContinuousDecorativeAnimationSelected(option));
        SyncPresentation();
    }

    /// <summary>
    /// Re-projects the binding surface from the coordinator's read snapshot.
    /// Silent: no writes and no preset application while projecting.
    /// </summary>
    public void SyncPresentation()
    {
        _isSyncingPresentation = true;
        try
        {
            PerformancePresentationSettings presentation =
                _settings.ReadPresentation();
            _selectedPerformanceMode = presentation.Mode;
            _selectedHiddenCacheCleanupDelaySeconds =
                presentation.HiddenCacheCleanupDelaySeconds;
            _selectedVisibleIdleCacheCleanupDelaySeconds =
                presentation.VisibleIdleCacheCleanupDelaySeconds;
            _selectedPerformanceCacheBudget = presentation.CacheBudget;
            _selectedHiddenCacheCleanupScope = presentation.HiddenCacheCleanupScope;
            _enableTextMarqueeAnimations = presentation.AllowTextMarqueeAnimations;
            _enableVinylRotationAnimations = presentation.AllowVinylRotationAnimations;
            _enableGlanceImageAutoRotation = presentation.AllowGlanceImageAutoRotation;
            _enableCompactAmbientAnimations = presentation.AllowCompactAmbientAnimations;
            IdleWorkingSetTrimEnabled = presentation.IdleWorkingSetTrimEnabled;
            ImmediateHiddenWorkingSetTrimEnabled =
                presentation.ImmediateHiddenWorkingSetTrimEnabled;
            QuiescenceWorkingSetTrimEnabled =
                presentation.QuiescenceWorkingSetTrimEnabled;
            KeepCapsuleContentWarm = presentation.KeepCapsuleContentWarm;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(SelectedPerformanceMode));
        OnPropertyChanged(nameof(AvailablePerformanceModeOptions));
        OnPropertyChanged(nameof(SelectedHiddenCacheCleanupDelaySeconds));
        OnPropertyChanged(nameof(SelectedVisibleIdleCacheCleanupDelaySeconds));
        OnPropertyChanged(nameof(SelectedPerformanceCacheBudget));
        OnPropertyChanged(nameof(SelectedHiddenCacheCleanupScope));
        OnPropertyChanged(nameof(KeepWarmCacheWhenHidden));
        OnPropertyChanged(nameof(AllowTextMarquee));
        OnPropertyChanged(nameof(AllowVinylRotation));
        OnPropertyChanged(nameof(AllowGlanceRotation));
        OnPropertyChanged(nameof(AllowCompactAmbient));
        OnPropertyChanged(nameof(ContinuousDecorativeAnimationsSummaryText));
        OnPropertyChanged(nameof(IdleWorkingSetTrimEnabled));
        OnPropertyChanged(nameof(ImmediateHiddenWorkingSetTrimEnabled));
        OnPropertyChanged(nameof(QuiescenceWorkingSetTrimEnabled));
        OnPropertyChanged(nameof(KeepCapsuleContentWarm));
    }

    /// <summary>
    /// Rebuilds the localized option tables and summaries after a language
    /// change (the option getters localize on demand; re-raise them so the
    /// combos drop stale display names).
    /// </summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(AvailablePerformanceModeOptions));
        OnPropertyChanged(nameof(AvailableHiddenCacheCleanupDelayOptions));
        OnPropertyChanged(nameof(AvailableVisibleIdleCacheCleanupDelayOptions));
        OnPropertyChanged(nameof(AvailablePerformanceCacheBudgetOptions));
        OnPropertyChanged(nameof(AvailableHiddenCacheCleanupScopeOptions));
        OnPropertyChanged(nameof(ContinuousDecorativeAnimationsSummaryText));
    }

    partial void OnIdleWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isSyncingPresentation)
        {
            return;
        }

        // Write owner since the interaction-section batch: the interaction
        // coordinator persists both trim switches through the performance
        // slice.
        _interactionSettings.SetIdleWorkingSetTrimEnabled(value);
    }

    partial void OnImmediateHiddenWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isSyncingPresentation)
        {
            return;
        }

        _interactionSettings.SetImmediateHiddenWorkingSetTrimEnabled(value);
    }

    partial void OnQuiescenceWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isSyncingPresentation)
        {
            return;
        }

        _settings.SetQuiescenceWorkingSetTrimEnabled(value);
    }

    partial void OnKeepCapsuleContentWarmChanged(bool value)
    {
        if (_isSyncingPresentation)
        {
            return;
        }

        _settings.SetKeepCapsuleContentWarm(value);
    }

    private void SetDecorativeAnimationOption(string option, ref bool backingField, bool value)
    {
        if (backingField == value)
        {
            return;
        }

        backingField = value;
        if (_isSyncingPresentation)
        {
            return;
        }

        _settings.SetDecorativeAnimationEnabled(option, value);
        SyncPresentation();
    }

    private SettingsOption CreateCleanupDelayOption(int value, string key) =>
        new(value, _localize(key));

    // Build real SettingsOption[] arrays (not collection expressions): the
    // hidden read-only-array type cannot marshal across the WinRT ABI in
    // Native AOT builds and would leave every {Binding} ItemsSource empty.
    private static IReadOnlyList<SettingsOption> WrapOptions(SettingsOption[] options) =>
        options;
}
