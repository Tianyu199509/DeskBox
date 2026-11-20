using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Services.Studio;

/// <summary>
/// Read-only persistence sink installed on the Studio companion process's local
/// <see cref="SettingsService"/> mirror so in-memory snapshot/layout updates never
/// touch <c>settings.json</c> on disk.
/// </summary>
internal sealed class ReadOnlySettingsPersistenceSink : ISettingsPersistenceSink
{
    public static readonly ReadOnlySettingsPersistenceSink Instance = new();

    public Task<bool> SaveAsync(SettingsService owner, SettingsPersistenceCommit commit) =>
        Task.FromResult(false);
}

/// <summary>
/// Out-of-process <see cref="IStudioHostBridge"/> implementation used inside
/// <c>DeskBox.exe --studio &lt;mainPid&gt;</c>. Synchronous reads project directly
/// from the host's local <see cref="SettingsService"/> mirror (kept fresh via
/// <c>Bootstrap</c>, mutation responses, and <c>layout-changed</c>/<c>settings-reloaded</c>
/// pushes), while desktop widget mutations travel over the Studio IPC pipe.
/// </summary>
internal sealed class RemoteStudioHostBridge : IStudioHostBridge
{
    private readonly SettingsIpcClient _client;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly Action<string> _log;
    private readonly Action<string>? _logVerbose;
    private readonly uint _mainProcessId;

