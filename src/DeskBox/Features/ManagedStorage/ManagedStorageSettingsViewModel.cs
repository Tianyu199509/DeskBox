using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.ManagedStorage;

/// <summary>
/// Managed-storage section settings editor. Owns the section's XAML binding
/// surface: the managed drop action (combo), the read-only default root path
/// display and the quick-access card presentation (status line, pin/unpin
/// button face and tooltip, action gate). The drop action reads project
/// through the read snapshot of <see cref="IManagedStorageSettings"/> and
/// user edits write through the same coordinator port the legacy shell used;
/// external refresh paths (settings broadcasts, default restores) re-sync the
/// projection instead of writing back. The root path and the quick-access
/// presentation are pushed by the shell: the folder picker, the storage
/// migration chain and the Explorer quick-access state machine stay on the
/// host because they reach host services. The section-level DataContext
/// switch means the property names no longer need to be unique across the
/// whole shell; the drop action drops its legacy prefix and the path its
/// storage qualifier, keeping clear of the flat facade-name ratchet. Like the
/// music editor, this class references neither App nor WinUI nor the settings
/// adapter; localization arrives as a delegate.
/// </summary>
public sealed partial class ManagedStorageSettingsViewModel : ObservableObject
{
    private static readonly string[] DropActions =
    [
        ManagedDropActions.Move,
        ManagedDropActions.Copy,
        ManagedDropActions.FollowWindows
    ];

    private static readonly string[] DropActionNameKeys =
    [
        "Settings.DropAction.Move",
        "Settings.DropAction.Copy",
        "Settings.DropAction.System"
    ];

    private static readonly string[] DragOutActions =
    [
        ManagedDropActions.FollowWindows,
        ManagedDropActions.Copy,
        ManagedDropActions.Move
    ];

    private static readonly string[] DragOutActionNameKeys =
    [
        "Settings.DropAction.System",
        "Settings.DropAction.Copy",
        "Settings.DropAction.Move"
    ];

    private readonly IManagedStorageSettings _settings;
    private readonly Func<string, string> _localize;
    private bool _isSyncingPresentation;
    private string[]? _cachedDropActionNames;
    private string[]? _cachedDragOutActionNames;
    private string _dropAction = ManagedDropActions.Move;
    private string _dragOutAction = ManagedDropActions.FollowWindows;
    private bool _dragOutModifierTipEnabled = true;
    private bool _dragOutResultHintEnabled = true;
    private string _rootPath = string.Empty;
    private bool _canInvokeQuickAccessAction = true;
    private bool _shouldUnpinQuickAccessAction;
    private string _quickAccessStatusText = string.Empty;
    private string _pinQuickAccessButtonText = string.Empty;
    private string _pinQuickAccessToolTipText = string.Empty;

