using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.GroupNavigation;

/// <summary>
/// Group-navigation section editor and binding surface (batch 44). The
/// WidgetGroups settings section switches its DataContext to this editor:
/// the five default fields (navigation style, title display mode, wheel
/// switch, hover switch, switch animation) bind TwoWay through
/// <see cref="IGroupNavigationSettings"/> with the coordinator owning
/// normalization, the unchanged-write skip and the debounced save, while the
/// existing-groups projection is built by the shell's group-editing state
/// machine and pushed in as read-only view state. The editor stays
/// WinUI-free: visibility gates are booleans the XAML runs through
/// <c>SettingsBoolToVisibilityConverter</c>, and the host-side group-
/// presentation notification stays on the shell through the
/// <see cref="PresentationUserChanged"/> event.
/// </summary>
public sealed partial class GroupNavigationSettingsViewModel : ObservableObject
{
    private const string NavigationKeyPrefix = "Settings.WidgetGroup";
    private const string TitleKeyPrefix = "Settings.WidgetGroup";
    private const string SwitchAnimationKeyPrefix = "Settings.WidgetGroup";

    private readonly IGroupNavigationSettings _settings;
    private readonly Func<string, string> _localize;

    private bool _isSyncingPresentation;
    private string[]? _cachedNavigationStyleNames;
    private string[]? _cachedTitleDisplayModeNames;
    private string[]? _cachedSwitchAnimationNames;

    private string _defaultNavigationStyle = WidgetGroupNavigationStyles.Stack;
    private string _defaultTitleDisplayMode = WidgetGroupTitleDisplayModes.IconAndText;
    private bool _wheelSwitchEnabled = true;
    private bool _hoverSwitchEnabled = true;
    private string _defaultSwitchAnimationStyle = WidgetGroupSwitchAnimationStyles.Auto;
    private IReadOnlyList<WidgetGroupSettingsItem> _existingGroups = [];
    private bool _hasExistingGroups;

