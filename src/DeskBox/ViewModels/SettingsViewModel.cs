using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
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
    private const string CornerSquare = SettingsService.WidgetCornerPreferenceSquare;
    private const string CornerSmall = SettingsService.WidgetCornerPreferenceSmall;
    private const string CornerRound = SettingsService.WidgetCornerPreferenceRound;
    private const string MaterialMica = SettingsService.WidgetMaterialTypeMica;
    private const string MaterialMicaAlt = SettingsService.WidgetMaterialTypeMicaAlt;
    private const string MaterialAcrylic = SettingsService.WidgetMaterialTypeAcrylic;
    private const string MaterialAcrylicBase = SettingsService.WidgetMaterialTypeAcrylicBase;
    private const string MaterialSolid = SettingsService.WidgetMaterialTypeSolid;
    private const string BorderColorNeutral = SettingsService.WidgetBorderColorModeNeutral;
    private const string BorderColorAccent = SettingsService.WidgetBorderColorModeAccent;
    private const string BorderColorNone = SettingsService.WidgetBorderColorModeNone;
    private const string BorderNone = SettingsService.WidgetBorderStyleNone;
    private const string BorderThin = SettingsService.WidgetBorderStyleThin;
    private const string BorderMedium = SettingsService.WidgetBorderStyleMedium;
    private const string BorderThick = SettingsService.WidgetBorderStyleThick;
    private const string AnimationPresetGentle = "Gentle";
    private const string AnimationPresetStandard = "Standard";
    private const string AnimationPresetEmphasized = "Emphasized";
    private const string AnimationPresetCustom = "Custom";
    private const string RepositoryUrl = "https://github.com/Tianyu199509/DeskBox";
    private const string OfficialWebsiteUrl = "https://deskbox.fun";
    private const string MicrosoftStoreProductId = "9PBZSNB4D69H";
    private const string MicrosoftStoreCampaignId = "deskbox_about_support";
    private const string MicrosoftStoreUrl =
        "https://apps.microsoft.com/detail/" + MicrosoftStoreProductId + "?cid=" + MicrosoftStoreCampaignId;
    private const string MicrosoftStoreAppUrl =
        "ms-windows-store://pdp/?ProductId=" + MicrosoftStoreProductId + "&cid=" + MicrosoftStoreCampaignId;

    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly DeskBox.Contracts.IQuickCaptureSettings _quickCaptureSettings;
    private readonly DeskBox.Features.QuickCapture.QuickCaptureSettingsViewModel _quickCaptureSettingsEditor;
    private readonly DeskBox.Features.Weather.WeatherSettingsViewModel _weatherSettings;
    private readonly DeskBox.Features.Appearance.AppearanceSettingsViewModel _appearanceSettings;
    private readonly DeskBox.Features.Capsule.CapsuleSettingsViewModel _capsuleSettings;
    private readonly DeskBox.Features.Interaction.InteractionSettingsViewModel _interactionSettings;
    private readonly DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel _fileDisplaySettings;
    private readonly DeskBox.Features.FileStack.FileStackSettingsViewModel _fileStackSettings;
    private readonly DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel _groupNavigationSettings;
    private readonly DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel _featureWidgetsSettings;
    private readonly DeskBox.Features.Music.MusicSettingsViewModel _musicSettings;
    private readonly DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel _managedStorageSettings;
    private readonly DeskBox.Features.Maintenance.MaintenanceSettingsViewModel _maintenanceSettings;
    // The performance section's binding surface (incl. the General
    // section's inline preset combo) lives on the performance editor
    // (batch 50); the shell keeps it for the settings-broadcast refresh
    // and the language-change relocalization below.
    private readonly DeskBox.Features.Performance.PerformanceSettingsViewModel _performanceSettings;
    // The backup family's binding surface and visit state machine live on
    // the backup editor (batch 49); the shell keeps it for the settings-
    // broadcast refresh and the diagnostics pushes below.
    private readonly DeskBox.Features.Backup.BackupSettingsViewModel _backupSettings;
    private readonly LocalizationService _localizationService;
    private readonly WidgetContentFactory _widgetContentFactory;
    private readonly IAppUpdateService _appUpdateService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isDisposed;
    private CancellationTokenSource? _updateOperationCts;
    private AppUpdateManifest? _availableUpdateManifest;
    private AppUpdateManifest? _latestUpdateManifest;
    private string? _downloadedUpdateInstallerPath;
    private bool _showManualUpdateFallback;
    private bool _lastUpdateDownloadFailed;
    private long _updateBytesReceived;
    private long? _updateTotalBytes;
    private Color _currentAccentColor;
    private string _selectedLanguage = SettingsService.LanguageSystem;
    private bool _useSystemAccentColor;
    private string _managedStorageRootPath = SettingsService.GetDefaultManagedStorageRootPath();
    private QuickAccessPinState _quickAccessPinState = QuickAccessPinState.Unknown;
    private bool _isQuickAccessBusy;
    private StartupRegistrationState _autoStartState =
        StartupRegistrationState.NotRegistered;
    private DragDropPermissionDiagnostic? _dragDropPermissionDiagnostic;
    private bool _isRestoringDefaults;
    private bool _isApplyingSettingsSnapshot;
    private bool _isUpdatingHoverButtonActionSelection;

    private string[]? _cachedLanguageDisplayNames;

    [ObservableProperty] public partial bool AutoStart { get; set; }
    private bool _autoStartUsedFallback;
    private bool _autoStartOperationFailed;
    // Captured before ApplyAutoStartState's first overwrite so "persisted
    // preference was on, but the registration is gone" can be told apart
    // from an ordinary user-off: security/cleanup tools remove the registry
    // entry on reboot (feedback 376/481) and the switch would otherwise
    // silently flip with no explanation.
    private bool? _autoStartPreferenceAtLoad;
    [ObservableProperty] public partial string SelectedAutoStartMode { get; set; } = nameof(StartupMode.Standard);
    public object[] AvailableAutoStartModeOptions =>
    [
        new SettingsOption(nameof(StartupMode.Standard), _localizationService.T("Settings.AutoStart.Mode.Standard")),
        new SettingsOption(nameof(StartupMode.ScheduledTask), _localizationService.T("Settings.AutoStart.Mode.ScheduledTask"))
    ];
    // Keep the choice available while off, including when Standard cannot be
    // registered (for example an executable command longer than Run's limit).
    // SetMode only records a preference while startup is disabled.
    public Visibility AutoStartModeVisibility => StartupService.Current is DirectStartupService
        ? Visibility.Visible : Visibility.Collapsed;
    public string AutoStartStatusText => _autoStartState switch
    {
        StartupRegistrationState.DisabledByUser =>
            _localizationService.T("Settings.AutoStart.WindowsDisabled"),
        StartupRegistrationState.DisabledByTaskScheduler =>
            _localizationService.T("Settings.AutoStart.TaskDisabled"),
        StartupRegistrationState.Pending =>
            _localizationService.T("Settings.AutoStart.Pending"),
        StartupRegistrationState.Enabled when _autoStartUsedFallback =>
            _localizationService.T("Settings.AutoStart.Fallback"),
        _ when _autoStartOperationFailed =>
            _localizationService.T("Settings.AutoStart.ChangeFailed"),
        StartupRegistrationState.PathMismatch or
        StartupRegistrationState.BlockedOrFailed =>
            _localizationService.T("Settings.AutoStart.Failed"),
        StartupRegistrationState.NotRegistered
            when _autoStartPreferenceAtLoad == true =>
            _localizationService.T("Settings.AutoStart.ExternallyRemoved"),
        _ => string.Empty
    };
    public Visibility AutoStartStatusVisibility => string.IsNullOrEmpty(AutoStartStatusText)
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AutoStartSystemSettingsVisibility =>
        _autoStartState == StartupRegistrationState.DisabledByUser
            ? Visibility.Visible
            : Visibility.Collapsed;
    [ObservableProperty] public partial bool AutoCheckForUpdates { get; set; } = true;
    [ObservableProperty] public partial bool SilentStartup { get; set; }
    [ObservableProperty] public partial bool ShowHoverButtons { get; set; } = true;
    [ObservableProperty] public partial bool ShowHoverActionLockPosition { get; set; }
    [ObservableProperty] public partial bool ShowHoverActionLockSize { get; set; }
    [ObservableProperty] public partial bool ShowHoverActionAdd { get; set; } = true;
    [ObservableProperty] public partial bool ShowHoverActionMore { get; set; } = true;
    [ObservableProperty] public partial bool ShowHoverActionDelete { get; set; } = true;
    [ObservableProperty] public partial bool IsCheckingForUpdates { get; set; }
    [ObservableProperty] public partial bool IsDownloadingUpdate { get; set; }
    [ObservableProperty] public partial string UpdateStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpdateDetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial double UpdateProgressValue { get; set; }

    public SettingsViewModel(
        SettingsService settingsService,
        ThemeService themeService,
        DeskBox.Features.Todo.TodoSettingsViewModel todoSettings,
        DeskBox.Features.Weather.WeatherSettingsViewModel weatherSettings,
        DeskBox.Features.Backup.BackupSettingsViewModel backupSettings,
        DeskBox.Contracts.IQuickCaptureSettings quickCaptureSettings,
        DeskBox.Features.QuickCapture.QuickCaptureSettingsViewModel quickCaptureSettingsEditor,
        DeskBox.Contracts.ISearchFeatureSettings searchFeatureSettings,
        DeskBox.Features.Appearance.AppearanceSettingsViewModel appearanceSettings,
        DeskBox.Features.Capsule.CapsuleSettingsViewModel capsuleSettings,
        DeskBox.Features.Interaction.InteractionSettingsViewModel interactionSettings,
        DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel fileDisplaySettings,
        DeskBox.Features.FileStack.FileStackSettingsViewModel fileStackSettings,
        DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel groupNavigationSettings,
        DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel featureWidgetsSettings,
        DeskBox.Features.Music.MusicSettingsViewModel musicSettings,
        DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel managedStorageSettings,
        DeskBox.Features.Maintenance.MaintenanceSettingsViewModel maintenanceSettings,
        DeskBox.Features.Performance.PerformanceSettingsViewModel performanceSettings,
        LocalizationService? localizationService = null,
        IAppUpdateService? appUpdateService = null)
    {
        _settingsService = settingsService;
        _todoSettings = todoSettings;
        _todoSettings.PropertyChanged += OnTodoSettingsPropertyChanged;
        _backupSettings = backupSettings;
        _quickCaptureSettings = quickCaptureSettings;
        _quickCaptureSettingsEditor = quickCaptureSettingsEditor;
        _weatherSettings = weatherSettings;
        _searchFeatureSettings = searchFeatureSettings;
        _appearanceSettings = appearanceSettings;
        _capsuleSettings = capsuleSettings;
        _interactionSettings = interactionSettings;
        _fileDisplaySettings = fileDisplaySettings;
        _fileStackSettings = fileStackSettings;
        _groupNavigationSettings = groupNavigationSettings;
        _featureWidgetsSettings = featureWidgetsSettings;
        _musicSettings = musicSettings;
        _managedStorageSettings = managedStorageSettings;
        _maintenanceSettings = maintenanceSettings;
        _performanceSettings = performanceSettings;
        _themeService = themeService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _widgetContentFactory = new WidgetContentFactory(_localizationService);
        _appUpdateService = appUpdateService ?? new AppUpdateService();
        _isRestoringDefaults = true;
        UpdateStatusText = _localizationService.T("Settings.Update.Status.Ready");
        UpdateDetailText = GetReadyUpdateDetailText();

        var settings = settingsService.Settings;
        _selectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);

        _useSystemAccentColor = !string.Equals(settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        AutoStart = StartupService.IsEnabled();
        _autoStartState = AutoStart
            ? StartupRegistrationState.Enabled
            : StartupService.GetState();
        if (StartupService.Current is DirectStartupService directStartup)
            SelectedAutoStartMode = directStartup.Mode.ToString();
        AutoCheckForUpdates = settings.AutoCheckForUpdates;
        SilentStartup = settings.SilentStartup;
        ShowHoverButtons = settings.ShowHoverButtons;
        ApplyHoverButtonActionSelection(settings.WidgetHoverButtonActions);
        // The file-stack section's presentation (including the custom-rule
        // collection and its aggregation) lives on the file-stack editor now
        // (batch 45); its constructor syncs itself from the coordinator
        // snapshot. The rule-preview entries are widget items at first, and
        // the shell pushes the freshly scanned disk entries whenever the
        // section is entered.
        _fileStackPreviewEntries = BuildFileStackPreviewEntries(includeMappedFolders: false);
        _fileStackSettings.UpdatePreviewEntries(_fileStackPreviewEntries);
        // The capsule family's presentation lives on the capsule editor now
        // (batch 44); its constructor syncs itself from the coordinator
        // snapshots, and the widget/group override projection is pushed in
        // right after the editor fields are wired below.
        // The Todo section's presentation (layout, tabs, content editor,
        // reminders, footer display) and the Weather section's presentation
        // (location mode, city search, units, view, skin, data source,
        // refresh interval, display toggles) live on their section editors
        // now (batches 47/48); their constructors sync themselves from the
        // coordinator snapshots.
        _isRestoringDefaults = false;
        _managedStorageRootPath = settings.DefaultManagedStorageRootPath;

        ApplyCachedUpdateResult();
        RefreshAccentPreview();
        RefreshDragDropPermissionDiagnostic();
// The managed-storage editor shows the quick-access card; project the initial
// unknown state now and let the async refresh push the live pin state.
PushQuickAccessPresentation();
// The weather editor's suggestion list starts with the nearby popular
// cities so the search box's dropdown is populated on first focus.
_ = PopulateWeatherNearbyCitiesAsync();
_ = RefreshQuickAccessStateAsync();
        _settingsService.SettingsChanged += OnSettingsChanged;
        _quickCaptureSettings.Changed += OnQuickCaptureSettingsChanged;
        _quickCaptureSettings.DiagnosticsChanged += OnQuickCaptureClipboardDiagnosticsChanged;
        _searchFeatureSettings.FeatureChanged += OnSearchFeatureChanged;
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;

        // Quick Capture-section host linkages: the editor owns the section's
        // binding surface (its constructor syncs itself from the coordinator
        // snapshots); the shell answers text-size commits with the shared
        // appearance save pass (preview + debounced persistence, incl. the
        // slider-drag suppression flags).
        _quickCaptureSettingsEditor.ListTextSizeCommitted += OnQuickCaptureListTextSizeCommitted;
        _quickCaptureSettingsEditor.ContentTextSizeCommitted += OnQuickCaptureContentTextSizeCommitted;

        // Todo-section text-size commits follow the same contract as Quick
        // Capture: the editor persists the raw override values and the shell
        // answers with the shared appearance save pass so slider-drag
        // suppression and per-widget previews keep their original timing.
        _todoSettings.ListTextSizeCommitted += OnTodoListTextSizeCommitted;
        _todoSettings.ContentTextSizeCommitted += OnTodoContentTextSizeCommitted;

        // Weather-section host linkage (batch 48): the editor owns the
        // section's binding surface and persisted writes; the shell answers
        // user location-mode edits by re-running the Windows location
        // lookup and pushing the status line back into the editor.
        _weatherSettings.AutoLocationUserChanged += OnWeatherAutoLocationUserChanged;

        // Interaction-section host linkages: the editor owns the section's
        // binding surface and persisted writes, the shell still owns the
        // host-side effects that ran around the legacy facade writes. The
        // handlers live in the partials that already carried them.
        _interactionSettings.LayerModeUserChanged += OnInteractionLayerModeUserChanged;
        _interactionSettings.ShowDesktopBehaviorUserChanged += OnInteractionShowDesktopBehaviorUserChanged;
        _interactionSettings.SnapEnabledUserChanged += OnInteractionSnapEnabledUserChanged;
        _interactionSettings.SnapSpacingUserChanged += OnInteractionSnapSpacingUserChanged;
        _interactionSettings.HotkeyEnabledUserChanged += OnInteractionHotkeyEnabledUserChanged;
        _interactionSettings.FileItemContextMenuEnabledUserChanged += OnInteractionFileItemContextMenuUserChanged;
        _interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());

        // Appearance-section host linkages: the editor owns the section
        // family's binding surface and persisted writes, the shell still owns
        // the live-preview orchestration and the theme/accent/group-nav state
        // machines. The handlers live in the appearance-options partial.
        _appearanceSettings.AppearanceValueCommitted += OnAppearanceValueCommitted;
        _appearanceSettings.TextSizeCommitted += OnAppearanceTextSizeCommitted;
        _appearanceSettings.LayoutDensityMarkedCustom += OnAppearanceLayoutDensityMarkedCustom;
        _appearanceSettings.AnimationPresetApplied += OnAppearanceAnimationPresetApplied;
        _appearanceSettings.ThemeUserChanged += OnAppearanceThemeUserChanged;
        _appearanceSettings.TrayIconStyleUserChanged += OnAppearanceTrayIconStyleUserChanged;
        _appearanceSettings.AccentColorSourceUserChanged += OnAppearanceAccentColorSourceUserChanged;
        _appearanceSettings.AccentColorUserChanged += OnAppearanceAccentColorUserChanged;
        PushAppearanceHostEnvironment();
        PushAppearanceThemeSelection();

        // Group-navigation and capsule host linkages (batch 44): the editors
        // own the section binding surfaces; the shell answers user edits with
        // the explicit widget-group presentation notification and rebuilds
        // the pushed existing-groups / override projections.
        _groupNavigationSettings.PresentationUserChanged += OnGroupNavigationPresentationUserChanged;
        NotifyExistingWidgetGroupPropertiesChanged();
        NotifyCapsuleOverridePropertiesChanged();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settingsService.SaveAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _todoSettings.PropertyChanged -= OnTodoSettingsPropertyChanged;
        _todoSettings.Dispose();
        _backupSettings.Dispose();
        _lifetimeCts.Cancel();
        _updateOperationCts?.Cancel();
        _updateOperationCts?.Dispose();
        _quickCaptureSettings.Changed -= OnQuickCaptureSettingsChanged;
        _quickCaptureSettings.DiagnosticsChanged -= OnQuickCaptureClipboardDiagnosticsChanged;
        _searchFeatureSettings.FeatureChanged -= OnSearchFeatureChanged;

        _settingsService.SettingsChanged -= OnSettingsChanged;
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _weatherSettings.AutoLocationUserChanged -= OnWeatherAutoLocationUserChanged;
        // Cancel the live city search; its owning invocation disposes the
        // source in its own finally once it unwinds on the canceled token
        // (the field is only non-null while a search invocation runs).
        _citySearchCts?.Cancel();
        _citySearchService?.Dispose();
        _lifetimeCts.Dispose();
    }

    private void OnAppearanceChanged()
    {
        RefreshAccentPreview();
    }

    private void RefreshAccentPreview()
    {
        _currentAccentColor = _themeService.GetEffectiveAccentColor();
        // The accent card lives on the appearance editor; push the effective
        // color and mode projection instead of mirroring shell properties.
        PushAppearanceAccentPresentation();
    }

    private static string FormatNumber(double value, int decimals)
    {
        string format = decimals <= 0 ? "0" : $"0.{new string('#', decimals)}";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.CurrentCulture, 
                $"{Math.Max(0, bytes)} {_localizationService.T("Size.Unit.Bytes")}", 
                CultureInfo.CurrentCulture);
        }

        var units = new[] 
        {
            _localizationService.T("Size.Unit.KB"),
            _localizationService.T("Size.Unit.MB"),
            _localizationService.T("Size.Unit.GB")
        };
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

    private void SaveAppearanceChange()
    {
        if (DeferAppearancePersistence)
        {
            _settingsService.RequestAppearancePreview();
            return;
        }

        if (!SuppressAppearanceNotifications)
        {
            _settingsService.RequestAppearancePreview();
        }

        _settingsService.SaveDebounced(
            notifySubscribers: !SuppressAppearanceNotifications,
            changeKind: SettingsChangeKind.Appearance);
    }

}
