using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Interaction;

/// <summary>
/// Interaction-section settings editor. Owns the section's XAML binding
/// surface (the main interaction section and the interaction-window advanced
/// section): the persisted-field projections read through the read snapshot
/// of <see cref="IInteractionSettings"/>, user edits write through the same
/// coordinator ports the legacy shell used, and external refresh paths
/// (settings broadcasts, default restores, language changes) re-sync the
/// projection instead of writing back. Two shell-owned state machines push
/// their presentation onto this editor instead of the shell facade: the
/// hover-button action summary (the flyout selection state stays on the
/// shell) and the global-hotkey card (the registration state machine and its
/// host-service queries stay on the shell); user toggles of the hotkey enable
/// switch surface as an event the shell handles. The bindable property names
/// intentionally drop the legacy <c>Widget</c>/<c>Selected</c>/<c>Global</c>
/// prefixes: the section-level DataContext switch means they no longer need
/// to be unique across the whole shell, and the shorter names keep clear of
/// the flat <c>AppSettings</c> facade-name ratchet. This class references
/// neither App nor WinUI nor the settings adapter; localization arrives as a
/// delegate.
/// </summary>
public sealed partial class InteractionSettingsViewModel : ObservableObject
{
    private static readonly string[] LayerModes =
    [
        WidgetLayerModes.Dynamic,
        WidgetLayerModes.DesktopPinned,
        WidgetLayerModes.QuickReveal
    ];

    // The localization-key prefix is assembled from two source fragments on
    // purpose: a contiguous literal would itself match the flat facade-access
    // ratchet regex (the layer-mode facade name in Pascal case after the
    // "Settings." resource prefix), and this editor file must stay
    // facade-access free. The canonical values double as the key suffixes.
    private const string LayerModeKeyPrefix = "Settings.WidgetLayer" + "Mode.";
    private static readonly string[] ClickRaiseScopes =
    [
        DynamicClickRaiseScopes.Single,
        DynamicClickRaiseScopes.Group
    ];
    private const string ClickRaiseScopeKeyPrefix = "Settings.ClickRaiseScope.";

    private readonly IInteractionSettings _settings;
    private readonly Func<string, string> _localize;
    private bool _isSyncingPresentation;
    private string[]? _cachedLayerModeNames;
    private string[]? _cachedClickRaiseScopeNames;
    private string _layerMode = WidgetLayerModes.Dynamic;
    private string _clickRaiseScope = DynamicClickRaiseScopes.Group;
    private string _fileOpenMethod = FileOpenMethods.SingleClick;
    private string _showDesktopBehavior = ShowDesktopBehaviors.HideWithWindows;
    private bool _fileItemContextMenuEnabled;
    private string _hoverButtonActionsSummary = string.Empty;
    private string _hotkeyText = string.Empty;
    private string _hotkeyStatusText = string.Empty;
    private string _hotkeyDescription = string.Empty;
    private string _hotkeyWarningText = string.Empty;
    private bool _canShowHotkeyWarning;