    public GroupNavigationSettingsViewModel(
        IGroupNavigationSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    /// <summary>
    /// Host linkage: a default field changed through a user edit; the shell
    /// answers with the explicit widget-group presentation notification and
    /// the existing-group projection rebuild (which it pushes back).
    /// </summary>
    public event Action? PresentationUserChanged;

    // --- Default-field binding surface ---

    public string DefaultNavigationStyle
    {
        get => _defaultNavigationStyle;
        set
        {
            string normalized = WidgetGroupNavigationStyles.Normalize(
                value,
                allowFollowDefault: false);
            if (!SetProperty(ref _defaultNavigationStyle, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (_settings.SetDefaultNavigationStyle(normalized))
            {
                PresentationUserChanged?.Invoke();
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableNavigationStyleOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            string[] values =
            [
                WidgetGroupNavigationStyles.Tabs,
                WidgetGroupNavigationStyles.Stack
            ];
            _cachedNavigationStyleNames ??= values
                .Select(value => _localize(NavigationKeyPrefix + "Navigation." + value))
                .ToArray();
            var options = new SettingsOption[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                options[index] = new SettingsOption(values[index], _cachedNavigationStyleNames[index]);
            }

            return options;
        }
    }

    public string DefaultTitleDisplayMode
    {
        get => _defaultTitleDisplayMode;
        set
        {
            string normalized = WidgetGroupTitleDisplayModes.Normalize(
                value,
                allowFollowDefault: false);
            if (!SetProperty(ref _defaultTitleDisplayMode, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (_settings.SetDefaultTitleDisplayMode(normalized))
            {
                PresentationUserChanged?.Invoke();
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableTitleDisplayModeOptions
    {
        get
        {
            string[] values =
            [
                WidgetGroupTitleDisplayModes.IconAndText,
                WidgetGroupTitleDisplayModes.IconOnly,
                WidgetGroupTitleDisplayModes.TextOnly
            ];
            _cachedTitleDisplayModeNames ??= values
                .Select(value => _localize(TitleKeyPrefix + "Title." + value))
                .ToArray();
            var options = new SettingsOption[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                options[index] = new SettingsOption(values[index], _cachedTitleDisplayModeNames[index]);
            }

            return options;
        }
    }

    public bool WheelSwitchEnabled
    {
        get => _wheelSwitchEnabled;
        set
        {
            if (!SetProperty(ref _wheelSwitchEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (_settings.SetWheelSwitchEnabled(value))
            {
                PresentationUserChanged?.Invoke();
            }
        }
    }

    public bool HoverSwitchEnabled
    {
        get => _hoverSwitchEnabled;
        set
        {
            if (!SetProperty(ref _hoverSwitchEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (_settings.SetHoverSwitchEnabled(value))
            {
                PresentationUserChanged?.Invoke();
            }
        }
    }

    public string DefaultSwitchAnimationStyle
    {
        get => _defaultSwitchAnimationStyle;
        set
        {
            string normalized = WidgetGroupSwitchAnimationStyles.Normalize(value);
            if (!SetProperty(ref _defaultSwitchAnimationStyle, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (_settings.SetWidgetGroupSwitchAnimationStyle(normalized))
            {
                PresentationUserChanged?.Invoke();
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableSwitchAnimationOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            string[] values =
            [
                WidgetGroupSwitchAnimationStyles.Auto,
                WidgetGroupSwitchAnimationStyles.Vertical,
                WidgetGroupSwitchAnimationStyles.Horizontal,
                WidgetGroupSwitchAnimationStyles.Fade,
                WidgetGroupSwitchAnimationStyles.None
            ];
            _cachedSwitchAnimationNames ??= values
                .Select(value => _localize(SwitchAnimationKeyPrefix + "SwitchAnimation." + value))
                .ToArray();
            var options = new SettingsOption[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                options[index] = new SettingsOption(values[index], _cachedSwitchAnimationNames[index]);
            }

            return options;
        }
    }

    // --- Pushed existing-groups projection (shell-owned state machine) ---

    public IReadOnlyList<WidgetGroupSettingsItem> ExistingGroups => _existingGroups;

    public bool HasExistingGroups => _hasExistingGroups;

    public bool ShowExistingGroupsEmpty => !_hasExistingGroups;

    public void UpdateExistingGroups(IReadOnlyList<WidgetGroupSettingsItem> items)
    {
        _isSyncingPresentation = true;
        try
        {
            _existingGroups = items;
            _hasExistingGroups = items.Count > 0;
            OnPropertyChanged(nameof(ExistingGroups));
            OnPropertyChanged(nameof(HasExistingGroups));
            OnPropertyChanged(nameof(ShowExistingGroupsEmpty));
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Re-projects the four defaults from the coordinator snapshot without
    /// firing host events (settings-changed/snapshot/restore paths).
    /// </summary>
    public void SyncPresentation()
    {
        _isSyncingPresentation = true;
        try
        {
            GroupNavigationSettingsSnapshot snapshot = _settings.ReadAll();
            DefaultNavigationStyle = WidgetGroupNavigationStyles.Normalize(
                snapshot.DefaultNavigationStyle,
                allowFollowDefault: false);
            DefaultTitleDisplayMode = WidgetGroupTitleDisplayModes.Normalize(
                snapshot.DefaultTitleDisplayMode,
                allowFollowDefault: false);
            WheelSwitchEnabled = snapshot.WheelSwitchEnabled;
            HoverSwitchEnabled = snapshot.HoverSwitchEnabled;
            DefaultSwitchAnimationStyle = WidgetGroupSwitchAnimationStyles.Normalize(
                snapshot.SwitchAnimationStyle);
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Drops the cached localized option names so the next option read
    /// rebuilds them in the new language (language-change path).
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedNavigationStyleNames = null;
        _cachedTitleDisplayModeNames = null;
        _cachedSwitchAnimationNames = null;
        OnPropertyChanged(nameof(AvailableNavigationStyleOptions));
        OnPropertyChanged(nameof(AvailableTitleDisplayModeOptions));
        OnPropertyChanged(nameof(AvailableSwitchAnimationOptions));
    }

    // --- Coordinator seam retained for non-XAML callers and tests ---

    public GroupNavigationSettingsSnapshot ReadAll() => _settings.ReadAll();

    public bool SetDefaultNavigationStyle(string? value) =>
        _settings.SetDefaultNavigationStyle(value);

    public bool SetDefaultTitleDisplayMode(string? value) =>
        _settings.SetDefaultTitleDisplayMode(value);

    public bool SetWheelSwitchEnabled(bool value) =>
        _settings.SetWheelSwitchEnabled(value);

    public bool SetHoverSwitchEnabled(bool value) =>
        _settings.SetHoverSwitchEnabled(value);

    public bool SetWidgetGroupSwitchAnimationStyle(string? value) =>
        _settings.SetWidgetGroupSwitchAnimationStyle(value);
}
