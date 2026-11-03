using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Launcher = Windows.System.Launcher;

namespace DeskBox.ViewModels;

public sealed record CustomWidgetSecurityPrompt(
    string ActionKind,
    string TargetDisplay,
    string WidgetName);

public sealed partial class CustomWidgetViewModel : ObservableObject, IDisposable
{
    public const string TemplateMetadataKey = "custom.templateId";
    private const string TrustedExternalTargetsSettingKey = "__trustedTargets";

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
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CustomWidgetRecord>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _networkSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastManualRefreshUtcTicksByKey = new(StringComparer.Ordinal);
    private readonly object _reloadDebounceLock = new();
    private IDisposable? _manifestWatcherSubscription;
    private CancellationTokenSource? _reloadDebounceCts;
    private CancellationTokenSource? _networkFetchCts;
    private CancellationTokenSource? _weatherFetchCts;
    private CancellationTokenSource? _aiActionCts;
    private bool _pendingReloadNotifyFeedback;
    private long _lastOwnSaveUtcTicks;
    private int _networkFetchInFlight;
    private int _pendingNetworkRefreshRequested;
    private int _weatherFetchInFlight;
    private int _deviceRefreshInFlight;
    private int _clipboardRefreshInFlight;
    private int _aiActionInFlight;
    private DispatcherQueueTimer? _pomodoroTimer;
    private DispatcherQueueTimer? _countdownTimer;
    private DispatcherQueueTimer? _calendarDayTimer;
    private DispatcherQueueTimer? _weatherSourceTimer;
    private DispatcherQueueTimer? _deviceStateTimer;
    private DispatcherQueueTimer? _clipboardTimer;
    private DispatcherQueueTimer? _storageDrivesTimer;
    private DispatcherQueueTimer? _clockTimer;
    private DispatcherQueueTimer? _networkSourceTimer;
    private DateOnly _lastCountdownDay;
    private DateOnly _lastCalendarDay;
    private string? _lastFetchedWeatherCityKey;
    private CustomWidgetManifest _manifest = new();
    private IReadOnlyList<CustomWidgetValidationDiagnostic> _diagnostics = [];
    private CustomWidgetSystemMetricsSnapshot _systemMetrics = CustomWidgetSystemMetricsSnapshot.Empty;
    private CustomWidgetAiMonitorSnapshot _aiMonitorSnapshot = CustomWidgetAiMonitorSnapshot.Empty;
    private CustomWidgetWeatherSnapshot _weatherSnapshot = CustomWidgetWeatherSnapshot.Empty;
    private CustomWidgetDeviceStateSnapshot _deviceStateSnapshot = CustomWidgetDeviceStateSnapshot.Empty;
    private CustomWidgetClipboardSnapshot _clipboardSnapshot = CustomWidgetClipboardSnapshot.Empty;
    private CustomWidgetStorageDrivesSnapshot _storageDrivesSnapshot = CustomWidgetStorageDrivesSnapshot.Empty;
    private CustomWidgetTransformResult? _lastTransformResult;
    private bool _isSubscribedToSystemMetrics;
    private bool _isSubscribedToAiMonitor;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;
    private bool _isInitialized;
    private bool _isDisposed;
    private long _liveStateVersion;
    private long _notifiedLiveStateVersion;
    private string _capsuleTitle = string.Empty;
    private string _capsuleSummary = string.Empty;
    private string _capsuleGlyph = "\uE71D";
    private string _capsuleBadge = string.Empty;
    private bool _capsuleBadgeIsWarning;
    private double? _capsuleProgress;
    private bool _capsuleEnableBounce = true;
    private bool _capsuleProjectionDirty;
    private readonly bool _isPreviewOnly;
    private string? _lastAutoLocalizedTitle;

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
        CustomWidgetSystemControlService? systemControlService = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(localizationService);

        Config = config;
        _localizationService = localizationService;
        _store = store ?? CustomWidgetDataStore.ForWidget(config.Id);
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

        _localizationService.LanguageChanged += OnLanguageChanged;
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
    public Func<IntPtr>? OwnerWindowHandleResolver { get; set; }

    public WidgetConfig Config { get; }
    public string ActiveCultureName => _localizationService.CurrentCultureName;
    public bool IsInitialized => _isInitialized;
    public CustomWidgetDataStore Store => _store;
    public CustomWidgetManifest Manifest => _manifest;
    public IReadOnlyList<CustomWidgetValidationDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlyDictionary<string, string> Settings => _settings;
    public IReadOnlyDictionary<string, string> Secrets => _secrets;
    public IReadOnlyDictionary<string, string> State => _state;
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> Collections => _collections;
    public CustomWidgetWeatherSnapshot WeatherSnapshot => _weatherSnapshot;
    public CustomWidgetDeviceStateSnapshot DeviceStateSnapshot => _deviceStateSnapshot;
    public CustomWidgetClipboardSnapshot ClipboardSnapshot => _clipboardSnapshot;
    public CustomWidgetStorageDrivesSnapshot StorageDrivesSnapshot => _storageDrivesSnapshot;
    public CustomWidgetTransformResult? LastTransformResult => _lastTransformResult;

    public string ResolveRuntimeAssetPath(string? rawPath) => _store.ResolveRuntimePath(rawPath);

    public bool HasDropZoneNode => NodeTreeContainsType(_manifest.View, "DropZone");

    public bool AcceptsFileDrops =>
        (_manifest.OnDrop is { } drop &&
         string.Equals(drop.Accept, "files", StringComparison.OrdinalIgnoreCase)) ||
        HasDropZoneNode;

    public IReadOnlyList<CustomWidgetSettingDefinition> SecretSettingDefinitions =>
        _manifest.Settings
            .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<CustomWidgetSettingDefinition> GetUnconfiguredSecretSettings()
    {
        if (_manifest.Settings.Count == 0)
        {
            return [];
        }

        List<CustomWidgetSettingDefinition>? missing = null;
        foreach (CustomWidgetSettingDefinition def in _manifest.Settings)
        {
            if (!string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_secrets.TryGetValue(def.Key, out string? val) || string.IsNullOrWhiteSpace(val))
            {
                missing ??= [];
                missing.Add(def);
            }
        }

        return missing is null ? [] : missing;
    }

    public string EffectiveDropMode
    {
        get
        {
            if (_manifest.OnDrop is null)
            {
                return "link";
            }
            string evaluated = CustomWidgetExpressionEvaluator.Interpolate(
                _manifest.OnDrop.Mode,
                CreateEvaluationContext());
            return string.Equals(evaluated, "copy", StringComparison.OrdinalIgnoreCase)
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

    public string LiveStateKey => $"{Config.Id}:{_liveStateVersion}";
    public string ManifestEmojiIcon { get; private set; } = string.Empty;
    public string TitleGlyph => CustomWidgetTemplates.IsSegoeFluentGlyph(_manifest.Icon)
        ? _manifest.Icon
        : CustomWidgetTemplates.MatchPresetOrNull(_manifest.Id)?.IconGlyph ?? "\uE71D";
    public string TitleIconKind =>
        WidgetTitleIconKindNames.FromCustomTemplateId(_manifest.Id) ??
        WidgetTitleIconKindNames.FromWidgetConfig(Config);

    public event EventHandler? ViewRebuildRequested;
    public event EventHandler? MetricsRefreshed;
    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;
    public event Action<string, string>? ManifestIdentityChanged;

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
            Collections = preserveRuntimeData
                ? _collections.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<CustomWidgetRecord>(kvp.Value),
                    StringComparer.Ordinal)
                : new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
        };

