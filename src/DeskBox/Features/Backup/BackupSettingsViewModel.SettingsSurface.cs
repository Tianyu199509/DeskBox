using System.Collections.ObjectModel;
using System.Globalization;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Backup;

/// <summary>
/// The bindable settings surface of the backup settings family (batch 49):
/// the local-backup section (switch, interval/retention combos, custom
/// directory projection and fallback warning), the cloud-backup section
/// (provider/endpoint fields, scope switches, interval/retention combos,
/// credential status, busy gate, connection-status line, last-success
/// summary and the remote-snapshot list projection) and the
/// compatibility-diagnostics section (drag-drop permission texts and the
/// runtime-health lines, which the shell computes from host services and
/// pushes in through the diagnostic ports below). User edits write through
/// <see cref="IBackupSettings.Update"/> exactly like the legacy shell
/// setters; the batch-4 visit state machine (endpoint-scoped reads,
/// generations and cancellation) in the main partial is untouched. The
/// password itself never enters this or any view model: the save/test
/// buttons pass the PasswordBox content straight through
/// <see cref="BackupSettingsViewModel.SaveCredentialAsync"/>/
/// <see cref="BackupSettingsViewModel.ProbeAsync"/> into the credential
/// store. Property names intentionally drop the legacy
/// AutomaticBackup/CloudBackup/DragDropPermission prefixes: the
/// section-level DataContext switch means they no longer need to be unique
/// across the shell, and unprefixed names keep the flat facade-name
/// ratchet shrinking. This partial references neither App nor WinUI nor
/// Services; localization and formatting arrive as delegates.
/// </summary>
public sealed partial class BackupSettingsViewModel
{
    private bool _isSyncingPresentation;
    private bool _commandBusy;
    private BackupEndpoint? _projectedEndpoint;
    private string _connectionStatusText = string.Empty;
    private string _localDirectory = string.Empty;
    private string _localFallbackWarningText = string.Empty;
    private bool _localBackupEnabled = BackupOptionKinds.DefaultLocalEnabled;
    private int _selectedLocalIntervalMinutes = BackupOptionKinds.DefaultLocalIntervalMinutes;
    private int _selectedLocalRetentionCount = BackupOptionKinds.DefaultLocalRetentionCount;
    private string _selectedProvider = BackupOptionKinds.ProviderNone;
    private string _serverUrl = string.Empty;
    private string _remotePath = "DeskBox/backups";
    private string _username = string.Empty;
    private bool _todoDataEnabled;
    private bool _quickCaptureDataEnabled;
    private bool _widgetStyleEnabled;
    private int _selectedIntervalMinutes = BackupOptionKinds.DefaultCloudIntervalMinutes;
    private int _selectedRetentionCount = BackupOptionKinds.DefaultCloudRetentionCount;
    private string _dragDropSummaryText = string.Empty;
    private string _dragDropDetailText = string.Empty;
    private string _dragDropProcessText = string.Empty;
    private string _dragDropExplorerText = string.Empty;
    private string _dragDropUacText = string.Empty;
    private string _dragDropAppCompatText = string.Empty;
    private string _dragDropStartupText = string.Empty;
    private string _dragDropShortcutText = string.Empty;
    private string _dragDropRepairStatusText = string.Empty;
    private bool _isDragDropRepairing;
    private bool _canRepairDragDrop;
    private string _runtimeSummaryText = string.Empty;
    private string _runtimeDetailText = string.Empty;
    private bool _isRuntimeResyncing;
    private string[]? _cachedLocalIntervalNames;
    private string[]? _cachedLocalRetentionNames;
    private string[]? _cachedProviderNames;
    private string[]? _cachedIntervalNames;
    private string[]? _cachedRetentionNames;

    /// <summary>
    /// Remote snapshot inventory for the restore list. The ListView's
    /// ItemsSource is assigned in code-behind (an object[] snapshot for the
    /// Native AOT projection path); this collection is the shell's
    /// change-signal source.
    /// </summary>
    public ObservableCollection<CloudBackupRemoteSnapshotItem> RemoteSnapshotItems { get; } = [];

    /// <summary>Called once from the constructor after the initial read.</summary>
    private void InitializeSurface()
    {
        PropertyChanged += OnSurfacePropertyChanged;
        ProjectState();
    }

