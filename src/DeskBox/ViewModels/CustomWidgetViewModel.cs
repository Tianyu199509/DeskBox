using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Launcher = Windows.System.Launcher;

namespace DeskBox.ViewModels;

public enum CustomWidgetSecurityConsentDecision
{
    Deny = 0,
    AllowOnce = 1,
    AllowAndRemember = 2
}

public sealed record CustomWidgetSecurityPrompt(
    string ActionKind,
    string TargetDisplay,
    string WidgetName);

public sealed record CustomWidgetScriptRuntimeError(
    string FunctionName,
    string ErrorMessage,
    int ErrorLine,
    string? Arg = null,
    long TimestampUtcTicks = 0);

public sealed partial class CustomWidgetViewModel : ObservableObject, IDisposable
{
    public const string TemplateMetadataKey = "custom.templateId";
    internal const string TrustedExternalTargetsSettingKey = "__trustedTargets";

    private readonly CustomWidgetDataStore _store;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Func<NativeAppNotificationService?>? _notificationServiceProvider;
    private readonly CustomWidgetNetworkSourceEngine _networkSourceEngine;
    private readonly DeskBoxAiService _aiService;
    private readonly Func<string?> _preferredAiProviderResolver;
    private readonly CustomWidgetSecretStore _secretStore;
    private readonly CustomWidgetWeatherSource _weatherSource;
    private readonly CustomWidgetSystemControlService _systemControlService;
    private readonly SettingsService? _settingsService;
    private readonly CustomWidgetRemoteCatalogService? _remoteCatalogService;
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _stateJson = new(StringComparer.Ordinal);
    private readonly CustomWidgetStructuredStateView _state;
    private readonly Dictionary<string, JsonElement> _computed = new(StringComparer.Ordinal);
    private readonly ComputedDictionaryWithRecordCache _computedView;
    private readonly Dictionary<string, List<CustomWidgetRecord>> _collections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionAuthorizedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CustomWidgetJobBus _jobBus = new();
    private readonly CustomWidgetCapabilityHost _capabilityHost;
    private readonly ConcurrentDictionary<string, long> _deniedExternalTargetCooldownUtcTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CustomWidgetScriptRuntimeCoordinator _scriptRuntime;
    private readonly CustomWidgetDropZoneCoordinator _dropZoneCoordinator;
    private readonly CustomWidgetActionDispatcher _actionDispatcher;
    private readonly CustomWidgetAiAndLocalActionExecutor _aiAndLocalExecutor;
    private readonly CustomWidgetPreviewAndReloadCoordinator _previewAndReload;
    private readonly object _headlessStateGate = new();
    private long _initializedWorkspaceWriteTicks;
    private bool _hasDirtyStateSinceLastScriptInit;
    private CustomWidgetManifest? _continuousScrubCacheManifest;
    private HashSet<string>? _continuousScrubStateKeys;
    internal long InitializedWorkspaceWriteTicks => Volatile.Read(ref _initializedWorkspaceWriteTicks);
    internal bool HasDirtyStateSinceLastScriptInitInternal
    {
        get => _hasDirtyStateSinceLastScriptInit;
        set => _hasDirtyStateSinceLastScriptInit = value;
    }
    internal bool HasScriptCodeInternal => _scriptRuntime.CompiledScript is not null;
    public const int MaxAiRunsPerDropBatch = 3;
    internal Func<uint>? ClipboardSequenceNumberProviderOverride { get; set; }
    internal Func<Task<string?>>? ClipboardTextReaderOverride { get; set; }
    internal Func<CancellationToken, Task<CustomWidgetMusicSnapshot>>? MusicSnapshotProviderOverride { get; set; }
    internal Func<int, CancellationToken, Task<CustomWidgetTodoSnapshot>>? TodoSnapshotProviderOverride { get; set; }
    private CustomWidgetManifest _manifest = new();
    private CustomWidgetLocaleCatalog? _loadedLocales;
    private string? _previewCultureOverride;
    private IReadOnlyList<CustomWidgetValidationDiagnostic> _diagnostics = [];
    private CustomWidgetTransformResult? _lastTransformResult;
    private bool _isWindowVisible = true;
    private bool _isPowerSuspended;
    private bool _isCompactCollapsed;
    private bool _isInitialized;
    private bool _isDisposed;
    private long _liveStateVersion;
    private long _notifiedLiveStateVersion;
    private long _collectionsMutationVersion = 1;
    private long _computedMutationVersion = 1;
    internal long CollectionsMutationVersion => Volatile.Read(ref _collectionsMutationVersion);
    internal void BumpCollectionsVersion() => Interlocked.Increment(ref _collectionsMutationVersion);
    internal long StateMutationVersion => _state.MutationVersion;
    internal long ComputedMutationVersion => Volatile.Read(ref _computedMutationVersion);
    internal void BumpComputedVersion() => Interlocked.Increment(ref _computedMutationVersion);
    internal bool IsAmbientAudioPlaying =>
        CustomWidgetAudioService.Shared.IsTrackPlaying($"{Config.Id}:ambient") ||
        _audioService.IsTrackPlaying("ambient");
    private string _capsuleTitle = string.Empty;
    private string _capsuleSummary = string.Empty;
    private string _capsuleGlyph = "\uE71D";
    private string _capsuleBadge = string.Empty;
    private bool _capsuleBadgeIsWarning;
    private double? _capsuleProgress;
    private bool _capsuleEnableBounce = true;
    private bool _capsuleHasPrimaryAction;
    private string _capsulePrimaryActionGlyph = "\uE710";
    private string _capsulePrimaryActionTooltip = string.Empty;
    private bool _capsuleProjectionDirty;
    private readonly object _debouncedPersistLock = new();
    private CancellationTokenSource? _debouncedPersistCts;
    private bool _hasPendingDebouncedPersist;
    private readonly bool _isPreviewOnly;

    public CustomWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        CustomWidgetDataStore? store = null,
        DispatcherQueue? dispatcherQueue = null,
        Func<NativeAppNotificationService?>? notificationServiceProvider = null,
        bool isPreviewOnly = false,
        CustomWidgetNetworkSourceEngine? networkSourceEngine = null,
        DeskBoxAiService? aiService = null,
        Func<string?>? preferredAiProviderResolver = null,
        CustomWidgetSecretStore? secretStore = null,
        CustomWidgetWeatherSource? weatherSource = null,
        CustomWidgetSystemControlService? systemControlService = null,
        SettingsService? settingsService = null,
        CustomWidgetRemoteCatalogService? remoteCatalogService = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(localizationService);

        _state = new CustomWidgetStructuredStateView(_stateJson);
        _computedView = new ComputedDictionaryWithRecordCache(_computed);
        Config = config;
        _localizationService = localizationService;
        _store = store ?? (isPreviewOnly
            ? new CustomWidgetDataStore(config.Id, createDirectory: false)
            : CustomWidgetDataStore.ForWidget(config.Id));
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();
        _notificationServiceProvider = notificationServiceProvider;
        _isPreviewOnly = isPreviewOnly;
        _networkSourceEngine = networkSourceEngine ?? CustomWidgetNetworkSourceEngine.Shared;
        _aiService = aiService ?? DeskBoxAiService.Shared;
        _preferredAiProviderResolver = preferredAiProviderResolver
            ?? (static () => CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId);
        _secretStore = secretStore ?? CustomWidgetSecretStore.Shared;
        _weatherSource = weatherSource ?? CustomWidgetWeatherSource.Shared;
        _systemControlService = systemControlService ?? CustomWidgetSystemControlService.Shared;
        _settingsService = settingsService;
        _systemControlService.AttachFallbackSettingsService(settingsService);
        _remoteCatalogService = isPreviewOnly
            ? remoteCatalogService
            : (remoteCatalogService ?? CustomWidgetRemoteCatalogService.Shared);