        ApplyManifestAndPayload(rawJson, preservedPayload);
        string previewTitle = _manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);
        if (!string.IsNullOrWhiteSpace(previewTitle))
        {
            Config.Name = previewTitle;
        }
        UpdateSourceSubscriptions();
        _isInitialized = true;
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool HasPreviewSampleData { get; private set; }

    public bool ApplyPreviewSampleDataJson(string? sampleDataJson)
    {
        if (_isDisposed || !_isPreviewOnly || string.IsNullOrWhiteSpace(sampleDataJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                sampleDataJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool appliedAny = false;
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("state", out JsonElement stateObj) &&
                stateObj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in stateObj.EnumerateObject())
                {
                    string val = JsonScalarToString(prop.Value);
                    if (!string.IsNullOrEmpty(prop.Name))
                    {
                        _state[prop.Name] = val;
                        appliedAny = true;
                    }
                }
            }

            JsonElement collectionsRoot = root;
            if (root.TryGetProperty("collections", out JsonElement colsObj) &&
                colsObj.ValueKind == JsonValueKind.Object)
            {
                collectionsRoot = colsObj;
            }

            foreach (JsonProperty colProp in collectionsRoot.EnumerateObject())
            {
                if (string.Equals(colProp.Name, "state", StringComparison.Ordinal) ||
                    colProp.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var records = new List<CustomWidgetRecord>();
                int idx = 0;
                foreach (JsonElement itemEl in colProp.Value.EnumerateArray())
                {
                    if (itemEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    DateTimeOffset createdUtc = DateTimeOffset.UtcNow.AddDays(-idx);
                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonProperty fieldProp in itemEl.EnumerateObject())
                    {
                        string strVal = JsonScalarToString(fieldProp.Value);
                        fields[fieldProp.Name] = strVal;
                        if (string.Equals(fieldProp.Name, "date", StringComparison.OrdinalIgnoreCase) &&
                            DateTimeOffset.TryParse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedDate))
                        {
                            createdUtc = parsedDate.ToUniversalTime();
                        }
                    }

                    records.Add(new CustomWidgetRecord
                    {
                        Id = $"sample-{idx + 1}",
                        CreatedAtUtcTicks = createdUtc.UtcTicks,
                        UpdatedAtUtcTicks = createdUtc.UtcTicks,
                        Fields = fields
                    });
                    idx++;
                }

                if (records.Count > 0)
                {
                    _collections[colProp.Name] = records;
                    appliedAny = true;
                }
            }

            if (appliedAny)
            {
                HasPreviewSampleData = true;
                RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            }

            return appliedAny;
        }
        catch
        {
            return false;
        }
    }

    public void ClearPreviewSampleData()
    {
        HasPreviewSampleData = false;
    }

    private static string JsonScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
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
            : CustomWidgetTemplates.WorkLoggerTemplateId;

        string rawJson = await _store.ReadOrCreateManifestJsonAsync(defaultTemplateId);
        CustomWidgetStorePayload payload = await _store.LoadAsync();
        ApplyManifestAndPayload(rawJson, payload);
        SyncManifestEmojiToTitleIcon(previousManifestIcon: null, isReload: false);

        StartManifestWatcher();
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
            string previousManifestName = _manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);
            string previousManifestIcon = _manifest.Icon;
            var previousSettings = new Dictionary<string, string>(_settings, StringComparer.Ordinal);
            string defaultTemplateId = Config.Metadata.TryGetValue(TemplateMetadataKey, out string? tpl) &&
                                       !string.IsNullOrWhiteSpace(tpl)
                ? tpl
                : CustomWidgetTemplates.WorkLoggerTemplateId;
            string rawJson = await _store.ReadOrCreateManifestJsonAsync(defaultTemplateId);
            CustomWidgetStorePayload payload = await _store.ReloadFromDiskAsync();
            ApplyManifestAndPayload(rawJson, payload, previousSettings);
            SyncManifestEmojiToTitleIcon(previousManifestIcon, isReload: true);

            string currentLocalizedName = _manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);
            bool nameChanged = !_diagnostics.Any(d => d.IsError) &&
                               !string.IsNullOrWhiteSpace(currentLocalizedName) &&
                               !string.Equals(previousManifestName, currentLocalizedName, StringComparison.Ordinal);
            bool iconChanged = !string.Equals(previousManifestIcon, _manifest.Icon, StringComparison.Ordinal);
            if (nameChanged && !CustomWidgetTemplates.Presets.Any(p =>
                    string.Equals($"deskbox.{p.Id}", _manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Config.Name = currentLocalizedName;
                _lastAutoLocalizedTitle = currentLocalizedName;
            }
            if (nameChanged || iconChanged)
            {
                ManifestIdentityChanged?.Invoke(currentLocalizedName, _manifest.Icon);
            }

            UpdateSourceSubscriptions();
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

    public CustomWidgetEvaluationContext CreateEvaluationContext() =>
        new()
        {
            I18n = _manifest.ResolveI18nDictionary(_localizationService.CurrentCultureName),
            Settings = _settings,
            Secrets = _secrets,
            State = _state,
            Collections = _collections,
            SystemMetrics = _systemMetrics,
            AiMonitor = _aiMonitorSnapshot,
            Weather = _weatherSnapshot,
            DeviceState = _deviceStateSnapshot,
            Clipboard = _clipboardSnapshot,
            StorageDrives = _storageDrivesSnapshot,
            TransformResult = _lastTransformResult,
            CultureName = _localizationService.CurrentCultureName
        };

    public void SetStateValueWithoutRebuild(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normalizedKey = NormalizeStateKey(key);
        _state[normalizedKey] = value;
        RefreshCapsuleProjection(bumpLiveState: false);
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

        _settings[key] = value;
        CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(_state, _settings, key);
        await PersistStoreAsync();
        UpdateSourceSubscriptions();
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

        _networkSourceLastFetchUtcTicks.Clear();
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
        Interlocked.Exchange(ref _lastOwnSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        await _store.ClearAllDataAsync(origin: this);
        Interlocked.Exchange(ref _lastOwnSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        CustomWidgetStorePayload payload = await _store.LoadAsync();
        HydrateFromPayload(payload);
        UpdateSourceSubscriptions();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        RaiseFeedback(
            _localizationService.T("CustomWidget.Toast.DataCleared"),
            WidgetFeedbackSeverity.Info);
    }

    private const int MaxActionExecutionDepth = 64;

    public async Task ExecuteActionsAsync(
        IReadOnlyList<CustomWidgetActionSpec> actions,
        CustomWidgetEvaluationContext? context = null,
        bool isNested = false,
        int depth = 0)
    {
        if (actions.Count == 0 || _isDisposed || depth > MaxActionExecutionDepth)
        {
            return;
        }

        long actionBatchStartTicks = DateTimeOffset.UtcNow.UtcTicks;
        Dictionary<int, string>? networkKeysBefore = null;
        if (!isNested && ManifestUsesNetworkSources())
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

        string? weatherCityBefore = !isNested && ManifestUsesWeather()
            ? ResolveEffectiveWeatherCity()
            : null;

        CustomWidgetEvaluationContext evalContext = context ?? CreateEvaluationContext();
        bool needsSave = false;
        bool needsRebuild = false;
        bool needsLiveRefreshOnly = false;

        foreach (CustomWidgetActionSpec action in actions)
        {
            if (!string.IsNullOrWhiteSpace(action.Guard) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(action.Guard, evalContext))
            {
                continue;
            }

            bool skipThen = false;
            string actionType = action.Type.Trim();
            switch (actionType.ToLowerInvariant())
            {
                case "state.set":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = NormalizeStateKey(action.Key);
                        string val = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                        _state[key] = val;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "state.toggle":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = NormalizeStateKey(action.Key);
                        bool current = _state.TryGetValue(key, out string? v) &&
                                       CustomWidgetExpressionEvaluator.IsTruthy(v);
                        _state[key] = current ? "false" : "true";
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "state.increment":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = NormalizeStateKey(action.Key);
                        double current = _state.TryGetValue(key, out string? v) &&
                                         double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
                            ? c
                            : 0;
                        double delta = double.TryParse(
                            CustomWidgetExpressionEvaluator.Interpolate(action.Value ?? "1", evalContext),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double d)
                            ? d
                            : 1;
                        _state[key] = (current + delta).ToString(CultureInfo.InvariantCulture);
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.prepend":
                case "collection.append":
                {
                    string colName = action.Collection ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(colName))
                    {
                        var record = new CustomWidgetRecord
                        {
                            UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                        };
                        bool hasBlankPrimaryDraftField = false;
                        List<string>? autoClearDraftKeys = null;
                        if (action.ItemFields is not null)
                        {
                            foreach ((string fk, string fv) in action.ItemFields)
                            {
                                string interpolated = CustomWidgetExpressionEvaluator.Interpolate(fv, evalContext);
                                record.Fields[fk] = interpolated;

                                string trimmedExpr = fv?.Trim() ?? string.Empty;
                                if (trimmedExpr.StartsWith("${state.", StringComparison.OrdinalIgnoreCase) &&
                                    trimmedExpr.EndsWith('}') &&
                                    trimmedExpr.IndexOf('}', 8) == trimmedExpr.Length - 1)
                                {
                                    string stateKey = trimmedExpr[8..^1].Trim();
                                    bool isPrimaryTextProp =
                                        string.Equals(fk, "title", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "text", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "name", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "content", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "label", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "note", StringComparison.OrdinalIgnoreCase);

                                    if (isPrimaryTextProp && string.IsNullOrWhiteSpace(interpolated))
                                    {
                                        hasBlankPrimaryDraftField = true;
                                    }

                                    if (isPrimaryTextProp &&
                                        (stateKey.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
                                         stateKey.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                                         stateKey.StartsWith("new", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        autoClearDraftKeys ??= [];
                                        autoClearDraftKeys.Add(stateKey);
                                    }
                                }
                            }
                        }

                        if (hasBlankPrimaryDraftField)
                        {
                            skipThen = true;
                            break;
                        }

                        if (!_collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                        {
                            list = [];
                            _collections[colName] = list;
                        }

                        if (string.Equals(actionType, "collection.prepend", StringComparison.OrdinalIgnoreCase))
                        {
                            list.Insert(0, record);
                        }
                        else
                        {
                            list.Add(record);
                        }

                        if (autoClearDraftKeys is { Count: > 0 })
                        {
                            foreach (string draftKey in autoClearDraftKeys)
                            {
                                bool explicitlySetLater =
                                    actions.Any(a => string.Equals(a.Type, "state.set", StringComparison.OrdinalIgnoreCase) &&
                                                     string.Equals(NormalizeStateKey(a.Key ?? string.Empty), draftKey, StringComparison.Ordinal)) ||
                                    (action.Then?.Any(a => string.Equals(a.Type, "state.set", StringComparison.OrdinalIgnoreCase) &&
                                                           string.Equals(NormalizeStateKey(a.Key ?? string.Empty), draftKey, StringComparison.Ordinal)) ?? false);
                                if (!explicitlySetLater)
                                {
                                    _state[draftKey] = string.Empty;
                                }
                            }
                        }

                        int maxItems = _manifest.Collections.TryGetValue(colName, out CustomWidgetCollectionSchema? schema)
                            ? schema.MaxItems
                            : 200;
                        while (list.Count > maxItems)
                        {
                            list.RemoveAt(list.Count - 1);
                        }

                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.remove":
                {
                    string colName = action.Collection ?? string.Empty;
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext);
                    if (!string.IsNullOrWhiteSpace(colName) &&
                        !string.IsNullOrWhiteSpace(targetId) &&
                        _collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                    {
                        int removed = list.RemoveAll(r => string.Equals(r.Id, targetId, StringComparison.Ordinal));
                        if (removed > 0)
                        {
                            needsSave = true;
                            needsRebuild = true;
                        }
                    }
                    break;
                }

                case "collection.update":
                {
                    string colName = action.Collection ?? string.Empty;
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext);
                    if (string.IsNullOrWhiteSpace(targetId))
                    {
                        targetId = evalContext.CurrentItem?.Id ?? string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(colName) &&
                        !string.IsNullOrWhiteSpace(targetId) &&
                        action.ItemFields is { Count: > 0 } &&
                        _collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                    {
                        int targetIndex = list.FindIndex(r =>
                            string.Equals(r.Id, targetId, StringComparison.Ordinal));
                        if (targetIndex >= 0)
                        {
                            CustomWidgetRecord target = list[targetIndex];
                            CustomWidgetEvaluationContext itemEvalCtx = evalContext.CurrentItem is not null
                                ? evalContext
                                : evalContext.WithItem(target, targetIndex);
                            foreach ((string fk, string fv) in action.ItemFields)
                            {
                                target.Fields[fk] = CustomWidgetExpressionEvaluator.Interpolate(fv, itemEvalCtx);
                            }
                            target.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                            needsSave = true;
                            needsRebuild = true;
                        }
                    }
                    break;
                }

                case "collection.clear":
                {
                    string colName = action.Collection ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(colName) &&
                        _collections.TryGetValue(colName, out List<CustomWidgetRecord>? list) &&
                        list.Count > 0)
                    {
                        list.Clear();
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.exportmarkdown":
                {
                    string colName = action.Collection ?? _collections.Keys.FirstOrDefault() ?? "logs";
                    IReadOnlyList<CustomWidgetRecord> records =
                        _collections.TryGetValue(colName, out List<CustomWidgetRecord>? list)
                            ? list
                            : [];
                    string markdown = CustomWidgetDataStore.FormatCollectionAsMarkdown(
                        Config.Name,
                        colName,
                        records);
                    CopyTextToClipboard(markdown);
                    RaiseFeedback(
                        _localizationService.T("CustomWidget.Toast.MarkdownCopied"),
                        WidgetFeedbackSeverity.Info);
                    break;
                }

                case "file.open":
                {
                    string path = CustomWidgetExpressionEvaluator.Interpolate(action.Path, evalContext);
                    await OpenFileWithDefaultAppAsync(path);
                    break;
                }

                case "file.reveal":
                {
                    string path = CustomWidgetExpressionEvaluator.Interpolate(action.Path, evalContext);
                    await RevealFileInExplorerAsync(path);
                    break;
                }

                case "clipboard.copy":
                {
                    string text = CustomWidgetExpressionEvaluator.Interpolate(action.Text ?? action.Value, evalContext);
                    if (!string.IsNullOrEmpty(text))
                    {
                        CopyTextToClipboard(text);
                        if (ManifestUsesClipboard())
                        {
                            _clipboardSnapshot = CustomWidgetClipboardSnapshot.FromText(text);
                            evalContext = CreateEvaluationContext().WithEventValue(evalContext.EventValue);
                            needsRebuild = true;
                        }
                        RaiseFeedback(
                            _localizationService.T("CustomWidget.Toast.Copied"),
                            WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "url.open":
                {
                    string url = CustomWidgetExpressionEvaluator.Interpolate(action.Url ?? action.Value, evalContext);
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        if (await EnsureCommunityExternalTargetApprovedAsync(
                                actionKind: "url.open",
                                trustToken: $"host:{uri.DnsSafeHost.ToLowerInvariant()}",
                                targetDisplay: uri.AbsoluteUri))
                        {
                            _ = Launcher.LaunchUriAsync(uri);
                        }
                    }
                    break;
                }

                case "source.refresh":
                case "refreshsource":
                {
                    string? targetSourceId = !string.IsNullOrWhiteSpace(action.TargetId)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext)
                        : !string.IsNullOrWhiteSpace(action.Value)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext)
                            : null;
                    if (ManifestUsesNetworkSources())
                    {
                        _ = RefreshNetworkSourcesNowAsync(targetSourceId, isManualUserAction: true);
                    }
                    if (ManifestUsesWeather() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "weather", StringComparison.OrdinalIgnoreCase) ||
                         _manifest.Sources.Any(s =>
                             string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase))))
                    {
                        _ = RefreshWeatherNowAsync(forceRefresh: true, isManualUserAction: true);
                    }
                    if (ManifestUsesDeviceState() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "deviceState", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = RefreshDeviceStateNowAsync();
                    }
                    if (ManifestUsesStorageDrives() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "storageDrives", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(targetSourceId, "drives", StringComparison.OrdinalIgnoreCase)))
                    {
                        RefreshStorageDrivesNow(triggerRebuild: true);
                    }
                    if (ManifestUsesClipboard() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "clipboard", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = RefreshClipboardNowAsync();
                    }
                    break;
                }

                case "toast.show":
                {
                    string msg = CustomWidgetExpressionEvaluator.Interpolate(action.Message ?? action.Text, evalContext);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        RaiseFeedback(msg, WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "reminder.notify":
                {
                    string? rawTitle = action.Title ?? (!string.IsNullOrWhiteSpace(action.Message) ? action.Text : null);
                    string title = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, evalContext).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = Config.Name;
                    }
                    string msg = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Message ?? action.Text ?? action.Value,
                        evalContext).Trim();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        RaiseFeedback(msg, WidgetFeedbackSeverity.Info);
                        DispatchReminderToastNotification(title, msg);
                    }
                    break;
                }

                case "file.transform":
                {
                    string rawSourcePath = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Path ?? evalContext.DropFile?.Path,
                        evalContext).Trim();
                    string resolvedSourcePath = _store.ResolveRuntimePath(rawSourcePath);
                    string formatOrOp = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Format ?? action.Value,
                        evalContext).Trim();
                    CustomWidgetTransformResult tr = await CustomWidgetFileTransformService.TransformFileAsync(
                        resolvedSourcePath,
                        formatOrOp,
                        action.MaxWidth,
                        action.Quality);
                    _lastTransformResult = tr;
                    evalContext = evalContext.WithTransform(tr);
                    _state["transformStatus"] = tr.Succeeded ? "ok" : "error";
                    _state["transformOutput"] = !string.IsNullOrWhiteSpace(tr.OutputPath) ? tr.OutputPath : tr.ResultText;
                    _state["transformError"] = tr.ErrorMessage ?? string.Empty;
                    needsSave = true;
                    needsRebuild = true;
                    if (!tr.Succeeded)
                    {
                        RaiseFeedback(
                            tr.ErrorMessage ?? _localizationService.T("CustomWidget.Toast.TransformFailed"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    else
                    {
                        string summary = !string.IsNullOrWhiteSpace(tr.OutputName)
                            ? $"{tr.OutputName} ({tr.SizeText})"
                            : tr.ResultText;
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            RaiseFeedback(summary, WidgetFeedbackSeverity.Info);
                        }
                    }
                    break;
                }

                case "file.pick":
                {
                    IntPtr ownerHwnd = OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero;
                    IReadOnlyList<string> picked = await FileOpenPickerService.PickFilesAsync(ownerHwnd);
                    if (picked.Count == 0)
                    {
                        skipThen = true;
                        break;
                    }

                    IReadOnlyList<CustomWidgetActionSpec> followUp = action.Then is { Count: > 0 }
                        ? action.Then
                        : (_manifest.OnDrop?.Actions ?? FindFirstDropZoneActions(_manifest.View));
                    if (followUp.Count > 0)
                    {
                        foreach (string pickedPath in picked)
                        {
                            CustomWidgetDropFileContext? dropCtx = await BuildDropFileContextAsync(pickedPath, usePortableStoreMode: action.Then is not { Count: > 0 } && _manifest.OnDrop is not null);
                            if (dropCtx is not null)
                            {
                                await ExecuteActionsAsync(
                                    followUp,
                                    evalContext.WithDrop(dropCtx),
                                    isNested: true,
                                    depth: depth + 1);
                            }
                        }
                    }
                    skipThen = true;
                    break;
                }

                case "text.transform":
                {
                    string op = !string.IsNullOrWhiteSpace(action.Format)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Format, evalContext).Trim()
                        : !string.IsNullOrWhiteSpace(action.Text) && !string.IsNullOrWhiteSpace(action.Value)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim()
                            : CustomWidgetExpressionEvaluator.Interpolate(action.TargetId ?? action.Value, evalContext).Trim();

                    string inputText;
                    if (!string.IsNullOrWhiteSpace(action.Text))
                    {
                        inputText = CustomWidgetExpressionEvaluator.Interpolate(action.Text, evalContext);
                    }
                    else if (!string.IsNullOrWhiteSpace(action.Format) && !string.IsNullOrWhiteSpace(action.Value))
                    {
                        inputText = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                    }
                    else
                    {
                        inputText = _clipboardSnapshot.Text;
                    }

                    CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
                        inputText,
                        op);
                    _lastTransformResult = tr;
                    evalContext = evalContext.WithTransform(tr);
                    _state["transformStatus"] = tr.Succeeded ? "ok" : "error";
                    _state["transformOutput"] = tr.ResultText;
                    _state["transformError"] = tr.ErrorMessage ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string outKey = NormalizeStateKey(action.Key);
                        if (!string.IsNullOrWhiteSpace(outKey))
                        {
                            _state[outKey] = tr.Succeeded ? tr.ResultText : (tr.ErrorMessage ?? string.Empty);
                        }
                    }

                    needsSave = true;
                    needsRebuild = true;
                    if (!tr.Succeeded)
                    {
                        RaiseFeedback(
                            tr.ErrorMessage ?? _localizationService.T("CustomWidget.Toast.TransformFailed"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    else if (string.Equals(action.Mode, "copy", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrEmpty(tr.ResultText))
                    {
                        CopyTextToClipboard(tr.ResultText);
                        if (ManifestUsesClipboard())
                        {
                            _clipboardSnapshot = CustomWidgetClipboardSnapshot.FromText(tr.ResultText);
                        }
                        RaiseFeedback(
                            _localizationService.T("CustomWidget.Toast.Copied"),
                            WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "launcher.open":
                {
                    string rawTarget = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Path ?? action.Url ?? action.Value,
                        evalContext).Trim();
                    if (!CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget(
                            rawTarget,
                            _store,
                            out QuickLaunchTarget resolvedTarget))
                    {
                        RaiseFeedback(
                            _localizationService.T("CustomWidget.Toast.FileNotFound"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                        break;
                    }

                    if (resolvedTarget.RequiresCommunityConsent)
                    {
                        bool forceForExecutable = !resolvedTarget.IsUri &&
                                                  CustomWidgetUtilityEngine.IsExecutableOrScriptPath(resolvedTarget.LaunchTarget);
                        bool approved = await EnsureCommunityExternalTargetApprovedAsync(
                            actionKind: "launcher.open",
                            trustToken: resolvedTarget.TrustToken,
                            targetDisplay: resolvedTarget.DisplayTarget,
                            requireConsentForAllOrigins: forceForExecutable);
                        if (!approved)
                        {
                            skipThen = true;
                            break;
                        }
                    }

                    bool launched = await CustomWidgetUtilityEngine.ExecuteQuickLaunchAsync(resolvedTarget);
                    if (!launched)
                    {
                        RaiseFeedback(
                            _localizationService.T("CustomWidget.Toast.FileNotFound"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    break;
                }

                case "system.control":
                {
                    string cmd = !string.IsNullOrWhiteSpace(action.Key)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Key, evalContext).Trim()
                        : !string.IsNullOrWhiteSpace(action.TargetId)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext).Trim()
                            : CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim();
                    string? rawVal = !string.IsNullOrWhiteSpace(action.Key) || !string.IsNullOrWhiteSpace(action.TargetId)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim()
                        : null;

                    if (cmd.Equals("recyclebin.empty", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("power.lock", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("lock", StringComparison.OrdinalIgnoreCase))
                    {
                        bool approved = await EnsureCommunityExternalTargetApprovedAsync(
                            actionKind: "system.control",
                            trustToken: $"sys:{cmd.ToLowerInvariant()}",
                            targetDisplay: cmd);
                        if (!approved)
                        {
                            skipThen = true;
                            break;
                        }
                    }

                    string? prevEventVal = evalContext.EventValue;
                    CustomWidgetSystemControlOutcome sysOutcome = await _systemControlService.ExecuteControlAsync(
                        cmd,
                        rawVal,
                        OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero,
                        ActiveCultureName);
                    _deviceStateSnapshot = _systemControlService.Latest;
                    evalContext = CreateEvaluationContext().WithEventValue(prevEventVal);
                    if (cmd.Equals("volume.set", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("brightness.set", StringComparison.OrdinalIgnoreCase))
                    {
                        needsLiveRefreshOnly = true;
                    }
                    else
                    {
                        needsRebuild = true;
                    }

                    if (!string.IsNullOrWhiteSpace(sysOutcome.FeedbackMessage) &&
                        !cmd.Equals("volume.set", StringComparison.OrdinalIgnoreCase) &&
                        !cmd.Equals("brightness.set", StringComparison.OrdinalIgnoreCase))
                    {
                        RaiseFeedback(
                            sysOutcome.FeedbackMessage,
                            sysOutcome.Succeeded ? WidgetFeedbackSeverity.Info : WidgetFeedbackSeverity.Warning);
                    }
                    if (!sysOutcome.Succeeded)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "system.opensetting":
                {
                    string target = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Key ?? action.Value ?? action.Url,
                        evalContext).Trim();
                    await _systemControlService.OpenSettingAsync(target);
                    break;
                }

                case "timer.toggle":
                case "pomodoro.toggle":
                {
                    CustomWidgetPomodoroEngine.ToggleStartPause(_state, _settings, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "timer.reset":
                case "pomodoro.reset":
                {
                    CustomWidgetPomodoroEngine.ResetCurrentPhase(_state, _settings, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.extend":
                {
                    string rawVal = CustomWidgetExpressionEvaluator.Interpolate(action.Value ?? "300", evalContext).Trim();
                    int extraSeconds = 300;
                    if (rawVal.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(rawVal[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins))
                    {
                        extraSeconds = Math.Clamp(mins, 1, 120) * 60;
                    }
                    else if (int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs))
                    {
                        extraSeconds = Math.Clamp(secs, 10, 7200);
                    }

                    CustomWidgetPomodoroEngine.ExtendSeconds(_state, _settings, extraSeconds, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsLiveRefreshOnly = true;
                    break;
                }

                case "pomodoro.skip":
                {
                    CustomWidgetPomodoroEngine.SkipPhase(_state, _settings, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.setphase":
                {
                    string targetPhase = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                    CustomWidgetPomodoroEngine.SetPhase(_state, _settings, targetPhase, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.cyclephase":
                {
                    CustomWidgetPomodoroEngine.CyclePhase(_state, _settings, DateTimeOffset.Now);
                    UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "countdown.pin":
                {
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Value,
                        evalContext);
                    if (CustomWidgetCountdownEngine.PinEvent(_collections, targetId))
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "countdown.delete":
                {
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Value,
                        evalContext);
                    if (CustomWidgetCountdownEngine.DeleteEvent(_collections, targetId))
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "ai.selectprovider":
                {
                    string providerId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Value ?? action.TargetId,
                        evalContext);
                    if (!string.IsNullOrWhiteSpace(providerId))
                    {
                        _state[CustomWidgetAiMonitorEngine.KeySelectedProvider] = providerId.Trim();
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "ai.refresh":
                {
                    _ = RefreshAiMonitorNowAsync(showToast: true);
                    break;
                }

                case "ai.run":
                {
                    (bool aiOk, bool aiMutated) = await ExecuteAiRunActionAsync(action, evalContext);
                    if (aiMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    if (!aiOk)
                    {
                        skipThen = true;
                    }
                    break;
                }
            }

            if (!skipThen && action.Then is { Count: > 0 })
            {
                await ExecuteActionsAsync(action.Then, evalContext, isNested: true, depth: depth + 1);
            }
        }

        bool dynamicNetworkUrlChanged = false;
        if (!isNested && networkKeysBefore is { Count: > 0 })
        {
            foreach ((int idx, string prevKey) in networkKeysBefore)
            {
                if (idx >= 0 && idx < _manifest.Sources.Count)
                {
                    CustomWidgetSourceSpec src = _manifest.Sources[idx];
                    string nextKey = BuildRuntimeNetworkSourceKey(src);
                    if (!string.Equals(prevKey, nextKey, StringComparison.Ordinal))
                    {
                        dynamicNetworkUrlChanged = true;
                        if (!_networkSourceLastFetchUtcTicks.TryGetValue(nextKey, out long fetchedTicks) ||
                            fetchedTicks < actionBatchStartTicks)
                        {
                            HydrateSingleNetworkSourceFromDiskCache(
                                src,
                                ResolveNetworkSourceUrl(src),
                                overwriteExistingCollection: true);
                        }
                    }
                }
            }

            if (dynamicNetworkUrlChanged)
            {
                needsRebuild = true;
            }
        }

        bool weatherCityChanged = weatherCityBefore is not null &&
            !string.Equals(weatherCityBefore, ResolveEffectiveWeatherCity(), StringComparison.OrdinalIgnoreCase);

        if (needsSave)
        {
            await PersistStoreAsync();
        }

        if (needsRebuild)
        {
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (needsLiveRefreshOnly)
        {
            RefreshCapsuleProjection(bumpLiveState: true);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }

        if (dynamicNetworkUrlChanged && !_isDisposed)
        {
            _ = RefreshDueNetworkSourcesAsync();
        }

        if (weatherCityChanged && !_isDisposed)
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }

    public async Task UpsertCountdownEventAsync(
        string? eventId,
        string title,
        string emoji,
        string dateIso,
        string calendar,
        string repeat,
        bool pinned,
        bool isLunarLeap = false)
    {
        if (_isDisposed)
        {
            return;
        }

        CustomWidgetCountdownEngine.UpsertEvent(
            _collections,
            eventId,
            title,
            emoji,
            dateIso,
            calendar,
            repeat,
            pinned,
            isLunarLeap,
            DateTimeOffset.Now,
            _localizationService.CurrentCultureName);

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task PinCountdownEventAsync(string eventId)
    {
        if (_isDisposed || !CustomWidgetCountdownEngine.PinEvent(_collections, eventId))
        {
            return;
        }

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteCountdownEventAsync(string eventId)
    {
        if (_isDisposed || !CustomWidgetCountdownEngine.DeleteEvent(_collections, eventId))
        {
            return;
        }

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task SelectAiProviderAsync(string providerId)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        _state[CustomWidgetAiMonitorEngine.KeySelectedProvider] = providerId.Trim();
        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task ToggleAiTrendMetricAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        string cur = _state.TryGetValue(CustomWidgetAiMonitorEngine.KeyTrendMetric, out string? v) ? v : "tokens";
        _state[CustomWidgetAiMonitorEngine.KeyTrendMetric] =
            string.Equals(cur, "cost", StringComparison.OrdinalIgnoreCase) ? "tokens" : "cost";
        await PersistStoreAsync();
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
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
        if (ok && !_isDisposed)
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

    public async Task<bool> ImportDroppedFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (!AcceptsFileDrops || filePaths.Count == 0)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> actions = _manifest.OnDrop?.Actions is { Count: > 0 } rootDrop
            ? rootDrop
            : FindFirstDropZoneActions(_manifest.View);
        if (actions.Count == 0)
        {
            return false;
        }

        bool usePortableStore = _manifest.OnDrop is not null;
        string mode = EffectiveDropMode;
        int importedCount = 0;

        foreach (string rawPath in filePaths)
        {
            CustomWidgetDropFileContext? dropCtx = await BuildDropFileContextAsync(rawPath, usePortableStore);
            if (dropCtx is null)
            {
                continue;
            }

            try
            {
                CustomWidgetEvaluationContext evalContext = CreateEvaluationContext().WithDrop(dropCtx);
                await ExecuteActionsAsync(actions, evalContext);
                importedCount++;
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget] Failed to import dropped file '{rawPath}': {ex.Message}");
            }
        }

        if (importedCount > 0 && usePortableStore)
        {
            RaiseFeedback(
                mode == "copy"
                    ? _localizationService.Format("CustomWidget.Toast.FilesCopied", importedCount)
                    : _localizationService.Format("CustomWidget.Toast.FilesLinked", importedCount),
                WidgetFeedbackSeverity.Info);
        }

        return importedCount > 0;
    }

    public async Task<bool> HandleDropZoneFilesAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<CustomWidgetActionSpec> nodeDropActions)
    {
        if (_isDisposed || filePaths.Count == 0)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> effectiveActions = nodeDropActions.Count > 0
            ? nodeDropActions
            : (_manifest.OnDrop?.Actions ?? []);
        if (effectiveActions.Count == 0)
        {
            return false;
        }

        bool usePortableStore = nodeDropActions.Count == 0 && _manifest.OnDrop is not null;
        int processed = 0;
        foreach (string rawPath in filePaths)
        {
            CustomWidgetDropFileContext? dropCtx = await BuildDropFileContextAsync(rawPath, usePortableStore);
            if (dropCtx is null)
            {
                continue;
            }

            CustomWidgetEvaluationContext evalContext = CreateEvaluationContext().WithDrop(dropCtx);
            await ExecuteActionsAsync(effectiveActions, evalContext);
            processed++;
        }

        return processed > 0;
    }

    private async Task<CustomWidgetDropFileContext?> BuildDropFileContextAsync(
        string? rawPath,
        bool usePortableStoreMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath) ||
            (!File.Exists(rawPath) && !Directory.Exists(rawPath)))
        {
            return null;
        }

        try
        {
            string mode = EffectiveDropMode;
            (string storedPath, string fullPath, string fileName, string actualMode) =
                (_isPreviewOnly || !usePortableStoreMode)
                    ? (
                        Path.GetFullPath(rawPath),
                        Path.GetFullPath(rawPath),
                        Path.GetFileName(rawPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        "link")
                    : await _store.ResolveDroppedFileWithPortablePathAsync(rawPath, mode);

            string ext = Path.GetExtension(fileName).TrimStart('.');
            string sizeText = string.Empty;
            string extractedText = string.Empty;
            if (File.Exists(fullPath))
            {
                long len = new FileInfo(fullPath).Length;
                sizeText = len >= 1024 * 1024
                    ? $"{(len / (1024.0 * 1024.0)):0.1} MB"
                    : $"{Math.Max(1, len / 1024)} KB";
                extractedText = await TryReadSafeDropTextAsync(fullPath, ext);
            }

            return new CustomWidgetDropFileContext
            {
                Name = fileName,
                Path = storedPath,
                Extension = ext,
                SizeText = sizeText,
                Mode = actualMode,
                Text = extractedText
            };
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Failed to inspect dropped/picked file '{rawPath}': {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<CustomWidgetActionSpec> FindFirstDropZoneActions(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "DropZone", StringComparison.OrdinalIgnoreCase) &&
            node.OnDropActions.Count > 0)
        {
            return node.OnDropActions;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            IReadOnlyList<CustomWidgetActionSpec> found = FindFirstDropZoneActions(child);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    private static bool NodeTreeContainsType(CustomWidgetNodeSpec node, string targetType)
    {
        if (string.Equals(node.Type, targetType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (node.ItemTemplate is not null && NodeTreeContainsType(node.ItemTemplate, targetType))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeContainsType(child, targetType))
            {
                return true;
            }
        }

        return false;
    }

    public const int MaxDropTextChars = 8192;
    private const int MaxDropReadBytes = 24576;

    private static readonly HashSet<string> SupportedDropTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "csv", "tsv", "json", "log", "yml", "yaml", "xml", "ini", "toml"
    };

    public static bool IsSupportedDropTextExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string clean = extension.Trim().TrimStart('.');
        return SupportedDropTextExtensions.Contains(clean);
    }

    public static async Task<string> TryReadSafeDropTextAsync(string fullPath, string? extension)
    {
        string ext = string.IsNullOrWhiteSpace(extension)
            ? Path.GetExtension(fullPath)
            : extension;
        if (!IsSupportedDropTextExtension(ext) || !File.Exists(fullPath))
        {
            return string.Empty;
        }

        try
        {
            byte[] buffer = new byte[MaxDropReadBytes];
            int totalRead = 0;
            await using (var fs = new FileStream(
                             fullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 4096,
                             useAsync: true))
            {
                while (totalRead < buffer.Length)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
                    if (n <= 0)
                    {
                        break;
                    }

                    totalRead += n;
                }
            }

            if (totalRead == 0)
            {
                return string.Empty;
            }

            ReadOnlySpan<byte> span = buffer.AsSpan(0, totalRead);
            string decoded;
            if (span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE)
            {
                int evenLen = (span.Length - 2) & ~1;
                decoded = Encoding.Unicode.GetString(span.Slice(2, evenLen));
            }
            else if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF)
            {
                int evenLen = (span.Length - 2) & ~1;
                decoded = Encoding.BigEndianUnicode.GetString(span.Slice(2, evenLen));
            }
            else
            {
                if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                {
                    span = span[3..];
                }

                // Reject binary payloads masquerading under text extensions.
                if (span.IndexOf((byte)0) >= 0)
                {
                    return string.Empty;
                }

                decoded = Encoding.UTF8.GetString(span);
            }

            string normalized = decoded
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            return normalized.Length > MaxDropTextChars
                ? normalized[..MaxDropTextChars]
                : normalized;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(bool Succeeded, bool MutatedState)> ExecuteAiRunActionAsync(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string rawPrompt = CustomWidgetExpressionEvaluator.Interpolate(
            action.Prompt ?? action.Text ?? action.Value,
            evalContext).Trim();

        if (string.IsNullOrWhiteSpace(rawPrompt))
        {
            RaiseFeedback(
                _localizationService.T("CustomWidget.AiRun.Error.EmptyPrompt"),
                WidgetFeedbackSeverity.Warning);
            return (false, false);
        }

        if (rawPrompt.Length > DeskBoxAiService.MaxPromptChars)
        {
            rawPrompt = rawPrompt[..DeskBoxAiService.MaxPromptChars];
        }

        if (Interlocked.CompareExchange(ref _aiActionInFlight, 1, 0) != 0)
        {
            RaiseFeedback(
                _localizationService.T("CustomWidget.AiRun.Busy"),
                WidgetFeedbackSeverity.Info);
            return (false, false);
        }

        try
        {
            string? providerId = _aiService.ResolveActiveProviderId(_preferredAiProviderResolver());
            if (string.IsNullOrWhiteSpace(providerId))
            {
                string noKeyMsg = _localizationService.T("CustomWidget.AiRun.Error.NoApiKey");
                _state["aiStatus"] = "error";
                _state["aiError"] = noKeyMsg;
                RaiseFeedback(noKeyMsg, WidgetFeedbackSeverity.Warning);
                return (false, true);
            }

            _state["aiStatus"] = "running";
            _state["aiError"] = string.Empty;
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);

            CancellationTokenSource? oldCts = Interlocked.Exchange(ref _aiActionCts, null);
            if (oldCts is not null)
            {
                try
                {
                    oldCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                oldCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _aiActionCts = cts;

            string systemPrompt = BuildAiRunSystemPrompt(action, _localizationService.CurrentCultureName);
            AiStructuredCompletionResult result = await _aiService.CompleteJsonAsync(
                userPrompt: rawPrompt,
                systemPrompt: systemPrompt,
                providerIdOverride: providerId,
                profile: AiTaskProfile.FastMicroTask,
                cancellationToken: cts.Token);

            if (_isDisposed || cts.IsCancellationRequested)
            {
                return (false, false);
            }

            if (!result.Success)
            {
                string err = result.ErrorMessage ?? _localizationService.T("CustomWidget.AiConfig.TestFailed");
                _state["aiStatus"] = "error";
                _state["aiError"] = err;
                RaiseFeedback(err, WidgetFeedbackSeverity.Warning);
                return (false, true);
            }

            if (!TryApplyAiRunStructuredOutput(action, result.Json, out string? applyError))
            {
                string err = applyError ?? _localizationService.T("CustomWidget.AiApi.Error.NoJsonBlockReturned");
                _state["aiStatus"] = "error";
                _state["aiError"] = err;
                RaiseFeedback(err, WidgetFeedbackSeverity.Warning);
                return (false, true);
            }

            _state["aiStatus"] = "idle";
            _state["aiError"] = string.Empty;
            _state["aiUpdatedAt"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            return (true, true);
        }
        catch (OperationCanceledException)
        {
            return (false, false);
        }
        catch (Exception ex)
        {
            string err = ex.Message;
            _state["aiStatus"] = "error";
            _state["aiError"] = err;
            RaiseFeedback(err, WidgetFeedbackSeverity.Warning);
            return (false, true);
        }
        finally
        {
            Interlocked.Exchange(ref _aiActionInFlight, 0);
        }
    }

    internal static string BuildAiRunSystemPrompt(CustomWidgetActionSpec action, string cultureName)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("You are DeskBox's fast structured micro-task engine running inside a desktop widget.");
        sb.AppendLine("Return ONLY a single valid JSON object. Do NOT wrap in Markdown fences, do NOT explain, and do NOT emit <think> tags.");
        sb.Append("Default output language (unless user input specifies otherwise): ")
          .AppendLine(string.IsNullOrWhiteSpace(cultureName) ? "zh-CN" : cultureName);

        var topFields = new List<string>();
        if (action.StateMap is { Count: > 0 })
        {
            foreach ((string stateKey, string jsonPath) in action.StateMap)
            {
                string prop = ExtractTopPropertyFromJsonPath(jsonPath, fallback: stateKey);
                if (!string.IsNullOrWhiteSpace(prop) && !topFields.Contains(prop, StringComparer.OrdinalIgnoreCase))
                {
                    topFields.Add(prop);
                }
            }
        }

        if (topFields.Count > 0)
        {
            sb.Append("Required top-level scalar fields in your JSON object: ")
              .AppendLine(string.Join(", ", topFields.Select(static f => $"\"{f}\"")));
        }

        if (!string.IsNullOrWhiteSpace(action.Collection))
        {
            string itemsProp = ExtractTopPropertyFromJsonPath(action.ItemsPath, fallback: "items");
            var itemKeys = new List<string>();
            if (action.FieldMap is { Count: > 0 })
            {
                foreach ((string fieldName, string relPath) in action.FieldMap)
                {
                    string prop = ExtractTopPropertyFromJsonPath(relPath, fallback: fieldName);
                    if (!string.IsNullOrWhiteSpace(prop) && !itemKeys.Contains(prop, StringComparer.OrdinalIgnoreCase))
                    {
                        itemKeys.Add(prop);
                    }
                }
            }

            if (itemKeys.Count == 0)
            {
                itemKeys.Add("title");
                itemKeys.Add("subtitle");
            }

            int maxItems = Math.Clamp(action.MaxItems, 1, 50);
            sb.Append($"Include a JSON array property \"{itemsProp}\" (1 to {maxItems} items). ")
              .Append("Each item object MUST contain fields: ")
              .Append(string.Join(", ", itemKeys.Select(static k => $"\"{k}\"")))
              .AppendLine(".");
            if (itemKeys.Contains("done", StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine("For boolean-like status fields such as \"done\", output \"false\" for pending items.");
            }
        }

        return sb.ToString();
    }

    private static string ExtractTopPropertyFromJsonPath(string? jsonPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return fallback;
        }

        string trimmed = jsonPath.Trim();
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..].TrimStart('.');
        }

        int dot = trimmed.IndexOf('.');
        if (dot > 0)
        {
            trimmed = trimmed[..dot];
        }

        int bracket = trimmed.IndexOf('[');
        if (bracket > 0)
        {
            trimmed = trimmed[..bracket];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private bool TryApplyAiRunStructuredOutput(
        CustomWidgetActionSpec action,
        string rawJson,
        out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string? effectiveItemsPath = action.ItemsPath;
            if (!string.IsNullOrWhiteSpace(action.Collection) && string.IsNullOrWhiteSpace(effectiveItemsPath))
            {
                using JsonDocument probeDoc = JsonDocument.Parse(
                    rawJson,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (probeDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string[] preferredArrays = ["items", "tasks", "records", "list", "data"];
                    foreach (string candidate in preferredArrays)
                    {
                        if (CustomWidgetNetworkSourceEngine.TryResolveJsonPath(
                                probeDoc.RootElement,
                                "$." + candidate,
                                out JsonElement arr) &&
                            arr.ValueKind == JsonValueKind.Array)
                        {
                            effectiveItemsPath = "$." + candidate;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(effectiveItemsPath))
                    {
                        foreach (JsonProperty prop in probeDoc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                effectiveItemsPath = "$." + prop.Name;
                                break;
                            }
                        }
                    }
                }
            }

            var syntheticSpec = new CustomWidgetSourceSpec(
                Type: "httpJson",
                TargetCollection: action.Collection,
                ItemsPath: effectiveItemsPath,
                FieldMap: action.FieldMap,
                StateMap: action.StateMap,
                MaxItems: Math.Clamp(action.MaxItems, 1, 50));

            (Dictionary<string, string> stateUpdates, List<CustomWidgetRecord> extractedItems) =
                CustomWidgetNetworkSourceEngine.ExtractHttpJson(rawJson, syntheticSpec);

            foreach ((string rawKey, string val) in stateUpdates)
            {
                string key = NormalizeStateKey(rawKey);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _state[key] = val;
                }
            }

            if (!string.IsNullOrWhiteSpace(action.Collection))
            {
                string colName = action.Collection.Trim();
                if (!_collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                {
                    list = [];
                    _collections[colName] = list;
                }

                string mode = (action.Mode ?? "prepend").Trim().ToLowerInvariant();
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                bool hasExplicitIdMapping = action.FieldMap is not null && action.FieldMap.ContainsKey("id");

                for (int i = 0; i < extractedItems.Count; i++)
                {
                    CustomWidgetRecord rec = extractedItems[i];
                    if (!hasExplicitIdMapping)
                    {
                        rec.Id = Guid.NewGuid().ToString("N")[..12];
                    }

                    rec.CreatedAtUtcTicks = nowTicks - (i * TimeSpan.TicksPerMillisecond);
                    rec.UpdatedAtUtcTicks = nowTicks;

                    if (action.FieldMap is not null &&
                        action.FieldMap.ContainsKey("done") &&
                        !rec.Fields.ContainsKey("done"))
                    {
                        rec.Fields["done"] = "false";
                    }
                }

                if (mode == "replace")
                {
                    list.Clear();
                    list.AddRange(extractedItems);
                }
                else if (mode == "append")
                {
                    list.AddRange(extractedItems);
                }
                else
                {
                    list.InsertRange(0, extractedItems);
                }

                int maxItems = _manifest.Collections.TryGetValue(colName, out CustomWidgetCollectionSchema? schema)
                    ? schema.MaxItems
                    : 200;
                while (list.Count > maxItems)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void OpenFileWithDefaultApp(string? path) =>
        _ = OpenFileWithDefaultAppAsync(path);

    public async Task OpenFileWithDefaultAppAsync(string? path)
    {
        string resolvedPath = _store.ResolveRuntimePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath) ||
            (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath)))
        {
            RaiseFeedback(
                _localizationService.T("CustomWidget.Toast.FileNotFound"),
                WidgetFeedbackSeverity.Warning);
            return;
        }

        if (!_store.IsInsideManagedAttachments(resolvedPath))
        {
            bool approved = await EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "file.open",
                trustToken: $"path:{resolvedPath.ToLowerInvariant()}",
                targetDisplay: resolvedPath,
                requireConsentForAllOrigins: CustomWidgetUtilityEngine.IsExecutableOrScriptPath(resolvedPath));
            if (!approved)
            {
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(resolvedPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Failed to open file '{resolvedPath}': {ex.Message}");
        }
    }

    public void RevealFileInExplorer(string? path) =>
        _ = RevealFileInExplorerAsync(path);

    public async Task RevealFileInExplorerAsync(string? path)
    {
        string resolvedPath = _store.ResolveRuntimePath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath) ||
            (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath)))
        {
            RaiseFeedback(
                _localizationService.T("CustomWidget.Toast.FileNotFound"),
                WidgetFeedbackSeverity.Warning);
            return;
        }

        if (!_store.IsInsideManagedAttachments(resolvedPath))
        {
            bool approved = await EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "file.reveal",
                trustToken: $"path:{resolvedPath.ToLowerInvariant()}",
                targetDisplay: resolvedPath);
            if (!approved)
            {
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{resolvedPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Failed to reveal file '{resolvedPath}': {ex.Message}");
        }
    }

    internal async Task<bool> EnsureCommunityExternalTargetApprovedAsync(
        string actionKind,
        string trustToken,
        string targetDisplay,
        bool requireConsentForAllOrigins = false)
    {
        if (!requireConsentForAllOrigins &&
            !string.Equals(_manifest.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsExternalTargetTrusted(trustToken))
        {
            return true;
        }

        if (ExternalActionConsentPrompt is null)
        {
            return false;
        }

        bool approved = await ExternalActionConsentPrompt(new CustomWidgetSecurityPrompt(
            actionKind,
            targetDisplay,
            string.IsNullOrWhiteSpace(_manifest.Name) ? Config.Name : _manifest.ResolveLocalizedName(ActiveCultureName)));
        if (!approved)
        {
            return false;
        }

        MarkExternalTargetTrusted(trustToken);
        await PersistStoreAsync();
        return true;
    }

    private bool IsExternalTargetTrusted(string trustToken)
    {
        if (!_settings.TryGetValue(TrustedExternalTargetsSettingKey, out string? raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (string item in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(item, trustToken, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void MarkExternalTargetTrusted(string trustToken)
    {
        if (IsExternalTargetTrusted(trustToken))
        {
            return;
        }

        if (_settings.TryGetValue(TrustedExternalTargetsSettingKey, out string? existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            _settings[TrustedExternalTargetsSettingKey] = $"{existing}|{trustToken}";
        }
        else
        {
            _settings[TrustedExternalTargetsSettingKey] = trustToken;
        }
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        bool wasVisible = _isWindowVisible;
        _isWindowVisible = visible;
        UpdateSourceSubscriptions();
        if (visible && !wasVisible && !_isDisposed && ManifestUsesPomodoro())
        {
            RefreshCapsuleProjection(bumpLiveState: false);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }
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

        if (string.Equals(_manifest.Id, "deskbox.sys-pulse", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Config.Name, "性能脉冲", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_manifest.Name))
        {
            Config.Name = _manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);
        }

        TrySyncAutoLocalizedTitle(notifyIdentityChange: false);
        HydrateFromPayload(payload, previousSettings);
        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
    }

    private void HydrateFromPayload(
        CustomWidgetStorePayload payload,
        IReadOnlyDictionary<string, string>? previousSettings = null)
    {
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

        _state.Clear();
        foreach ((string k, string v) in _manifest.State)
        {
            _state[k] = v;
        }
        foreach ((string k, string v) in payload.State)
        {
            _state[k] = v;
        }

        _collections.Clear();
        foreach ((string colName, _) in _manifest.Collections)
        {
            _collections[colName] = [];
        }
        foreach ((string colName, List<CustomWidgetRecord> records) in payload.Collections)
        {
            _collections[colName] = new List<CustomWidgetRecord>(records);
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
            bool settledExpired = CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(
                _state,
                _settings,
                _collections,
                DateTimeOffset.Now,
                out CustomWidgetPomodoroCompletionNotice? notice,
                _localizationService.CurrentCultureName);
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
            _lastCountdownDay = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime);
            if (CustomWidgetCountdownEngine.EnsureSeeded(
                    _state,
                    _collections,
                    DateTimeOffset.Now,
                    _localizationService.CurrentCultureName))
            {
                _ = PersistStoreAsync();
            }
        }

        if (ManifestUsesStorageDrives())
        {
            RefreshStorageDrivesNow(triggerRebuild: false);
        }

        if (ManifestUsesNetworkSources())
        {
            HydrateNetworkSourcesFromDiskCache();
        }
    }

    public async Task ApplyExternalDataPushAsync(
        IReadOnlyDictionary<string, string>? stateUpdates,
        string? collectionName = null,
        string? collectionMode = null,
        IReadOnlyList<Dictionary<string, string>>? records = null)
    {
        if (_isDisposed)
        {
            return;
        }

        bool changed = false;
        if (stateUpdates is { Count: > 0 })
        {
            foreach ((string rawKey, string val) in stateUpdates)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    continue;
                }

                string key = NormalizeStateKey(rawKey.Trim());
                _state[key] = val ?? string.Empty;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            string colName = collectionName.Trim();
            if (!_collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
            {
                list = [];
                _collections[colName] = list;
            }

            string mode = (collectionMode ?? "prepend").Trim().ToLowerInvariant();
            if (mode is "clear" or "replace")
            {
                if (list.Count > 0)
                {
                    list.Clear();
                    changed = true;
                }
            }

            if (records is { Count: > 0 } && mode != "clear")
            {
                for (int i = 0; i < records.Count; i++)
                {
                    Dictionary<string, string> fields = records[i];
                    string? explicitId = fields.TryGetValue("id", out string? idVal) && !string.IsNullOrWhiteSpace(idVal)
                        ? idVal.Trim()
                        : null;

                    if (mode == "upsert" && explicitId is not null)
                    {
                        CustomWidgetRecord? existing = list.FirstOrDefault(r =>
                            string.Equals(r.Id, explicitId, StringComparison.Ordinal));
                        if (existing is not null)
                        {
                            foreach ((string fk, string fv) in fields)
                            {
                                if (!string.Equals(fk, "id", StringComparison.Ordinal))
                                {
                                    existing.Fields[fk] = fv ?? string.Empty;
                                }
                            }
                            existing.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                            changed = true;
                            continue;
                        }
                    }

                    long recordNowTicks = DateTimeOffset.UtcNow.UtcTicks;
                    var rec = new CustomWidgetRecord
                    {
                        Id = explicitId ?? Guid.NewGuid().ToString("N")[..10],
                        CreatedAtUtcTicks = recordNowTicks,
                        UpdatedAtUtcTicks = recordNowTicks
                    };
                    foreach ((string fk, string fv) in fields)
                    {
                        if (!string.Equals(fk, "id", StringComparison.Ordinal))
                        {
                            rec.Fields[fk] = fv ?? string.Empty;
                        }
                    }

                    if (mode is "append" or "replace")
                    {
                        list.Add(rec);
                    }
                    else
                    {
                        list.Insert(i < list.Count ? i : list.Count, rec);
                    }
                    changed = true;
                }

                int maxItems = _manifest.Collections.TryGetValue(colName, out CustomWidgetCollectionSchema? schema)
                    ? schema.MaxItems
                    : 200;
                while (list.Count > maxItems)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }
        }

        if (!changed)
        {
            return;
        }

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistStoreAsync()
    {
        if (_isPreviewOnly)
        {
            return;
        }

        var safeSettings = new Dictionary<string, string>(_settings, StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in _manifest.Settings)
        {
            if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                safeSettings.Remove(def.Key);
            }
        }

        var payload = new CustomWidgetStorePayload
        {
            Settings = safeSettings,
            State = new Dictionary<string, string>(_state, StringComparer.Ordinal),
            Collections = _collections.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<CustomWidgetRecord>(kvp.Value),
                StringComparer.Ordinal)
        };
        Interlocked.Exchange(ref _lastOwnSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        await _store.SaveAsync(payload, origin: this);
        Interlocked.Exchange(ref _lastOwnSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private (
        string Title,
        string Summary,
        string Glyph,
        string Badge,
        bool BadgeIsWarning,
        double? Progress,
        bool EnableBounce) ComputeCapsuleProjectionValues()
    {
        CustomWidgetEvaluationContext ctx = CreateEvaluationContext();
        CustomWidgetCapsuleSpec? cap = _manifest.Capsule;

        if (cap is null)
        {
            return (
                Config.Name,
                _manifest.ResolveLocalizedDescription(ActiveCultureName),
                _manifest.Icon,
                string.Empty,
                false,
                null,
                false);
        }

        string title = CustomWidgetExpressionEvaluator.Interpolate(cap.Title, ctx);
        string resolvedTitle = string.IsNullOrWhiteSpace(title) ? Config.Name : title;
        string summary = CustomWidgetExpressionEvaluator.Interpolate(cap.Summary, ctx);
        string resolvedGlyph = CustomWidgetExpressionEvaluator.Interpolate(cap.Glyph, ctx);
        string glyph = string.IsNullOrWhiteSpace(resolvedGlyph) ? _manifest.Icon : resolvedGlyph;
        string badgeRaw = CustomWidgetExpressionEvaluator.Interpolate(cap.Badge, ctx);
        string badge = badgeRaw == "0" ? string.Empty : badgeRaw;
        bool badgeWarn = CustomWidgetExpressionEvaluator.EvaluateCondition(cap.BadgeIsWarning, ctx);
        double? rawPct = CustomWidgetExpressionEvaluator.EvaluateDouble(cap.Progress, ctx);
        double? progress = rawPct.HasValue
            ? Math.Clamp(rawPct.Value / 100.0, 0.0, 1.0)
            : null;
        return (resolvedTitle, summary, glyph, badge, badgeWarn, progress, cap.EnableBounceOnUpdate);
    }

    private void EnsureCapsuleProjectionFresh()
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
            _capsuleEnableBounce
        ) = ComputeCapsuleProjectionValues();
    }

    private void RefreshCapsuleProjection(bool bumpLiveState, bool forceNotify = false)
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
        var (title, summary, glyph, badge, badgeWarn, progress, enableBounce) = ComputeCapsuleProjectionValues();

        bool titleChanged = !string.Equals(_capsuleTitle, title, StringComparison.Ordinal);
        bool summaryChanged = !string.Equals(_capsuleSummary, summary, StringComparison.Ordinal);
        bool glyphChanged = !string.Equals(_capsuleGlyph, glyph, StringComparison.Ordinal);
        bool badgeChanged = !string.Equals(_capsuleBadge, badge, StringComparison.Ordinal);
        bool badgeWarnChanged = _capsuleBadgeIsWarning != badgeWarn;
        bool progressChanged = _capsuleProgress != progress;
        bool bounceChanged = _capsuleEnableBounce != enableBounce;

        _capsuleTitle = title;
        _capsuleSummary = summary;
        _capsuleGlyph = glyph;
        _capsuleBadge = badge;
        _capsuleBadgeIsWarning = badgeWarn;
        _capsuleProgress = progress;
        _capsuleEnableBounce = enableBounce;

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
        if (bounceChanged)
        {
            OnPropertyChanged(nameof(CapsuleEnableBounce));
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

        string? currentEmojiOverride = WidgetTitleIconCustomization.GetEmojiOverride(Config);
        if (!Config.Metadata.ContainsKey(WidgetTitleIconCustomization.PresetIconMigratedMetadataKey) &&
            CustomWidgetTemplates.IsLegacyOfficialDefaultEmoji(_manifest.Id, currentEmojiOverride))
        {
            WidgetTitleIconCustomization.Clear(Config);
            Config.Metadata[WidgetTitleIconCustomization.PresetIconMigratedMetadataKey] = "1";
            ManifestEmojiIcon = string.Empty;
            OnPropertyChanged(nameof(TitleGlyph));
            OnPropertyChanged(nameof(TitleIconKind));
            OnPropertyChanged(nameof(ManifestEmojiIcon));
            return;
        }

        string? effectiveEmoji = CustomWidgetTemplates.IsEmojiIcon(_manifest.Icon) &&
                                 !CustomWidgetTemplates.IsLegacyOfficialDefaultEmoji(_manifest.Id, _manifest.Icon)
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

    private static bool CapsuleReferencesToken(CustomWidgetCapsuleSpec? capsule, string tokenPrefix)
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
               HasToken(capsule.Progress);
    }

    private static bool CapsuleReferencesSystemMetrics(CustomWidgetCapsuleSpec? capsule) =>
        CapsuleReferencesToken(capsule, "sys.") ||
        CapsuleReferencesToken(capsule, "time.");

    private static bool NodeTreeReferencesGpu(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "GpuCard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static bool HasGpu(string? s) =>
            !string.IsNullOrEmpty(s) && s.Contains("sys.gpu.", StringComparison.OrdinalIgnoreCase);

        if (HasGpu(node.Text) || HasGpu(node.Title) || HasGpu(node.Subtitle) ||
            HasGpu(node.Value) || HasGpu(node.Progress) || HasGpu(node.Sparkline))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeReferencesGpu(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NodeTreeReferencesProcesses(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "ProcessList", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeReferencesProcesses(child))
            {
                return true;
            }
        }

        return false;
    }

    private CustomWidgetMetricsSubscriptionOptions BuildMetricsSubscriptionOptions()
    {
        int defaultInterval = _manifest.Sources
            .FirstOrDefault(s => string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase))
            ?.IntervalMs ?? 2000;

        int intervalMs = defaultInterval;
        if (_settings.TryGetValue("refreshRate", out string? rateRaw) &&
            int.TryParse(rateRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate))
        {
            intervalMs = Math.Clamp(parsedRate, 500, 60000);
        }

        bool capsuleUsesGpu = CapsuleReferencesToken(_manifest.Capsule, "sys.gpu.");
        if (_isCompactCollapsed)
        {
            return new CustomWidgetMetricsSubscriptionOptions(
                IntervalMs: intervalMs,
                EnableGpu: capsuleUsesGpu,
                EnableProcesses: false);
        }

        CustomWidgetSettingDefinition? moduleDef = _manifest.Settings.FirstOrDefault(
            static d => string.Equals(d.Type, "moduleList", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Key, "modules", StringComparison.OrdinalIgnoreCase));

        bool enableGpu;
        bool enableProcesses;
        if (moduleDef is not null)
        {
            _settings.TryGetValue(moduleDef.Key, out string? rawModules);
            bool hasGpuOption = moduleDef.Options.Any(static o =>
                string.Equals(o.Value, "gpu", StringComparison.OrdinalIgnoreCase));
            bool hasProcOption = moduleDef.Options.Any(static o =>
                string.Equals(o.Value, "processes", StringComparison.OrdinalIgnoreCase));

            enableGpu = capsuleUsesGpu ||
                        (hasGpuOption
                            ? CustomWidgetModuleListHelper.IsModuleEnabled(rawModules, "gpu", moduleDef.Options)
                            : NodeTreeReferencesGpu(_manifest.View));
            enableProcesses = hasProcOption
                ? CustomWidgetModuleListHelper.IsModuleEnabled(rawModules, "processes", moduleDef.Options)
                : NodeTreeReferencesProcesses(_manifest.View);
        }
        else
        {
            enableGpu = capsuleUsesGpu || NodeTreeReferencesGpu(_manifest.View);
            enableProcesses = NodeTreeReferencesProcesses(_manifest.View);
        }

        return new CustomWidgetMetricsSubscriptionOptions(intervalMs, enableGpu, enableProcesses);
    }

    private void UpdateSourceSubscriptions()
    {
        bool activeWhileCollapsed = !_isCompactCollapsed || CapsuleReferencesSystemMetrics(_manifest.Capsule);
        bool wantsMetrics = !_isDisposed &&
                            _isWindowVisible &&
                            activeWhileCollapsed &&
                            _manifest.Sources.Any(s =>
                                string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase));

        if (wantsMetrics)
        {
            CustomWidgetMetricsSubscriptionOptions options = BuildMetricsSubscriptionOptions();
            if (!_isSubscribedToSystemMetrics)
            {
                _isSubscribedToSystemMetrics = true;
                CustomWidgetSystemMetricsSource.Shared.MetricsUpdated += OnSystemMetricsUpdated;
                CustomWidgetSystemMetricsSource.Shared.Subscribe(this, options);
                _systemMetrics = CustomWidgetSystemMetricsSource.Shared.Latest;
                RefreshCapsuleProjection(bumpLiveState: false);
                MetricsRefreshed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                CustomWidgetSystemMetricsSource.Shared.Subscribe(this, options);
            }
        }
        else if (_isSubscribedToSystemMetrics)
        {
            _isSubscribedToSystemMetrics = false;
            CustomWidgetSystemMetricsSource.Shared.MetricsUpdated -= OnSystemMetricsUpdated;
            CustomWidgetSystemMetricsSource.Shared.Unsubscribe(this);
        }

        UpdatePomodoroTimerSubscription();
        UpdateCountdownTimerSubscription();
        UpdateCalendarSourceSubscription();
        UpdateWeatherSourceSubscription();
        UpdateDeviceStateSubscription();
        UpdateClipboardSourceSubscription();
        UpdateStorageDrivesSubscription();
        UpdateClockSourceSubscription();
        UpdateAiMonitorSubscription();
        UpdateNetworkSourcesSubscription();
    }

    private bool ManifestUsesNetworkSources() =>
        _manifest.Sources.Any(static s =>
            string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase));

    private bool IsStudioGalleryThumbnailPreview =>
        Config.Id.StartsWith("__studio_template_", StringComparison.Ordinal);

    private string ResolveNetworkSourceUrl(CustomWidgetSourceSpec spec) =>
        CustomWidgetExpressionEvaluator.Interpolate(spec.Url, CreateEvaluationContext()).Trim();

    private IReadOnlyDictionary<string, string>? ResolveNetworkSourceHeaders(CustomWidgetSourceSpec spec)
    {
        if (spec.Headers is not { Count: > 0 })
        {
            return null;
        }

        CustomWidgetEvaluationContext ctx = CreateEvaluationContext();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string k, string v) in spec.Headers)
        {
            if (string.IsNullOrWhiteSpace(k))
            {
                continue;
            }

            string resolved = CustomWidgetExpressionEvaluator.Interpolate(v, ctx).Trim();
            if (!string.IsNullOrEmpty(resolved))
            {
                map[k.Trim()] = resolved;
            }
        }

        return map.Count > 0 ? map : null;
    }

    private bool TryFindMissingReferencedSecretLabel(CustomWidgetSourceSpec spec, out string missingLabel)
    {
        missingLabel = string.Empty;
        static IEnumerable<string> ScanSecretKeys(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr) || !expr.Contains("$secrets.", StringComparison.Ordinal))
            {
                yield break;
            }

            int idx = 0;
            while ((idx = expr.IndexOf("$secrets.", idx, StringComparison.Ordinal)) >= 0)
            {
                int start = idx + "$secrets.".Length;
                int end = start;
                while (end < expr.Length && (char.IsLetterOrDigit(expr[end]) || expr[end] == '_'))
                {
                    end++;
                }

                if (end > start)
                {
                    yield return expr[start..end];
                }

                idx = end;
            }
        }

        IEnumerable<string> candidates = ScanSecretKeys(spec.Url);
        if (spec.Headers is { Count: > 0 })
        {
            foreach (string hv in spec.Headers.Values)
            {
                candidates = candidates.Concat(ScanSecretKeys(hv));
            }
        }

        foreach (string key in candidates)
        {
            if (!_secrets.TryGetValue(key, out string? val) || string.IsNullOrWhiteSpace(val))
            {
                CustomWidgetSettingDefinition? def = _manifest.Settings.FirstOrDefault(
                    d => string.Equals(d.Key, key, StringComparison.Ordinal));
                missingLabel = !string.IsNullOrWhiteSpace(def?.Label) ? def.Label : key;
                return true;
            }
        }

        return false;
    }

    private void HydrateNetworkSourcesFromDiskCache(bool overwriteExistingCollection = false)
    {
        foreach (CustomWidgetSourceSpec spec in _manifest.Sources)
        {
            if (!string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(spec.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryFindMissingReferencedSecretLabel(spec, out _))
            {
                continue;
            }

            string resolvedUrl = ResolveNetworkSourceUrl(spec);
            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                continue;
            }

            HydrateSingleNetworkSourceFromDiskCache(spec, resolvedUrl, overwriteExistingCollection);
        }
    }

    private void HydrateSingleNetworkSourceFromDiskCache(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        bool overwriteExistingCollection)
    {
        IReadOnlyDictionary<string, string>? resolvedHeaders = ResolveNetworkSourceHeaders(spec);
        CustomWidgetNetworkFetchOutcome? cached = _networkSourceEngine.TryLoadCachedOutcome(spec, resolvedUrl, resolvedHeaders);
        if (cached is null)
        {
            return;
        }

        foreach ((string sk, string sv) in cached.StateUpdates)
        {
            _state[sk] = sv;
        }

        if (!string.IsNullOrWhiteSpace(spec.TargetCollection) &&
            cached.Items.Count > 0 &&
            (overwriteExistingCollection ||
             !_collections.TryGetValue(spec.TargetCollection, out List<CustomWidgetRecord>? curList) ||
             curList.Count == 0))
        {
            _collections[spec.TargetCollection] = new List<CustomWidgetRecord>(cached.Items);
        }

        if (cached.UpdatedAtUtcTicks > 0)
        {
            DateTimeOffset localUpdated = new DateTimeOffset(cached.UpdatedAtUtcTicks, TimeSpan.Zero).ToLocalTime();
            _state["sourceUpdatedAt"] = localUpdated.ToString("HH:mm", CultureInfo.InvariantCulture);
            _state["sourceStatus"] = "ok";
            _state["sourceError"] = string.Empty;
        }
    }

    private void UpdateNetworkSourcesSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_diagnostics) &&
                         ManifestUsesNetworkSources();

        if (!shouldRun)
        {
            StopNetworkSourcesPolling();
            return;
        }

        int minIntervalMs = _manifest.Sources
            .Where(static s =>
                string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            .Select(static s => CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(s.IntervalMs))
            .DefaultIfEmpty(CustomWidgetNetworkSecurity.DefaultNetworkSourceIntervalMs)
            .Min();

        if (_dispatcherQueue is not null)
        {
            if (_networkSourceTimer is null)
            {
                _networkSourceTimer = _dispatcherQueue.CreateTimer();
                _networkSourceTimer.IsRepeating = true;
                _networkSourceTimer.Tick += OnNetworkSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(minIntervalMs);
            if (_networkSourceTimer.Interval != interval)
            {
                _networkSourceTimer.Interval = interval;
            }

            if (!_networkSourceTimer.IsRunning)
            {
                _networkSourceTimer.Start();
            }
        }

        // Trigger initial background refresh if any source is due
        _ = RefreshDueNetworkSourcesAsync();
    }

    private void StopNetworkSourcesPolling()
    {
        if (_networkSourceTimer is not null)
        {
            _networkSourceTimer.Stop();
            _networkSourceTimer.Tick -= OnNetworkSourceTimerTick;
            _networkSourceTimer = null;
        }

        if (_networkFetchCts is { } cts)
        {
            _networkFetchCts = null;
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

    private void OnNetworkSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = RefreshDueNetworkSourcesAsync();
    }

    private Task RefreshDueNetworkSourcesAsync()
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        bool anyDue = false;
        foreach (CustomWidgetSourceSpec spec in _manifest.Sources)
        {
            if (!string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(spec.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string key = BuildRuntimeNetworkSourceKey(spec);
            int intervalMs = CustomWidgetNetworkSourceEngine.ClampNetworkIntervalMs(spec.IntervalMs);
            if (!_networkSourceLastFetchUtcTicks.TryGetValue(key, out long lastTicks) ||
                nowTicks - lastTicks >= TimeSpan.FromMilliseconds(intervalMs).Ticks)
            {
                anyDue = true;
                break;
            }
        }

        return anyDue
            ? RefreshNetworkSourcesNowAsync(targetSourceId: null, isManualUserAction: false)
            : Task.CompletedTask;
    }

    public async Task<bool> RefreshNetworkSourcesNowAsync(
        string? targetSourceId = null,
        bool isManualUserAction = false)
    {
        if (_isDisposed || !ManifestUsesNetworkSources())
        {
            return false;
        }

        CustomWidgetSourceSpec[] specs = _manifest.Sources
            .Where(s =>
                (string.Equals(s.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.Type, "httpJson", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(targetSourceId) ||
                 string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (specs.Length == 0)
        {
            return false;
        }

        if (isManualUserAction)
        {
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            long cooldownTicks = TimeSpan.FromSeconds(CustomWidgetNetworkSecurity.ManualRefreshCooldownSeconds).Ticks;
            bool allOnCooldown = true;
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                string key = BuildRuntimeNetworkSourceKey(spec);
                if (!_lastManualRefreshUtcTicksByKey.TryGetValue(key, out long prevManual) ||
                    prevManual <= 0 ||
                    nowTicks - prevManual >= cooldownTicks)
                {
                    allOnCooldown = false;
                    break;
                }
            }

            if (allOnCooldown && Volatile.Read(ref _networkFetchInFlight) == 0)
            {
                RaiseFeedback(
                    _localizationService.T("CustomWidget.Toast.SourceRefreshCooldown"),
                    WidgetFeedbackSeverity.Info);
                return false;
            }

            foreach (CustomWidgetSourceSpec spec in specs)
            {
                _lastManualRefreshUtcTicksByKey[BuildRuntimeNetworkSourceKey(spec)] = nowTicks;
            }
        }

        if (Interlocked.CompareExchange(ref _networkFetchInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 1);
            return false;
        }

        try
        {
            _networkFetchCts?.Dispose();
            var cts = new CancellationTokenSource();
            _networkFetchCts = cts;

            SetNetworkLoadingIndicatorOnUi(isLoading: true);

            bool anyUpdated = false;
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_isDisposed || cts.IsCancellationRequested)
                {
                    break;
                }

                if (TryFindMissingReferencedSecretLabel(spec, out string missingSecretLabel))
                {
                    bool isZh = ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                    string authMsg = isZh
                        ? $"请先配置密钥：{missingSecretLabel}"
                        : $"API secret required: {missingSecretLabel}";
                    ApplyNetworkAuthRequiredOnUi(spec.Id, authMsg, preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    _networkSourceLastFetchUtcTicks[BuildRuntimeNetworkSourceKey(spec)] = DateTimeOffset.UtcNow.UtcTicks;
                    continue;
                }

                string resolvedUrl = ResolveNetworkSourceUrl(spec);
                IReadOnlyDictionary<string, string>? resolvedHeaders = ResolveNetworkSourceHeaders(spec);
                string keyAtStart = BuildRuntimeNetworkSourceKey(spec);
                if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(resolvedUrl, out Uri? uri, out string? urlErr) ||
                    uri is null)
                {
                    ApplyNetworkErrorOnUi(spec.Id, urlErr ?? "Invalid HTTPS URL", preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    continue;
                }

                bool approved = await EnsureCommunityExternalTargetApprovedAsync(
                    actionKind: "network.fetch",
                    trustToken: $"host:{uri.DnsSafeHost.ToLowerInvariant()}",
                    targetDisplay: uri.AbsoluteUri);
                if (!approved)
                {
                    ApplyNetworkErrorOnUi(
                        spec.Id,
                        _localizationService.T("CustomWidget.Error.NetworkSourceUnauthorized"),
                        preserveOkWhenOtherSourceSucceeded: anyUpdated);
                    continue;
                }

                CustomWidgetNetworkFetchOutcome outcome =
                    await _networkSourceEngine.FetchAsync(spec, uri.AbsoluteUri, cts.Token, resolvedHeaders);
                _networkSourceLastFetchUtcTicks[keyAtStart] = DateTimeOffset.UtcNow.UtcTicks;

                if (!string.Equals(BuildRuntimeNetworkSourceKey(spec), keyAtStart, StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 1);
                    continue;
                }

                if (outcome.Succeeded)
                {
                    ApplyNetworkOutcomeOnUi(spec, outcome);
                    anyUpdated = true;
                }
                else
                {
                    ApplyNetworkErrorOnUi(
                        spec.Id,
                        outcome.ErrorMessage ?? "Network fetch failed",
                        preserveOkWhenOtherSourceSucceeded: anyUpdated);
                }
            }

            if (anyUpdated && !_isDisposed)
            {
                await PersistStoreAsync();
                if (isManualUserAction)
                {
                    RaiseFeedback(
                        _localizationService.T("CustomWidget.Toast.SourceRefreshed"),
                        WidgetFeedbackSeverity.Info);
                }
            }

            return anyUpdated;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _networkFetchInFlight, 0);
            if (!_isDisposed && Interlocked.Exchange(ref _pendingNetworkRefreshRequested, 0) == 1)
            {
                _ = RefreshDueNetworkSourcesAsync();
            }
        }
    }

    private string BuildRuntimeNetworkSourceKey(CustomWidgetSourceSpec spec)
    {
        string url = ResolveNetworkSourceUrl(spec);
        IReadOnlyDictionary<string, string>? headers = ResolveNetworkSourceHeaders(spec);
        if (headers is not { Count: > 0 })
        {
            return $"{spec.Id ?? spec.Type}|{url}";
        }

        string headerSig = string.Join(
            "&",
            headers.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static kv => $"{kv.Key.ToLowerInvariant()}={kv.Value}"));
        return $"{spec.Id ?? spec.Type}|{url}|{headerSig.GetHashCode(StringComparison.Ordinal):X8}";
    }

    private void SetNetworkLoadingIndicatorOnUi(bool isLoading)
    {
        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            _state["sourceStatus"] = isLoading ? "loading" : "ok";
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void ApplyNetworkOutcomeOnUi(
        CustomWidgetSourceSpec spec,
        CustomWidgetNetworkFetchOutcome outcome)
    {
        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            foreach ((string sk, string sv) in outcome.StateUpdates)
            {
                _state[sk] = sv;
            }

            if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                int maxItems = _manifest.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
                    ? schema.MaxItems
                    : Math.Clamp(spec.MaxItems, 1, 100);
                _collections[spec.TargetCollection] = outcome.Items.Take(maxItems).ToList();
            }

            DateTimeOffset localUpdated = outcome.UpdatedAtUtcTicks > 0
                ? new DateTimeOffset(outcome.UpdatedAtUtcTicks, TimeSpan.Zero).ToLocalTime()
                : DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                _state[$"sourceStatus.{spec.Id}"] = "ok";
                _state[$"sourceError.{spec.Id}"] = string.Empty;
            }
            _state["sourceStatus"] = "ok";
            _state["sourceError"] = string.Empty;
            _state["sourceUpdatedAt"] = localUpdated.ToString("HH:mm", CultureInfo.InvariantCulture);

            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void ApplyNetworkAuthRequiredOnUi(
        string? sourceId,
        string message,
        bool preserveOkWhenOtherSourceSucceeded = false)
    {
        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                _state[$"sourceStatus.{sourceId}"] = "auth_required";
                _state[$"sourceError.{sourceId}"] = message;
            }

            if (!preserveOkWhenOtherSourceSucceeded)
            {
                _state["sourceStatus"] = "auth_required";
            }

            _state["sourceError"] = message;
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void ApplyNetworkErrorOnUi(
        string? sourceId,
        string errorMessage,
        bool preserveOkWhenOtherSourceSucceeded = false)
    {
        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                _state[$"sourceStatus.{sourceId}"] = "error";
                _state[$"sourceError.{sourceId}"] = errorMessage;
            }

            if (!preserveOkWhenOtherSourceSucceeded)
            {
                _state["sourceStatus"] = "error";
            }

            _state["sourceError"] = !string.IsNullOrWhiteSpace(sourceId)
                ? $"[{sourceId}] {errorMessage}"
                : errorMessage;
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private bool ManifestUsesAiMonitor() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(_manifest.Id, "deskbox.ai-pulse", StringComparison.OrdinalIgnoreCase) ||
        NodeTreeReferencesAiMonitor(_manifest.View);

    private static bool NodeTreeReferencesAiMonitor(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "AiFocusCard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, "AiRadarList", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, "AiTokensCard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, "AiTrendCard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeReferencesAiMonitor(child))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateAiMonitorSubscription()
    {
        bool activeWhileCollapsed = !_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "ai.");
        bool wantsAi = !_isDisposed &&
                       _isWindowVisible &&
                       activeWhileCollapsed &&
                       ManifestUsesAiMonitor();

        if (wantsAi)
        {
            int intervalMs = _manifest.Sources
                .FirstOrDefault(static s => string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase))
                ?.IntervalMs ?? 60_000;
            if (_settings.TryGetValue("refreshIntervalMs", out string? rawRate) &&
                int.TryParse(rawRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate))
            {
                intervalMs = Math.Clamp(parsedRate, 15_000, 900_000);
            }

            if (!_isSubscribedToAiMonitor)
            {
                _isSubscribedToAiMonitor = true;
                CustomWidgetAiMonitorSource.Shared.SnapshotUpdated += OnAiMonitorSnapshotUpdated;
                _aiMonitorSnapshot = CustomWidgetAiMonitorSource.Shared.Latest;
                CustomWidgetAiMonitorSource.Shared.Subscribe(this, intervalMs);
                RefreshCapsuleProjection(bumpLiveState: false);
            }
            else
            {
                CustomWidgetAiMonitorSource.Shared.Subscribe(this, intervalMs);
            }
        }
        else if (_isSubscribedToAiMonitor)
        {
            _isSubscribedToAiMonitor = false;
            CustomWidgetAiMonitorSource.Shared.SnapshotUpdated -= OnAiMonitorSnapshotUpdated;
            CustomWidgetAiMonitorSource.Shared.Unsubscribe(this);
        }
    }

    private void OnAiMonitorSnapshotUpdated(CustomWidgetAiMonitorSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        void ApplyOnUi()
        {
            if (_isDisposed)
            {
                return;
            }

            CustomWidgetAiMonitorSnapshot previous = _aiMonitorSnapshot;
            bool visuallyUnchanged = AreAiMonitorSnapshotsVisuallyEqual(previous, snapshot);
            bool structurallyEqual = AreAiMonitorSnapshotsStructurallyEqual(previous, snapshot, _state);
            _aiMonitorSnapshot = snapshot;
            if (visuallyUnchanged)
            {
                return;
            }

            RefreshCapsuleProjection(bumpLiveState: true);
            if (structurallyEqual)
            {
                MetricsRefreshed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ApplyOnUi);
        }
        else
        {
            ApplyOnUi();
        }
    }

    internal static bool AreAiMonitorSnapshotsStructurallyEqual(
        CustomWidgetAiMonitorSnapshot a,
        CustomWidgetAiMonitorSnapshot b,
        IReadOnlyDictionary<string, string>? state = null)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.ActiveProviders.Count != b.ActiveProviders.Count ||
            a.LocalSummary.Last14Days.Count != b.LocalSummary.Last14Days.Count)
        {
            return false;
        }

        IReadOnlyDictionary<string, string> effectiveState = state ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CustomWidgetAiProviderSnapshot? focusA = CustomWidgetAiMonitorEngine.SelectFocusProvider(a, effectiveState);
        CustomWidgetAiProviderSnapshot? focusB = CustomWidgetAiMonitorEngine.SelectFocusProvider(b, effectiveState);
        if (!string.Equals(focusA?.Id, focusB?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 0; i < a.ActiveProviders.Count; i++)
        {
            CustomWidgetAiProviderSnapshot pa = a.ActiveProviders[i];
            CustomWidgetAiProviderSnapshot pb = b.ActiveProviders[i];
            if (!string.Equals(pa.Id, pb.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pa.Name, pb.Name, StringComparison.Ordinal) ||
                !string.Equals(pa.ShortName, pb.ShortName, StringComparison.Ordinal) ||
                !string.Equals(pa.Monogram, pb.Monogram, StringComparison.Ordinal) ||
                !string.Equals(pa.BrandHex, pb.BrandHex, StringComparison.Ordinal) ||
                pa.BillingKind != pb.BillingKind ||
                (pa.ErrorMessage is not null) != (pb.ErrorMessage is not null) ||
                pa.WalletRemainingPercent.HasValue != pb.WalletRemainingPercent.HasValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreAiMonitorSnapshotsVisuallyEqual(
        CustomWidgetAiMonitorSnapshot a,
        CustomWidgetAiMonitorSnapshot b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.AllProviders.Count != b.AllProviders.Count ||
            a.ActiveProviders.Count != b.ActiveProviders.Count)
        {
            return false;
        }

        for (int i = 0; i < a.AllProviders.Count; i++)
        {
            if (!AreAiProvidersVisuallyEqual(a.AllProviders[i], b.AllProviders[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < a.ActiveProviders.Count; i++)
        {
            if (!string.Equals(a.ActiveProviders[i].Id, b.ActiveProviders[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        CustomWidgetAiLocalSummary la = a.LocalSummary;
        CustomWidgetAiLocalSummary lb = b.LocalSummary;
        if (la.TodayTokens != lb.TodayTokens ||
            Math.Round(la.TodayCostUsd, 2) != Math.Round(lb.TodayCostUsd, 2) ||
            Math.Round(la.CacheHitPercent, 0) != Math.Round(lb.CacheHitPercent, 0) ||
            la.Last30DaysTokens != lb.Last30DaysTokens ||
            Math.Round(la.Last30DaysCostUsd, 2) != Math.Round(lb.Last30DaysCostUsd, 2) ||
            la.DetectedClaudeFiles != lb.DetectedClaudeFiles ||
            la.DetectedCodexFiles != lb.DetectedCodexFiles ||
            la.Last14Days.Count != lb.Last14Days.Count)
        {
            return false;
        }

        for (int i = 0; i < la.Last14Days.Count; i++)
        {
            CustomWidgetAiDailyBucket ba = la.Last14Days[i];
            CustomWidgetAiDailyBucket bb = lb.Last14Days[i];
            if (ba.Date != bb.Date ||
                ba.TotalTokens != bb.TotalTokens ||
                Math.Round(ba.CostUsd, 2) != Math.Round(bb.CostUsd, 2))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreAiProvidersVisuallyEqual(
        CustomWidgetAiProviderSnapshot a,
        CustomWidgetAiProviderSnapshot b)
    {
        if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
            a.BillingKind != b.BillingKind ||
            a.IsConfigured != b.IsConfigured ||
            a.HasLiveData != b.HasLiveData ||
            !string.Equals(a.PlanBadge, b.PlanBadge, StringComparison.Ordinal) ||
            a.Health != b.Health ||
            !AreAiWindowsVisuallyEqual(a.PrimaryWindow, b.PrimaryWindow) ||
            !AreAiWindowsVisuallyEqual(a.SecondaryWindow, b.SecondaryWindow) ||
            !string.Equals(a.CurrencySymbol, b.CurrencySymbol, StringComparison.Ordinal) ||
            Math.Round(a.WalletBalance, 2) != Math.Round(b.WalletBalance, 2) ||
            !string.Equals(a.WalletFormatted, b.WalletFormatted, StringComparison.Ordinal) ||
            !string.Equals(a.WalletBreakdown, b.WalletBreakdown, StringComparison.Ordinal) ||
            Math.Round(a.WalletRemainingPercent ?? -1.0, 1) != Math.Round(b.WalletRemainingPercent ?? -1.0, 1) ||
            !string.Equals(a.RadarValueText, b.RadarValueText, StringComparison.Ordinal) ||
            !string.Equals(a.RadarSubtext, b.RadarSubtext, StringComparison.Ordinal) ||
            !string.Equals(a.ErrorMessage, b.ErrorMessage, StringComparison.Ordinal))
        {
            return false;
        }

        if (a.BillingKind == AiProviderBillingKind.WalletBalance)
        {
            string timeA = a.UpdatedAt > DateTimeOffset.MinValue
                ? a.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            string timeB = b.UpdatedAt > DateTimeOffset.MinValue
                ? b.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            if (!string.Equals(timeA, timeB, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreAiWindowsVisuallyEqual(
        CustomWidgetAiWindowQuota? a,
        CustomWidgetAiWindowQuota? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(a.Label, b.Label, StringComparison.Ordinal) &&
               Math.Round(a.UsedPercent, 1) == Math.Round(b.UsedPercent, 1) &&
               string.Equals(a.ResetText, b.ResetText, StringComparison.Ordinal) &&
               a.Health == b.Health;
    }

    private bool ManifestUsesCountdown() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "countdown", StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(_manifest.Id, "deskbox.countdown", StringComparison.OrdinalIgnoreCase) ||
        NodeTreeReferencesCountdown(_manifest.View);

    private static bool NodeTreeReferencesCountdown(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "CountdownHero", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, "CountdownList", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, "TimeProgressCard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeReferencesCountdown(child))
            {
                return true;
            }
        }

        return false;
    }

    internal static TimeSpan ComputeCountdownNextMidnightDelay(DateTimeOffset now)
    {
        DateTime localNow = now.ToLocalTime().DateTime;
        DateTime nextMidnight = localNow.Date.AddDays(1);
        TimeSpan delay = (nextMidnight - localNow) + TimeSpan.FromMilliseconds(150);
        return delay < TimeSpan.FromMilliseconds(150)
            ? TimeSpan.FromMilliseconds(150)
            : delay;
    }

    private void UpdateCountdownTimerSubscription()
    {
        bool activeWhileCollapsed = !_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "countdown.");
        bool shouldWatchDayRollover = !_isDisposed &&
                                      _isWindowVisible &&
                                      activeWhileCollapsed &&
                                      ManifestUsesCountdown();
        if (shouldWatchDayRollover)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DateOnly currentDay = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
            if (_lastCountdownDay == default)
            {
                _lastCountdownDay = currentDay;
            }
            else if (currentDay != _lastCountdownDay)
            {
                _lastCountdownDay = currentDay;
                RefreshCapsuleProjection(bumpLiveState: true);
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            }

            TryDispatchDueTodayCountdownToast(now);

            if (_countdownTimer is null && _dispatcherQueue is not null)
            {
                _countdownTimer = _dispatcherQueue.CreateTimer();
                _countdownTimer.IsRepeating = false;
                _countdownTimer.Tick += OnCountdownTimerTick;
            }

            if (_countdownTimer is not null && !_countdownTimer.IsRunning)
            {
                _countdownTimer.Interval = ComputeCountdownNextMidnightDelay(now);
                _countdownTimer.Start();
            }
        }
        else if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= OnCountdownTimerTick;
            _countdownTimer = null;
        }
    }

    private void OnCountdownTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly currentDay = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (currentDay != _lastCountdownDay)
        {
            _lastCountdownDay = currentDay;
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            TryDispatchDueTodayCountdownToast(now);
        }

        sender.Interval = ComputeCountdownNextMidnightDelay(now);
        sender.Start();
    }

    private void TryDispatchDueTodayCountdownToast(DateTimeOffset now)
    {
        if (_isPreviewOnly || _isDisposed)
        {
            return;
        }

        if (_settings.TryGetValue("notifyDueToday", out string? notifyFlag) &&
            string.Equals(notifyFlag, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string todayIso = DateOnly.FromDateTime(now.ToLocalTime().DateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_state.TryGetValue("__lastDueToastDate", out string? lastDate) &&
            string.Equals(lastDate, todayIso, StringComparison.Ordinal))
        {
            return;
        }

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            _state,
            _settings,
            _collections,
            now,
            _localizationService.CurrentCultureName);
        var dueToday = snap.AllEvents
            .Where(static e => e.Direction == CountdownDirection.Today)
            .ToList();
        if (dueToday.Count == 0)
        {
            return;
        }

        _state["__lastDueToastDate"] = todayIso;
        _ = PersistStoreAsync();

        bool isZh = ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string title = isZh ? $"🎉 今日提醒 · {Config.Name}" : $"🎉 Due Today · {Config.Name}";
        string message = dueToday.Count == 1
            ? (isZh ? $"今天是「{dueToday[0].Title}」" : $"Today is \"{dueToday[0].Title}\"")
            : string.Join(" · ", dueToday.Take(3).Select(static e => e.Title));
        DispatchReminderToastNotification(title, message);
    }

    private void DispatchReminderToastNotification(string title, string message)
    {
        try
        {
            string shortId = Config.Id.Length > 8 ? Config.Id[..8] : Config.Id;
            _notificationServiceProvider?.Invoke()?.TryShow(
                title,
                message,
                arguments: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "custom-reminder",
                    ["widgetId"] = Config.Id
                },
                options: new NativeAppNotificationOptions(
                    Tag: $"rem-{shortId}",
                    Group: "CustomReminder",
                    MuteAudio: false));
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Reminder toast notification skipped: {ex.Message}");
        }
    }

    private bool ManifestUsesCalendar() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "calendar", StringComparison.OrdinalIgnoreCase)) ||
        NodeTreeContainsType(_manifest.View, "CalendarGrid") ||
        CapsuleReferencesToken(_manifest.Capsule, "calendar.");

    private void UpdateCalendarSourceSubscription()
    {
        bool shouldWatch = !_isDisposed &&
                           _isWindowVisible &&
                           (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "calendar.")) &&
                           ManifestUsesCalendar();

        if (!shouldWatch)
        {
            if (_calendarDayTimer is not null)
            {
                _calendarDayTimer.Stop();
                _calendarDayTimer.Tick -= OnCalendarDayTimerTick;
                _calendarDayTimer = null;
            }
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (!_state.TryGetValue("selectedDate", out string? curSelected) ||
            string.IsNullOrWhiteSpace(curSelected))
        {
            _state["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (_lastCalendarDay == default)
        {
            _lastCalendarDay = today;
        }
        else if (today != _lastCalendarDay)
        {
            string prevIso = _lastCalendarDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _lastCalendarDay = today;
            if (_state.TryGetValue("selectedDate", out string? sel) &&
                string.Equals(sel, prevIso, StringComparison.Ordinal))
            {
                _state["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_calendarDayTimer is null && _dispatcherQueue is not null)
        {
            _calendarDayTimer = _dispatcherQueue.CreateTimer();
            _calendarDayTimer.IsRepeating = false;
            _calendarDayTimer.Tick += OnCalendarDayTimerTick;
        }

        if (_calendarDayTimer is not null && !_calendarDayTimer.IsRunning)
        {
            _calendarDayTimer.Interval = ComputeCountdownNextMidnightDelay(now);
            _calendarDayTimer.Start();
        }
    }

    private void OnCalendarDayTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (today != _lastCalendarDay)
        {
            string prevIso = _lastCalendarDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _lastCalendarDay = today;
            if (_state.TryGetValue("selectedDate", out string? sel) &&
                string.Equals(sel, prevIso, StringComparison.Ordinal))
            {
                _state["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        sender.Interval = ComputeCountdownNextMidnightDelay(now);
        sender.Start();
    }

    private bool ManifestUsesWeather() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase)) ||
        CapsuleReferencesToken(_manifest.Capsule, "weather.");

    private string ResolveEffectiveWeatherCity()
    {
        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase));
        if (spec is not null && !string.IsNullOrWhiteSpace(spec.City))
        {
            string evalCity = CustomWidgetExpressionEvaluator.Interpolate(spec.City, CreateEvaluationContext()).Trim();
            if (!string.IsNullOrWhiteSpace(evalCity))
            {
                return evalCity;
            }
        }

        if (_state.TryGetValue("city", out string? stateCity) && !string.IsNullOrWhiteSpace(stateCity))
        {
            return stateCity.Trim();
        }

        if (_settings.TryGetValue("city", out string? settingCity) && !string.IsNullOrWhiteSpace(settingCity))
        {
            return settingCity.Trim();
        }

        return string.Empty;
    }

    private void UpdateWeatherSourceSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "weather.")) &&
                         ManifestUsesWeather();

        if (!shouldRun)
        {
            if (_weatherSourceTimer is not null)
            {
                _weatherSourceTimer.Stop();
                _weatherSourceTimer.Tick -= OnWeatherSourceTimerTick;
                _weatherSourceTimer = null;
            }
            if (_weatherFetchCts is { } wCts)
            {
                _weatherFetchCts = null;
                try
                {
                    wCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                wCts.Dispose();
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 900_000, 300_000, 7_200_000);

        if (_dispatcherQueue is not null)
        {
            if (_weatherSourceTimer is null)
            {
                _weatherSourceTimer = _dispatcherQueue.CreateTimer();
                _weatherSourceTimer.IsRepeating = true;
                _weatherSourceTimer.Tick += OnWeatherSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_weatherSourceTimer.Interval != interval)
            {
                _weatherSourceTimer.Interval = interval;
            }
            if (!_weatherSourceTimer.IsRunning)
            {
                _weatherSourceTimer.Start();
            }
        }

        string cityKey = $"{ResolveEffectiveWeatherCity()}|{ActiveCultureName}";
        if (!_weatherSnapshot.HasData || !string.Equals(_lastFetchedWeatherCityKey, cityKey, StringComparison.OrdinalIgnoreCase))
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }

    private void OnWeatherSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            _ = RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }

    public async Task<bool> RefreshWeatherNowAsync(bool forceRefresh = false, bool isManualUserAction = false)
    {
        if (_isDisposed || !ManifestUsesWeather())
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _weatherFetchInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            _weatherFetchCts?.Dispose();
            var cts = new CancellationTokenSource();
            _weatherFetchCts = cts;

            string city = ResolveEffectiveWeatherCity();
            string culture = ActiveCultureName;
            CustomWidgetWeatherSnapshot snap = await _weatherSource.FetchSnapshotAsync(
                city,
                culture,
                forceRefresh: forceRefresh,
                cancellationToken: cts.Token);

            if (_isDisposed || cts.IsCancellationRequested)
            {
                return false;
            }

            string targetCollection = _manifest.Sources
                .FirstOrDefault(static s => string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase))
                ?.TargetCollection ?? "forecast";
            if (string.IsNullOrWhiteSpace(targetCollection))
            {
                targetCollection = "forecast";
            }

            void ApplyWeatherOnUi()
            {
                if (_isDisposed)
                {
                    return;
                }

                _weatherSnapshot = snap;
                _lastFetchedWeatherCityKey = $"{city}|{culture}";
                if (snap.ForecastRecords.Count > 0)
                {
                    _collections[targetCollection] = new List<CustomWidgetRecord>(snap.ForecastRecords);
                }

                RefreshCapsuleProjection(bumpLiveState: true);
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
                if (isManualUserAction && snap.HasData)
                {
                    RaiseFeedback(_localizationService.T("CustomWidget.Toast.SourceRefreshed"), WidgetFeedbackSeverity.Info);
                }
            }

            if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(ApplyWeatherOnUi);
            }
            else
            {
                ApplyWeatherOnUi();
            }

            return snap.HasData;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _weatherFetchInFlight, 0);
        }
    }

    private bool ManifestUsesDeviceState() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "deviceState", StringComparison.OrdinalIgnoreCase)) ||
        NodeTreeContainsType(_manifest.View, "ControlTile") ||
        CapsuleReferencesToken(_manifest.Capsule, "device.");

    private void UpdateDeviceStateSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "device.")) &&
                         ManifestUsesDeviceState();

        if (!shouldRun)
        {
            if (_deviceStateTimer is not null)
            {
                _deviceStateTimer.Stop();
                _deviceStateTimer.Tick -= OnDeviceStateTimerTick;
                _deviceStateTimer = null;
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "deviceState", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 3000, 2000, 60_000);

        if (_dispatcherQueue is not null)
        {
            if (_deviceStateTimer is null)
            {
                _deviceStateTimer = _dispatcherQueue.CreateTimer();
                _deviceStateTimer.IsRepeating = true;
                _deviceStateTimer.Tick += OnDeviceStateTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_deviceStateTimer.Interval != interval)
            {
                _deviceStateTimer.Interval = interval;
            }
            if (!_deviceStateTimer.IsRunning)
            {
                _deviceStateTimer.Start();
            }
        }

        _ = RefreshDeviceStateNowAsync();
    }

    private void OnDeviceStateTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            _ = RefreshDeviceStateNowAsync();
        }
    }

    public async Task RefreshDeviceStateNowAsync()
    {
        if (_isDisposed || !ManifestUsesDeviceState())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _deviceRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CustomWidgetDeviceStateSnapshot snap = await _systemControlService.RefreshSnapshotAsync();
            if (_isDisposed)
            {
                return;
            }

            void ApplyDeviceOnUi()
            {
                if (_isDisposed || Equals(_deviceStateSnapshot, snap))
                {
                    return;
                }

                _deviceStateSnapshot = snap;
                RefreshCapsuleProjection(bumpLiveState: true);
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            }

            if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(ApplyDeviceOnUi);
            }
            else
            {
                ApplyDeviceOnUi();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _deviceRefreshInFlight, 0);
        }
    }

    private bool ManifestUsesClipboard() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "clipboard", StringComparison.OrdinalIgnoreCase)) ||
        CapsuleReferencesToken(_manifest.Capsule, "clipboard.");

    private void UpdateClipboardSourceSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "clipboard.")) &&
                         ManifestUsesClipboard();

        if (!shouldRun)
        {
            if (_clipboardTimer is not null)
            {
                _clipboardTimer.Stop();
                _clipboardTimer.Tick -= OnClipboardTimerTick;
                _clipboardTimer = null;
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "clipboard", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 2000, 1000, 30_000);

        if (_dispatcherQueue is not null)
        {
            if (_clipboardTimer is null)
            {
                _clipboardTimer = _dispatcherQueue.CreateTimer();
                _clipboardTimer.IsRepeating = true;
                _clipboardTimer.Tick += OnClipboardTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_clipboardTimer.Interval != interval)
            {
                _clipboardTimer.Interval = interval;
            }
            if (!_clipboardTimer.IsRunning)
            {
                _clipboardTimer.Start();
            }
        }

        _ = RefreshClipboardNowAsync();
    }

    private void OnClipboardTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            _ = RefreshClipboardNowAsync();
        }
    }

    public async Task RefreshClipboardNowAsync()
    {
        if (_isDisposed || !ManifestUsesClipboard())
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshClipboardNowAsync());
            return;
        }

        if (Interlocked.CompareExchange(ref _clipboardRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            string? text = null;
            try
            {
                DataPackageView content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    text = await content.GetTextAsync();
                }
            }
            catch
            {
                return;
            }

            if (_isDisposed)
            {
                return;
            }

            CustomWidgetClipboardSnapshot next = CustomWidgetClipboardSnapshot.FromText(text);
            if (!string.Equals(_clipboardSnapshot.Text, next.Text, StringComparison.Ordinal))
            {
                _clipboardSnapshot = next;
                RefreshCapsuleProjection(bumpLiveState: true);
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _clipboardRefreshInFlight, 0);
        }
    }

    private bool ManifestUsesStorageDrives() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase)) ||
        CapsuleReferencesToken(_manifest.Capsule, "drives.");

    private void UpdateStorageDrivesSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "drives.")) &&
                         ManifestUsesStorageDrives();

        if (!shouldRun)
        {
            if (_storageDrivesTimer is not null)
            {
                _storageDrivesTimer.Stop();
                _storageDrivesTimer.Tick -= OnStorageDrivesTimerTick;
                _storageDrivesTimer = null;
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 15_000, 5000, 300_000);

        if (_dispatcherQueue is not null)
        {
            if (_storageDrivesTimer is null)
            {
                _storageDrivesTimer = _dispatcherQueue.CreateTimer();
                _storageDrivesTimer.IsRepeating = true;
                _storageDrivesTimer.Tick += OnStorageDrivesTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_storageDrivesTimer.Interval != interval)
            {
                _storageDrivesTimer.Interval = interval;
            }
            if (!_storageDrivesTimer.IsRunning)
            {
                _storageDrivesTimer.Start();
            }
        }

        RefreshStorageDrivesNow(triggerRebuild: false);
    }

    private void OnStorageDrivesTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            RefreshStorageDrivesNow(triggerRebuild: true);
        }
    }

    public void RefreshStorageDrivesNow(bool triggerRebuild)
    {
        if (_isDisposed || !ManifestUsesStorageDrives())
        {
            return;
        }

        CustomWidgetStorageDrivesSnapshot next = CustomWidgetStorageDrivesSnapshot.Capture();
        string targetCol = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "storageDrives", StringComparison.OrdinalIgnoreCase))
            ?.TargetCollection ?? "drives";
        if (string.IsNullOrWhiteSpace(targetCol))
        {
            targetCol = "drives";
        }

        bool summaryChanged = !string.Equals(_storageDrivesSnapshot.Summary, next.Summary, StringComparison.Ordinal) ||
                              _storageDrivesSnapshot.Count != next.Count;
        _storageDrivesSnapshot = next;
        _collections[targetCol] = new List<CustomWidgetRecord>(next.Records);

        if (triggerRebuild && summaryChanged)
        {
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool ManifestUsesClock() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "clock", StringComparison.OrdinalIgnoreCase)) ||
        CapsuleReferencesToken(_manifest.Capsule, "clock.") ||
        CapsuleReferencesToken(_manifest.Capsule, "tz(");

    private void UpdateClockSourceSubscription()
    {
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         (!_isCompactCollapsed ||
                          CapsuleReferencesToken(_manifest.Capsule, "clock.") ||
                          CapsuleReferencesToken(_manifest.Capsule, "time.") ||
                          CapsuleReferencesToken(_manifest.Capsule, "tz(")) &&
                         ManifestUsesClock();

        if (!shouldRun)
        {
            if (_clockTimer is not null)
            {
                _clockTimer.Stop();
                _clockTimer.Tick -= OnClockTimerTick;
                _clockTimer = null;
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _manifest.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "clock", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 1000, 500, 60_000);

        if (_dispatcherQueue is not null)
        {
            if (_clockTimer is null)
            {
                _clockTimer = _dispatcherQueue.CreateTimer();
                _clockTimer.IsRepeating = true;
                _clockTimer.Tick += OnClockTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_clockTimer.Interval != interval)
            {
                _clockTimer.Interval = interval;
            }
            if (!_clockTimer.IsRunning)
            {
                _clockTimer.Start();
            }
        }
    }

    private void OnClockTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        RefreshCapsuleProjection(bumpLiveState: false);
        MetricsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private bool ManifestUsesPomodoro() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "pomodoro", StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(_manifest.Id, "deskbox.pomodoro", StringComparison.OrdinalIgnoreCase) ||
        NodeTreeReferencesPomodoro(_manifest.View);

    private static bool NodeTreeReferencesPomodoro(CustomWidgetNodeSpec node)
    {
        if (string.Equals(node.Type, "PomodoroCard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (NodeTreeReferencesPomodoro(child))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdatePomodoroTimerSubscription()
    {
        bool shouldTick = !_isDisposed && CustomWidgetPomodoroEngine.IsRunning(_state);
        if (shouldTick)
        {
            if (_pomodoroTimer is null && _dispatcherQueue is not null)
            {
                _pomodoroTimer = _dispatcherQueue.CreateTimer();
                _pomodoroTimer.Tick += OnPomodoroTimerTick;
            }

            if (_pomodoroTimer is not null)
            {
                bool needsUiTick = _isWindowVisible &&
                                   (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "pomodoro."));
                TimeSpan targetInterval = needsUiTick
                    ? TimeSpan.FromSeconds(1)
                    : ComputePomodoroSleepInterval();
                if (_pomodoroTimer.Interval != targetInterval || !_needsUiTickMatchesCurrent(needsUiTick, _pomodoroTimer.Interval))
                {
                    _pomodoroTimer.Interval = targetInterval;
                }
                if (!_pomodoroTimer.IsRunning || !needsUiTick)
                {
                    _pomodoroTimer.Start();
                }
            }
        }
        else if (_pomodoroTimer is not null)
        {
            _pomodoroTimer.Stop();
            _pomodoroTimer.Tick -= OnPomodoroTimerTick;
            _pomodoroTimer = null;
        }
    }

    private static bool _needsUiTickMatchesCurrent(bool needsUiTick, TimeSpan currentInterval) =>
        !needsUiTick || currentInterval == TimeSpan.FromSeconds(1);

    private TimeSpan ComputePomodoroSleepInterval()
    {
        if (_state.TryGetValue(CustomWidgetPomodoroEngine.KeyEndUtcTicks, out string? endRaw) &&
            long.TryParse(endRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long endTicks) &&
            endTicks > 0)
        {
            long remainingTicks = endTicks - DateTimeOffset.UtcNow.UtcTicks;
            if (remainingTicks <= TimeSpan.FromMilliseconds(250).Ticks)
            {
                return TimeSpan.FromMilliseconds(250);
            }

            long maxSleepTicks = TimeSpan.FromMinutes(5).Ticks;
            return TimeSpan.FromTicks(Math.Min(remainingTicks, maxSleepTicks));
        }

        return TimeSpan.FromSeconds(1);
    }

    private void OnPomodoroTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        if (CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(
                _state,
                _settings,
                _collections,
                DateTimeOffset.Now,
                out CustomWidgetPomodoroCompletionNotice? notice,
                _localizationService.CurrentCultureName))
        {
            UpdatePomodoroTimerSubscription();
            _ = PersistStoreAsync();
            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);

            if (notice is not null)
            {
                DispatchPomodoroCompletionNotification(notice);
            }
            return;
        }

        bool needsUiTick = _isWindowVisible &&
                           (!_isCompactCollapsed || CapsuleReferencesToken(_manifest.Capsule, "pomodoro."));
        if (!needsUiTick)
        {
            UpdatePomodoroTimerSubscription();
            return;
        }

        if (sender.Interval != TimeSpan.FromSeconds(1))
        {
            sender.Interval = TimeSpan.FromSeconds(1);
        }

        RefreshCapsuleProjection(bumpLiveState: false);
        MetricsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private void DispatchPomodoroCompletionNotification(CustomWidgetPomodoroCompletionNotice notice)
    {
        RaiseFeedback(notice.Title, WidgetFeedbackSeverity.Info);

        if (string.Equals(notice.NotifyMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            bool muteAudio = string.Equals(notice.NotifyMode, "toast_silent", StringComparison.OrdinalIgnoreCase);
            string shortId = Config.Id.Length > 8 ? Config.Id[..8] : Config.Id;
            _notificationServiceProvider?.Invoke()?.TryShow(
                notice.Title,
                notice.Message,
                arguments: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "custom-pomodoro",
                    ["widgetId"] = Config.Id
                },
                options: new NativeAppNotificationOptions(
                    Tag: $"pomo-{shortId}",
                    Group: "CustomPomodoro",
                    MuteAudio: muteAudio));
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Pomodoro toast notification skipped: {ex.Message}");
        }
    }

    private void OnSystemMetricsUpdated(CustomWidgetSystemMetricsSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        void ApplyOnUi()
        {
            if (_isDisposed)
            {
                return;
            }
            _systemMetrics = snapshot;
            RefreshCapsuleProjection(bumpLiveState: false);
            MetricsRefreshed?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ApplyOnUi);
        }
        else
        {
            ApplyOnUi();
        }
    }

    private void StartManifestWatcher()
    {
        _store.StoreMutated -= OnStoreMutated;
        _store.StoreMutated += OnStoreMutated;
        _manifestWatcherSubscription ??= _store.RegisterManifestWatcher(OnManifestFileChanged);
    }

    private void OnStoreMutated(object? origin, bool isManifestChange)
    {
        if (_isDisposed || !_isInitialized || ReferenceEquals(origin, this))
        {
            return;
        }

        ScheduleDebouncedReload(notifyFeedback: isManifestChange, delayMs: 40);
    }

    private void OnManifestFileChanged(FileSystemEventArgs e)
    {
        if (_isDisposed || !_isInitialized)
        {
            return;
        }

        string fileName = Path.GetFileName(e.FullPath);
        bool isManifestChange = string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase);
        bool isStoreChange = string.Equals(fileName, "store.json", StringComparison.OrdinalIgnoreCase);
        if (!isManifestChange && !isStoreChange)
        {
            return;
        }

        if (isStoreChange &&
            DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastOwnSaveUtcTicks) < TimeSpan.FromMilliseconds(750).Ticks)
        {
            return;
        }

        ScheduleDebouncedReload(notifyFeedback: isManifestChange, delayMs: 200);
    }

    private void ScheduleDebouncedReload(bool notifyFeedback, int delayMs)
    {
        CancellationTokenSource cts;
        lock (_reloadDebounceLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _pendingReloadNotifyFeedback |= notifyFeedback;
            if (_reloadDebounceCts is { } previousCts)
            {
                _reloadDebounceCts = null;
                try
                {
                    previousCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                previousCts.Dispose();
            }

            cts = new CancellationTokenSource();
            _reloadDebounceCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                bool shouldNotifyFeedback;
                lock (_reloadDebounceLock)
                {
                    if (_isDisposed || cts.IsCancellationRequested || !ReferenceEquals(_reloadDebounceCts, cts))
                    {
                        return;
                    }

                    shouldNotifyFeedback = _pendingReloadNotifyFeedback;
                    _pendingReloadNotifyFeedback = false;
                }

                if (_dispatcherQueue is not null)
                {
                    _dispatcherQueue.TryEnqueue(() => _ = ReloadManifestFromDiskAsync(notifyFeedback: shouldNotifyFeedback));
                }
                else
                {
                    await ReloadManifestFromDiskAsync(notifyFeedback: shouldNotifyFeedback);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private static string NormalizeStateKey(string key) =>
        key.StartsWith("state.", StringComparison.Ordinal)
            ? key["state.".Length..]
            : key;

    private static void CopyTextToClipboard(string text)
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

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        void ApplyLanguageChangeOnUi()
        {
            if (_isDisposed)
            {
                return;
            }

            TrySyncAutoLocalizedTitle(notifyIdentityChange: true);
            if (_isSubscribedToAiMonitor)
            {
                _ = CustomWidgetAiMonitorSource.Shared.RefreshNowAsync(forceCloudRefresh: false);
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

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ApplyLanguageChangeOnUi);
        }
        else
        {
            ApplyLanguageChangeOnUi();
        }
    }

    private void TrySyncAutoLocalizedTitle(bool notifyIdentityChange)
    {
        string culture = _localizationService.CurrentCultureName;
        CustomWidgetPresetInfo? matchedPreset = CustomWidgetTemplates.MatchPresetOrNull(_manifest.Id);
        string targetTitle = matchedPreset is not null
            ? _localizationService.T(matchedPreset.NameKey)
            : _manifest.ResolveLocalizedName(culture);

        if (string.IsNullOrWhiteSpace(targetTitle) ||
            string.Equals(targetTitle, Config.Name, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(targetTitle))
            {
                _lastAutoLocalizedTitle = targetTitle;
            }
            return;
        }

        if (!IsKnownAutoTitle(Config.Name, matchedPreset))
        {
            return;
        }

        Config.Name = targetTitle;
        _lastAutoLocalizedTitle = targetTitle;
        if (notifyIdentityChange)
        {
            ManifestIdentityChanged?.Invoke(targetTitle, _manifest.Icon);
        }
    }

    private bool IsKnownAutoTitle(string? currentName, CustomWidgetPresetInfo? matchedPreset)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_lastAutoLocalizedTitle) &&
            string.Equals(currentName, _lastAutoLocalizedTitle, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(currentName, _manifest.Name, StringComparison.Ordinal))
        {
            return true;
        }

        if (matchedPreset is not null)
        {
            if (string.Equals(currentName, matchedPreset.DefaultTitle, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (string code in LocalizationService.SupportedLanguageCodes)
            {
                if (string.Equals(
                        currentName,
                        LocalizationService.TranslateForCulture(code, matchedPreset.NameKey),
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        foreach (IReadOnlyDictionary<string, string> dict in _manifest.I18n.Values)
        {
            if ((dict.TryGetValue("widgetName", out string? wn) &&
                 string.Equals(currentName, wn, StringComparison.Ordinal)) ||
                (dict.TryGetValue("name", out string? n) &&
                 string.Equals(currentName, n, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private void RaiseFeedback(string message, WidgetFeedbackSeverity severity)
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

        _isDisposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_aiActionCts is { } aiCts)
        {
            _aiActionCts = null;
            try
            {
                aiCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            aiCts.Dispose();
        }
        UpdateSourceSubscriptions();

        lock (_reloadDebounceLock)
        {
            _pendingReloadNotifyFeedback = false;
            if (_reloadDebounceCts is { } cts)
            {
                _reloadDebounceCts = null;
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

        _store.StoreMutated -= OnStoreMutated;
        _manifestWatcherSubscription?.Dispose();
        _manifestWatcherSubscription = null;
        CustomWidgetDataStore.ReleaseWidgetIfUnused(Config.Id);
    }
}
