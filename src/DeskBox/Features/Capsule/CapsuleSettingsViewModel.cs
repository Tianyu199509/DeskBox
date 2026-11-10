using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Capsule;

/// <summary>
/// Capsule/compact-mode section editor and binding surface (batch 44). The
/// capsule family sections (the main capsule section plus the behavior,
/// arrangement, animation and overrides subsections) switch their DataContext
/// to this editor: persisted fields bind TwoWay through
/// <see cref="ICapsuleSettings"/> with the coordinator owning normalization,
/// the preset pair-writes and the debounced save, while the hover-response
/// selection stays a derived view state that is never persisted (batch 33
/// semantics — presets write their delay pair, manual delay edits flip the
/// label to Custom). The widget/group override lists are built by the shell's
/// override state machine and pushed in as read-only view state. The editor
/// stays WinUI-free: visibility gates are booleans the XAML runs through
/// <c>SettingsBoolToVisibilityConverter</c>.
/// </summary>
public sealed partial class CapsuleSettingsViewModel : ObservableObject
{
    private readonly ICapsuleSettings _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _localizeFormat;

    private bool _isSyncingPresentation;
    private bool _isApplyingHoverResponse;

    private string[]? _cachedWidthModeNames;
    private string[]? _cachedExpansionDirectionNames;
    private string[]? _cachedContentModeNames;
    private string[]? _cachedArrangementNames;
    private string[]? _cachedBarPlacementNames;
    private string[]? _cachedBarDirectionNames;
    private string[]? _cachedAnimationEffectNames;
    private string[]? _cachedHoverResponseNames;
    private string[]? _cachedCollapseBehaviorNames;

    // Behavior state.
    private string _collapseBehavior = CapsuleOptionKinds.CollapseClick;
    private string _contentMode = CapsuleOptionKinds.ContentModeSmart;
    private bool _hideSensitiveContent;

    // Geometry state.
    private string _widthMode = CapsuleOptionKinds.WidthModeAligned;
    private string _expansionDirection = CapsuleOptionKinds.ExpansionDirectionAuto;

    // Arrangement state.
    private string _arrangementMode = CapsuleOptionKinds.ArrangementFree;
    private string _barPlacement = CapsuleOptionKinds.BarPlacementFloating;
    private string _barDirection = CapsuleOptionKinds.BarDirectionAuto;
    private double _barSpacing = CapsuleOptionKinds.DefaultBarSpacing;

    // Animation state.
    private string _animationEffect = CapsuleOptionKinds.AnimationSmooth;
    private double _animationDurationMs = CapsuleOptionKinds.DefaultAnimationDurationMs;

    // Hover-response derived view state.
    private string _hoverResponse = CapsuleOptionKinds.HoverResponseBalanced;
    private double _expandDelayMs = CapsuleOptionKinds.DefaultExpandDelayMs;
    private double _collapseDelayMs = CapsuleOptionKinds.DefaultCollapseDelayMs;

    // Shell-pushed override projection state.
    private IReadOnlyList<CapsuleOverrideSettingsItem> _overrideItems = [];
    private bool _hasOverrides;
    private string _overrideSummaryText = string.Empty;
    private bool _hasWidthOverrides;
    private bool _smartCollapseOverrideActive;

    public CapsuleSettingsViewModel(
        ICapsuleSettings settings,
        Func<string, string> localize,
        Func<string, object[], string> localizeFormat)
    {
        _settings = settings;
        _localize = localize;
        _localizeFormat = localizeFormat;
        SyncPresentation();
    }

    // --- Collapse behavior (main section) ---

