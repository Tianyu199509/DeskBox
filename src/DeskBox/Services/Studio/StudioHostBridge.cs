using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

/// <summary>
/// Narrow host boundary used by Custom Widget Studio UI and session layers so they
/// never resolve global application singletons or service locators directly.
/// </summary>
internal interface IStudioHostBridge
{
    event Action? DisplayTopologyChanged;
    event Action? AppearanceChanged;

    bool HasWidgetManager { get; }

    Windows.UI.Color GetEffectiveAccentColor();

    (int Width, int Height) GetDefaultWidgetSize();

    StudioShellVisualSettings GetShellVisualSettings();

    WidgetChromeMode ResolvePreviewWidgetChromeMode(
        CustomWidgetManifest? manifest,
        string? existingWidgetId,
        LocalizationService localization);

    WidgetBorderVisuals ResolvePreviewBorderVisuals(
        bool isDark,
        Windows.UI.Color accentColor,
        string? existingWidgetId);

    IReadOnlyList<GlanceWidgetInstanceInfo> GetCustomWidgetInstances();

    WidgetConfig? ResolveCustomWidgetConfig(string widgetId);

    void BringAuxiliaryWindowToFront(IntPtr hwnd, string reason);

    void ReleaseRaisedBandGuest(IntPtr hwnd, string reason);

    Task<GlanceWidgetInstanceInfo?> CreateCustomWidgetAsync(
        string? templateOrPackageSpec,
        string? logicJsOverride = null,
        bool hasLogicJsOverride = false,
        CustomWidgetLocaleCatalog? localesOverride = null,
        bool hasLocalesOverride = false,
        IReadOnlyDictionary<string, string>? initialSettings = null,
        IReadOnlyDictionary<string, string>? initialSecrets = null);

    Task<GlanceWidgetInstanceInfo?> UpdateCustomWidgetManifestAsync(
        string widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false);

    Task ApplyCustomWidgetMutationAsync(CustomWidgetSettingsMutation mutation);

    Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled);

    Task LocateCustomWidgetAsync(string widgetId);

    Task RemoveCustomWidgetAsync(string widgetId);

    void ShowNotification(string title, string body);

    IReadOnlyList<AgentStageSummary> GetPendingAgentStages();

    bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error);

    Task<string> CommitAgentStageAsync(string stageId);

    Task DiscardAgentStageAsync(string stageId);

    void Log(string message);
}

/// <summary>
/// Slimmed main-process bridge used by <see cref="StudioIpcRouter"/> to execute
/// desktop widget mutations, window-band requests, notifications, and stage operations.
/// </summary>
internal sealed class InProcessStudioHostBridge : IStudioHostBridge
{
    private readonly Func<WidgetManager?> _getWidgetManager;
    private readonly Func<NativeAppNotificationService?> _getNotificationService;
    private readonly Func<string, Task<string>> _commitAgentStageAsync;
    private readonly Func<string, Task> _discardAgentStageAsync;
    private readonly Action<string>? _log;

    public InProcessStudioHostBridge(
        Func<WidgetManager?> getWidgetManager,
        Func<NativeAppNotificationService?> getNotificationService,
        Func<string, Task<string>> commitAgentStageAsync,
        Func<string, Task> discardAgentStageAsync,
        Action<string>? log = null)
    {
        _getWidgetManager = getWidgetManager ?? throw new ArgumentNullException(nameof(getWidgetManager));
        _getNotificationService = getNotificationService ?? throw new ArgumentNullException(nameof(getNotificationService));
        _commitAgentStageAsync = commitAgentStageAsync ?? throw new ArgumentNullException(nameof(commitAgentStageAsync));
        _discardAgentStageAsync = discardAgentStageAsync ?? throw new ArgumentNullException(nameof(discardAgentStageAsync));
        _log = log;
    }

    public event Action? DisplayTopologyChanged
    {
        add { }
        remove { }
    }

    public event Action? AppearanceChanged
    {
        add { }
        remove { }
    }

    public bool HasWidgetManager => _getWidgetManager() is not null;

    public Windows.UI.Color GetEffectiveAccentColor() =>
        NullStudioHostBridge.Instance.GetEffectiveAccentColor();