    public RemoteStudioHostBridge(
        SettingsIpcClient client,
        SettingsService settingsService,
        ThemeService themeService,
        Action<string> log,
        Action<string>? logVerbose = null,
        uint mainProcessId = 0)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _logVerbose = logVerbose;
        _mainProcessId = mainProcessId;
    }

    private void GrantMainProcessForegroundRight()
    {
        if (_mainProcessId == 0)
        {
            return;
        }

        try
        {
            Win32Helper.AllowSetForegroundWindow(_mainProcessId);
        }
        catch
        {
        }
    }

    public event Action? DisplayTopologyChanged;

    public event Action? AppearanceChanged
    {
        add
        {
            if (value is not null)
            {
                _themeService.AppearanceChanged += value;
            }
        }
        remove
        {
            if (value is not null)
            {
                _themeService.AppearanceChanged -= value;
            }
        }
    }

    internal void NotifyDisplayTopologyChanged() => DisplayTopologyChanged?.Invoke();

    public bool HasWidgetManager => true;

    public Windows.UI.Color GetEffectiveAccentColor() =>
        _themeService.GetEffectiveAccentColor();

    public (int Width, int Height) GetDefaultWidgetSize()
    {
        AppSettings settings = _settingsService.Settings;
        double rawW = settings.WidgetShell.DefaultWidgetWidth;
        double rawH = settings.WidgetShell.DefaultWidgetHeight;
        int width = Math.Clamp(
            (int)Math.Round(double.IsFinite(rawW) && rawW > 0 ? rawW : SettingsService.DefaultWidgetWidth),
            180,
            1200);
        int height = Math.Clamp(
            (int)Math.Round(double.IsFinite(rawH) && rawH > 0 ? rawH : SettingsService.DefaultWidgetHeight),
            80,
            1200);
        return (width, height);
    }

    public StudioShellVisualSettings GetShellVisualSettings()
    {
        AppSettings settings = _settingsService.Settings;
        return new StudioShellVisualSettings(
            DefaultWidgetWidth: settings.WidgetShell.DefaultWidgetWidth,
            DefaultWidgetHeight: settings.WidgetShell.DefaultWidgetHeight,
            WidgetMaterialType: settings.WidgetShell.WidgetMaterialType,
            WidgetOpacity: settings.WidgetShell.WidgetOpacity,
            WidgetMaterialIntensity: settings.WidgetShell.WidgetMaterialIntensity,
            WidgetCornerPreference: settings.WidgetShell.WidgetCornerPreference,
            WidgetTextShadowEnabled: settings.WidgetShell.WidgetTextShadowEnabled,
            WidgetBorderStyle: settings.WidgetShell.WidgetBorderStyle,
            WidgetBorderColorMode: settings.WidgetShell.WidgetBorderColorMode,
            WidgetTitleIconMode: settings.WidgetShell.WidgetTitleIconMode,
            TextSize: settings.WidgetShell.TextSize,
            IconSize: settings.WidgetShell.IconSize);
    }

    public WidgetChromeMode ResolvePreviewWidgetChromeMode(
        CustomWidgetManifest? manifest,
        string? existingWidgetId,
        LocalizationService localization)
    {
        WidgetContentDescriptor descriptor = new WidgetContentFactory(localization).GetDescriptor(WidgetKind.Custom);
        WidgetChromeCategory category = CustomWidgetChromeCategoryClassifier.Classify(manifest);

        if (!string.IsNullOrWhiteSpace(existingWidgetId))
        {
            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                existingWidgetId);
            if (group is not null)
            {
                return WidgetGroupChromePolicy.NormalizePersistedMode(group.ChromeMode);
            }

            WidgetConfig? existingConfig = _settingsService.Settings.WidgetLayout.Widgets.FirstOrDefault(
                w => string.Equals(w.Id, existingWidgetId, StringComparison.Ordinal));
            if (existingConfig is not null)
            {
                return new WidgetChromeModeResolver(_settingsService).Resolve(
                    existingConfig,
                    descriptor,
                    category);
            }
        }

        return new WidgetChromeModeResolver(_settingsService).Resolve(
            new WidgetConfig { WidgetKind = WidgetKind.Custom },
            descriptor,
            category);
    }

    public WidgetBorderVisuals ResolvePreviewBorderVisuals(
        bool isDark,
        Windows.UI.Color accentColor,
        string? existingWidgetId)
    {
        AppSettings settings = _settingsService.Settings;
        WidgetConfig? existingConfig = !string.IsNullOrWhiteSpace(existingWidgetId)
            ? settings.WidgetLayout.Widgets.FirstOrDefault(
                w => string.Equals(w.Id, existingWidgetId, StringComparison.Ordinal))
            : null;
        (string style, string colorMode) = existingConfig is not null
            ? WidgetBorderCustomization.ApplyOverride(
                WidgetBorderCustomization.GetModeOverride(existingConfig),
                settings.WidgetShell.WidgetBorderStyle,
                settings.WidgetShell.WidgetBorderColorMode,
                WidgetBackgroundCustomization.HasCustomBackground(existingConfig) ||
                WidgetBackgroundCustomization.IsGlobalImageBackgroundActive(settings))
            : (settings.WidgetShell.WidgetBorderStyle, settings.WidgetShell.WidgetBorderColorMode);
        return WidgetBorderVisualCalculator.Resolve(style, colorMode, isDark, accentColor);
    }

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetCustomWidgetInstances()
    {
        AppSettings settings = _settingsService.Settings;
        bool featureEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Custom);
        List<string> deletedIds = settings.WidgetLayout.DeletedWidgetIds;
        return settings.WidgetLayout.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Custom && !deletedIds.Contains(widget.Id))
            .Select(widget => new GlanceWidgetInstanceInfo(
                widget.Id,
                widget.Name,
                featureEnabled && !widget.IsDisabled))
            .ToList();
    }

    public WidgetConfig? ResolveCustomWidgetConfig(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return null;
        }

        AppSettings settings = _settingsService.Settings;
        List<string> deletedIds = settings.WidgetLayout.DeletedWidgetIds;
        string needle = widgetId.Trim();

        WidgetConfig? byId = settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            w.WidgetKind == WidgetKind.Custom &&
            !deletedIds.Contains(w.Id) &&
            string.Equals(w.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        string altNeedle = needle.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? needle["deskbox.".Length..]
            : $"deskbox.{needle}";

        return settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            w.WidgetKind == WidgetKind.Custom &&
            !deletedIds.Contains(w.Id) &&
            w.Metadata.TryGetValue(CustomWidgetViewModel.TemplateMetadataKey, out string? tpl) &&
            (string.Equals(tpl, needle, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(tpl, altNeedle, StringComparison.OrdinalIgnoreCase)));
    }

    public void BringAuxiliaryWindowToFront(IntPtr hwnd, string reason)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        string payload = SettingsIpcPayload.Encode(
            new StudioWindowBandPayload(hwnd.ToInt64(), reason ?? string.Empty),
            SettingsIpcJsonContext.Default.StudioWindowBandPayload);
        FireAndForgetRequest(StudioIpcMethods.BringToFront, payload);
    }

    public void ReleaseRaisedBandGuest(IntPtr hwnd, string reason)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        string payload = SettingsIpcPayload.Encode(
            new StudioWindowBandPayload(hwnd.ToInt64(), reason ?? string.Empty),
            SettingsIpcJsonContext.Default.StudioWindowBandPayload);
        FireAndForgetRequest(StudioIpcMethods.ReleaseBand, payload);
    }

    public async Task<GlanceWidgetInstanceInfo?> CreateCustomWidgetAsync(
        string? templateOrPackageSpec,
        string? logicJsOverride = null,
        bool hasLogicJsOverride = false,
        CustomWidgetLocaleCatalog? localesOverride = null,
        bool hasLocalesOverride = false,
        IReadOnlyDictionary<string, string>? initialSettings = null,
        IReadOnlyDictionary<string, string>? initialSecrets = null)
    {
        var request = new StudioCreateWidgetRequest(
            TemplateOrPackageSpec: templateOrPackageSpec,
            LogicJsOverride: logicJsOverride,
            HasLogicJsOverride: hasLogicJsOverride,
            LocalesJsonOverride: StudioLocaleWireHelper.Serialize(localesOverride),
            HasLocalesOverride: hasLocalesOverride && localesOverride is { HasAnyLocale: true },
            InitialSettings: initialSettings is { Count: > 0 }
                ? new Dictionary<string, string>(initialSettings, StringComparer.Ordinal)
                : null,
            InitialSecrets: initialSecrets is { Count: > 0 }
                ? new Dictionary<string, string>(initialSecrets, StringComparer.Ordinal)
                : null);

        string payload = SettingsIpcPayload.Encode(
            request,
            SettingsIpcJsonContext.Default.StudioCreateWidgetRequest);
        GrantMainProcessForegroundRight();
        StudioWidgetMutationResponse? response = await SendMutationAsync(
            StudioIpcMethods.CreateWidget,
            payload);
        return response?.Instance;
    }

    public async Task<GlanceWidgetInstanceInfo?> UpdateCustomWidgetManifestAsync(
        string widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false)
    {
        var request = new StudioUpdateManifestRequest(
            WidgetId: widgetId,
            ManifestJson: manifestJson,
            ResizeToDefault: resizeToDefault,
            Reveal: reveal,
            LogicJs: logicJs,
            UpdateLogicJs: updateLogicJs,
            LocalesJson: StudioLocaleWireHelper.Serialize(locales),
            HasLocales: locales is { HasAnyLocale: true },
            UpdateLocales: updateLocales);

        string payload = SettingsIpcPayload.Encode(
            request,
            SettingsIpcJsonContext.Default.StudioUpdateManifestRequest);
        if (reveal)
        {
            GrantMainProcessForegroundRight();
        }
        StudioWidgetMutationResponse? response = await SendMutationAsync(
            StudioIpcMethods.UpdateWidgetManifest,
            payload);
        return response?.Instance;
    }

    public async Task ApplyCustomWidgetMutationAsync(CustomWidgetSettingsMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        string payload = SettingsIpcPayload.Encode(
            mutation,
            SettingsIpcJsonContext.Default.CustomWidgetSettingsMutation);
        _ = await SendMutationAsync(StudioIpcMethods.ApplyMutation, payload);
    }

    public async Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled)
    {
        string payload = SettingsIpcPayload.Encode(
            new StudioSetWidgetEnabledRequest(widgetId, enabled),
            SettingsIpcJsonContext.Default.StudioSetWidgetEnabledRequest);
        _ = await SendMutationAsync(StudioIpcMethods.SetWidgetEnabled, payload);
    }

    public async Task LocateCustomWidgetAsync(string widgetId)
    {
        GrantMainProcessForegroundRight();
        (bool ok, _, string? error) = await _client.RequestAsync(StudioIpcMethods.LocateWidget, widgetId);
        if (!ok)
        {
            _log($"[RemoteStudioHostBridge] LocateWidget failed for '{widgetId}': {error}");
        }
    }

    public async Task RemoveCustomWidgetAsync(string widgetId)
    {
        _ = await SendMutationAsync(StudioIpcMethods.RemoveWidget, widgetId);
    }

    public void ShowNotification(string title, string body)
    {
        string payload = SettingsIpcPayload.Encode(
            new StudioNotificationPayload(title ?? string.Empty, body ?? string.Empty),
            SettingsIpcJsonContext.Default.StudioNotificationPayload);
        FireAndForgetRequest(StudioIpcMethods.ShowNotification, payload);
    }

    public IReadOnlyList<AgentStageSummary> GetPendingAgentStages() =>
        CustomWidgetAgentStageStore.ListPending();

    public bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error) =>
        CustomWidgetAgentStageStore.TryReadBundle(stageId, out bundle, out error);

    public async Task<string> CommitAgentStageAsync(string stageId)
    {
        (bool ok, string? rawPayload, string? error) =
            await _client.RequestAsync(StudioIpcMethods.CommitAgentStage, stageId);
        if (!ok)
        {
            _log($"[RemoteStudioHostBridge] CommitAgentStage failed for '{stageId}': {error}");
            return "{\"ok\":false,\"error\":\"Studio IPC commit request failed.\"}";
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return "{\"ok\":false,\"error\":\"Empty stage commit response.\"}";
        }

        StudioStageCommitResponse? parsed = SettingsIpcPayload.Decode(
            rawPayload,
            SettingsIpcJsonContext.Default.StudioStageCommitResponse);
        if (parsed is null)
        {
            return rawPayload;
        }

        if (!string.IsNullOrWhiteSpace(parsed.LayoutSliceJson))
        {
            _settingsService.ApplyExternalLayout(parsed.LayoutSliceJson);
        }

        return parsed.ResponseJson;
    }

    public async Task DiscardAgentStageAsync(string stageId)
    {
        (bool ok, _, string? error) =
            await _client.RequestAsync(StudioIpcMethods.DiscardAgentStage, stageId);
        if (!ok)
        {
            _log($"[RemoteStudioHostBridge] DiscardAgentStage failed for '{stageId}': {error}");
        }
    }

    public void Log(string message) => _log(message);

    private async Task<StudioWidgetMutationResponse?> SendMutationAsync(string method, string? payload)
    {
        (bool ok, string? rawResponse, string? error) = await _client.RequestAsync(method, payload);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"Studio IPC '{method}' failed: {error ?? "unknown error"}");
        }

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return null;
        }

        StudioWidgetMutationResponse? parsed = SettingsIpcPayload.Decode(
            rawResponse,
            SettingsIpcJsonContext.Default.StudioWidgetMutationResponse);
        if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.LayoutSliceJson))
        {
            _settingsService.ApplyExternalLayout(parsed.LayoutSliceJson);
        }

        return parsed;
    }

    private void FireAndForgetRequest(string method, string? payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.RequestAsync(method, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logVerbose?.Invoke($"[RemoteStudioHostBridge] Fire-and-forget '{method}' ignored: {ex.Message}");
            }
        });
    }
}