    public string CollapseBehavior
    {
        get => _collapseBehavior;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeCollapseBehavior(value);
            if (!SetProperty(ref _collapseBehavior, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCapsuleActive));
            OnPropertyChanged(nameof(CollapseSummaryText));
            OnPropertyChanged(nameof(ShowHoverResponseEntry));
            OnPropertyChanged(nameof(IsBarEnabled));
            OnPropertyChanged(nameof(IsBarSpacingEnabled));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCollapseBehavior(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableCollapseBehaviorOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.CollapseExpanded,
                CapsuleOptionKinds.CollapseClick,
                CapsuleOptionKinds.CollapseSmart
            ];
            _cachedCollapseBehaviorNames ??= values
                .Select(value => _localize("Settings.CollapseBehavior." + value))
                .ToArray();
            return BuildOptions(values, _cachedCollapseBehaviorNames);
        }
    }

    public bool IsCapsuleActive =>
        !CapsuleOptionKinds.IsCollapseExpanded(_collapseBehavior) || _hasOverrides;

    public string CollapseSummaryText =>
        _hasOverrides && !string.IsNullOrWhiteSpace(_overrideSummaryText)
            ? $"{_localize("Settings.CollapseBehavior." + _collapseBehavior)} · {_overrideSummaryText}"
            : _localize("Settings.CollapseBehavior.Description");

    // The hover-response entry is gated on the Smart collapse behavior,
    // including any per-widget/per-group Smart override (pushed by the shell).
    public bool ShowHoverResponseEntry =>
        CapsuleOptionKinds.IsCollapseSmart(_collapseBehavior) || _smartCollapseOverrideActive;

    // --- Compact geometry (main section) ---

    public string WidthMode
    {
        get => _widthMode;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeWidthMode(value);
            if (!SetProperty(ref _widthMode, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCompactWidthMode(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidthModeOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.WidthModeAligned,
                CapsuleOptionKinds.WidthModeIndependent
            ];
            _cachedWidthModeNames ??=
            [
                _localize("Settings.Capsule.WidthMode.Aligned"),
                _localize("Settings.Capsule.WidthMode.Independent")
            ];
            return BuildOptions(values, _cachedWidthModeNames);
        }
    }

    public string ExpansionDirection
    {
        get => _expansionDirection;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeExpansionDirection(value);
            if (!SetProperty(ref _expansionDirection, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCompactExpansionDirection(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableExpansionDirectionOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.ExpansionDirectionAuto,
                CapsuleOptionKinds.ExpansionDirectionDown,
                CapsuleOptionKinds.ExpansionDirectionUp
            ];
            _cachedExpansionDirectionNames ??=
            [
                _localize("Settings.Capsule.ExpansionDirection.Auto"),
                _localize("Settings.Capsule.ExpansionDirection.Down"),
                _localize("Settings.Capsule.ExpansionDirection.Up")
            ];
            return BuildOptions(values, _cachedExpansionDirectionNames);
        }
    }

    // --- Compact content and privacy (main section) ---

    public string ContentMode
    {
        get => _contentMode;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeContentMode(value);
            if (!SetProperty(ref _contentMode, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCompactContentMode(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableContentModeOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.ContentModeMinimal,
                CapsuleOptionKinds.ContentModeSummary,
                CapsuleOptionKinds.ContentModeSmart
            ];
            _cachedContentModeNames ??=
            [
                _localize("Settings.CompactContent.Minimal"),
                _localize("Settings.CompactContent.Summary"),
                _localize("Settings.CompactContent.Smart")
            ];
            return BuildOptions(values, _cachedContentModeNames);
        }
    }

    public bool HideSensitiveContent
    {
        get => _hideSensitiveContent;
        set
        {
            if (!SetProperty(ref _hideSensitiveContent, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCompactHideSensitiveContent(value);
        }
    }

    // --- Capsule arrangement (main section + arrangement subsection) ---

    public string ArrangementMode
    {
        get => _arrangementMode;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeArrangementMode(value);
            if (!SetProperty(ref _arrangementMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBarEnabled));
            OnPropertyChanged(nameof(IsBarSpacingEnabled));
            OnPropertyChanged(nameof(ShowArrangementEntry));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCapsuleArrangementMode(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableArrangementOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.ArrangementFree,
                CapsuleOptionKinds.ArrangementBar
            ];
            _cachedArrangementNames ??=
            [
                _localize("Settings.Capsule.Arrangement.Free"),
                _localize("Settings.Capsule.Arrangement.Bar")
            ];
            return BuildOptions(values, _cachedArrangementNames);
        }
    }

    public bool IsBarEnabled => _arrangementMode == CapsuleOptionKinds.ArrangementBar;

    public bool IsBarSpacingEnabled => IsBarEnabled;

    public bool ShowArrangementEntry => IsBarEnabled;

    public string BarPlacement
    {
        get => _barPlacement;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeBarPlacement(value);
            if (!SetProperty(ref _barPlacement, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ArrangementDetailsSummary));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCapsuleBarPlacement(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableBarPlacementOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.BarPlacementFloating,
                CapsuleOptionKinds.BarPlacementTop,
                CapsuleOptionKinds.BarPlacementBottom,
                CapsuleOptionKinds.BarPlacementLeft,
                CapsuleOptionKinds.BarPlacementRight
            ];
            _cachedBarPlacementNames ??= values
                .Select(value => _localize("Settings.Capsule.Placement." + value))
                .ToArray();
            return BuildOptions(values, _cachedBarPlacementNames);
        }
    }

    public string BarDirection
    {
        get => _barDirection;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeBarDirection(value);
            if (!SetProperty(ref _barDirection, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ArrangementDetailsSummary));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCapsuleBarDirection(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableBarDirectionOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.BarDirectionAuto,
                CapsuleOptionKinds.BarDirectionHorizontal,
                CapsuleOptionKinds.BarDirectionVertical
            ];
            _cachedBarDirectionNames ??=
            [
                _localize("Settings.Capsule.Direction.Auto"),
                _localize("Settings.Capsule.Direction.Horizontal"),
                _localize("Settings.Capsule.Direction.Vertical")
            ];
            return BuildOptions(values, _cachedBarDirectionNames);
        }
    }

    public double BarSpacing
    {
        get => _barSpacing;
        set
        {
            double normalized = CapsuleOptionKinds.NormalizeBarSpacing(value);
            if (!SetProperty(ref _barSpacing, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(BarSpacingText));
            OnPropertyChanged(nameof(ArrangementDetailsSummary));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCapsuleBarSpacing(normalized);
        }
    }

    public string BarSpacingText => $"{Math.Round(BarSpacing):0} px";

    public string ArrangementDetailsSummary => _localizeFormat(
        "Settings.Capsule.Arrangement.Summary",
        [
            _localize("Settings.Capsule.Placement." + BarPlacement),
            _localize("Settings.Capsule.Direction." + BarDirection),
            BarSpacingText
        ]);

    // --- Compact animation (main section + animation subsection) ---

    public string AnimationEffect
    {
        get => _animationEffect;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeAnimationEffect(value);
            if (!SetProperty(ref _animationEffect, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowAnimationCustom));
            OnPropertyChanged(nameof(CanOpenAnimationDetails));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWidgetCompactAnimationEffect(normalized);
            if (CapsuleOptionKinds.AnimationPresetDurationMs(normalized) is { } duration &&
                SetProperty(ref _animationDurationMs, duration, nameof(AnimationDurationMs)))
            {
                OnPropertyChanged(nameof(AnimationDurationText));
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAnimationEffectOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.AnimationSnappy,
                CapsuleOptionKinds.AnimationSmooth,
                CapsuleOptionKinds.AnimationSlow,
                CapsuleOptionKinds.AnimationNone,
                CapsuleOptionKinds.AnimationCustom
            ];
            _cachedAnimationEffectNames ??= values
                .Select(value => _localize("Settings.Capsule.Animation." + value))
                .ToArray();
            return BuildOptions(values, _cachedAnimationEffectNames);
        }
    }

    public bool IsAnimationCustom => _animationEffect == CapsuleOptionKinds.AnimationCustom;

    public bool ShowAnimationCustom => IsAnimationCustom;

    public bool CanOpenAnimationDetails => IsAnimationCustom;

    public double AnimationDurationMs
    {
        get => _animationDurationMs;
        set
        {
            int normalized = CapsuleOptionKinds.NormalizeAnimationDurationMs((int)Math.Round(value));
            if (!SetProperty(ref _animationDurationMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AnimationDurationText));
            if (_isSyncingPresentation)
            {
                return;
            }

            if (_animationEffect is not
                (CapsuleOptionKinds.AnimationCustom or CapsuleOptionKinds.AnimationNone))
            {
                _animationEffect = CapsuleOptionKinds.AnimationCustom;
                OnPropertyChanged(nameof(AnimationEffect));
                OnPropertyChanged(nameof(ShowAnimationCustom));
                OnPropertyChanged(nameof(CanOpenAnimationDetails));
            }

            _settings.SetWidgetCompactAnimationDurationMs(normalized);
        }
    }

    public string AnimationDurationText => $"{Math.Round(AnimationDurationMs):0} ms";

    // --- Hover response (main section + behavior subsection) ---
    //
    // The Sensitive/Balanced/PreventAccidental selection is derived view
    // state with no persisted field: picking a preset writes its delay pair
    // through the delay setters below, and a manual delay edit flips the
    // label back to Custom (batch 33 semantics).

    public string HoverResponse
    {
        get => _hoverResponse;
        set
        {
            string normalized = CapsuleOptionKinds.NormalizeHoverResponse(value);
            if (!SetProperty(ref _hoverResponse, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowHoverResponseCustom));
            OnPropertyChanged(nameof(CanOpenHoverResponseDetails));
            if (_isSyncingPresentation)
            {
                return;
            }

            if (CapsuleOptionKinds.HoverResponsePresetDelays(normalized) is not { } preset)
            {
                return;
            }

            _isApplyingHoverResponse = true;
            try
            {
                ExpandDelayMs = preset.Expand;
                CollapseDelayMs = preset.Collapse;
            }
            finally
            {
                _isApplyingHoverResponse = false;
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableHoverResponseOptions
    {
        get
        {
            string[] values =
            [
                CapsuleOptionKinds.HoverResponseSensitive,
                CapsuleOptionKinds.HoverResponseBalanced,
                CapsuleOptionKinds.HoverResponsePreventAccidental,
                CapsuleOptionKinds.HoverResponseCustom
            ];
            _cachedHoverResponseNames ??= values
                .Select(value => _localize("Settings.Capsule.HoverResponse." + value))
                .ToArray();
            return BuildOptions(values, _cachedHoverResponseNames);
        }
    }

    public bool IsHoverResponseCustom => _hoverResponse == CapsuleOptionKinds.HoverResponseCustom;

    public bool ShowHoverResponseCustom => IsHoverResponseCustom;

    public bool CanOpenHoverResponseDetails => IsHoverResponseCustom;

    public double ExpandDelayMs
    {
        get => _expandDelayMs;
        set
        {
            int normalized = CapsuleOptionKinds.NormalizeExpandDelayMs((int)Math.Round(value));
            if (!SetProperty(ref _expandDelayMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpandDelayText));
            if (_isSyncingPresentation)
            {
                return;
            }

            MarkHoverResponseCustom();
            _settings.SetWidgetCompactExpandDelayMs(normalized);
        }
    }

    public string ExpandDelayText => $"{Math.Round(ExpandDelayMs):0} ms";

    public double CollapseDelayMs
    {
        get => _collapseDelayMs;
        set
        {
            int normalized = CapsuleOptionKinds.NormalizeCollapseDelayMs((int)Math.Round(value));
            if (!SetProperty(ref _collapseDelayMs, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(CollapseDelayText));
            if (_isSyncingPresentation)
            {
                return;
            }

            MarkHoverResponseCustom();
            _settings.SetWidgetCompactCollapseDelayMs(normalized);
        }
    }

    public string CollapseDelayText => $"{Math.Round(CollapseDelayMs):0} ms";

    private void MarkHoverResponseCustom()
    {
        if (_isApplyingHoverResponse || IsHoverResponseCustom)
        {
            return;
        }

        _hoverResponse = CapsuleOptionKinds.HoverResponseCustom;
        OnPropertyChanged(nameof(HoverResponse));
        OnPropertyChanged(nameof(ShowHoverResponseCustom));
        OnPropertyChanged(nameof(CanOpenHoverResponseDetails));
    }

    // --- Pushed widget/group override projection (shell-owned state machine) ---

    public IReadOnlyList<CapsuleOverrideSettingsItem> OverrideItems => _overrideItems;

    public bool ShowOverridesEntry => _hasOverrides;

    public bool ShowOverridesList => _hasOverrides;

    public bool ShowOverridesEmpty => !_hasOverrides;

    public string OverrideSummaryText => _overrideSummaryText;

    public bool HasWidthOverrides => _hasWidthOverrides;

    /// <summary>
    /// Pushes the shell-built override projection: the override item list,
    /// the entry/list/empty gates, the entry summary text, the width-override
    /// reset button gate and whether any per-widget/per-group Smart collapse
    /// override keeps the hover-response entry visible.
    /// </summary>
    public void UpdateOverridePresentation(
        IReadOnlyList<CapsuleOverrideSettingsItem> items,
        bool hasOverrides,
        string overrideSummaryText,
        bool hasWidthOverrides,
        bool smartCollapseOverrideActive)
    {
        _isSyncingPresentation = true;
        try
        {
            _overrideItems = items;
            _hasOverrides = hasOverrides;
            _overrideSummaryText = overrideSummaryText;
            _hasWidthOverrides = hasWidthOverrides;
            bool smartChanged = _smartCollapseOverrideActive != smartCollapseOverrideActive;
            _smartCollapseOverrideActive = smartCollapseOverrideActive;
            OnPropertyChanged(nameof(OverrideItems));
            OnPropertyChanged(nameof(ShowOverridesEntry));
            OnPropertyChanged(nameof(ShowOverridesList));
            OnPropertyChanged(nameof(ShowOverridesEmpty));
            OnPropertyChanged(nameof(OverrideSummaryText));
            OnPropertyChanged(nameof(HasWidthOverrides));
            OnPropertyChanged(nameof(IsCapsuleActive));
            OnPropertyChanged(nameof(CollapseSummaryText));
            if (smartChanged)
            {
                OnPropertyChanged(nameof(ShowHoverResponseEntry));
            }
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Re-projects every persisted field from the coordinator snapshots
    /// without firing writes (settings-changed/snapshot/restore paths); the
    /// hover-response label re-derives from the persisted delay pair.
    /// </summary>
    public void SyncPresentation()
    {
        _isSyncingPresentation = true;
        try
        {
            CapsuleBehaviorSettings behavior = _settings.ReadBehavior();
            CapsuleGeometrySettings geometry = _settings.ReadGeometry();
            CapsuleArrangementSettings arrangement = _settings.ReadArrangement();
            CapsuleAnimationSettings animation = _settings.ReadAnimation();
            CapsuleTimingSettings timing = _settings.ReadTiming();

            CollapseBehavior = CapsuleOptionKinds.NormalizeCollapseBehavior(
                behavior.CollapseBehavior);
            ContentMode = CapsuleOptionKinds.NormalizeContentMode(behavior.CompactContentMode);
            HideSensitiveContent = behavior.HideSensitiveContent;

            WidthMode = CapsuleOptionKinds.NormalizeWidthMode(geometry.CompactWidthMode);
            ExpansionDirection = CapsuleOptionKinds.NormalizeExpansionDirection(
                geometry.CompactExpansionDirection);

            ArrangementMode = CapsuleOptionKinds.NormalizeArrangementMode(arrangement.ArrangementMode);
            BarPlacement = CapsuleOptionKinds.NormalizeBarPlacement(arrangement.BarPlacement);
            BarDirection = CapsuleOptionKinds.NormalizeBarDirection(arrangement.BarDirection);
            BarSpacing = CapsuleOptionKinds.NormalizeBarSpacing(arrangement.BarSpacing);

            AnimationEffect = CapsuleOptionKinds.NormalizeAnimationEffect(animation.CompactAnimationEffect);
            AnimationDurationMs = CapsuleOptionKinds.NormalizeAnimationDurationMs(
                animation.CompactAnimationDurationMs);

            ExpandDelayMs = CapsuleOptionKinds.NormalizeExpandDelayMs(timing.ExpandDelayMs);
            CollapseDelayMs = CapsuleOptionKinds.NormalizeCollapseDelayMs(timing.CollapseDelayMs);
            HoverResponse = CapsuleOptionKinds.ResolveHoverResponse(
                timing.ExpandDelayMs,
                timing.CollapseDelayMs);

            OnPropertyChanged(nameof(IsCapsuleActive));
            OnPropertyChanged(nameof(CollapseSummaryText));
            OnPropertyChanged(nameof(ShowHoverResponseEntry));
            OnPropertyChanged(nameof(IsBarEnabled));
            OnPropertyChanged(nameof(IsBarSpacingEnabled));
            OnPropertyChanged(nameof(ShowArrangementEntry));
            OnPropertyChanged(nameof(ShowAnimationCustom));
            OnPropertyChanged(nameof(CanOpenAnimationDetails));
            OnPropertyChanged(nameof(ShowHoverResponseCustom));
            OnPropertyChanged(nameof(CanOpenHoverResponseDetails));
            OnPropertyChanged(nameof(BarSpacingText));
            OnPropertyChanged(nameof(AnimationDurationText));
            OnPropertyChanged(nameof(ExpandDelayText));
            OnPropertyChanged(nameof(CollapseDelayText));
            OnPropertyChanged(nameof(ArrangementDetailsSummary));
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Drops the cached localized option names so the next option read
    /// rebuilds them in the new language; pushed texts are re-pushed by the
    /// shell (language-change path).
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedWidthModeNames = null;
        _cachedExpansionDirectionNames = null;
        _cachedContentModeNames = null;
        _cachedArrangementNames = null;
        _cachedBarPlacementNames = null;
        _cachedBarDirectionNames = null;
        _cachedAnimationEffectNames = null;
        _cachedHoverResponseNames = null;
        _cachedCollapseBehaviorNames = null;
        OnPropertyChanged(nameof(AvailableCollapseBehaviorOptions));
        OnPropertyChanged(nameof(AvailableWidthModeOptions));
        OnPropertyChanged(nameof(AvailableExpansionDirectionOptions));
        OnPropertyChanged(nameof(AvailableContentModeOptions));
        OnPropertyChanged(nameof(AvailableArrangementOptions));
        OnPropertyChanged(nameof(AvailableBarPlacementOptions));
        OnPropertyChanged(nameof(AvailableBarDirectionOptions));
        OnPropertyChanged(nameof(AvailableAnimationEffectOptions));
        OnPropertyChanged(nameof(AvailableHoverResponseOptions));
        OnPropertyChanged(nameof(ArrangementDetailsSummary));
        OnPropertyChanged(nameof(CollapseSummaryText));
    }

    private IReadOnlyList<SettingsOption> BuildOptions(string[] values, string[] displayNames)
    {
        // Build a real SettingsOption[] (not a collection expression): the
        // hidden read-only-array type cannot marshal across the WinRT ABI in
        // Native AOT builds and would leave the ItemsSource empty.
        var options = new SettingsOption[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            options[index] = new SettingsOption(values[index], displayNames[index]);
        }

        return options;
    }

    // --- Coordinator seam retained for non-XAML callers and tests ---

    public CapsuleBehaviorSettings ReadBehavior() => _settings.ReadBehavior();

    public CapsuleGeometrySettings ReadGeometry() => _settings.ReadGeometry();

    public CapsuleArrangementSettings ReadArrangement() => _settings.ReadArrangement();

    public CapsuleAnimationSettings ReadAnimation() => _settings.ReadAnimation();

    public CapsuleTimingSettings ReadTiming() => _settings.ReadTiming();

    public void SetWidgetCollapseBehavior(string? behavior) =>
        _settings.SetWidgetCollapseBehavior(behavior);

    public void SetWidgetCompactContentMode(string? mode) =>
        _settings.SetWidgetCompactContentMode(mode);

    public void SetWidgetCompactHideSensitiveContent(bool value) =>
        _settings.SetWidgetCompactHideSensitiveContent(value);

    public void SetWidgetCompactWidthMode(string? mode) =>
        _settings.SetWidgetCompactWidthMode(mode);

    public void SetWidgetCompactExpansionDirection(string? direction) =>
        _settings.SetWidgetCompactExpansionDirection(direction);

    public void SetWidgetCapsuleArrangementMode(string? mode) =>
        _settings.SetWidgetCapsuleArrangementMode(mode);

    public void SetWidgetCapsuleBarPlacement(string? placement) =>
        _settings.SetWidgetCapsuleBarPlacement(placement);

    public void SetWidgetCapsuleBarDirection(string? direction) =>
        _settings.SetWidgetCapsuleBarDirection(direction);

    public void SetWidgetCapsuleBarSpacing(double spacing) =>
        _settings.SetWidgetCapsuleBarSpacing(spacing);

    public void SetWidgetCompactAnimationEffect(string? effect) =>
        _settings.SetWidgetCompactAnimationEffect(effect);

    public void SetWidgetCompactAnimationDurationMs(double value) =>
        _settings.SetWidgetCompactAnimationDurationMs(value);

    public void SetWidgetCompactExpandDelayMs(double value) =>
        _settings.SetWidgetCompactExpandDelayMs(value);

    public void SetWidgetCompactCollapseDelayMs(double value) =>
        _settings.SetWidgetCompactCollapseDelayMs(value);

    public void SetWidgetCompactMediaCornerMode(string? mode) =>
        _settings.SetWidgetCompactMediaCornerMode(mode);
}