    public ManagedStorageSettingsViewModel(
        IManagedStorageSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    public string DropAction
    {
        get => _dropAction;
        set
        {
            // The combo only offers canonical values and the coordinator
            // normalizes writes, so the editor stores the raw selection and
            // re-syncs from the normalized snapshot on the next broadcast.
            if (!SetProperty(ref _dropAction, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DragBehaviorSummaryText));
            if (!_isSyncingPresentation)
            {
                _settings.SetManagedDropAction(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDropActionOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedDropActionNames ??= DropActionNameKeys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[DropActions.Length];
            for (int index = 0; index < DropActions.Length; index++)
            {
                options[index] = new SettingsOption(DropActions[index], _cachedDropActionNames[index]);
            }

            return options;
        }
    }

    public object[] AvailableDragInBehaviorOptionItems =>
        AvailableDropActionOptions.Cast<object>().ToArray();

    /// <summary>
    /// The preferred drop effect advertised to external targets when files
    /// are dragged out of a file widget (FollowWindows/Copy/Move).
    /// </summary>
    public string DragOutAction
    {
        get => _dragOutAction;
        set
        {
            if (!SetProperty(ref _dragOutAction, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DragBehaviorSummaryText));
            if (!_isSyncingPresentation)
            {
                _settings.SetManagedDragOutAction(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDragOutActionOptions
    {
        get
        {
            _cachedDragOutActionNames ??= DragOutActionNameKeys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[DragOutActions.Length];
            for (int index = 0; index < DragOutActions.Length; index++)
            {
                options[index] = new SettingsOption(DragOutActions[index], _cachedDragOutActionNames[index]);
            }

            return options;
        }
    }

    public object[] AvailableDragOutBehaviorOptionItems =>
        AvailableDragOutActionOptions.Cast<object>().ToArray();

    public string DragBehaviorSummaryText =>
        $"{GetOptionDisplayName(AvailableDropActionOptions, _dropAction)} · {GetOptionDisplayName(AvailableDragOutActionOptions, _dragOutAction)}";

    private static string GetOptionDisplayName(
        IReadOnlyList<SettingsOption> options,
        string value)
    {
        foreach (SettingsOption option in options)
        {
            if (option.Value is string candidate &&
                string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return option.DisplayName;
            }
        }

        return options.Count > 0 ? options[0].DisplayName : string.Empty;
    }

    /// <summary>
    /// Whether the in-drag modifier tip (Shift=move / Ctrl=copy) is shown at
    /// drag start. Defaults on; the editor writes through the coordinator.
    /// </summary>
    public bool DragOutModifierTipEnabled
    {
        get => _dragOutModifierTipEnabled;
        set
        {
            if (!SetProperty(ref _dragOutModifierTipEnabled, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetDragOutModifierTipEnabled(value);
            }
        }
    }

    /// <summary>
    /// Whether the post-drop receipt hint is shown after an external drop.
    /// </summary>
    public bool DragOutResultHintEnabled
    {
        get => _dragOutResultHintEnabled;
        set
        {
            if (!SetProperty(ref _dragOutResultHintEnabled, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetDragOutResultHintEnabled(value);
            }
        }
    }

    /// <summary>
    /// Pushed by the shell: the normalized default root path display. The
    /// folder picker and the migration commit chain stay on the host.
    /// </summary>
    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    /// <summary>
    /// Pushed by the shell: whether the pin/unpin action can run right now.
    /// </summary>
    public bool CanInvokeQuickAccessAction
    {
        get => _canInvokeQuickAccessAction;
        private set => SetProperty(ref _canInvokeQuickAccessAction, value);
    }

    /// <summary>
    /// Pushed by the shell: whether the pinned state means the next action
    /// unpins. Consumed by the code-behind action handler.
    /// </summary>
    public bool ShouldUnpinQuickAccessAction
    {
        get => _shouldUnpinQuickAccessAction;
        private set => SetProperty(ref _shouldUnpinQuickAccessAction, value);
    }

    /// <summary>Pushed by the shell: the quick-access status line.</summary>
    public string QuickAccessStatusText
    {
        get => _quickAccessStatusText;
        private set => SetProperty(ref _quickAccessStatusText, value);
    }

    /// <summary>Pushed by the shell: the pin/unpin button face.</summary>
    public string PinQuickAccessButtonText
    {
        get => _pinQuickAccessButtonText;
        private set => SetProperty(ref _pinQuickAccessButtonText, value);
    }

    /// <summary>Pushed by the shell: the pin/unpin button tooltip.</summary>
    public string PinQuickAccessToolTipText
    {
        get => _pinQuickAccessToolTipText;
        private set => SetProperty(ref _pinQuickAccessToolTipText, value);
    }

    /// <summary>
    /// Re-projects the persisted managed-storage presentation (drop action
    /// and root path) onto the binding surface without writing back. Called
    /// on construction, settings broadcasts and default restores.
    /// </summary>
    public void SyncPresentation()
    {
        ManagedStoragePresentationSettings snapshot =
            _settings.ReadManagedStoragePresentation();
        _isSyncingPresentation = true;
        try
        {
            DropAction = snapshot.DropAction;
            DragOutAction = snapshot.DragOutAction;
            DragOutModifierTipEnabled = snapshot.DragOutModifierTipEnabled;
            DragOutResultHintEnabled = snapshot.DragOutResultHintEnabled;
            RootPath = snapshot.RootPath;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(DragBehaviorSummaryText));
    }

    /// <summary>
    /// Drops the localized drop-action name cache after a language change so
    /// the options list re-projects in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedDropActionNames = null;
        _cachedDragOutActionNames = null;
        OnPropertyChanged(nameof(AvailableDropActionOptions));
        OnPropertyChanged(nameof(AvailableDragInBehaviorOptionItems));
        OnPropertyChanged(nameof(AvailableDragOutActionOptions));
        OnPropertyChanged(nameof(AvailableDragOutBehaviorOptionItems));
        OnPropertyChanged(nameof(DragBehaviorSummaryText));
    }

    /// <summary>
    /// Commits a new default root path through the coordinator (normalize,
    /// store, one debounced save) and re-projects it onto the binding
    /// surface. Called by the shell's migration commit step; the migration
    /// chain itself stays on the host.
    /// </summary>
    public string CommitRootPath(string path)
    {
        string normalizedPath = _settings.SetDefaultRootPath(path);
        UpdateRootPath(normalizedPath);
        return normalizedPath;
    }

    /// <summary>
    /// Re-projects the default root path the shell committed after its
    /// folder picker and migration chain finished.
    /// </summary>
    public void UpdateRootPath(string rootPath)
    {
        RootPath = rootPath;
    }

    /// <summary>
    /// Re-projects the quick-access card presentation the shell computed from
    /// its Explorer quick-access state machine. Does not write anything back.
    /// </summary>
    public void UpdateQuickAccessPresentation(QuickAccessPresentationSettings presentation)
    {
        CanInvokeQuickAccessAction = presentation.CanInvoke;
        ShouldUnpinQuickAccessAction = presentation.ShouldUnpin;
        QuickAccessStatusText = presentation.StatusText;
        PinQuickAccessButtonText = presentation.ButtonText;
        PinQuickAccessToolTipText = presentation.ToolTipText;
    }
}