    public (int Width, int Height) GetDefaultWidgetSize() =>
        NullStudioHostBridge.Instance.GetDefaultWidgetSize();

    public StudioShellVisualSettings GetShellVisualSettings() =>
        NullStudioHostBridge.DefaultShellVisualSettings;

    public WidgetChromeMode ResolvePreviewWidgetChromeMode(
        CustomWidgetManifest? manifest,
        string? existingWidgetId,
        LocalizationService localization) =>
        NullStudioHostBridge.Instance.ResolvePreviewWidgetChromeMode(manifest, existingWidgetId, localization);

    public WidgetBorderVisuals ResolvePreviewBorderVisuals(
        bool isDark,
        Windows.UI.Color accentColor,
        string? existingWidgetId) =>
        NullStudioHostBridge.Instance.ResolvePreviewBorderVisuals(isDark, accentColor, existingWidgetId);

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetCustomWidgetInstances() =>
        _getWidgetManager()?.GetCustomWidgetInstances() ?? [];

    public WidgetConfig? ResolveCustomWidgetConfig(string widgetId) =>
        _getWidgetManager()?.ResolveCustomWidgetConfig(widgetId);

    public void BringAuxiliaryWindowToFront(IntPtr hwnd, string reason) =>
        _getWidgetManager()?.BringAuxiliaryWindowToFront(hwnd, reason);

    public void ReleaseRaisedBandGuest(IntPtr hwnd, string reason) =>
        _getWidgetManager()?.ReleaseRaisedBandGuest(hwnd, reason);

    public async Task<GlanceWidgetInstanceInfo?> CreateCustomWidgetAsync(
        string? templateOrPackageSpec,
        string? logicJsOverride = null,
        bool hasLogicJsOverride = false,
        CustomWidgetLocaleCatalog? localesOverride = null,
        bool hasLocalesOverride = false,
        IReadOnlyDictionary<string, string>? initialSettings = null,
        IReadOnlyDictionary<string, string>? initialSecrets = null)
    {
        if (_getWidgetManager() is not { } manager)
        {
            return null;
        }

        return await manager.CreateCustomWidgetAsync(
            templateOrPackageSpec,
            logicJsOverride: logicJsOverride,
            hasLogicJsOverride: hasLogicJsOverride,
            localesOverride: localesOverride,
            hasLocalesOverride: hasLocalesOverride,
            initialSettings: initialSettings,
            initialSecrets: initialSecrets,
            allowTrustedJsonAuthoring: true);
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
        if (_getWidgetManager() is not { } manager)
        {
            return null;
        }

        return await manager.UpdateCustomWidgetManifestAsync(
            widgetId,
            manifestJson,
            resizeToDefault: resizeToDefault,
            reveal: reveal,
            logicJs: logicJs,
            updateLogicJs: updateLogicJs,
            locales: locales,
            updateLocales: updateLocales);
    }

    public Task ApplyCustomWidgetMutationAsync(CustomWidgetSettingsMutation mutation) =>
        _getWidgetManager()?.ApplyCustomWidgetMutationAsync(mutation) ?? Task.CompletedTask;

    public Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled) =>
        _getWidgetManager()?.SetCustomWidgetInstanceEnabledAsync(widgetId, enabled) ?? Task.CompletedTask;

    public async Task LocateCustomWidgetAsync(string widgetId)
    {
        if (_getWidgetManager() is { } manager)
        {
            _ = await manager.LocateCustomWidgetAsync(widgetId);
        }
    }

    public Task RemoveCustomWidgetAsync(string widgetId) =>
        _getWidgetManager()?.RemoveCustomWidgetAsync(widgetId) ?? Task.CompletedTask;

    public void ShowNotification(string title, string body) =>
        _getNotificationService()?.TryShow(title, body);

    public IReadOnlyList<AgentStageSummary> GetPendingAgentStages() =>
        CustomWidgetAgentStageStore.ListPending();

    public bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error) =>
        CustomWidgetAgentStageStore.TryReadBundle(stageId, out bundle, out error);

    public Task<string> CommitAgentStageAsync(string stageId) =>
        _commitAgentStageAsync(stageId);

    public Task DiscardAgentStageAsync(string stageId) =>
        _discardAgentStageAsync(stageId);

    public void Log(string message) => _log?.Invoke(message);
}