    private void OnSurfacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(State):
                if (_projectedEndpoint is not null && _projectedEndpoint != State.Endpoint)
                    _connectionStatusText = string.Empty;
                ProjectState();
                OnPropertyChanged(nameof(ConnectionStatusText));
                break;
            case nameof(CredentialSaved):
                OnPropertyChanged(nameof(CredentialStatusText));
                break;
            case nameof(RemoteSnapshots):
                SyncRemoteSnapshotItems();
                if (Message.Kind == BackupPageMessageKind.None)
                    _connectionStatusText = string.Empty;
                OnPropertyChanged(nameof(ConnectionStatusText));
                break;
            case nameof(Message):
                ApplyMessageToConnectionStatus();
                break;
            case nameof(IsBusy):
                OnPropertyChanged(nameof(ActionsEnabled));
                break;
        }
    }

    /// <summary>
    /// Re-projects the persisted snapshot onto the binding surface without
    /// writing back (settings broadcasts, writes' trailing refresh).
    /// </summary>
    private void ProjectState()
    {
        BackupSettingsSnapshot state = State;
        _projectedEndpoint = state.Endpoint;
        _isSyncingPresentation = true;
        try
        {
            LocalBackupEnabled = state.LocalEnabled;
            SelectedLocalIntervalMinutes = state.LocalIntervalMinutes;
            SelectedLocalRetentionCount = state.LocalRetentionCount;
            _localDirectory = state.LocalDirectory;
            SelectedProvider = BackupOptionKinds.NormalizeProvider(state.CloudProvider);
            ServerUrl = state.CloudServerUrl;
            RemotePath = state.CloudRemotePath;
            Username = state.CloudUsername;
            IncludeTodoData = state.CloudTodoEnabled;
            IncludeQuickCaptureData = state.CloudQuickCaptureEnabled;
            IncludeWidgetStyle = state.CloudWidgetStyleEnabled;
            SelectedIntervalMinutes = state.CloudIntervalMinutes;
            SelectedRetentionCount = state.CloudRetentionCount;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        _localFallbackWarningText = state.LocalDirectoryFallback
            ? Format("Settings.DataBackup.AutomaticBackupDirectory.FallbackWarning",
                state.EffectiveLocalDirectory)
            : string.Empty;
        OnPropertyChanged(nameof(LocalDirectoryDisplayText));
        OnPropertyChanged(nameof(LocalFallbackWarningText));
        OnPropertyChanged(nameof(ShowLocalFallbackWarning));
        OnPropertyChanged(nameof(ShowCustomDirectoryRecoveryNote));
        OnPropertyChanged(nameof(ShowWebDavFields));
        OnPropertyChanged(nameof(ShowHttpWarning));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// The shell runs the manual backup/restore/delete flows (host runtime
    /// entry points) and pushes their in-flight state here so every action
    /// control disables while a round-trip is running.
    /// </summary>
    public void SetCommandBusy(bool busy)
    {
        if (_commandBusy == busy)
        {
            return;
        }

        _commandBusy = busy;
        OnPropertyChanged(nameof(ActionsEnabled));
    }

    /// <summary>Action controls stay enabled only while nothing is in flight.</summary>
    public bool ActionsEnabled => !_commandBusy && !IsBusy;

    /// <summary>Transient feedback line under the test-connection action.</summary>
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        set => SetProperty(ref _connectionStatusText, value);
    }

    // --- Local automatic snapshots ---

    public bool LocalBackupEnabled
    {
        get => _localBackupEnabled;
        set
        {
            if (!SetProperty(ref _localBackupEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(LocalEnabled: value));
        }
    }

    public int SelectedLocalIntervalMinutes
    {
        get => _selectedLocalIntervalMinutes;
        set
        {
            int normalized = BackupOptionKinds.NormalizeLocalIntervalMinutes(value);
            if (!SetProperty(ref _selectedLocalIntervalMinutes, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(LocalIntervalMinutes: normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableLocalIntervalOptions
    {
        get
        {
            _cachedLocalIntervalNames ??= BackupOptionKinds.LocalIntervalMinutes
                .Select(GetLocalIntervalDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(BackupOptionKinds.LocalIntervalMinutes, _cachedLocalIntervalNames));
        }
    }

    public int SelectedLocalRetentionCount
    {
        get => _selectedLocalRetentionCount;
        set
        {
            int normalized = BackupOptionKinds.NormalizeLocalRetentionCount(value);
            if (!SetProperty(ref _selectedLocalRetentionCount, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(LocalRetentionCount: normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableLocalRetentionOptions
    {
        get
        {
            _cachedLocalRetentionNames ??= BackupOptionKinds.LocalRetentionCounts
                .Select(count => Format("Settings.DataBackup.Retention.Count", count))
                .ToArray();
            return WrapOptions(BuildOptions(BackupOptionKinds.LocalRetentionCounts, _cachedLocalRetentionNames));
        }
    }

    /// <summary>Folder shown in the UI: the configured folder, or the effective default one.</summary>
    public string LocalDirectoryDisplayText =>
        _localDirectory.Length > 0 ? _localDirectory : State.EffectiveLocalDirectory;

    public string LocalFallbackWarningText => _localFallbackWarningText;

    public bool ShowLocalFallbackWarning => !string.IsNullOrEmpty(_localFallbackWarningText);

    /// <summary>
    /// Disclosure for the wipe-recovery limitation: a custom snapshot folder
    /// survives a configuration wipe, but the folder is only recorded inside
    /// settings.json — after a wipe the automatic recovery offer cannot see
    /// those snapshots, so restoring from them stays a manual step.
    /// </summary>
    public bool ShowCustomDirectoryRecoveryNote => _localDirectory.Length > 0;

    public string CustomDirectoryRecoveryNoteText =>
        Format("Settings.DataBackup.AutomaticBackupDirectory.CustomDirectoryRecoveryNote");

    /// <summary>
    /// Writes a custom snapshot folder chosen in the folder picker; empty
    /// resets to the default recovery folder.
    /// </summary>
    public void UpdateLocalDirectory(string path)
    {
        string normalized = BackupOptionKinds.NormalizeLocalDirectory(path) ?? string.Empty;
        Update(new(LocalDirectory: normalized));
    }

    /// <summary>
    /// Re-reads the effective snapshot folder and refreshes the fallback
    /// warning; called when the local-backup section is entered.
    /// </summary>
    public void RefreshLocalStatus() => RefreshState();

    // --- Cloud provider, endpoint and scope ---

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            string normalized = BackupOptionKinds.NormalizeProvider(value);
            if (!SetProperty(ref _selectedProvider, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowWebDavFields));
            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudProvider: normalized));

            // The provider dropdown is the cloud-backup master switch:
            // turning it OFF switches every cloud domain off in the same
            // breath (the coordinator clears them in the same persisted
            // write; these local flips make the page reflect it instantly
            // and send idempotent no-op updates — by then the coordinator
            // already holds them false).
            if (normalized == BackupOptionKinds.ProviderNone)
            {
                IncludeTodoData = false;
                IncludeQuickCaptureData = false;
                IncludeWidgetStyle = false;
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableProviderOptions
    {
        get
        {
            _cachedProviderNames ??=
            [
                Localize("Settings.CloudBackup.Provider.Off"),
                Localize("Settings.CloudBackup.Provider.WebDav")
            ];
            return WrapOptions(BuildOptions(
                [BackupOptionKinds.ProviderNone, BackupOptionKinds.ProviderWebDav],
                _cachedProviderNames));
        }
    }

    /// <summary>WebDAV-only fields are hidden when the provider is off.</summary>
    public bool ShowWebDavFields => _selectedProvider == BackupOptionKinds.ProviderWebDav;

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            if (!SetProperty(ref _serverUrl, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowHttpWarning));
            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudServerUrl: value ?? string.Empty));
        }
    }

    /// <summary>Plain-HTTP endpoints send Basic credentials on a cleartext channel.</summary>
    public string HttpWarningText => Localize("Settings.CloudBackup.HttpWarning");

    public bool ShowHttpWarning =>
        Uri.TryCreate(_serverUrl, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttp;

    public string RemotePath
    {
        get => _remotePath;
        set
        {
            if (!SetProperty(ref _remotePath, value ?? string.Empty))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudRemotePath: value ?? string.Empty));
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (!SetProperty(ref _username, value ?? string.Empty))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudUsername: value ?? string.Empty));
        }
    }

    public string CredentialStatusText => CredentialSaved
        ? Localize("Settings.CloudBackup.Password.Saved")
        : Localize("Settings.CloudBackup.Password.NotSaved");

    public bool IncludeTodoData
    {
        get => _todoDataEnabled;
        set
        {
            if (!SetProperty(ref _todoDataEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudTodoEnabled: value));
        }
    }

    public bool IncludeQuickCaptureData
    {
        get => _quickCaptureDataEnabled;
        set
        {
            if (!SetProperty(ref _quickCaptureDataEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudQuickCaptureEnabled: value));
        }
    }

    public bool IncludeWidgetStyle
    {
        get => _widgetStyleEnabled;
        set
        {
            if (!SetProperty(ref _widgetStyleEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudWidgetStyleEnabled: value));
        }
    }

    public int SelectedIntervalMinutes
    {
        get => _selectedIntervalMinutes;
        set
        {
            int normalized = BackupOptionKinds.NormalizeCloudIntervalMinutes(value);
            if (!SetProperty(ref _selectedIntervalMinutes, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudIntervalMinutes: normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableIntervalOptions
    {
        get
        {
            _cachedIntervalNames ??= BackupOptionKinds.CloudIntervalMinutes
                .Select(GetCloudIntervalDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(BackupOptionKinds.CloudIntervalMinutes, _cachedIntervalNames));
        }
    }

    public int SelectedRetentionCount
    {
        get => _selectedRetentionCount;
        set
        {
            int normalized = BackupOptionKinds.NormalizeCloudRetentionCount(value);
            if (!SetProperty(ref _selectedRetentionCount, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            Update(new(CloudRetentionCount: normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableRetentionOptions
    {
        get
        {
            _cachedRetentionNames ??= BackupOptionKinds.CloudRetentionCounts
                .Select(count => Format("Settings.CloudBackup.Retention.Count", count))
                .ToArray();
            return WrapOptions(BuildOptions(BackupOptionKinds.CloudRetentionCounts, _cachedRetentionNames));
        }
    }

    /// <summary>
    /// Last successful upload — plus the latest failure when it is newer, so
    /// a silently-broken scheduled backup can't hide behind a stale success.
    /// An accepted-but-never-listed upload is stamped separately: it is not a
    /// failure, yet the snapshot may never have landed.
    /// </summary>
    public string StatusText
    {
        get
        {
            BackupSettingsSnapshot state = State;
            long successTicks = state.CloudLastSuccessUtcTicks;
            long failureTicks = state.CloudLastFailureUtcTicks;
            string status = successTicks > 0
                ? Format("Settings.CloudBackup.LastSuccess",
                    new DateTimeOffset(successTicks, TimeSpan.Zero).ToLocalTime().ToString("g"))
                : Localize("Settings.CloudBackup.LastSuccess.Never");

            if (failureTicks > successTicks)
            {
                status += " · " + Format("Settings.CloudBackup.LastFailure",
                    new DateTimeOffset(failureTicks, TimeSpan.Zero).ToLocalTime().ToString("g"));
            }

            long unverifiedTicks = state.CloudLastUnverifiedUtcTicks;
            if (unverifiedTicks > 0)
            {
                status += " · " + Format("Settings.CloudBackup.LastUnverified",
                    new DateTimeOffset(unverifiedTicks, TimeSpan.Zero).ToLocalTime().ToString("g"));
            }

            return status;
        }
    }

    private void ApplyMessageToConnectionStatus()
    {
        BackupPageMessage message = Message;
        _connectionStatusText = message.Kind switch
        {
            BackupPageMessageKind.PasswordSaved =>
                Localize("Settings.CloudBackup.Password.Saved"),
            BackupPageMessageKind.PasswordMissing =>
                Localize("Settings.CloudBackup.Password.NotSaved"),
            BackupPageMessageKind.PasswordEmpty =>
                Localize("Settings.CloudBackup.Password.EmptyBody"),
            BackupPageMessageKind.PasswordSaveFailed =>
                Format("Settings.CloudBackup.Password.SaveFailed", message.Error ?? string.Empty),
            BackupPageMessageKind.ProbeSucceeded =>
                Localize("Settings.CloudBackup.TestConnection.Success"),
            BackupPageMessageKind.ProbeFailed =>
                Format("Settings.CloudBackup.TestConnection.Failed", message.Error ?? string.Empty),
            BackupPageMessageKind.ListFailed =>
                Format("Settings.CloudBackup.RefreshSnapshots.Failed", message.Error ?? string.Empty),
            BackupPageMessageKind.NotYetVisible =>
                Localize("Settings.CloudBackup.SnapshotList.NotYetVisible"),
            _ => string.Empty
        };
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    private void SyncRemoteSnapshotItems()
    {
        RemoteSnapshotItems.Clear();
        BackupEndpoint endpoint = Endpoint;
        foreach (BackupRemoteSnapshot item in RemoteSnapshots)
        {
            string title = item.Name;
            string details = item.Name;
            if (item.CreatedAtUtc is { } createdUtc)
            {
                title = createdUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                string stem = item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? item.Name[..^4] : item.Name;
                string tail = stem.Split('-').Last();
                string device = tail.Length == 8 ? tail : item.Name;
                string size = item.Length is { } length ? $" · {FormatBytes(length)}" : string.Empty;
                details = Format("Settings.CloudBackup.SnapshotDetails", device, size);
            }
            RemoteSnapshotItems.Add(new(item.Name, title, details, endpoint));
        }
    }

    /// <summary>
    /// Drops the localized option-name caches after a language change so the
    /// option tables, the status lines and the warning texts re-project in
    /// the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedLocalIntervalNames = null;
        _cachedLocalRetentionNames = null;
        _cachedProviderNames = null;
        _cachedIntervalNames = null;
        _cachedRetentionNames = null;
        OnPropertyChanged(nameof(AvailableLocalIntervalOptions));
        OnPropertyChanged(nameof(AvailableLocalRetentionOptions));
        OnPropertyChanged(nameof(AvailableProviderOptions));
        OnPropertyChanged(nameof(AvailableIntervalOptions));
        OnPropertyChanged(nameof(AvailableRetentionOptions));
        // Replacing localized option arrays makes WinUI reset every bound
        // ComboBox.SelectedIndex to -1; re-notify the selections.
        OnPropertyChanged(nameof(SelectedLocalIntervalMinutes));
        OnPropertyChanged(nameof(SelectedLocalRetentionCount));
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedIntervalMinutes));
        OnPropertyChanged(nameof(SelectedRetentionCount));
        OnPropertyChanged(nameof(HttpWarningText));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LocalFallbackWarningText));
        OnPropertyChanged(nameof(ShowLocalFallbackWarning));
        OnPropertyChanged(nameof(LocalDirectoryDisplayText));
        OnPropertyChanged(nameof(ShowCustomDirectoryRecoveryNote));
    }

    // --- Compatibility diagnostics (computed on the shell, pushed in) ---

    /// <summary>
    /// The drag-drop permission diagnostic projection pushed in by the shell
    /// (the diagnose/repair runs against host services there).
    /// </summary>
    public sealed record DragDropDiagnosticsView(
        string SummaryText,
        string DetailText,
        string ProcessText,
        string ExplorerText,
        string UacText,
        string AppCompatText,
        string StartupText,
        string ShortcutText,
        bool CanRepair);

    public string DragDropSummaryText => _dragDropSummaryText;
    public string DragDropDetailText => _dragDropDetailText;
    public string DragDropProcessText => _dragDropProcessText;
    public string DragDropExplorerText => _dragDropExplorerText;
    public string DragDropUacText => _dragDropUacText;
    public string DragDropAppCompatText => _dragDropAppCompatText;
    public string DragDropStartupText => _dragDropStartupText;
    public string DragDropShortcutText => _dragDropShortcutText;

    public string DragDropRepairStatusText
    {
        get => _dragDropRepairStatusText;
        private set => SetProperty(ref _dragDropRepairStatusText, value);
    }

    public bool CanRepairDragDrop => _canRepairDragDrop && !_isDragDropRepairing;

    public void SetDragDropDiagnostic(DragDropDiagnosticsView view)
    {
        _dragDropSummaryText = view.SummaryText;
        _dragDropDetailText = view.DetailText;
        _dragDropProcessText = view.ProcessText;
        _dragDropExplorerText = view.ExplorerText;
        _dragDropUacText = view.UacText;
        _dragDropAppCompatText = view.AppCompatText;
        _dragDropStartupText = view.StartupText;
        _dragDropShortcutText = view.ShortcutText;
        _canRepairDragDrop = view.CanRepair;
        OnPropertyChanged(nameof(DragDropSummaryText));
        OnPropertyChanged(nameof(DragDropDetailText));
        OnPropertyChanged(nameof(DragDropProcessText));
        OnPropertyChanged(nameof(DragDropExplorerText));
        OnPropertyChanged(nameof(DragDropUacText));
        OnPropertyChanged(nameof(DragDropAppCompatText));
        OnPropertyChanged(nameof(DragDropStartupText));
        OnPropertyChanged(nameof(DragDropShortcutText));
        OnPropertyChanged(nameof(CanRepairDragDrop));
    }

    /// <summary>The shell pushes the busy gate while a repair round-trip runs.</summary>
    public void SetDragDropRepairBusy(bool busy)
    {
        if (_isDragDropRepairing == busy)
        {
            return;
        }

        _isDragDropRepairing = busy;
        OnPropertyChanged(nameof(CanRepairDragDrop));
    }

    public void SetDragDropRepairStatus(string status) => DragDropRepairStatusText = status;

    public string RuntimeSummaryText => _runtimeSummaryText;
    public string RuntimeDetailText => _runtimeDetailText;

    public bool CanResyncRuntime => !_isRuntimeResyncing;

    public void SetRuntimeHealth(string summaryText, string detailText)
    {
        _runtimeSummaryText = summaryText;
        _runtimeDetailText = detailText;
        OnPropertyChanged(nameof(RuntimeSummaryText));
        OnPropertyChanged(nameof(RuntimeDetailText));
    }

    /// <summary>The shell pushes the busy gate while a runtime resync runs.</summary>
    public void SetRuntimeResyncBusy(bool busy)
    {
        if (_isRuntimeResyncing == busy)
        {
            return;
        }

        _isRuntimeResyncing = busy;
        OnPropertyChanged(nameof(CanResyncRuntime));
    }

    private string GetLocalIntervalDisplayName(int minutes) => minutes switch
    {
        5 or 30 => Format("Settings.DataBackup.Interval.Minutes", minutes),
        60 => Localize("Settings.DataBackup.Interval.Hour"),
        720 => Format("Settings.DataBackup.Interval.Hours", 12),
        1440 => Localize("Settings.DataBackup.Interval.Day"),
        7200 => Format("Settings.DataBackup.Interval.Days", 5),
        _ => minutes.ToString()
    };

    private string GetCloudIntervalDisplayName(int minutes) => minutes switch
    {
        60 => Localize("Settings.CloudBackup.Interval.Hour"),
        360 => Format("Settings.CloudBackup.Interval.Hours", 6),
        720 => Format("Settings.CloudBackup.Interval.Hours", 12),
        1440 => Localize("Settings.CloudBackup.Interval.Day"),
        10080 => Format("Settings.CloudBackup.Interval.Days", 7),
        _ => minutes.ToString()
    };

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.CurrentCulture,
                $"{Math.Max(0, bytes)} {Localize("Size.Unit.Bytes")}",
                CultureInfo.CurrentCulture);
        }

        string[] units =
        [
            Localize("Size.Unit.KB"),
            Localize("Size.Unit.MB"),
            Localize("Size.Unit.GB")
        ];
        double value = bytes;
        int unitIndex = -1;
        do
        {
            value /= 1024d;
            unitIndex++;
        }
        while (value >= 1024d && unitIndex < units.Length - 1);

        return string.Format(CultureInfo.CurrentCulture,
            $"{value:0.#} {units[unitIndex]}",
            CultureInfo.CurrentCulture);
    }

    // Build real SettingsOption[] arrays (not collection expressions): the
    // hidden read-only-array type cannot marshal across the WinRT ABI in
    // Native AOT builds and would leave the ItemsSource empty.
    private static SettingsOption[] BuildOptions<T>(T[] values, string[] displayNames)
    {
        var options = new SettingsOption[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            options[index] = new SettingsOption(values[index]!, displayNames[index]);
        }

        return options;
    }

    private static IReadOnlyList<SettingsOption> WrapOptions(SettingsOption[] options) =>
        options;
}