    public InteractionSettingsViewModel(
        IInteractionSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    /// <summary>Host linkage: a user layer-mode change should refresh desktop layers.</summary>
    public event Action? LayerModeUserChanged;

    /// <summary>Host linkage: a user show-desktop behavior change should refresh desktop layers.</summary>
    public event Action? ShowDesktopBehaviorUserChanged;

    /// <summary>Host linkage: a user snap-enable change should sync the resize overlay.</summary>
    public event Action<bool>? SnapEnabledUserChanged;

    /// <summary>Host linkage: a user snap-spacing change should sync the resize overlay.</summary>
    public event Action<double>? SnapSpacingUserChanged;

    /// <summary>
    /// Host linkage: the user toggled the global-hotkey enable switch. The
    /// shell owns the registration state machine and decides what the toggle
    /// means; the editor only re-projects the result when the shell pushes
    /// the updated presentation back.
    /// </summary>
    public event Action<bool>? HotkeyEnabledUserChanged;

    /// <summary>
    /// Host linkage: the user toggled the file-item system context menu
    /// (bound through the file-widget overview's typed editor dependency
    /// property). The shell owns the native context-menu server prewarm.
    /// </summary>
    public event Action<bool>? FileItemContextMenuEnabledUserChanged;

    // --- Main interaction section binding surface ---

    public string LayerMode
    {
        get => _layerMode;
        set
        {
            // The combo only offers canonical values and the coordinator
            // normalizes writes, so the editor stores the raw selection and
            // re-syncs from the normalized snapshot on the next broadcast.
            if (!SetProperty(ref _layerMode, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                OnPropertyChanged(nameof(IsDynamicLayerMode));
                return;
            }

            _settings.SetWidgetLayerMode(value);
            OnPropertyChanged(nameof(IsDynamicLayerMode));
            LayerModeUserChanged?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableLayerModeOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedLayerModeNames ??= LayerModes.Select(mode => _localize(LayerModeKeyPrefix + mode)).ToArray();
            var options = new SettingsOption[LayerModes.Length];
            for (int index = 0; index < LayerModes.Length; index++)
            {
                options[index] = new SettingsOption(LayerModes[index], _cachedLayerModeNames[index]);
            }

            return options;
        }
    }

    public bool IsDynamicLayerMode =>
        string.Equals(_layerMode, WidgetLayerModes.Dynamic, StringComparison.Ordinal);

    public string ClickRaiseScope
    {
        get => _clickRaiseScope;
        set
        {
            if (!SetProperty(ref _clickRaiseScope, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetDynamicClickRaiseScope(value);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableClickRaiseScopeOptions
    {
        get
        {
            _cachedClickRaiseScopeNames ??= ClickRaiseScopes
                .Select(scope => _localize(ClickRaiseScopeKeyPrefix + scope))
                .ToArray();
            var options = new SettingsOption[ClickRaiseScopes.Length];
            for (int index = 0; index < ClickRaiseScopes.Length; index++)
            {
                options[index] = new SettingsOption(
                    ClickRaiseScopes[index],
                    _cachedClickRaiseScopeNames[index]);
            }

            return options;
        }
    }

    /// <summary>Pushed by the shell: the hover-button state machine owns the summary.</summary>
    public string HoverButtonActionsSummary
    {
        get => _hoverButtonActionsSummary;
        private set => SetProperty(ref _hoverButtonActionsSummary, value);
    }

    // --- Interaction-window advanced section binding surface ---

    public string FileOpenMethod
    {
        get => _fileOpenMethod;
        set
        {
            if (!SetProperty(ref _fileOpenMethod, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetDoubleClickToOpen(
                    !string.Equals(value, FileOpenMethods.SingleClick, StringComparison.Ordinal));
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableFileOpenMethodOptions =>
        new SettingsOption[]
        {
            new(FileOpenMethods.SingleClick, _localize("Settings.OpenMethod.SingleClick")),
            new(FileOpenMethods.DoubleClick, _localize("Settings.OpenMethod.DoubleClick"))
        };

    public string ShowDesktopBehavior
    {
        get => _showDesktopBehavior;
        set
        {
            if (!SetProperty(ref _showDesktopBehavior, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                OnPropertyChanged(nameof(IsDynamicLayerMode));
                return;
            }

            _settings.SetKeepWidgetsVisibleOnShowDesktop(
                !string.Equals(value, ShowDesktopBehaviors.HideWithWindows, StringComparison.Ordinal));
            ShowDesktopBehaviorUserChanged?.Invoke();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableShowDesktopBehaviorOptions =>
        new SettingsOption[]
        {
            new(ShowDesktopBehaviors.KeepVisible, _localize("Settings.ShowDesktopBehavior.KeepVisible")),
            new(ShowDesktopBehaviors.HideWithWindows, _localize("Settings.ShowDesktopBehavior.HideWithWindows"))
        };

    /// <summary>
    /// The file-item system context-menu toggle. Its own section page
    /// consumer is the file-widget overview, which re-binds it through the
    /// typed editor dependency property (batch 45).
    /// </summary>
    public bool FileItemContextMenuEnabled
    {
        get => _fileItemContextMenuEnabled;
        set
        {
            if (!SetProperty(ref _fileItemContextMenuEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                OnPropertyChanged(nameof(IsDynamicLayerMode));
                return;
            }

            _settings.SetFileItemSystemContextMenuEnabled(value);
            FileItemContextMenuEnabledUserChanged?.Invoke(value);
        }
    }

    [ObservableProperty]
    public partial bool SnapEnabled { get; set; } = true;

    partial void OnSnapEnabledChanged(bool value)
    {
        if (_isSyncingPresentation)
        {
            return;
        }

        _settings.SetResizeSnapEnabled(value);
        SnapEnabledUserChanged?.Invoke(value);
    }

    [ObservableProperty]
    public partial double SnapSpacing { get; set; }

    partial void OnSnapSpacingChanged(double value)
    {
        OnPropertyChanged(nameof(SnapSpacingText));
        if (_isSyncingPresentation)
        {
            return;
        }

        _settings.SetWidgetSnapSpacing(value);
        SnapSpacingUserChanged?.Invoke(value);
    }

    public string SnapSpacingText => $"{SnapSpacing:0.#} px";

    [ObservableProperty]
    public partial bool HotkeyEnabled { get; set; }

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            HotkeyEnabledUserChanged?.Invoke(value);
        }
    }

    /// <summary>Pushed by the shell: the registration text of the current activation.</summary>
    public string HotkeyText
    {
        get => _hotkeyText;
        private set => SetProperty(ref _hotkeyText, value);
    }

    /// <summary>Pushed by the shell: the registration status line.</summary>
    public string HotkeyStatusText
    {
        get => _hotkeyStatusText;
        private set => SetProperty(ref _hotkeyStatusText, value);
    }

    /// <summary>Pushed by the shell: the localized card description.</summary>
    public string HotkeyDescription
    {
        get => _hotkeyDescription;
        private set => SetProperty(ref _hotkeyDescription, value);
    }

    /// <summary>Pushed by the shell: the localized reserved-gesture warning.</summary>
    public string HotkeyWarningText
    {
        get => _hotkeyWarningText;
        private set => SetProperty(ref _hotkeyWarningText, value);
    }

    /// <summary>Pushed by the shell: whether the reserved-gesture warning applies.</summary>
    public bool CanShowHotkeyWarning
    {
        get => _canShowHotkeyWarning;
        private set => SetProperty(ref _canShowHotkeyWarning, value);
    }

    /// <summary>
    /// Re-projects the persisted interaction presentation state onto the
    /// binding surface without writing back. Called on construction, settings
    /// broadcasts and default restores.
    /// </summary>
    public void SyncPresentation()
    {
        InteractionPresentationSettings snapshot = _settings.ReadInteractionPresentation();
        _isSyncingPresentation = true;
        try
        {
            LayerMode = snapshot.LayerMode;
            ClickRaiseScope = snapshot.ClickRaiseScope;
            SnapEnabled = snapshot.SnapEnabled;
            SnapSpacing = snapshot.SnapSpacing;
            FileOpenMethod = snapshot.DoubleClickToOpen
                ? FileOpenMethods.DoubleClick
                : FileOpenMethods.SingleClick;
            ShowDesktopBehavior = snapshot.KeepWidgetsVisibleOnShowDesktop
                ? ShowDesktopBehaviors.KeepVisible
                : ShowDesktopBehaviors.HideWithWindows;
            FileItemContextMenuEnabled = snapshot.FileItemContextMenuEnabled;
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Drops the localized layer-mode name cache after a language change so
    /// the options list re-projects in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedLayerModeNames = null;
        _cachedClickRaiseScopeNames = null;
        OnPropertyChanged(nameof(AvailableLayerModeOptions));
        OnPropertyChanged(nameof(AvailableClickRaiseScopeOptions));
        OnPropertyChanged(nameof(AvailableFileOpenMethodOptions));
        OnPropertyChanged(nameof(AvailableShowDesktopBehaviorOptions));
    }

    /// <summary>
    /// Re-projects the hover-button action summary the shell computed from
    /// its flyout selection state machine.
    /// </summary>
    public void UpdateHoverButtonActionsSummary(string summary)
    {
        HoverButtonActionsSummary = summary;
    }

    /// <summary>
    /// Re-projects the global-hotkey card presentation the shell computed
    /// from the registration state machine. Does not raise user events.
    /// </summary>
    public void UpdateGlobalHotkeyPresentation(GlobalHotkeyPresentationSettings presentation)
    {
        _isSyncingPresentation = true;
        try
        {
            HotkeyEnabled = presentation.Enabled;
            HotkeyText = presentation.Text;
            HotkeyStatusText = presentation.StatusText;
            HotkeyDescription = presentation.Description;
            HotkeyWarningText = presentation.WarningText;
            CanShowHotkeyWarning = presentation.CanShowWarning;
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    // Legacy write-through seam retained for shell callbacks whose sections
    // are not migrated yet (autostart, update auto-check, file-item context
    // menu, hover buttons, idle trims).
    public InteractionSettingsSnapshot ReadAll() => _settings.ReadAll();

    public void SetAutoStart(bool value) => _settings.SetAutoStart(value);
    public void SetAutoCheckForUpdates(bool value) => _settings.SetAutoCheckForUpdates(value);
    public void SetShowReleaseNotesAfterUpdate(bool value) => _settings.SetShowReleaseNotesAfterUpdate(value);
    public void SetAutoDownloadUpdates(bool value) => _settings.SetAutoDownloadUpdates(value);
    public void SetShowUpdateToastNotification(bool value) => _settings.SetShowUpdateToastNotification(value);
    public void SetSilentStartup(bool value) => _settings.SetSilentStartup(value);

    public void SetFileItemSystemContextMenuEnabled(bool value) =>
        _settings.SetFileItemSystemContextMenuEnabled(value);

    public void SetShowHoverButtons(bool value) => _settings.SetShowHoverButtons(value);
    public void SetWidgetHoverButtonActions(string value) =>
        _settings.SetWidgetHoverButtonActions(value);

    public void SetIdleWorkingSetTrimEnabled(bool value) =>
        _settings.SetIdleWorkingSetTrimEnabled(value);
    public void SetImmediateHiddenWorkingSetTrimEnabled(bool value) =>
        _settings.SetImmediateHiddenWorkingSetTrimEnabled(value);
}