        _capabilityHost = new CustomWidgetCapabilityHost(this);
        _scriptRuntime = new CustomWidgetScriptRuntimeCoordinator(this);
        _dropZoneCoordinator = new CustomWidgetDropZoneCoordinator(this);
        _actionDispatcher = new CustomWidgetActionDispatcher(this);
        _actionDispatcher.RuntimeTrace.ErrorRecorded += OnRuntimeTraceErrorRecorded;
        _aiAndLocalExecutor = new CustomWidgetAiAndLocalActionExecutor(this);
        _previewAndReload = new CustomWidgetPreviewAndReloadCoordinator(this);
        _localizationService.LanguageChanged += OnLanguageChanged;
        _jobBus.ActiveStateChanged += OnJobBusActiveStateChanged;
        CustomWidgetAudioService.Shared.AmbientOwnerPreempted += OnSharedAmbientOwnerPreempted;
    }

    private void OnSharedAmbientOwnerPreempted(string preemptedOwnerId)
    {
        if (_isDisposed || !string.Equals(preemptedOwnerId, Config.Id, StringComparison.Ordinal))
        {
            return;
        }

        void ApplyPreemptionOnUi()
        {
            if (_isDisposed)
            {
                return;
            }

            _audioService.StopAll();
            if (HasScriptCodeInternal)
            {
                if (_scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onAmbientPreempted") == true)
                {
                    TryRunScriptLifecycleHook(
                        "onAmbientPreempted",
                        CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                        persistIfMutated: false);
                }
                else
                {
                    TryRunScriptLifecycleHook(
                        "onInit",
                        CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                        persistIfMutated: false);
                }

                if (!_isPreviewOnly)
                {
                    ScheduleDebouncedPersist();
                }

                RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
                NotifyMetricsRefreshed();
                NotifyViewRebuildRequested();
            }
        }

        if (_dispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(ApplyPreemptionOnUi);
        }
        else
        {
            ApplyPreemptionOnUi();
        }
    }

    private void OnJobBusActiveStateChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() =>
            {
                if (!_isDisposed)
                {
                    UpdateScriptTickSubscription();
                }
            });
            return;
        }

        UpdateScriptTickSubscription();
    }

    private static DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public Func<CustomWidgetSecurityPrompt, Task<bool>>? ExternalActionConsentPrompt { get; set; }
    public Func<CustomWidgetSecurityPrompt, Task<CustomWidgetSecurityConsentDecision>>? ExternalActionDecisionPrompt { get; set; }
    public Func<IntPtr>? OwnerWindowHandleResolver { get; set; }

    internal bool HasExternalActionConsentPrompt =>
        ExternalActionDecisionPrompt is not null || ExternalActionConsentPrompt is not null;

    internal async Task<CustomWidgetSecurityConsentDecision> PromptSecurityDecisionAsync(CustomWidgetSecurityPrompt prompt)
    {
        if (ExternalActionDecisionPrompt is { } decisionFunc)
        {
            return await decisionFunc(prompt).ConfigureAwait(false);
        }

        if (ExternalActionConsentPrompt is { } legacyFunc)
        {
            bool approved = await legacyFunc(prompt).ConfigureAwait(false);
            return approved
                ? CustomWidgetSecurityConsentDecision.AllowAndRemember
                : CustomWidgetSecurityConsentDecision.Deny;
        }

        return CustomWidgetSecurityConsentDecision.Deny;
    }

    public WidgetConfig Config { get; }
    public string ActiveCultureName => !string.IsNullOrWhiteSpace(_previewCultureOverride)
        ? _previewCultureOverride!
        : _localizationService.CurrentCultureName;
    public bool IsInitialized => _isInitialized;
    public CustomWidgetDataStore Store => _store;
    public CustomWidgetManifest Manifest => _manifest;
    public IReadOnlyList<CustomWidgetValidationDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlyDictionary<string, string> Settings => _settings;
    public IReadOnlyDictionary<string, string> Secrets => _secrets;
    public IReadOnlyDictionary<string, string> State => _state;
    public IReadOnlyDictionary<string, JsonElement> StateJson => _stateJson;
    public IReadOnlyDictionary<string, JsonElement> Computed => _computedView;
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> Collections => _collections;
    public CustomWidgetJobBus JobBus => _jobBus;
    public string? RawLogicJs => _scriptRuntime.RawLogicJs;
    public CustomWidgetScriptRuntimeError? LastScriptError { get; internal set; }
    public CustomWidgetWeatherSnapshot WeatherSnapshot => _capabilityHost.WeatherSnapshot;
    public CustomWidgetDeviceStateSnapshot DeviceStateSnapshot => _capabilityHost.DeviceStateSnapshot;
    public CustomWidgetClipboardSnapshot ClipboardSnapshot => _capabilityHost.ClipboardSnapshot;
    public CustomWidgetStorageDrivesSnapshot StorageDrivesSnapshot => _capabilityHost.StorageDrivesSnapshot;
    public CustomWidgetMusicSnapshot MusicSnapshot => _capabilityHost.MusicSnapshot;
    public CustomWidgetTodoSnapshot TodoSnapshot => _capabilityHost.TodoSnapshot;
    public CustomWidgetTransformResult? LastTransformResult => _lastTransformResult;

    internal void RunWithHeadlessStateGate(Action action)
    {
        if (_dispatcherQueue is null)
        {
            lock (_headlessStateGate)
            {
                action();
            }
        }
        else
        {
            action();
        }
    }

    public bool HasDropZoneNode => HasAnyDropZoneNode();

    public string EffectiveDropAccept =>
        !string.IsNullOrWhiteSpace(_manifest.OnDrop?.Accept)
            ? _manifest.OnDrop!.Accept
            : (FindAnyDropZoneAccept() ?? "files");

    public bool AcceptsDrops =>
        _manifest.OnDrop is not null || HasDropZoneNode;

    public bool AcceptsFileDrops =>
        DoesManifestAcceptFileDrops(_manifest);

    public bool HasRecycleBinDropTarget =>
        NodeTreeContainsType(_manifest.View, "RecycleBin") ||
        _manifest.Popovers.Any(static p => NodeTreeContainsType(p, "RecycleBin"));

    public bool HasActiveBackgroundWork =>
        !_isDisposed &&
        (_jobBus.ActiveCount > 0 ||
         _aiAndLocalExecutor.HasAiActionInFlight);

    public long ScriptTickExecutionCount =>
        _scriptRuntime.ExecutionCount;

    public IReadOnlyList<CustomWidgetSettingDefinition> SecretSettingDefinitions =>
        _manifest.ResolveLocalizedSettings(ActiveCultureName)
            .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public string EffectiveDropMode
    {
        get
        {
            string? rawMode = _manifest.OnDrop?.Mode ?? FindAnyDropZoneStorageMode();
            if (string.IsNullOrWhiteSpace(rawMode))
            {
                return "link";
            }
            string evaluated = CustomWidgetExpressionEvaluator.Interpolate(
                rawMode,
                CreateEvaluationContext());
            return string.Equals(evaluated, "copy", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(evaluated, "auto", StringComparison.OrdinalIgnoreCase)
                ? "copy"
                : "link";
        }
    }

    // Capsule projection properties (lazily evaluated when expanded to avoid background PropertyChanged churn)
    public string CapsuleTitle
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleTitle;
        }
    }

    public string CapsuleSummary
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleSummary;
        }
    }

    public string CapsuleGlyph
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleGlyph;
        }
    }

    public bool IsUsingDynamicCapsuleGlyph
    {
        get
        {
            if (_manifest.Capsule is not { } cap || string.IsNullOrWhiteSpace(cap.Glyph))
            {
                return false;
            }

            string currentGlyph = CapsuleGlyph;
            return !string.IsNullOrWhiteSpace(currentGlyph) &&
                   !string.Equals(currentGlyph, _manifest.Icon, StringComparison.Ordinal);
        }
    }

    public string CapsuleBadge
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleBadge;
        }
    }

    public bool CapsuleBadgeIsWarning
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleBadgeIsWarning;
        }
    }

    public double? CapsuleProgress
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleProgress;
        }
    }

    public bool CapsuleEnableBounce
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleEnableBounce;
        }
    }

    public bool CapsuleHasPrimaryAction
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsuleHasPrimaryAction;
        }
    }

    public string CapsulePrimaryActionGlyph
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsulePrimaryActionGlyph;
        }
    }

    public string CapsulePrimaryActionTooltip
    {
        get
        {
            EnsureCapsuleProjectionFresh();
            return _capsulePrimaryActionTooltip;
        }
    }

    public Task ExecuteCapsulePrimaryActionAsync()
    {
        if (_isDisposed || _manifest.Capsule?.PrimaryAction?.OnClick is not { Count: > 0 } actions)
        {
            return Task.CompletedTask;
        }

        return ExecuteActionsAsync(actions);
    }

    public string LiveStateKey => $"{Config.Id}:{_liveStateVersion}";
    public string ManifestEmojiIcon { get; private set; } = string.Empty;
    public string TitleGlyph => CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs(
        CustomWidgetTemplates.IsSegoeFluentGlyph(_manifest.Icon)
            ? _manifest.Icon
            : CustomWidgetTemplates.MatchPresetOrNull(_manifest.Id) is { } p
                ? (CustomWidgetTemplates.IsSegoeFluentGlyph(p.IconGlyph) ? p.IconGlyph : p.MonochromeFallbackGlyph)
                : "\uE71D",
        WindowsCompatibilityService.IsWindows11OrLater);
    public string TitleIconKind =>
        WidgetTitleIconKindNames.FromCustomTemplateId(_manifest.Id) ??
        WidgetTitleIconKindNames.FromWidgetConfig(Config);

    public event EventHandler? ViewRebuildRequested;
    public event EventHandler? MetricsRefreshed;
    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;
    public event EventHandler<(string Title, string Message)>? PreviewNotificationDispatched;
    public event Action<string, string>? ManifestIdentityChanged;
    public event Action<CustomWidgetScriptRuntimeError?>? ScriptErrorChanged;
    public event Action<string, string>? SettingChanged;
    public event Action<string, string>? SecretChanged;

    public bool AllowPreviewHostStandIns { get; set; } = true;

    public void ApplyPreviewBundle(
        string rawJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales = null,
        bool preserveRuntimeData = true)
    {
        if (_isDisposed)
        {
            return;
        }

        _scriptRuntime.RawLogicJs = string.IsNullOrWhiteSpace(logicJs) ? null : logicJs;
        _loadedLocales = locales is { HasAnyLocale: true } ? locales : null;
        ApplyPreviewManifestJson(rawJson, preserveRuntimeData);
    }

    public void SetPreviewCultureOverride(string? cultureName)
    {
        if (_isDisposed)
        {
            return;
        }

        string? normalized = string.IsNullOrWhiteSpace(cultureName) ? null : cultureName.Trim();
        if (string.Equals(_previewCultureOverride, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewCultureOverride = normalized;
        string previewTitle = _manifest.ResolveLocalizedName(ActiveCultureName);
        if (!string.IsNullOrWhiteSpace(previewTitle))
        {
            Config.Name = previewTitle;
        }

        TryRunScriptLifecycleHook(
            "onInit",
            CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
            persistIfMutated: false);
        if (_scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onTick") == true)
        {
            TryRunScriptLifecycleHook(
                "onTick",
                CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                persistIfMutated: false);
        }
        if (ManifestUsesNetworkSources() &&
            _scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onSourceData") == true)
        {
            HydrateNetworkSourcesFromDiskCache(overwriteExistingCollection: false);
        }
        if (ManifestUsesTodo() && !IsStudioGalleryThumbnailPreview)
        {
            _ = _capabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: _isInitialized);
        }
        if (ManifestUsesWeather() && !IsStudioGalleryThumbnailPreview)
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        if (_isInitialized)
        {
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyPreviewManifestJson(string rawJson, bool preserveRuntimeData = true)
    {
        if (_isDisposed)
        {
            return;
        }

        var preservedPayload = new CustomWidgetStorePayload
        {
            Settings = preserveRuntimeData
                ? new Dictionary<string, string>(_settings, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            State = preserveRuntimeData
                ? new Dictionary<string, string>(_state, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            StateJson = preserveRuntimeData
                ? _stateJson.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone(),
                    StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Collections = preserveRuntimeData
                ? _collections.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<CustomWidgetRecord>(kvp.Value),
                    StringComparer.Ordinal)
                : new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
        };

        if (!preserveRuntimeData)
        {
            _capabilityHost.Network.ClearFetchTimestamps();
            _hasUserStateMutation = false;
            _userMutatedCollections.Clear();
            _sampleSeededCollections.Clear();
            _sampleSeededStateKeys.Clear();
            HasPreviewSampleData = false;
            _actionDispatcher.RuntimeTrace.Clear();
        }
        ApplyManifestAndPayload(rawJson, preservedPayload);
        string previewTitle = _manifest.ResolveLocalizedName(ActiveCultureName);
        if (!string.IsNullOrWhiteSpace(previewTitle))
        {
            Config.Name = previewTitle;
        }
        if (!IsStudioGalleryThumbnailPreview &&
            !IsUntrustedStagePreview &&
            (_manifest.Script?.TickIntervalMs ?? 0) > 0 &&
            _scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onTick") == true)
        {
            TryRunScriptLifecycleHook(
                "onTick",
                CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                persistIfMutated: false);
            RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        }
        UpdateSourceSubscriptions();
        _isInitialized = true;
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool HasPreviewSampleData { get; private set; }
    public bool HasUserRuntimeMutations =>
        _hasUserStateMutation || _userMutatedCollections.Count > 0;

    private readonly HashSet<string> _sampleSeededCollections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userMutatedCollections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sampleSeededStateKeys = new(StringComparer.Ordinal);
    private bool _hasUserStateMutation;
    private bool _isApplyingSampleData;

    internal CustomWidgetTrustStore? TrustStoreOverride { get; set; }

    /// <summary>
    /// True when rendering an uncommitted external/agent stage preview (e.g. <c>deskbox_capture_widget_preview</c>
    /// or unconfirmed Studio stage preview) where side-effecting local file/command/LAN sources must not auto-run.
    /// </summary>
    public bool IsUntrustedStagePreview { get; set; }

    /// <summary>
    /// Fired when an action requests opening, closing, or toggling an anchored popover (`(mode, popoverId)`).
    /// </summary>
    public event Action<string, string>? PopoverCommandRequested;

    /// <summary>
    /// Fired when an action requests opening, closing, or toggling a native <c>TeachingTip</c> (`(mode, tipId)`).
    /// </summary>
    public event Action<string, string>? TeachingTipCommandRequested;

    /// <summary>
    /// Fired whenever the runtime trace buffer records a new <c>error</c> or <c>denied</c> entry.
    /// </summary>
    public event Action<CustomWidgetRuntimeTraceEntry>? RuntimeTraceErrorRecorded;

    private void OnRuntimeTraceErrorRecorded(CustomWidgetRuntimeTraceEntry entry)
    {
        if (!_isDisposed)
        {
            RuntimeTraceErrorRecorded?.Invoke(entry);
        }
    }

    private readonly CustomWidgetAudioService _audioService = new();

    public CustomWidgetAudioService AudioService => _audioService;

    internal CustomWidgetTrustStore EffectiveTrustStore => TrustStoreOverride ?? CustomWidgetTrustStore.Shared;

    public CustomWidgetTrustProfile GetTrustProfile()
    {
        if (IsUntrustedStagePreview)
        {
            return new CustomWidgetTrustProfile(
                Config.Id,
                TrustedLocalAuthoring: false,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }

        if (_isPreviewOnly && TrustStoreOverride is null)
        {
            bool trustedPreview =
                !string.Equals(_manifest.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_manifest.Origin, CustomWidgetOriginKinds.Agent, StringComparison.OrdinalIgnoreCase);
            return new CustomWidgetTrustProfile(
                Config.Id,
                TrustedLocalAuthoring: trustedPreview,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }

        return EffectiveTrustStore.GetProfile(Config.Id, _manifest.Origin, _manifest.Id);
    }

    public bool IsPermissionAllowed(CustomWidgetPermissionKind kind, bool isReadOnlyPublicGet = false) =>
        GetTrustProfile().IsPermissionGranted(kind, isReadOnlyPublicGet);

    public void RequestPopoverCommand(string mode, string? popoverId)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(popoverId))
        {
            return;
        }

        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();
        if (normalizedMode is not ("open" or "close" or "toggle"))
        {
            normalizedMode = "toggle";
        }

        PopoverCommandRequested?.Invoke(normalizedMode, popoverId.Trim());
    }

    public void RequestTeachingTipCommand(string mode, string? tipId)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(tipId))
        {
            return;
        }

        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();
        if (normalizedMode is not ("open" or "close" or "toggle"))
        {
            normalizedMode = "toggle";
        }

        TeachingTipCommandRequested?.Invoke(normalizedMode, tipId.Trim());
    }


    internal static string JsonScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array or JsonValueKind.Object => el.GetRawText(),
        _ => string.Empty
    };

    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        string defaultTemplateId = Config.Metadata.TryGetValue(TemplateMetadataKey, out string? tpl) &&
                                   !string.IsNullOrWhiteSpace(tpl)
            ? tpl
            : CustomWidgetTemplates.SystemPulseTemplateId;

        string rawJson;
        string? rawLogicJs = null;
        if (_isPreviewOnly)
        {
            rawJson = await _store.ReadOrCreateManifestJsonAsync(
                defaultTemplateId,
                _remoteCatalogService,
                createIfMissing: false);
            if (_isDisposed)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                rawLogicJs = await _store.ReadLogicJsOrNullAsync();
                if (_isDisposed)
                {
                    return;
                }
                _loadedLocales = await _store.ReadLazyRuntimeLocalesAsync(ActiveCultureName);
                if (_isDisposed)
                {
                    return;
                }
            }
            else
            {
                CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.ResolveEffectiveTemplateBundle(defaultTemplateId, ActiveCultureName);
                rawJson = bundle.ManifestJson;
                rawLogicJs = bundle.LogicJs;
                _loadedLocales = bundle.Locales;
            }
        }
        else
        {
            rawJson = await _store.ReadOrCreateManifestJsonAsync(defaultTemplateId, _remoteCatalogService);
            if (_isDisposed)
            {
                return;
            }
            rawLogicJs = await _store.ReadLogicJsOrNullAsync();
            if (_isDisposed)
            {
                return;
            }
            _loadedLocales = await _store.ReadLazyRuntimeLocalesAsync(ActiveCultureName);
            if (_isDisposed)
            {
                return;
            }
        }
        _scriptRuntime.RawLogicJs = rawLogicJs;
        CustomWidgetStorePayload payload = await _store.LoadAsync();
        if (_isDisposed)
        {
            return;
        }
        ApplyManifestAndPayload(rawJson, payload);
        if (_isDisposed)
        {
            return;
        }
        SyncManifestEmojiToTitleIcon(previousManifestIcon: null, isReload: false);
        StampInitializedWorkspaceTimestamps();

        if (!_isPreviewOnly)
        {
            StartManifestWatcher();
        }
        if (_dispatcherQueue is not null &&
            !IsStudioGalleryThumbnailPreview &&
            !IsUntrustedStagePreview &&
            (_manifest.Script?.TickIntervalMs ?? 0) >= 5_000 &&
            _scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onTick") == true)
        {
            TryRunScriptLifecycleHook(
                "onTick",
                CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                persistIfMutated: false);
            RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        }
        if (_isDisposed)
        {
            return;
        }
        UpdateSourceSubscriptions();
        _isInitialized = true;
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReloadManifestFromDiskAsync(bool notifyFeedback = false)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            string defaultTemplateId = Config.Metadata.TryGetValue(TemplateMetadataKey, out string? tpl) &&
                                       !string.IsNullOrWhiteSpace(tpl)
                ? tpl
                : CustomWidgetTemplates.SystemPulseTemplateId;
            string rawJson = await _store.ReadOrCreateManifestJsonAsync(
                defaultTemplateId,
                _remoteCatalogService,
                createIfMissing: false);
            if (string.IsNullOrWhiteSpace(rawJson) || _isDisposed)
            {
                App.LogVerbose($"[CustomWidget] Reload skipped for {Config.Id}: manifest missing on disk or disposed.");
                return;
            }
            string? rawLogicJs = _scriptRuntime.RawLogicJs;
            if (!_isPreviewOnly)
            {
                rawLogicJs = await _store.ReadLogicJsOrNullAsync();
                if (_isDisposed)
                {
                    return;
                }
                _loadedLocales = await _store.ReadLazyRuntimeLocalesAsync(ActiveCultureName);
                if (_isDisposed)
                {
                    return;
                }
                _secretStore.InvalidateCache();
            }
            _scriptRuntime.RawLogicJs = rawLogicJs;
            CustomWidgetStorePayload payload = await _store.ReloadFromDiskAsync();
            if (_isDisposed)
            {
                return;
            }

            string previousManifestName = string.Empty;
            string previousManifestIcon = string.Empty;
            bool secretsChanged = false;
            bool settingsChanged = false;
            RunWithHeadlessStateGate(() =>
            {
                previousManifestName = _manifest.ResolveLocalizedName(ActiveCultureName);
                previousManifestIcon = _manifest.Icon;
                var previousSettings = new Dictionary<string, string>(_settings, StringComparer.Ordinal);
                var previousSecrets = new Dictionary<string, string>(_secrets, StringComparer.Ordinal);
                ApplyManifestAndPayload(rawJson, payload, previousSettings);
                SyncManifestEmojiToTitleIcon(previousManifestIcon, isReload: true);
                secretsChanged = !DictionariesEqual(previousSecrets, _secrets);
                settingsChanged = !DictionariesEqual(previousSettings, _settings);
                if (secretsChanged || settingsChanged)
                {
                    _capabilityHost.Network.ClearFetchTimestamps();
                    _capabilityHost.FileText.ClearFetchTimestamps();
                    _capabilityHost.Command.ClearFetchTimestamps();
                }
            });

            string currentLocalizedName = _manifest.ResolveLocalizedName(ActiveCultureName);
            bool nameChanged = !_diagnostics.Any(d => d.IsError) &&
                               !string.IsNullOrWhiteSpace(currentLocalizedName) &&
                               !string.Equals(previousManifestName, currentLocalizedName, StringComparison.Ordinal);
            bool iconChanged = !string.Equals(previousManifestIcon, _manifest.Icon, StringComparison.Ordinal);
            if (nameChanged && !CustomWidgetTemplates.IsKnownBuiltInTemplate(_manifest.Id))
            {
                Config.Name = currentLocalizedName;
                _previewAndReload.LastAutoLocalizedTitle = currentLocalizedName;
            }
            if (nameChanged || iconChanged)
            {
                ManifestIdentityChanged?.Invoke(currentLocalizedName, _manifest.Icon);
            }

            _capabilityHost.FileText.ClearFetchTimestamps();
            _capabilityHost.Command.ClearFetchTimestamps();
            StampInitializedWorkspaceTimestamps();
            UpdateSourceSubscriptions();
            if (ManifestUsesFileTextSources() && !_isDisposed)
            {
                await RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
            }
            if ((secretsChanged || settingsChanged) && ManifestUsesNetworkSources())
            {
                _ = RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false);
            }
            if (settingsChanged && ManifestUsesWeather())
            {
                _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
            }

            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);

            if (notifyFeedback)
            {
                RaiseFeedback(
                    _diagnostics.Any(d => d.IsError)
                        ? _localizationService.Format("CustomWidget.Toast.ValidationIssues", _diagnostics.Count(d => d.IsError))
                        : _localizationService.T("CustomWidget.Toast.Reloaded"),
                    _diagnostics.Any(d => d.IsError)
                        ? WidgetFeedbackSeverity.Warning
                        : WidgetFeedbackSeverity.Info);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Reload failed for {Config.Id}: {ex.Message}");
        }
    }

    public async Task ReloadStoreOnlyFromDiskAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            CustomWidgetStorePayload payload = await _store.ReloadFromDiskAsync();
            if (_isDisposed)
            {
                return;
            }

            string? weatherCityBefore = ManifestUsesWeather() ? ResolveEffectiveWeatherCity() : null;
            Dictionary<int, string>? networkKeysBefore = null;
            if (ManifestUsesNetworkSources())
            {
                networkKeysBefore = new Dictionary<int, string>();
                for (int i = 0; i < _manifest.Sources.Count; i++)
                {
                    CustomWidgetSourceSpec src = _manifest.Sources[i];
                    if (string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
                    {
                        networkKeysBefore[i] = BuildRuntimeNetworkSourceKey(src);
                    }
                }
            }

            bool settingsChanged = false;
            RunWithHeadlessStateGate(() =>
            {
                var previousSettings = new Dictionary<string, string>(_settings, StringComparer.Ordinal);
                HydrateFromPayload(payload, previousSettings, preserveHostMirroredCollections: true);
                settingsChanged = !DictionariesEqual(previousSettings, _settings);
            });

            if (settingsChanged)
            {
                if (networkKeysBefore is { Count: > 0 })
                {
                    foreach ((int idx, string prevKey) in networkKeysBefore)
                    {
                        if (idx >= 0 && idx < _manifest.Sources.Count)
                        {
                            CustomWidgetSourceSpec src = _manifest.Sources[idx];
                            string nextKey = BuildRuntimeNetworkSourceKey(src);
                            if (!string.Equals(prevKey, nextKey, StringComparison.Ordinal))
                            {
                                _capabilityHost.Network.RemoveFetchTimestamp(nextKey);
                            }
                        }
                    }
                }

                _capabilityHost.FileText.ClearFetchTimestamps();
                UpdateSourceSubscriptions();

                if (ManifestUsesFileTextSources() && !_isDisposed)
                {
                    await RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
                }

                if (ManifestUsesNetworkSources() && !_isDisposed)
                {
                    _ = RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false);
                }

                if (weatherCityBefore is not null &&
                    !string.Equals(weatherCityBefore, ResolveEffectiveWeatherCity(), StringComparison.OrdinalIgnoreCase) &&
                    !_isDisposed)
                {
                    _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
                }
            }
            else
            {
                UpdateScriptTickSubscription();
            }

            RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Store-only reload failed for {Config.Id}: {ex.Message}");
        }
    }

    public Action<string?>? OpenSearchPopupOverride { get; set; }

    internal string ResolveHostSearchShortcutText()
    {
        SearchSettingsSlice? search = _settingsService?.Settings.Search;
        if (search is null || !search.SearchHotkeyEnabled)
        {
            return string.Empty;
        }

        GlobalHotkeyActivation activation = GlobalHotkeyService.NormalizeActivation(
            search.SearchHotkeyActivationKind,
            search.SearchHotkeyModifiers,
            search.SearchHotkeyKey);
        return GlobalHotkeyService.FormatActivation(activation, _localizationService);
    }

    public CustomWidgetEvaluationContext CreateEvaluationContext() =>
        new()
        {
            I18n = _manifest.ResolveI18nDictionary(ActiveCultureName),
            Settings = _settings,
            Secrets = _secrets,
            State = _state,
            StateJson = _stateJson,
            Computed = _computedView,
            Collections = _collections,
            SystemMetrics = _capabilityHost.SystemMetricsSnapshot,
            AiMonitor = _capabilityHost.AiMonitorSnapshot,
            Weather = _capabilityHost.WeatherSnapshot,
            DeviceState = _capabilityHost.DeviceStateSnapshot,
            Clipboard = _capabilityHost.ClipboardSnapshot,
            StorageDrives = _capabilityHost.StorageDrivesSnapshot,
            Music = _capabilityHost.MusicSnapshot,
            Todo = _capabilityHost.TodoSnapshot,
            TransformResult = _lastTransformResult,
            CultureName = ActiveCultureName,
            WidgetId = Config.Id,
            SearchShortcutText = ResolveHostSearchShortcutText()
        };

    internal void SetStructuredStateValue(string key, JsonElement value) =>
        _state.SetStructured(key, value);

    internal void SyncFlatStateValue(string key, string? value) =>
        _state[key] = value ?? string.Empty;

    public void SetStateValueWithoutRebuild(
        string key,
        string value,
        bool isTransientInteractiveScrub = false,
        bool notifyLiveBindings = false)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string trimmed = key.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}') && trimmed.Length > 3)
        {
            trimmed = trimmed[2..^1].Trim();
        }
        if (trimmed.StartsWith("settings.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
        {
            string settingKey = NormalizeSettingKey(trimmed);
            if (!string.IsNullOrWhiteSpace(settingKey))
            {
                _ = SetSettingValueAsync(settingKey, value);
            }
            return;
        }

        string normalizedKey = NormalizeStateKey(key);
        string nextVal = value ?? string.Empty;
        bool hadPrevious = _state.TryGetValue(normalizedKey, out string? prevVal);
        bool valueChanged = !hadPrevious || !string.Equals(prevVal, nextVal, StringComparison.Ordinal);
        SyncFlatStateValue(normalizedKey, nextVal);
        if (valueChanged)
        {
            _hasDirtyStateSinceLastScriptInit = true;
        }

        bool effectiveTransientScrub = isTransientInteractiveScrub || IsContinuousScrubBoundStateKey(normalizedKey);
        if (_isPreviewOnly && !_isApplyingSampleData)
        {
            _sampleSeededStateKeys.Remove(normalizedKey);
            _hasUserStateMutation = true;
        }
        else if (!_isPreviewOnly &&
                 !_isApplyingSampleData &&
                 !effectiveTransientScrub &&
                 !IsEphemeralPersistExcludedStateKey(normalizedKey))
        {
            ScheduleDebouncedPersist();
        }

        if (notifyLiveBindings && valueChanged && HasScriptCodeInternal)
        {
            _computedView.ClearCache();
            TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
            if (_scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onStateChange") == true)
            {
                TryRunScriptLifecycleHook(
                    "onStateChange",
                    CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                    persistIfMutated: false);
            }
        }

        RefreshCapsuleProjection(bumpLiveState: false);
        if (effectiveTransientScrub || (notifyLiveBindings && valueChanged))
        {
            NotifyMetricsRefreshed();
        }
    }

    private bool IsContinuousScrubBoundStateKey(string normalizedKey)
    {
        CustomWidgetManifest currentManifest = _manifest;
        HashSet<string>? keys = _continuousScrubStateKeys;
        if (!ReferenceEquals(_continuousScrubCacheManifest, currentManifest) || keys is null)
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CustomWidgetNodeSpec node in currentManifest.EnumerateAllNodes())
            {
                if ((string.Equals(node.Type, "Slider", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Type, "RadialGauge", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(node.Bind))
                {
                    string trimmed = node.Bind.Trim();
                    if (!trimmed.StartsWith("settings.", StringComparison.OrdinalIgnoreCase) &&
                        !trimmed.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
                    {
                        string norm = NormalizeStateKey(trimmed);
                        if (!string.IsNullOrWhiteSpace(norm))
                        {
                            keys.Add(norm);
                        }
                    }
                }
            }

            _continuousScrubStateKeys = keys;
            _continuousScrubCacheManifest = currentManifest;
        }

        return keys.Contains(normalizedKey);
    }

    internal void ScheduleDebouncedPersist(int delayMilliseconds = 400, bool coalesceExistingPending = false)
    {
        CancellationTokenSource cts;
        lock (_debouncedPersistLock)
        {
            if (_isDisposed || _isPreviewOnly)
            {
                return;
            }

            if (coalesceExistingPending &&
                _hasPendingDebouncedPersist &&
                _debouncedPersistCts is { IsCancellationRequested: false })
            {
                return;
            }

            _hasPendingDebouncedPersist = true;
            if (_debouncedPersistCts is { } prevCts)
            {
                _debouncedPersistCts = null;
                try
                {
                    prevCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                prevCts.Dispose();
            }

            cts = new CancellationTokenSource();
            _debouncedPersistCts = cts;
        }

        int effectiveDelayMs = Math.Clamp(delayMilliseconds, 100, 15_000);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(effectiveDelayMs, cts.Token).ConfigureAwait(false);
                lock (_debouncedPersistLock)
                {
                    if (_isDisposed || cts.IsCancellationRequested || !ReferenceEquals(_debouncedPersistCts, cts))
                    {
                        return;
                    }

                    _debouncedPersistCts = null;
                    _hasPendingDebouncedPersist = false;
                }

                if (_dispatcherQueue is not null)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isDisposed)
                        {
                            _ = PersistStoreAsync();
                        }
                    });
                }
                else
                {
                    CustomWidgetStorePayload? payload = null;
                    RunWithHeadlessStateGate(() =>
                    {
                        if (!_isDisposed)
                        {
                            payload = BuildStorePayloadSnapshot();
                        }
                    });
                    if (payload is not null)
                    {
                        await _store.SaveAsync(payload, origin: this).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try { cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        });
    }

    private void CancelPendingDebouncedPersist(out bool hadPendingDirtyState)
    {
        lock (_debouncedPersistLock)
        {
            hadPendingDirtyState = _hasPendingDebouncedPersist;
            _hasPendingDebouncedPersist = false;
            if (_debouncedPersistCts is { } cts)
            {
                _debouncedPersistCts = null;
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                cts.Dispose();
            }
        }
    }

    public async Task SetSettingValueAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_manifest.Settings.Any(d =>
                string.Equals(d.Key, key, StringComparison.Ordinal) &&
                string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase)))
        {
            await SetSecretValueAsync(key, value);
            return;
        }

        Dictionary<int, string>? networkKeysBefore = null;
        if (ManifestUsesNetworkSources())
        {
            networkKeysBefore = new Dictionary<int, string>();
            for (int i = 0; i < _manifest.Sources.Count; i++)
            {
                CustomWidgetSourceSpec src = _manifest.Sources[i];
                if (string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
                {
                    networkKeysBefore[i] = BuildRuntimeNetworkSourceKey(src);
                }
            }
        }

        string? weatherCityBefore = ManifestUsesWeather() ? ResolveEffectiveWeatherCity() : null;

        _settings[key] = value;
        CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(_state, _settings, key);
        TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
        if (_scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onSettingsChange") == true)
        {
            TryRunScriptLifecycleHook("onSettingsChange", CustomWidgetScriptEngine.DefaultActionTimeoutMs, persistIfMutated: false);
        }
        else if (_scriptRuntime.CompiledScript?.ExportedFunctions.Contains("onSettingChange") == true)
        {
            TryRunScriptLifecycleHook("onSettingChange", CustomWidgetScriptEngine.DefaultActionTimeoutMs, persistIfMutated: false);
        }

        if (networkKeysBefore is { Count: > 0 })
        {
            foreach ((int idx, string prevKey) in networkKeysBefore)
            {
                if (idx >= 0 && idx < _manifest.Sources.Count)
                {
                    CustomWidgetSourceSpec src = _manifest.Sources[idx];
                    string nextKey = BuildRuntimeNetworkSourceKey(src);
                    if (!string.Equals(prevKey, nextKey, StringComparison.Ordinal))
                    {
                        _capabilityHost.Network.RemoveFetchTimestamp(nextKey);
                        string resolvedUrl = ResolveNetworkSourceUrl(src);
                        CustomWidgetNetworkFetchOutcome? cached = _networkSourceEngine.TryLoadCachedOutcome(
                            src,
                            resolvedUrl,
                            ResolveNetworkSourceHeaders(src));
                        if (cached is not null)
                        {
                            HydrateSingleNetworkSourceFromDiskCache(src, resolvedUrl, overwriteExistingCollection: true);
                        }
                        else if (!string.IsNullOrWhiteSpace(src.TargetCollection))
                        {
                            _collections[src.TargetCollection] = [];
                        }
                    }
                }
            }
        }

        bool weatherCityChanged = weatherCityBefore is not null &&
            !string.Equals(weatherCityBefore, ResolveEffectiveWeatherCity(), StringComparison.OrdinalIgnoreCase);

        _capabilityHost.FileText.ClearFetchTimestamps();
        SettingChanged?.Invoke(key, value);
        await PersistStoreAsync();
        UpdateSourceSubscriptions();
        if (ManifestUsesFileTextSources() && !_isDisposed)
        {
            await RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
        }
        if (weatherCityChanged && !_isDisposed)
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }

        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public Task SetSecretValueAsync(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || _isDisposed)
        {
            return Task.CompletedTask;
        }

        string normKey = key.Trim();
        string normVal = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normVal))
        {
            _secrets.Remove(normKey);
        }
        else
        {
            _secrets[normKey] = normVal;
        }

        _settings.Remove(normKey);
        if (!_isPreviewOnly)
        {
            _secretStore.SetSecret(Config.Id, normKey, normVal);
        }
        SecretChanged?.Invoke(normKey, normVal);

        _capabilityHost.Network.ClearFetchTimestamps();
        UpdateSourceSubscriptions();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        if (ManifestUsesNetworkSources())
        {
            _ = RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false);
        }

        return Task.CompletedTask;
    }

    public async Task ClearAllDataAsync()
    {
        await _store.ClearAllDataAsync(origin: this);
        _capabilityHost.Network.ClearFetchTimestamps();
        _capabilityHost.FileText.ClearFetchTimestamps();
        _capabilityHost.Command.ClearFetchTimestamps();
        CustomWidgetStorePayload payload = await _store.LoadAsync();
        RunWithHeadlessStateGate(() => HydrateFromPayload(payload));
        UpdateSourceSubscriptions();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        RaiseFeedback(
            _localizationService.T("CustomWidget.Toast.DataCleared"),
            WidgetFeedbackSeverity.Info);
    }

    public async Task ApplyAuthoritativeMutationAsync(CustomWidgetSettingsMutation mutation)
    {
        if (_isDisposed || mutation is null)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ApplyAuthoritativeMutationAsync(mutation);
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }))
            {
                tcs.TrySetResult();
            }

            await tcs.Task;
            return;
        }

        if (mutation.ClearAllData)
        {
            await ClearAllDataAsync();
        }

        if (mutation.Secrets is { Count: > 0 })
        {
            foreach ((string key, string val) in mutation.Secrets)
            {
                await SetSecretValueAsync(key, val);
            }
        }

        if (mutation.Permissions is { Count: > 0 })
        {
            EffectiveTrustStore.InvalidateCache();
            bool wasTrusted = GetTrustProfile().TrustedLocalAuthoring;
            EffectiveTrustStore.ApplyPermissionMutations(
                Config.Id,
                mutation.Permissions,
                defaultTrustedIfNew: wasTrusted);
            _capabilityHost.FileText.ClearFetchTimestamps();
            _capabilityHost.Command.ClearFetchTimestamps();
            StampInitializedWorkspaceTimestamps();
            UpdateSourceSubscriptions();
            if (ManifestUsesNetworkSources() && IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true))
            {
                _capabilityHost.Network.ClearFetchTimestamps();
                _ = RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false);
            }
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (mutation.Settings is { Count: > 0 })
        {
            foreach ((string key, string val) in mutation.Settings)
            {
                await SetSettingValueAsync(key, val);
            }
        }
    }

    internal static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string k, string lv) in left)
        {
            if (!right.TryGetValue(k, out string? rv) || !string.Equals(lv, rv, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public async Task RefreshAiMonitorNowAsync(bool showToast = false)
    {
        if (_isDisposed)
        {
            return;
        }

        await CustomWidgetAiMonitorSource.Shared.RefreshNowAsync(forceCloudRefresh: true);
        if (showToast && !_isDisposed)
        {
            RaiseFeedback(_localizationService.T("CustomWidget.Toast.AiQuotaRefreshed"), WidgetFeedbackSeverity.Info);
        }
    }

    public async Task<(bool Success, string Message)> SaveAndTestAiCredentialAsync(AiProviderCredentialEntry entry)
    {
        var (ok, msg, _) = await CustomWidgetAiMonitorSource.Shared.TestAndSaveCredentialAsync(entry);
        if (ok && entry.Enabled && !_isDisposed)
        {
            _state[CustomWidgetAiMonitorEngine.KeySelectedProvider] = entry.ProviderId;
            await PersistStoreAsync();
        }
        return (ok, msg);
    }

    public async Task RemoveAiCredentialAsync(string providerId)
    {
        await CustomWidgetAiMonitorSource.Shared.RemoveCredentialAsync(providerId);
    }

    internal static bool NodeTreeContainsType(CustomWidgetNodeSpec node, string targetType) =>
        node.EnumerateAllNodes().Any(n => string.Equals(n.Type, targetType, StringComparison.OrdinalIgnoreCase));

    public const int MaxDropTextChars = 8192;
    internal const int MaxDropReadBytes = 24576;

    public bool IsWindowVisible => _isWindowVisible && !_isPowerSuspended;
    internal bool IsPowerSuspendedInternal => _isPowerSuspended;

    public void OnMonitorOcclusionChanged(bool isOccluded) =>
        OnWindowVisibilityChanged(!isOccluded);

    public void OnWindowVisibilityChanged(bool visible)
    {
        bool wasEffectiveVisible = IsWindowVisible;
        _isWindowVisible = visible;
        UpdateSourceSubscriptions();
        if (IsWindowVisible && !wasEffectiveVisible && !_isDisposed)
        {
            RefreshCapsuleProjection(bumpLiveState: false);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetPowerSuspended(bool suspended)
    {
        if (_isPowerSuspended == suspended || _isDisposed)
        {
            return;
        }

        bool wasEffectiveVisible = IsWindowVisible;
        _isPowerSuspended = suspended;
        _audioService.SetPowerSuspended(suspended);
        UpdateSourceSubscriptions();
        if (IsWindowVisible && !wasEffectiveVisible && !_isDisposed)
        {
            RefreshCapsuleProjection(bumpLiveState: false);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshAfterSystemResumeAsync()
    {
        if (_isDisposed || _isPowerSuspended)
        {
            return;
        }

        if (ManifestUsesCountdown())
        {
            _capabilityHost.Countdown.EvaluateDayRolloverAndToast(DateTimeOffset.Now);
        }

        if (ManifestUsesStorageDrives())
        {
            RefreshStorageDrivesNow(triggerRebuild: false);
        }

        TryRunScriptLifecycleHook(
            "onInit",
            CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
            persistIfMutated: !_isPreviewOnly);

        if (ManifestUsesTodo())
        {
            await RefreshTodoNowAsync(triggerRebuild: false);
        }

        if (ManifestUsesWeather())
        {
            await RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }

        if (ManifestUsesNetworkSources())
        {
            await RefreshDueNetworkSourcesAsync();
        }

        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        UpdateSourceSubscriptions();
        if (collapsed && !_isDisposed)
        {
            RefreshCapsuleProjection(bumpLiveState: false, forceNotify: true);
        }
        else if (!collapsed && !_isDisposed && ManifestUsesPomodoro())
        {
            RefreshCapsuleProjection(bumpLiveState: false);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyManifestAndPayload(
        string rawJson,
        CustomWidgetStorePayload payload,
        IReadOnlyDictionary<string, string>? previousSettings = null)
    {
        CustomWidgetParseResult parseResult = CustomWidgetManifestParser.Parse(rawJson);
        _diagnostics = parseResult.Diagnostics;
        bool incompatibleVersion = CustomWidgetManifestParser.IsIncompatibleClientVersion(_diagnostics);

        if (incompatibleVersion)
        {
            _manifest = new CustomWidgetManifest
            {
                Id = parseResult.Manifest?.Id ?? "custom.widget",
                Name = parseResult.Manifest?.Name ?? Config.Name,
                Icon = parseResult.Manifest?.Icon ?? "\uE895",
                View = new CustomWidgetNodeSpec
                {
                    Type = "EmptyState",
                    Icon = "\uE895",
                    Title = _localizationService.T("CustomWidget.ClientUpgrade.Title"),
                    Subtitle = _diagnostics.FirstOrDefault(static d => d.IsError)?.Message ??
                               _localizationService.T("CustomWidget.ClientUpgrade.Subtitle")
                }
            };
        }
        else
        {
            _manifest = parseResult.Manifest ?? new CustomWidgetManifest
            {
                Name = Config.Name,
                View = new CustomWidgetNodeSpec
                {
                    Type = "EmptyState",
                    Icon = "\uE783",
                    Title = _localizationService.T("CustomWidget.Error.ManifestParseTitle"),
                    Subtitle = parseResult.Diagnostics.FirstOrDefault()?.Message ??
                               _localizationService.T("CustomWidget.Error.ManifestParseSubtitle")
                }
            };
        }

        if (!incompatibleVersion && _loadedLocales is { HasAnyLocale: true })
        {
            _manifest = _manifest.WithLocales(_loadedLocales);
        }

        TrySyncAutoLocalizedTitle(notifyIdentityChange: false);
        CompileCurrentScript();
        HydrateFromPayload(payload, previousSettings);
        SeedCalendarStandInIfEmpty();
        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
    }

    private void HydrateFromPayload(
        CustomWidgetStorePayload payload,
        IReadOnlyDictionary<string, string>? previousSettings = null,
        bool preserveHostMirroredCollections = false)
    {
        _computed.Clear();
        _computedView.ClearCache();
        _hasDirtyStateSinceLastScriptInit = false;
        var secretKeys = new HashSet<string>(
            _manifest.Settings
                .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(static d => d.Key),
            StringComparer.Ordinal);

        if (!_isPreviewOnly)
        {
            _secrets.Clear();
            foreach ((string sk, string sv) in _secretStore.GetSecretsForWidget(Config.Id))
            {
                if (!string.IsNullOrWhiteSpace(sv))
                {
                    _secrets[sk] = sv;
                }
            }
        }

        _settings.Clear();
        foreach (CustomWidgetSettingDefinition def in _manifest.Settings)
        {
            if (secretKeys.Contains(def.Key))
            {
                continue;
            }
            _settings[def.Key] = def.DefaultValue;
        }
        foreach ((string k, string v) in payload.Settings)
        {
            if (secretKeys.Contains(k))
            {
                continue;
            }
            _settings[k] = v;
        }

        _stateJson.Clear();
        CustomWidgetEvaluationContext? coldStartEvalCtx = null;
        foreach ((string k, string v) in _manifest.State)
        {
            string effective = v;
            if (!payload.StateJson.ContainsKey(k) &&
                !string.IsNullOrEmpty(v) &&
                v.Contains("${", StringComparison.Ordinal))
            {
                coldStartEvalCtx ??= new CustomWidgetEvaluationContext
                {
                    I18n = _manifest.ResolveI18nDictionary(ActiveCultureName),
                    Settings = _settings,
                    Secrets = _secrets,
                    CultureName = ActiveCultureName,
                    WidgetId = Config.Id
                };
                effective = CustomWidgetExpressionEvaluator.Interpolate(v, coldStartEvalCtx);
            }
            _state[k] = effective;
        }
        foreach ((string k, JsonElement el) in _manifest.StateJson)
        {
            if (el.ValueKind == JsonValueKind.String &&
                !payload.StateJson.ContainsKey(k) &&
                (el.GetString()?.Contains("${", StringComparison.Ordinal) ?? false))
            {
                continue;
            }
            _stateJson[k] = el.Clone();
        }
        foreach ((string k, JsonElement el) in payload.StateJson)
        {
            SetStructuredStateValue(k, el);
        }

        Dictionary<string, List<CustomWidgetRecord>>? preservedHostCollections = null;
        if (preserveHostMirroredCollections)
        {
            HashSet<string> hostMirrored = CustomWidgetHealthAnalyzer.GetHostMirroredCollectionNames(_manifest);
            foreach (string mirroredCol in hostMirrored)
            {
                if (!payload.Collections.ContainsKey(mirroredCol) &&
                    _collections.TryGetValue(mirroredCol, out List<CustomWidgetRecord>? existingRows) &&
                    existingRows.Count > 0)
                {
                    preservedHostCollections ??= new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
                    preservedHostCollections[mirroredCol] = existingRows;
                }
            }
        }

        _collections.Clear();
        bool seededAnyCollection = false;
        CustomWidgetEvaluationContext? seedEvalCtx = null;
        foreach ((string colName, CustomWidgetCollectionSchema colSchema) in _manifest.Collections)
        {
            if (!payload.LoadedFromDisk &&
                !payload.Collections.ContainsKey(colName) &&
                colSchema.SeedItems is { Count: > 0 })
            {
                seedEvalCtx ??= CreateEvaluationContext().WithAllowSecrets(false);
                var seededList = new List<CustomWidgetRecord>(colSchema.SeedItems.Count);
                foreach (CustomWidgetRecord templateRec in colSchema.SeedItems)
                {
                    CustomWidgetRecord copy = templateRec.Clone();
                    foreach (string fk in copy.FieldsJson.Keys.ToArray())
                    {
                        if (copy.FieldsJson[fk].ValueKind == JsonValueKind.String &&
                            copy.FieldsJson[fk].GetString() is { } rawStr &&
                            rawStr.Contains("${", StringComparison.Ordinal))
                        {
                            copy.Fields[fk] = CustomWidgetExpressionEvaluator.Interpolate(rawStr, seedEvalCtx);
                        }
                    }

                    if (copy.Id.Contains("${", StringComparison.Ordinal))
                    {
                        string evalId = CustomWidgetExpressionEvaluator.Interpolate(copy.Id, seedEvalCtx).Trim();
                        copy.Id = string.IsNullOrEmpty(evalId) ? Guid.NewGuid().ToString("N")[..12] : evalId;
                    }

                    seededList.Add(copy);
                }

                _collections[colName] = seededList;
                seededAnyCollection = true;
            }
            else
            {
                _collections[colName] = [];
            }
        }
        foreach ((string colName, List<CustomWidgetRecord> records) in payload.Collections)
        {
            _collections[colName] = records.Select(static r => r.Clone()).ToList();
        }
        if (preservedHostCollections is not null)
        {
            foreach ((string colName, List<CustomWidgetRecord> existingRows) in preservedHostCollections)
            {
                _collections[colName] = existingRows;
            }
        }
        BumpCollectionsVersion();
        if (seededAnyCollection && !_isPreviewOnly)
        {
            ScheduleDebouncedPersist();
        }

        if (ManifestUsesPomodoro())
        {
            bool syncedIdleDuration = false;
            if (previousSettings is not null)
            {
                _state.TryGetValue(CustomWidgetPomodoroEngine.KeyRemainingSec, out string? beforeRem);
                _state.TryGetValue(CustomWidgetPomodoroEngine.KeyTotalSec, out string? beforeTot);
                foreach ((string k, string v) in _settings)
                {
                    if (!previousSettings.TryGetValue(k, out string? prevVal) ||
                        !string.Equals(prevVal, v, StringComparison.Ordinal))
                    {
                        CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(_state, _settings, k);
                    }
                }
                _state.TryGetValue(CustomWidgetPomodoroEngine.KeyRemainingSec, out string? afterRem);
                _state.TryGetValue(CustomWidgetPomodoroEngine.KeyTotalSec, out string? afterTot);
                syncedIdleDuration = !string.Equals(beforeRem, afterRem, StringComparison.Ordinal) ||
                                     !string.Equals(beforeTot, afterTot, StringComparison.Ordinal);
            }

            bool rolledOver = CustomWidgetPomodoroEngine.EnsureTodayRollover(_state, _settings, DateTimeOffset.Now);
            bool settledExpired = CustomWidgetPomodoroEngine.TryPumpForMode(
                _state,
                _settings,
                _collections,
                DateTimeOffset.Now,
                CustomWidgetPomodoroEngine.IsSedentaryMode(_settings) ? ProbeKeyboardMouseIdleSeconds() : null,
                out CustomWidgetPomodoroCompletionNotice? notice,
                ActiveCultureName);
            if (settledExpired && notice is not null)
            {
                DispatchPomodoroCompletionNotification(notice);
            }
            if (syncedIdleDuration || rolledOver || settledExpired)
            {
                _ = PersistStoreAsync();
            }
        }

        if (ManifestUsesCountdown())
        {
            _capabilityHost.Countdown.SyncObservedDay(DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime));
            if (string.Equals(_manifest.Id, "deskbox.countdown", StringComparison.OrdinalIgnoreCase) &&
                CustomWidgetCountdownEngine.EnsureSeeded(
                    _state,
                    _collections,
                    DateTimeOffset.Now,
                    ActiveCultureName))
            {
                _ = PersistStoreAsync();
            }
        }

        if (ManifestUsesStorageDrives())
        {
            RefreshStorageDrivesNow(triggerRebuild: false);
        }

        if (_capabilityHost.Calendar.ManifestUsesCalendar() &&
            (!_state.TryGetValue("selectedDate", out string? curSelected) || string.IsNullOrWhiteSpace(curSelected)))
        {
            _state["selectedDate"] = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        TryRunScriptLifecycleHook(
            "onInit",
            CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
            persistIfMutated: !_isPreviewOnly);

        if (ManifestUsesNetworkSources())
        {
            HydrateNetworkSourcesFromDiskCache();
        }
    }

    public async Task ApplyExternalDataPushAsync(
        IReadOnlyDictionary<string, string>? stateUpdates,
        string? collectionName = null,
        string? collectionMode = null,
        IReadOnlyList<Dictionary<string, string>>? records = null,
        IReadOnlyDictionary<string, JsonElement>? stateJsonUpdates = null,
        IReadOnlyList<Dictionary<string, JsonElement>>? jsonRecords = null,
        string? idField = null,
        int? maxItemsOverride = null)
    {
        if (_isDisposed)
        {
            return;
        }

        bool changed = CustomWidgetStoreMutator.ApplyExternalDataPush(
            setStructuredState: SetStructuredStateValue,
            setFlatState: SyncFlatStateValue,
            collections: _collections,
            manifest: _manifest,
            stateUpdates: stateUpdates,
            collectionName: collectionName,
            collectionMode: collectionMode,
            records: records,
            stateJsonUpdates: stateJsonUpdates,
            jsonRecords: jsonRecords,
            idField: idField,
            maxItemsOverride: maxItemsOverride);

        if (!changed)
        {
            return;
        }

        TryRunScriptLifecycleHook(
            "onInit",
            CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
            persistIfMutated: false);
        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    internal async Task PersistStoreAsync()
    {
        CancelPendingDebouncedPersist(out _);
        if (_isPreviewOnly)
        {
            if (!_isApplyingSampleData)
            {
                _hasUserStateMutation = true;
                foreach ((string colName, List<CustomWidgetRecord> list) in _collections)
                {
                    if ((list.Count == 0 && _sampleSeededCollections.Contains(colName)) ||
                        list.Any(static r => !r.Id.StartsWith("sample-", StringComparison.Ordinal) ||
                                             r.UpdatedAtUtcTicks > r.CreatedAtUtcTicks))
                    {
                        _userMutatedCollections.Add(colName);
                    }
                }
            }
            return;
        }

        CustomWidgetStorePayload payload = BuildStorePayloadSnapshot();
        await _store.SaveAsync(payload, origin: this).ConfigureAwait(false);
    }

    private void StampInitializedWorkspaceTimestamps()
    {
        long maxTicks = 0;
        static long ReadTicks(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        maxTicks = Math.Max(maxTicks, ReadTicks(_store.ManifestPath));
        maxTicks = Math.Max(maxTicks, ReadTicks(_store.LogicJsPath));
        if (Directory.Exists(_store.LocalesDirectoryPath))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(_store.LocalesDirectoryPath, "*.json"))
                {
                    maxTicks = Math.Max(maxTicks, ReadTicks(file));
                }
            }
            catch
            {
            }
        }

        if (maxTicks <= 0)
        {
            maxTicks = DateTime.UtcNow.Ticks;
        }

        Volatile.Write(ref _initializedWorkspaceWriteTicks, maxTicks);
    }

    private CustomWidgetStorePayload BuildStorePayloadSnapshot()
    {
        var safeSettings = new Dictionary<string, string>(_settings, StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in _manifest.Settings)
        {
            if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                safeSettings.Remove(def.Key);
            }
        }

        HashSet<string> hostMirrored = CustomWidgetHealthAnalyzer.GetHostMirroredCollectionNames(_manifest);
        var persistedState = new Dictionary<string, string>(StringComparer.Ordinal);
        var persistedStateJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string sk, string sv) in _state)
        {
            if (IsEphemeralPersistExcludedStateKey(sk))
            {
                continue;
            }
            persistedState[sk] = sv;
            if (_stateJson.TryGetValue(sk, out JsonElement jsonEl) &&
                jsonEl.ValueKind != JsonValueKind.Undefined)
            {
                persistedStateJson[sk] = jsonEl.Clone();
            }
        }

        return new CustomWidgetStorePayload
        {
            Settings = safeSettings,
            State = persistedState,
            StateJson = persistedStateJson,
            Collections = _collections
                .Where(kvp => !hostMirrored.Contains(kvp.Key))
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<CustomWidgetRecord>(kvp.Value),
                    StringComparer.Ordinal)
        };
    }

    internal static string NormalizeRuntimeGlyphOrFallback(
        string? rawGlyph,
        string fallback,
        bool? isWindows11OrLater = null)
    {
        bool win11 = isWindows11OrLater ?? WindowsCompatibilityService.IsWindows11OrLater;
        string Normalize(string value) =>
            CustomWidgetTemplates.IsSegoeFluentGlyph(value)
                ? CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs(value, win11)
                : value;

        if (string.IsNullOrWhiteSpace(rawGlyph))
        {
            return Normalize(fallback);
        }

        string trimmed = rawGlyph.Trim();
        if (CustomWidgetTemplates.IsEmojiIcon(trimmed) || trimmed.Length == 1)
        {
            return Normalize(trimmed);
        }

        if (CustomWidgetSemanticIcons.TryResolve(trimmed, out string semanticGlyph))
        {
            return Normalize(semanticGlyph);
        }

        if ((trimmed.StartsWith(@"\u", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) &&
            trimmed.Length == 6 &&
            ushort.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort uCode))
        {
            return Normalize(((char)uCode).ToString());
        }

        if (trimmed.Length == 4 &&
            ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort hexCode) &&
            hexCode >= 0xE000)
        {
            return Normalize(((char)hexCode).ToString());
        }

        return Normalize(ResolveNamedSegoeGlyph(trimmed) ?? trimmed);
    }

    private static string? ResolveNamedSegoeGlyph(string name) => name.ToLowerInvariant() switch
    {
        "add" or "plus" => "\uE710",
        "refresh" or "sync" => "\uE72C",
        "play" => "\uE768",
        "pause" => "\uE769",
        "stop" => "\uE71A",
        "next" or "skip" => "\uE893",
        "previous" or "prev" => "\uE892",
        "edit" => "\uE70F",
        "delete" or "trash" => "\uE74D",
        "copy" => "\uE8C8",
        "save" => "\uE74E",
        "search" => "\uE721",
        "settings" or "gear" => "\uE713",
        "checkmark" or "check" or "accept" or "done" => "\uE73E",
        "cancel" or "close" => "\uE711",
        "clear" => "\uE894",
        "volume" or "audio" => "\uE767",
        "mute" => "\uE74F",
        "pin" => "\uE718",
        "unpin" => "\uE77A",
        "calendar" => "\uE787",
        "clock" or "timer" => "\uE916",
        "send" => "\uE724",
        "open" or "external" => "\uE8A7",
        "sparkle" or "ai" => "\uE945",
        "star" or "favorite" => "\uE734",
        "heart" => "\uEB51",
        "water" or "drop" => "\uEB42",
        "lightning" or "bolt" => "\uE945",
        "camera" => "\uE722",
        "folder" => "\uE8B7",
        "home" => "\uE80F",
        "info" => "\uE946",
        "warning" => "\uE7BA",
        "chevronright" => "\uE76C",
        "chevronleft" => "\uE76B",
        "up" or "chevronup" => "\uE70E",
        "down" or "chevrondown" => "\uE70D",
        _ => null
    };

    private (
        string Title,
        string Summary,
        string Glyph,
        string Badge,
        bool BadgeIsWarning,
        double? Progress,
        bool EnableBounce,
        bool HasPrimaryAction,
        string PrimaryActionGlyph,
        string PrimaryActionTooltip) ComputeCapsuleProjectionValues()
    {
        CustomWidgetEvaluationContext ctx = CreateEvaluationContext();
        CustomWidgetCapsuleSpec? cap = _manifest.Capsule;

        if (cap is null)
        {
            return (
                Config.Name,
                _manifest.ResolveLocalizedDescription(ActiveCultureName),
                NormalizeRuntimeGlyphOrFallback(_manifest.Icon, "\uE71D"),
                string.Empty,
                false,
                null,
                false,
                false,
                "\uE710",
                string.Empty);
        }

        string title = CustomWidgetExpressionEvaluator.Interpolate(cap.Title, ctx);
        string resolvedTitle = string.IsNullOrWhiteSpace(title) ? Config.Name : title;
        string summary = CustomWidgetExpressionEvaluator.Interpolate(cap.Summary, ctx);
        string resolvedGlyph = CustomWidgetExpressionEvaluator.Interpolate(cap.Glyph, ctx);
        string glyph = NormalizeRuntimeGlyphOrFallback(resolvedGlyph, _manifest.Icon);
        string badgeRaw = CustomWidgetExpressionEvaluator.Interpolate(cap.Badge, ctx);
        string badge = badgeRaw.Trim() is "0" or "0%" ? string.Empty : badgeRaw;
        bool badgeWarn = CustomWidgetExpressionEvaluator.EvaluateCondition(cap.BadgeIsWarning, ctx);
        double? rawPct = CustomWidgetExpressionEvaluator.EvaluateDouble(cap.Progress, ctx);
        double? progress = rawPct.HasValue
            ? Math.Clamp(rawPct.Value / 100.0, 0.0, 1.0)
            : null;

        bool hasPrimaryAction = cap.PrimaryAction?.OnClick is { Count: > 0 };
        string primaryActionGlyph = hasPrimaryAction
            ? NormalizeRuntimeGlyphOrFallback(
                CustomWidgetExpressionEvaluator.Interpolate(cap.PrimaryAction!.Icon, ctx),
                "\uE710")
            : "\uE710";
        string primaryActionTooltip = hasPrimaryAction
            ? CustomWidgetExpressionEvaluator.Interpolate(cap.PrimaryAction!.Tooltip, ctx)
            : string.Empty;
        if (hasPrimaryAction && string.IsNullOrWhiteSpace(primaryActionTooltip))
        {
            primaryActionTooltip = resolvedTitle;
        }

        return (
            resolvedTitle,
            summary,
            glyph,
            badge,
            badgeWarn,
            progress,
            cap.EnableBounceOnUpdate,
            hasPrimaryAction,
            primaryActionGlyph,
            primaryActionTooltip);
    }

    internal void EnsureCapsuleProjectionFresh()
    {
        if (!_capsuleProjectionDirty)
        {
            return;
        }

        _capsuleProjectionDirty = false;
        (
            _capsuleTitle,
            _capsuleSummary,
            _capsuleGlyph,
            _capsuleBadge,
            _capsuleBadgeIsWarning,
            _capsuleProgress,
            _capsuleEnableBounce,
            _capsuleHasPrimaryAction,
            _capsulePrimaryActionGlyph,
            _capsulePrimaryActionTooltip
        ) = ComputeCapsuleProjectionValues();
    }

    internal void RefreshCapsuleProjection(bool bumpLiveState, bool forceNotify = false)
    {
        if (bumpLiveState)
        {
            _liveStateVersion++;
        }

        if (!_isCompactCollapsed && !forceNotify)
        {
            _capsuleProjectionDirty = true;
            return;
        }

        _capsuleProjectionDirty = false;
        var (
            title,
            summary,
            glyph,
            badge,
            badgeWarn,
            progress,
            enableBounce,
            hasPrimaryAction,
            primaryActionGlyph,
            primaryActionTooltip
        ) = ComputeCapsuleProjectionValues();

        bool titleChanged = !string.Equals(_capsuleTitle, title, StringComparison.Ordinal);
        bool summaryChanged = !string.Equals(_capsuleSummary, summary, StringComparison.Ordinal);
        bool glyphChanged = !string.Equals(_capsuleGlyph, glyph, StringComparison.Ordinal);
        bool badgeChanged = !string.Equals(_capsuleBadge, badge, StringComparison.Ordinal);
        bool badgeWarnChanged = _capsuleBadgeIsWarning != badgeWarn;
        bool progressChanged = _capsuleProgress != progress;
        bool bounceChanged = _capsuleEnableBounce != enableBounce;
        bool hasPrimaryActionChanged = _capsuleHasPrimaryAction != hasPrimaryAction;
        bool primaryActionGlyphChanged = !string.Equals(_capsulePrimaryActionGlyph, primaryActionGlyph, StringComparison.Ordinal);
        bool primaryActionTooltipChanged = !string.Equals(_capsulePrimaryActionTooltip, primaryActionTooltip, StringComparison.Ordinal);

        _capsuleTitle = title;
        _capsuleSummary = summary;
        _capsuleGlyph = glyph;
        _capsuleBadge = badge;
        _capsuleBadgeIsWarning = badgeWarn;
        _capsuleProgress = progress;
        _capsuleEnableBounce = enableBounce;
        _capsuleHasPrimaryAction = hasPrimaryAction;
        _capsulePrimaryActionGlyph = primaryActionGlyph;
        _capsulePrimaryActionTooltip = primaryActionTooltip;

        if (titleChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleTitle));
        }
        if (summaryChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleSummary));
        }
        if (glyphChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleGlyph));
            OnPropertyChanged(nameof(IsUsingDynamicCapsuleGlyph));
        }
        if (badgeChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleBadge));
        }
        if (badgeWarnChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleBadgeIsWarning));
        }
        if (progressChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleProgress));
        }
        if (bounceChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleEnableBounce));
        }
        if (hasPrimaryActionChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsuleHasPrimaryAction));
        }
        if (primaryActionGlyphChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsulePrimaryActionGlyph));
        }
        if (primaryActionTooltipChanged || forceNotify)
        {
            OnPropertyChanged(nameof(CapsulePrimaryActionTooltip));
        }

        if (_notifiedLiveStateVersion != _liveStateVersion || forceNotify)
        {
            _notifiedLiveStateVersion = _liveStateVersion;
            OnPropertyChanged(nameof(LiveStateKey));
        }
    }

    private void SyncManifestEmojiToTitleIcon(string? previousManifestIcon, bool isReload)
    {
        Config.Metadata ??= [];
        if (_manifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) &&
            CustomWidgetTemplates.MatchPresetOrNull(_manifest.Id) is { } matchedPreset &&
            !Config.Metadata.ContainsKey(TemplateMetadataKey))
        {
            Config.Metadata[TemplateMetadataKey] = matchedPreset.Id;
        }

        bool hasDedicatedVectorIcon = WidgetTitleIconKindNames.FromCustomTemplateId(_manifest.Id) is not null;
        string? currentEmojiOverride = WidgetTitleIconCustomization.GetEmojiOverride(Config);
        string? effectiveEmoji = hasDedicatedVectorIcon
            ? null
            : CustomWidgetTemplates.IsEmojiIcon(_manifest.Icon)
                ? _manifest.Icon.Trim()
                : _manifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) &&
                  CustomWidgetTemplates.MatchPresetOrNull(_manifest.Id) is { } preset &&
                  CustomWidgetTemplates.IsEmojiIcon(preset.IconGlyph)
                    ? preset.IconGlyph
                    : null;
        if (effectiveEmoji is null)
        {
            OnPropertyChanged(nameof(TitleGlyph));
            OnPropertyChanged(nameof(TitleIconKind));
            return;
        }

        ManifestEmojiIcon = effectiveEmoji;

        bool manifestIconChanged = isReload &&
            !string.Equals(previousManifestIcon, _manifest.Icon, StringComparison.Ordinal);
        bool hasCustomOverride = WidgetTitleIconCustomization.HasCustomIcon(Config);

        if (!hasCustomOverride ||
            (manifestIconChanged && !string.Equals(currentEmojiOverride, effectiveEmoji, StringComparison.Ordinal)))
        {
            WidgetTitleIconCustomization.SetEmojiOverride(Config, effectiveEmoji);
            OnPropertyChanged(nameof(ManifestEmojiIcon));
        }

        OnPropertyChanged(nameof(TitleGlyph));
        OnPropertyChanged(nameof(TitleIconKind));
    }

    internal static bool CapsuleReferencesToken(CustomWidgetCapsuleSpec? capsule, string tokenPrefix)
    {
        if (capsule is null)
        {
            return false;
        }

        bool HasToken(string? text) =>
            !string.IsNullOrEmpty(text) &&
            text.Contains(tokenPrefix, StringComparison.OrdinalIgnoreCase);

        return HasToken(capsule.Title) ||
               HasToken(capsule.Summary) ||
               HasToken(capsule.Glyph) ||
               HasToken(capsule.Badge) ||
               HasToken(capsule.BadgeIsWarning) ||
               HasToken(capsule.Progress) ||
               HasToken(capsule.PrimaryAction?.Icon) ||
               HasToken(capsule.PrimaryAction?.Tooltip);
    }

    internal static bool CapsuleReferencesSystemMetrics(CustomWidgetCapsuleSpec? capsule) =>
        CapsuleReferencesToken(capsule, "sys.") ||
        CapsuleReferencesToken(capsule, "time.");

    internal static bool NodeTreeReferencesSubstring(CustomWidgetNodeSpec? node, string substring)
    {
        if (node is null)
        {
            return false;
        }

        bool Has(string? s) => !string.IsNullOrEmpty(s) && s.Contains(substring, StringComparison.OrdinalIgnoreCase);

        foreach (CustomWidgetNodeSpec cur in node.EnumerateAllNodes())
        {
            if (Has(cur.Text) || Has(cur.Title) || Has(cur.Subtitle) || Has(cur.Value) ||
                Has(cur.Progress) || Has(cur.Sparkline) || Has(cur.Items) || Has(cur.Collection) ||
                Has(cur.When) || Has(cur.CenterTitle) || Has(cur.CenterValue))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool NodeTreeReferencesGpu(CustomWidgetNodeSpec node) =>
        NodeTreeReferencesSubstring(node, "sys.gpu.");

    internal static bool NodeTreeReferencesProcesses(CustomWidgetNodeSpec node) =>
        NodeTreeReferencesSubstring(node, "sys.topProcesses") ||
        NodeTreeReferencesSubstring(node, "sys.processes");


    internal LocalizationService LocalizationServiceInternal => _localizationService;
    internal DispatcherQueue? DispatcherQueueInternal => _dispatcherQueue;
    internal Func<NativeAppNotificationService?>? NotificationServiceProviderInternal => _notificationServiceProvider;
    internal CustomWidgetNetworkSourceEngine NetworkSourceEngineInternal => _networkSourceEngine;
    internal CustomWidgetWeatherSource WeatherSourceInternal => _weatherSource;
    internal CustomWidgetSystemControlService SystemControlServiceInternal => _systemControlService;
    internal SettingsService? SettingsServiceInternal => _settingsService;
    internal Dictionary<string, string> SettingsInternal => _settings;
    internal Dictionary<string, string> SecretsInternal => _secrets;
    internal CustomWidgetStructuredStateView StateInternal => _state;
    internal Dictionary<string, List<CustomWidgetRecord>> CollectionsInternal => _collections;
    internal HashSet<string> SampleSeededCollectionsInternal => _sampleSeededCollections;
    internal ConcurrentDictionary<string, long> DeniedExternalTargetCooldownUtcTicksInternal => _deniedExternalTargetCooldownUtcTicks;
    internal CustomWidgetCompiledScript? CompiledScriptInternal => _scriptRuntime.CompiledScript;
    internal IReadOnlyList<CustomWidgetValidationDiagnostic> DiagnosticsInternal => _diagnostics;
    internal CustomWidgetManifest ManifestInternal { get => _manifest; set => _manifest = value; }
    internal bool IsWindowVisibleInternal => _isWindowVisible && !_isPowerSuspended;
    internal bool IsCompactCollapsedInternal => _isCompactCollapsed;

    internal bool CapsuleUsesDynamicBindings()
    {
        CustomWidgetCapsuleSpec? cap = _manifest.Capsule;
        if (cap is null)
        {
            return false;
        }

        static bool HasToken(string? s) => !string.IsNullOrWhiteSpace(s) && s.IndexOf('$') >= 0;
        return HasToken(cap.Title) ||
               HasToken(cap.Summary) ||
               HasToken(cap.Glyph) ||
               HasToken(cap.Badge) ||
               HasToken(cap.BadgeIsWarning) ||
               HasToken(cap.Progress) ||
               HasToken(cap.PrimaryAction?.Icon) ||
               HasToken(cap.PrimaryAction?.Tooltip);
    }
    internal bool IsPreviewOnlyInternal => _isPreviewOnly;
    internal bool IsDisposedInternal => _isDisposed;
    internal CustomWidgetCapabilityHost CapabilityHost => _capabilityHost;

    internal DeskBoxAiService AiServiceInternal => _aiService;
    internal Func<string?> PreferredAiProviderResolverInternal => _preferredAiProviderResolver;
    internal CustomWidgetRemoteCatalogService? RemoteCatalogServiceInternal => _remoteCatalogService;
    internal CustomWidgetJobBus JobBusInternal => _jobBus;
    internal HashSet<string> SessionAuthorizedPathsInternal => _sessionAuthorizedPaths;
    internal HashSet<string> UserMutatedCollectionsInternal => _userMutatedCollections;
    internal HashSet<string> SampleSeededStateKeysInternal => _sampleSeededStateKeys;
    internal Dictionary<string, JsonElement> StateJsonInternal => _stateJson;
    internal Dictionary<string, JsonElement> ComputedInternal => _computed;
    internal CustomWidgetLocaleCatalog? LoadedLocalesInternal
    {
        get => _loadedLocales;
        set => _loadedLocales = value;
    }
    internal CustomWidgetTransformResult? LastTransformResultInternal
    {
        get => _lastTransformResult;
        set => _lastTransformResult = value;
    }
    internal bool IsInitializedInternal
    {
        get => _isInitialized;
        set => _isInitialized = value;
    }
    internal bool HasUserStateMutationInternal
    {
        get => _hasUserStateMutation;
        set => _hasUserStateMutation = value;
    }
    internal bool IsApplyingSampleDataInternal
    {
        get => _isApplyingSampleData;
        set => _isApplyingSampleData = value;
    }
    internal bool HasPreviewSampleDataInternal
    {
        get => HasPreviewSampleData;
        set => HasPreviewSampleData = value;
    }
    internal string? RawLogicJsInternal
    {
        get => _scriptRuntime.RawLogicJs;
        set => _scriptRuntime.RawLogicJs = value;
    }

    internal CustomWidgetDropZoneCoordinator DropZoneCoordinator => _dropZoneCoordinator;

    internal void NotifyPropertyChanged(string propertyName) => OnPropertyChanged(propertyName);
    internal void NotifyScriptErrorChanged(CustomWidgetScriptRuntimeError? error) => ScriptErrorChanged?.Invoke(error);
    internal void NotifyManifestIdentityChanged(string title, string icon) => ManifestIdentityChanged?.Invoke(title, icon);
    internal bool TryCancelPendingAiAction() => _aiAndLocalExecutor.TryCancelPendingAiAction();

    // Script runtime forwarding
    internal void CompileCurrentScript() => _scriptRuntime.CompileCurrentScript();
    internal void TryRunScriptLifecycleHook(string hookName, uint timeoutMs, bool persistIfMutated) =>
        _scriptRuntime.TryRunScriptLifecycleHook(hookName, timeoutMs, persistIfMutated);
    internal bool InvokeScriptCore(
        string functionName,
        string? arg,
        string? eventValue,
        CustomWidgetRecord? currentItem,
        CustomWidgetDropFileContext? dropFile,
        string? sourceId,
        string? rawSourcePayload,
        uint timeoutMs,
        bool requireExport,
        bool allowCopy,
        out bool mutatedPersisted,
        out bool mutatedUi,
        out bool mutatedStructuralUi) =>
        _scriptRuntime.InvokeScriptCore(
            functionName,
            arg,
            eventValue,
            currentItem,
            dropFile,
            sourceId,
            rawSourcePayload,
            timeoutMs,
            requireExport,
            allowCopy,
            out mutatedPersisted,
            out mutatedUi,
            out mutatedStructuralUi);
    internal bool InvokeScriptCore(
        string functionName,
        string? arg,
        string? eventValue,
        CustomWidgetRecord? currentItem,
        CustomWidgetDropFileContext? dropFile,
        string? sourceId,
        string? rawSourcePayload,
        uint timeoutMs,
        bool requireExport,
        bool allowCopy,
        out bool mutatedPersisted,
        out bool mutatedUi,
        out bool mutatedStructuralUi,
        out IReadOnlyList<CustomWidgetActionSpec>? dispatchedActions,
        IReadOnlyDictionary<string, string>? args = null,
        string? eventId = null,
        double? eventX = null,
        double? eventY = null,
        int? fromIndex = null,
        int? toIndex = null,
        CustomWidgetScriptCallKind callKind = CustomWidgetScriptCallKind.UiEvent,
        IReadOnlyList<CustomWidgetDropFileContext>? dropFiles = null,
        string? rawJsonPayload = null,
        double? eventNormX = null,
        double? eventNormY = null,
        bool? eventIsPressed = null,
        string? eventKind = null,
        string? eventText = null,
        IReadOnlyList<CustomWidgetDropItemRecord>? eventItems = null,
        IReadOnlyDictionary<string, JsonElement>? argsJson = null) =>
        _scriptRuntime.InvokeScriptCore(
            functionName,
            arg,
            eventValue,
            currentItem,
            dropFile,
            sourceId,
            rawSourcePayload,
            timeoutMs,
            requireExport,
            allowCopy,
            out mutatedPersisted,
            out mutatedUi,
            out mutatedStructuralUi,
            out dispatchedActions,
            args,
            eventId,
            eventX,
            eventY,
            fromIndex,
            toIndex,
            callKind,
            dropFiles,
            rawJsonPayload,
            eventNormX,
            eventNormY,
            eventIsPressed,
            eventKind,
            eventText,
            eventItems,
            argsJson);
    internal void InvalidateComputedRecordCacheKeys(HashSet<string>? changedRootKeys) =>
        _computedView.InvalidateKeys(changedRootKeys);

    internal int ResolveEffectiveScriptTickIntervalMs() => _scriptRuntime.ResolveEffectiveScriptTickIntervalMs();
    internal void UpdateScriptTickSubscription() => _scriptRuntime.UpdateScriptTickSubscription();
    internal IReadOnlyDictionary<string, string> BuildEffectiveScriptSettings() =>
        _scriptRuntime.BuildEffectiveScriptSettings();

    // DropZone & authorization forwarding
    internal static string TryUnwrapFileUriToLocalPath(string rawCandidate) =>
        CustomWidgetDropZoneCoordinator.TryUnwrapFileUriToLocalPath(rawCandidate);
    public string ResolveAuthorizedImagePath(string? rawPath) =>
        _dropZoneCoordinator.ResolveAuthorizedImagePath(rawPath);
    internal bool HasAnyDropZoneNode() => _dropZoneCoordinator.HasAnyDropZoneNode();
    internal string? FindAnyDropZoneAccept() => _dropZoneCoordinator.FindAnyDropZoneAccept();
    internal string? FindAnyDropZoneStorageMode() => _dropZoneCoordinator.FindAnyDropZoneStorageMode();
    public Task<bool> ImportDroppedFilesAsync(
        IReadOnlyList<string> filePaths,
        bool forceCopyToPortableStore = false) =>
        _dropZoneCoordinator.ImportDroppedFilesAsync(filePaths, forceCopyToPortableStore);
    public Task<bool> HandleDropZoneFilesAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<CustomWidgetActionSpec> nodeDropActions,
        bool forceCopyToPortableStore = false,
        string? nodeDropMode = null) =>
        _dropZoneCoordinator.HandleDropZoneFilesAsync(filePaths, nodeDropActions, forceCopyToPortableStore, nodeDropMode);
    public Task<bool> HandleStructuredDropAsync(
        CustomWidgetStructuredDropPayload payload,
        IReadOnlyList<CustomWidgetActionSpec>? nodeDropActions = null,
        string? nodeDropMode = null,
        string? nodeAcceptSpec = null) =>
        _dropZoneCoordinator.HandleStructuredDropAsync(payload, nodeDropActions, nodeDropMode, nodeAcceptSpec);
    internal static bool DoesManifestAcceptFileDrops(CustomWidgetManifest? manifest) =>
        CustomWidgetDropZoneCoordinator.DoesManifestAcceptFileDrops(manifest);
    internal static bool DoesManifestJsonAcceptFileDrops(string? rawJson) =>
        CustomWidgetDropZoneCoordinator.DoesManifestJsonAcceptFileDrops(rawJson);
    public static bool IsSupportedDropTextExtension(string? extension) =>
        CustomWidgetDropZoneCoordinator.IsSupportedDropTextExtension(extension);
    public static Task<string> TryReadSafeDropTextAsync(string fullPath, string? extension) =>
        CustomWidgetDropZoneCoordinator.TryReadSafeDropTextAsync(fullPath, extension);

    // Action & collection forwarding
    public Task ExecuteActionsAsync(
        IReadOnlyList<CustomWidgetActionSpec> actions,
        CustomWidgetEvaluationContext? context = null,
        bool isNested = false,
        int depth = 0) =>
        _actionDispatcher.ExecuteActionsAsync(actions, context, isNested, depth);
    public Task<bool> ReorderCollectionItemsAsync(
        string collectionName,
        int fromIndex,
        int toIndex,
        IReadOnlyList<CustomWidgetActionSpec>? onReorderActions = null) =>
        _actionDispatcher.ReorderCollectionItemsAsync(collectionName, fromIndex, toIndex, onReorderActions);
    public Task<bool> CommitInlineItemEditAsync(
        string collectionName,
        string itemId,
        string fieldName,
        string newValue,
        IReadOnlyList<CustomWidgetActionSpec>? onInlineCommitActions = null) =>
        _actionDispatcher.CommitInlineItemEditAsync(collectionName, itemId, fieldName, newValue, onInlineCommitActions);

    internal CustomWidgetRuntimeTraceBuffer RuntimeTrace => _actionDispatcher.RuntimeTrace;

    // AI & local action forwarding
    internal static bool ActionsContainAiRun(IReadOnlyList<CustomWidgetActionSpec>? actions) =>
        CustomWidgetAiAndLocalActionExecutor.ActionsContainAiRun(actions);
    internal Task<(bool Succeeded, bool MutatedState, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteAiRunActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteAiRunActionAsync(action, evalContext);
    public void OpenFileWithDefaultApp(string? path) => _aiAndLocalExecutor.OpenFileWithDefaultApp(path);
    public Task OpenFileWithDefaultAppAsync(string? path) => _aiAndLocalExecutor.OpenFileWithDefaultAppAsync(path);
    public void RevealFileInExplorer(string? path) => _aiAndLocalExecutor.RevealFileInExplorer(path);
    public Task RevealFileInExplorerAsync(string? path) => _aiAndLocalExecutor.RevealFileInExplorerAsync(path);
    internal Task<bool> EnsureCommunityExternalTargetApprovedAsync(
        string actionKind,
        string trustToken,
        string targetDisplay,
        bool requireConsentForAllOrigins = false,
        bool respectDenialCooldown = false) =>
        _aiAndLocalExecutor.EnsureCommunityExternalTargetApprovedAsync(
            actionKind,
            trustToken,
            targetDisplay,
            requireConsentForAllOrigins,
            respectDenialCooldown);
    internal Task<byte[]?> FetchValidatedRemoteImageBytesAsync(Uri httpsUri, CancellationToken ct = default) =>
        _aiAndLocalExecutor.FetchValidatedRemoteImageBytesAsync(httpsUri, ct);
    internal Task ExecuteAudioActionAsync(
        string actionType,
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteAudioActionAsync(actionType, action, evalContext);
    internal bool ExecuteCountdownUpsertAction(CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteCountdownUpsertAction(action, evalContext);
    internal Task<bool> EnsureWidgetPermissionGrantedAsync(
        CustomWidgetPermissionKind kind,
        string actionKind,
        string targetDisplay,
        bool isReadOnlyPublicGet = false,
        bool showBlockedToast = true) =>
        _aiAndLocalExecutor.EnsureWidgetPermissionGrantedAsync(kind, actionKind, targetDisplay, isReadOnlyPublicGet, showBlockedToast);
    internal Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteFileWriteTextActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteFileWriteTextActionAsync(action, evalContext);
    internal Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteCommandRunActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteCommandRunActionAsync(action, evalContext);
    internal Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteFileListDirActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteFileListDirActionAsync(action, evalContext);
    internal Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteFileManageActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteFileManageActionAsync(action, evalContext);
    internal Task<(bool Succeeded, bool Mutated, CustomWidgetDropFileContext? CapturedFile, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteClipboardCaptureImageActionAsync(
        CustomWidgetActionSpec action, CustomWidgetEvaluationContext evalContext) =>
        _aiAndLocalExecutor.ExecuteClipboardCaptureImageActionAsync(action, evalContext);

    // Preview, reload & source-folder forwarding
    public IReadOnlyList<CustomWidgetSettingDefinition> GetUnconfiguredSecretSettings() =>
        _previewAndReload.GetUnconfiguredSecretSettings();
    public void ApplyPreviewSeedPayload(CustomWidgetStorePayload? payload) =>
        _previewAndReload.ApplyPreviewSeedPayload(payload);
    public CustomWidgetStorePayload? BuildUserEnteredStoreSeedPayload() =>
        _previewAndReload.BuildUserEnteredStoreSeedPayload();
    public bool ApplyPreviewSampleDataJson(string? sampleDataJson, bool seedOnlyIfEmptyOrSample = false) =>
        _previewAndReload.ApplyPreviewSampleDataJson(sampleDataJson, seedOnlyIfEmptyOrSample);
    public void ClearPreviewSampleData() =>
        _previewAndReload.ClearPreviewSampleData();
    internal void SeedCalendarStandInIfEmpty() => _previewAndReload.SeedCalendarStandInIfEmpty();
    internal void StartManifestWatcher() => _previewAndReload.StartManifestWatcher();
    private void OnLanguageChanged() => _previewAndReload.OnLanguageChanged();
    internal void TrySyncAutoLocalizedTitle(bool notifyIdentityChange) => _previewAndReload.TrySyncAutoLocalizedTitle(notifyIdentityChange);

    internal bool ManifestUsesFileTextSources() => _capabilityHost.FileText.ManifestUsesFileTextSources();
    internal bool ManifestUsesCommandSources() => _capabilityHost.Command.ManifestUsesCommandSources();
    public Task<bool> RefreshFileTextSourcesNowAsync(string? targetSourceId = null, bool respectMinInterval = false) =>
        _capabilityHost.FileText.RefreshFileTextSourcesNowAsync(targetSourceId, respectMinInterval);
    public Task<bool> RefreshCommandSourcesNowAsync(string? targetSourceId = null, bool respectMinInterval = false) =>
        _capabilityHost.Command.RefreshCommandSourcesNowAsync(targetSourceId, respectMinInterval);
    internal static IReadOnlyList<string> ResolveAllowedExtensionsForFolderEnumeration(
        string? rawAccept,
        CustomWidgetSettingDefinition? def) =>
        LocalSourcePayloadHelper.ResolveAllowedExtensionsForFolderEnumeration(rawAccept, def);


    internal bool IsStudioGalleryThumbnailPreview =>
        Config.Id.StartsWith("__studio_template_", StringComparison.Ordinal) ||
        Config.Id.StartsWith("__studio_mywidget_", StringComparison.Ordinal);

    internal void NotifyViewRebuildRequested() => ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    internal void NotifyMetricsRefreshed() => MetricsRefreshed?.Invoke(this, EventArgs.Empty);
    internal void NotifyPreviewNotificationDispatched(string title, string message) =>
        PreviewNotificationDispatched?.Invoke(this, (title, message));

    private void UpdateSourceSubscriptions()
    {
        if (_isDisposed)
        {
            return;
        }

        _capabilityHost.UpdateAllSubscriptions();
        UpdateScriptTickSubscription();
    }

    internal bool ManifestUsesNetworkSources() => _capabilityHost.Network.ManifestUsesNetworkSources();
    internal string ResolveNetworkSourceUrl(CustomWidgetSourceSpec spec) => _capabilityHost.Network.ResolveNetworkSourceUrl(spec);
    internal IReadOnlyDictionary<string, string>? ResolveNetworkSourceHeaders(CustomWidgetSourceSpec spec) =>
        _capabilityHost.Network.ResolveNetworkSourceHeaders(spec);
    internal void HydrateNetworkSourcesFromDiskCache(bool overwriteExistingCollection = false) =>
        _capabilityHost.Network.HydrateNetworkSourcesFromDiskCache(overwriteExistingCollection);
    internal void HydrateSingleNetworkSourceFromDiskCache(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        bool overwriteExistingCollection) =>
        _capabilityHost.Network.HydrateSingleNetworkSourceFromDiskCache(spec, resolvedUrl, overwriteExistingCollection);
    internal Task RefreshDueNetworkSourcesAsync() => _capabilityHost.Network.RefreshDueNetworkSourcesAsync();
    public Task<bool> RefreshNetworkSourcesNowAsync(string? targetSourceId = null, bool isManualUserAction = false) =>
        _capabilityHost.Network.RefreshNetworkSourcesNowAsync(targetSourceId, isManualUserAction);
    internal string BuildRuntimeNetworkSourceKey(CustomWidgetSourceSpec spec) =>
        _capabilityHost.Network.BuildRuntimeNetworkSourceKey(spec);
    internal static bool AreAiMonitorSnapshotsStructurallyEqual(
        CustomWidgetAiMonitorSnapshot a,
        CustomWidgetAiMonitorSnapshot b,
        IReadOnlyDictionary<string, string>? state = null) =>
        AiMonitorCapabilityProvider.AreAiMonitorSnapshotsStructurallyEqual(a, b, state);
    internal bool ManifestUsesCountdown() => _capabilityHost.Countdown.ManifestUsesCountdown();
    internal static TimeSpan ComputeCountdownNextMidnightDelay(DateTimeOffset now) =>
        CountdownCapabilityProvider.ComputeCountdownNextMidnightDelay(now);
    internal static TimeSpan ComputeWakeResilientMidnightDelay(DateTimeOffset now) =>
        CountdownCapabilityProvider.ComputeWakeResilientMidnightDelay(now);
    internal void DispatchReminderToastNotification(string title, string message) =>
        _capabilityHost.Countdown.DispatchReminderToastNotification(title, message);
    internal bool ManifestUsesWeather() => _capabilityHost.Weather.ManifestUsesWeather();
    internal string ResolveEffectiveWeatherCity() => _capabilityHost.Weather.ResolveEffectiveWeatherCity();
    public Task<bool> RefreshWeatherNowAsync(bool forceRefresh = false, bool isManualUserAction = false) =>
        _capabilityHost.Weather.RefreshWeatherNowAsync(forceRefresh, isManualUserAction);
    internal bool ManifestUsesDeviceState() => _capabilityHost.DeviceState.ManifestUsesDeviceState();
    public Task RefreshDeviceStateNowAsync() => _capabilityHost.DeviceState.RefreshDeviceStateNowAsync();
    internal bool ManifestUsesClipboard() => _capabilityHost.Clipboard.ManifestUsesClipboard();
    public Task RefreshClipboardNowAsync() => _capabilityHost.Clipboard.RefreshClipboardNowAsync();
    internal bool ManifestUsesStorageDrives() => _capabilityHost.StorageDrives.ManifestUsesStorageDrives();
    public void RefreshStorageDrivesNow(bool triggerRebuild) => _capabilityHost.StorageDrives.RefreshStorageDrivesNow(triggerRebuild);
    internal bool ManifestUsesMusic() => _capabilityHost.Music.ManifestUsesMusic();
    public Task RefreshMusicNowAsync(bool triggerRebuild = true) => _capabilityHost.Music.RefreshMusicNowAsync(triggerRebuild);
    internal bool ManifestUsesTodo() => _capabilityHost.Todo.ManifestUsesTodo();
    public Task RefreshTodoNowAsync(bool triggerRebuild = true) => _capabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild);
    internal bool ManifestUsesClock() => _capabilityHost.Clock.ManifestUsesClock();
    internal bool ManifestUsesPomodoro() => _capabilityHost.Pomodoro.ManifestUsesPomodoro();
    internal static double? ProbeKeyboardMouseIdleSeconds() => PomodoroCapabilityProvider.ProbeKeyboardMouseIdleSeconds();
    internal void DispatchPomodoroCompletionNotification(CustomWidgetPomodoroCompletionNotice notice) =>
        _capabilityHost.Pomodoro.DispatchPomodoroCompletionNotification(notice);
    internal void UpdatePomodoroTimerSubscription() => _capabilityHost.Pomodoro.UpdatePomodoroTimerSubscription();
    private static bool IsEphemeralPersistExcludedStateKey(string key) =>
        CustomWidgetRuntimeTokens.IsEphemeralStateKey(key);

    internal static string NormalizeStateKey(string key)
    {
        string k = key.Trim();
        if (k.StartsWith("${", StringComparison.Ordinal) && k.EndsWith('}') && k.Length > 3)
        {
            k = k[2..^1].Trim();
        }
        if (k.StartsWith("$state.", StringComparison.Ordinal))
        {
            return k["$state.".Length..];
        }
        if (k.StartsWith("state.", StringComparison.Ordinal))
        {
            return k["state.".Length..];
        }
        return k;
    }

    internal static string NormalizeSettingKey(string key)
    {
        string k = key.Trim();
        if (k.StartsWith("${", StringComparison.Ordinal) && k.EndsWith('}') && k.Length > 3)
        {
            k = k[2..^1].Trim();
        }
        if (k.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
        {
            return k["$settings.".Length..].Trim();
        }
        if (k.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
        {
            return k["settings.".Length..].Trim();
        }
        return k;
    }

    internal static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            DeskBoxClipboardWriteScope.MarkText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Clipboard copy failed: {ex.Message}");
        }
    }

    internal void RaiseFeedback(string message, WidgetFeedbackSeverity severity)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(new WidgetFeedbackRequest(message, severity)));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        CancelPendingDebouncedPersist(out bool hadPendingDirtyState);
        if (hadPendingDirtyState && !_isPreviewOnly)
        {
            CustomWidgetStorePayload pendingPayload = BuildStorePayloadSnapshot();
            _ = _store.SaveAsync(pendingPayload, origin: this);
        }

        _isDisposed = true;
        _computedView.ClearCache();
        CustomWidgetAudioService.Shared.AmbientOwnerPreempted -= OnSharedAmbientOwnerPreempted;
        CustomWidgetAudioService.Shared.StopTracksForOwner(Config.Id);
        _jobBus.ActiveStateChanged -= OnJobBusActiveStateChanged;
        _actionDispatcher.RuntimeTrace.ErrorRecorded -= OnRuntimeTraceErrorRecorded;
        _audioService.Dispose();
        _jobBus.Dispose();
        _localizationService.LanguageChanged -= OnLanguageChanged;

        _aiAndLocalExecutor.Dispose();
        _capabilityHost.Dispose();
        _scriptRuntime.Dispose();
        _previewAndReload.Dispose();
        if (!_isPreviewOnly)
        {
            CustomWidgetDataStore.ReleaseWidgetIfUnused(Config.Id);
        }
    }

    private sealed class ComputedDictionaryWithRecordCache(Dictionary<string, JsonElement> inner)
        : IReadOnlyDictionary<string, JsonElement>, IComputedRecordCacheOwner
    {
        private readonly object _cacheGate = new();
        private readonly Dictionary<string, IReadOnlyList<CustomWidgetRecord>> _recordCache =
            new(StringComparer.Ordinal);

        public JsonElement this[string key] => inner[key];
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<JsonElement> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, out JsonElement value) => inner.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => inner.GetEnumerator();

        public IReadOnlyList<CustomWidgetRecord> GetOrAddProjectedRecords(
            string normalizedSubPath,
            Func<string, IReadOnlyList<CustomWidgetRecord>> factory)
        {
            lock (_cacheGate)
            {
                if (_recordCache.TryGetValue(normalizedSubPath, out IReadOnlyList<CustomWidgetRecord>? cached))
                {
                    return cached;
                }
            }

            IReadOnlyList<CustomWidgetRecord> built = factory(normalizedSubPath);
            lock (_cacheGate)
            {
                _recordCache[normalizedSubPath] = built;
            }

            return built;
        }

        public void ClearCache()
        {
            lock (_cacheGate)
            {
                _recordCache.Clear();
            }
        }

        public void InvalidateKeys(HashSet<string>? changedRootKeys)
        {
            lock (_cacheGate)
            {
                if (_recordCache.Count == 0)
                {
                    return;
                }

                if (changedRootKeys is null || changedRootKeys.Count == 0)
                {
                    _recordCache.Clear();
                    return;
                }

                List<string>? toRemove = null;
                foreach (string cachedPath in _recordCache.Keys)
                {
                    string root = ExtractRootSegment(cachedPath);
                    if (changedRootKeys.Contains(root))
                    {
                        toRemove ??= [];
                        toRemove.Add(cachedPath);
                    }
                }

                if (toRemove is not null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        _recordCache.Remove(toRemove[i]);
                    }
                }
            }
        }

        private static string ExtractRootSegment(string subPath)
        {
            int dot = subPath.IndexOf('.');
            int bracket = subPath.IndexOf('[');
            int split = dot >= 0 && bracket >= 0
                ? Math.Min(dot, bracket)
                : (dot >= 0 ? dot : bracket);
            return split >= 0 ? subPath[..split] : subPath;
        }
    }
}