internal sealed class NullStudioHostBridge : IStudioHostBridge
{
    internal static readonly NullStudioHostBridge Instance = new();

    internal static readonly StudioShellVisualSettings DefaultShellVisualSettings = new(
        DefaultWidgetWidth: SettingsService.DefaultWidgetWidth,
        DefaultWidgetHeight: SettingsService.DefaultWidgetHeight,
        WidgetMaterialType: SettingsService.WidgetMaterialTypeAcrylic,
        WidgetOpacity: SettingsService.DefaultWidgetOpacity,
        WidgetMaterialIntensity: SettingsService.DefaultWidgetMaterialIntensity,
        WidgetCornerPreference: SettingsService.WidgetCornerPreferenceRound,
        WidgetTextShadowEnabled: false,
        WidgetBorderStyle: SettingsService.WidgetBorderStyleThin,
        WidgetBorderColorMode: SettingsService.WidgetBorderColorModeNeutral,
        WidgetTitleIconMode: WidgetTitleIconModeNames.Color,
        TextSize: SettingsService.DefaultTextSize,
        IconSize: SettingsService.DefaultIconSize);

    public event Action? DisplayTopologyChanged
    {
        add { }
        remove { }
    }

    public event Action? AppearanceChanged
    {
        add { }
        remove { }
    }

    public bool HasWidgetManager => false;

    public Windows.UI.Color GetEffectiveAccentColor() => AccentColorHelper.DefaultAccentColor;

    public (int Width, int Height) GetDefaultWidgetSize() =>
        ((int)SettingsService.DefaultWidgetWidth, (int)SettingsService.DefaultWidgetHeight);

    public StudioShellVisualSettings GetShellVisualSettings() => DefaultShellVisualSettings;

    public WidgetChromeMode ResolvePreviewWidgetChromeMode(
        CustomWidgetManifest? manifest,
        string? existingWidgetId,
        LocalizationService localization) => WidgetChromeMode.Standard;

    public WidgetBorderVisuals ResolvePreviewBorderVisuals(
        bool isDark,
        Windows.UI.Color accentColor,
        string? existingWidgetId) =>
        WidgetBorderVisualCalculator.Resolve(
            SettingsService.WidgetBorderStyleThin,
            SettingsService.WidgetBorderColorModeNeutral,
            isDark,
            accentColor);

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetCustomWidgetInstances() => [];

    public WidgetConfig? ResolveCustomWidgetConfig(string widgetId) => null;

    public void BringAuxiliaryWindowToFront(IntPtr hwnd, string reason) { }

    public void ReleaseRaisedBandGuest(IntPtr hwnd, string reason) { }

    public Task<GlanceWidgetInstanceInfo?> CreateCustomWidgetAsync(
        string? templateOrPackageSpec,
        string? logicJsOverride = null,
        bool hasLogicJsOverride = false,
        CustomWidgetLocaleCatalog? localesOverride = null,
        bool hasLocalesOverride = false,
        IReadOnlyDictionary<string, string>? initialSettings = null,
        IReadOnlyDictionary<string, string>? initialSecrets = null) =>
        Task.FromResult<GlanceWidgetInstanceInfo?>(null);

    public Task<GlanceWidgetInstanceInfo?> UpdateCustomWidgetManifestAsync(
        string widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false) =>
        Task.FromResult<GlanceWidgetInstanceInfo?>(null);

    public Task ApplyCustomWidgetMutationAsync(CustomWidgetSettingsMutation mutation) => Task.CompletedTask;

    public Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled) => Task.CompletedTask;

    public Task LocateCustomWidgetAsync(string widgetId) => Task.CompletedTask;

    public Task RemoveCustomWidgetAsync(string widgetId) => Task.CompletedTask;

    public void ShowNotification(string title, string body) { }

    public IReadOnlyList<AgentStageSummary> GetPendingAgentStages() => [];

    public bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error)
    {
        bundle = null;
        error = string.Empty;
        return false;
    }

    public Task<string> CommitAgentStageAsync(string stageId) => Task.FromResult(string.Empty);

    public Task DiscardAgentStageAsync(string stageId) => Task.CompletedTask;

    public void Log(string message) { }
}
