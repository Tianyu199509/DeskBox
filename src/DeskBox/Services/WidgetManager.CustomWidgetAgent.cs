using System.Runtime.InteropServices.WindowsRuntime;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.Services;

public sealed record CustomWidgetLiveRuntimeSnapshot(
    bool LiveLoaded,
    string DisplayMode,
    Dictionary<string, string> State,
    Dictionary<string, int> CollectionCounts,
    Dictionary<string, List<Dictionary<string, string>>> SampleItems);

public sealed partial class WidgetManager
{
    internal string ActiveCultureName => _localizationService.CurrentCultureName;
    internal Func<string?>? ResolveStudioAgentUpsertTargetWidgetId { get; set; }
    internal Action<string, string, string, string?, bool, CustomWidgetLocaleCatalog?, bool>? NotifyStudioAgentWidgetUpserted { get; set; }

    internal WidgetConfig? ResolveCustomWidgetConfig(string? widgetIdOrTemplateId)
    {
        if (string.IsNullOrWhiteSpace(widgetIdOrTemplateId))
        {
            return null;
        }

        string needle = widgetIdOrTemplateId.Trim();
        WidgetConfig? byId = _settingsService.Settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            w.WidgetKind == WidgetKind.Custom &&
            !IsDeleted(w.Id) &&
            string.Equals(w.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        string altNeedle = needle.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? needle["deskbox.".Length..]
            : $"deskbox.{needle}";

        return _settingsService.Settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            w.WidgetKind == WidgetKind.Custom &&
            !IsDeleted(w.Id) &&
            w.Metadata.TryGetValue(CustomWidgetViewModel.TemplateMetadataKey, out string? tpl) &&
            (string.Equals(tpl, needle, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(tpl, altNeedle, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<GlanceWidgetInstanceInfo> UpdateCustomWidgetManifestAsync(
        string widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                UpdateCustomWidgetManifestAsync(
                    widgetId,
                    manifestJson,
                    resizeToDefault,
                    reveal,
                    logicJs,
                    updateLogicJs,
                    locales,
                    updateLocales));
        }

        if (updateLogicJs)
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs);
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        if (parsed.Manifest is null || parsed.Diagnostics.Any(d => d.IsError))
        {
            string msg = parsed.Diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "Invalid manifest.json";
            throw new InvalidDataException(msg);
        }

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetId);
        if (config is null)
        {
            return await CreateCustomWidgetAsync(
                $"json:{manifestJson}",
                logicJsOverride: logicJs,
                hasLogicJsOverride: updateLogicJs,
                localesOverride: locales,
                hasLocalesOverride: updateLocales);
        }

        CustomWidgetManifest manifest = parsed.Manifest;
        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);

        string? forkedFromId = manifest.ForkedFromId;
        string? forkedFromVersion = manifest.ForkedFromVersion;
        string? createdAtUtc = manifest.CreatedAtUtc;
        if (File.Exists(store.ManifestPath))
        {
            try
            {
                string prevRaw = await File.ReadAllTextAsync(store.ManifestPath);
                CustomWidgetParseResult prevParsed = CustomWidgetManifestParser.Parse(prevRaw);
                if (prevParsed.Manifest is { } prevManifest)
                {
                    if (string.IsNullOrWhiteSpace(createdAtUtc) && !string.IsNullOrWhiteSpace(prevManifest.CreatedAtUtc))
                    {
                        createdAtUtc = prevManifest.CreatedAtUtc;
                    }

                    if (string.IsNullOrWhiteSpace(forkedFromId))
                    {
                        if (!string.IsNullOrWhiteSpace(prevManifest.ForkedFromId))
                        {
                            forkedFromId = prevManifest.ForkedFromId;
                            forkedFromVersion = string.IsNullOrWhiteSpace(forkedFromVersion)
                                ? prevManifest.ForkedFromVersion
                                : forkedFromVersion;
                        }
                        else if (prevManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) ||
                                 prevManifest.Id.StartsWith("starter.", StringComparison.OrdinalIgnoreCase))
                        {
                            forkedFromId = prevManifest.Id;
                            forkedFromVersion = prevManifest.Version;
                        }
                    }
                }
            }
            catch
            {
                // Ignore best-effort fork lineage read failure
            }
        }

        string stampedJson = manifest.IsCustomized
            ? ((!string.IsNullOrWhiteSpace(forkedFromId) && string.IsNullOrWhiteSpace(manifest.ForkedFromId)) ||
               (!string.IsNullOrWhiteSpace(createdAtUtc) && string.IsNullOrWhiteSpace(manifest.CreatedAtUtc))
                ? CustomWidgetManifestMetadataStamper.StampProvenance(
                    manifestJson,
                    origin: manifest.Origin,
                    isCustomized: true,
                    forkedFromId: forkedFromId,
                    forkedFromVersion: forkedFromVersion,
                    createdAtUtc: createdAtUtc)
                : manifestJson)
            : CustomWidgetManifestMetadataStamper.StampProvenance(
                manifestJson,
                origin: string.Equals(manifest.Origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase)
                    ? CustomWidgetOriginKinds.Local
                    : manifest.Origin,
                isCustomized: true,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion,
                createdAtUtc: createdAtUtc);

        await store.WriteWorkspaceBundleAsync(
            stampedJson,
            logicJs: logicJs,
            updateLogicJs: updateLogicJs,
            locales: locales,
            updateLocales: updateLocales);
        await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, config.Id);

        CustomWidgetLocaleCatalog? effectiveLocales = updateLocales
            ? locales
            : await store.ReadLazyRuntimeLocalesAsync(_localizationService.CurrentCultureName);
        if (effectiveLocales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(effectiveLocales);
        }

        config.Metadata[CustomWidgetViewModel.TemplateMetadataKey] = manifest.Id;
        if (!string.IsNullOrWhiteSpace(manifest.Name) &&
            !CustomWidgetTemplates.Presets.Any(p =>
                string.Equals($"deskbox.{p.Id}", manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            config.Name = manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);
            config.IsDefaultTitle = false;
        }

        if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
        {
            WidgetTitleIconCustomization.SetEmojiOverride(config, manifest.Icon.Trim());
        }

        if (resizeToDefault)
        {
            config.Width = manifest.DefaultSize.Width;
            config.Height = manifest.DefaultSize.Height;
        }

        _settingsService.UpdateWidget(config);
        await _settingsService.SaveAsync();

        IReadOnlyList<CustomWidgetContentAdapter> liveAdapters = FindLiveCustomWidgetAdapters(config.Id);
        foreach (CustomWidgetContentAdapter adapter in liveAdapters)
        {
            await adapter.ViewModel.ReloadManifestFromDiskAsync(notifyFeedback: true);
        }

        if (reveal)
        {
            await ShowWidgetAsync(config.Id, reveal: true, autoRestoreOnReveal: false);
        }

        return new GlanceWidgetInstanceInfo(
            config.Id,
            config.Name,
            GetFeatureWidgetEnabledState(WidgetKind.Custom) && !config.IsDisabled);
    }

    public async Task<GlanceWidgetInstanceInfo> UpsertCustomWidgetFromAgentAsync(
        string? widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                UpsertCustomWidgetFromAgentAsync(
                    widgetId,
                    manifestJson,
                    resizeToDefault,
                    reveal,
                    logicJs,
                    updateLogicJs,
                    locales,
                    updateLocales));
        }

        if (updateLogicJs)
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs);
        }

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            widgetId = ResolveStudioAgentUpsertTargetWidgetId?.Invoke();
        }

        CustomWidgetParseResult incomingParsed = CustomWidgetManifestParser.Parse(manifestJson);
        bool incomingIsCommunity = string.Equals(
            incomingParsed.Manifest?.Origin,
            CustomWidgetOriginKinds.Community,
            StringComparison.OrdinalIgnoreCase);

        WidgetConfig? resolvedExisting = !string.IsNullOrWhiteSpace(widgetId)
            ? ResolveCustomWidgetConfig(widgetId)
            : null;
        if (resolvedExisting is null &&
            incomingParsed.Manifest is not null &&
            !CustomWidgetTemplates.Presets.Any(p =>
                string.Equals($"deskbox.{p.Id}", incomingParsed.Manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedExisting = ResolveCustomWidgetConfig(incomingParsed.Manifest.Id);
        }

        bool preserveCommunityOrigin = incomingIsCommunity;
        if (!preserveCommunityOrigin && resolvedExisting is not null)
        {
            string? diskManifestJson = await CustomWidgetDataStore.ForWidget(resolvedExisting.Id).TryReadExistingManifestJsonAsync();
            string? diskOrigin = !string.IsNullOrWhiteSpace(diskManifestJson)
                ? CustomWidgetManifestParser.Parse(diskManifestJson).Manifest?.Origin
                : null;
            preserveCommunityOrigin = !CustomWidgetTrustStore.Shared
                .GetProfile(resolvedExisting.Id, diskOrigin)
                .TrustedLocalAuthoring;
        }

        string agentStampedJson = CustomWidgetManifestMetadataStamper.StampProvenance(
            manifestJson,
            origin: preserveCommunityOrigin ? CustomWidgetOriginKinds.Community : CustomWidgetOriginKinds.Agent,
            isCustomized: true);

        GlanceWidgetInstanceInfo upserted;
        if (resolvedExisting is not null)
        {
            upserted = await UpdateCustomWidgetManifestAsync(
                resolvedExisting.Id,
                agentStampedJson,
                resizeToDefault,
                reveal,
                logicJs,
                updateLogicJs,
                locales,
                updateLocales);
        }
        else
        {
            upserted = await CreateCustomWidgetAsync(
                $"json:{agentStampedJson}",
                logicJsOverride: logicJs,
                hasLogicJsOverride: updateLogicJs,
                localesOverride: locales,
                hasLocalesOverride: updateLocales);
        }

        NotifyStudioAgentWidgetUpserted?.Invoke(
            upserted.Id,
            upserted.Name,
            agentStampedJson,
            logicJs,
            updateLogicJs,
            locales,
            updateLocales);
        return upserted;
    }

    public async Task<(bool Success, string? WidgetId, string? Error)> PushCustomWidgetDataAsync(
        string widgetIdOrTemplateId,
        IReadOnlyDictionary<string, string>? stateUpdates,
        string? collectionName = null,
        string? collectionMode = null,
        IReadOnlyList<Dictionary<string, string>>? records = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                PushCustomWidgetDataAsync(widgetIdOrTemplateId, stateUpdates, collectionName, collectionMode, records));
        }

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetIdOrTemplateId);
        if (config is null &&
            (string.Equals(widgetIdOrTemplateId, CustomWidgetTemplates.AgentHudTemplateId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(widgetIdOrTemplateId, "deskbox.agent-hud", StringComparison.OrdinalIgnoreCase)))
        {
            GlanceWidgetInstanceInfo created = await CreateCustomWidgetAsync(CustomWidgetTemplates.AgentHudTemplateId);
            config = ResolveCustomWidgetConfig(created.Id);
        }

        if (config is null)
        {
            return (false, null, $"Custom widget '{widgetIdOrTemplateId}' not found.");
        }

        IReadOnlyList<CustomWidgetContentAdapter> liveAdapters = FindLiveCustomWidgetAdapters(config.Id);
        if (liveAdapters.Count > 0)
        {
            foreach (CustomWidgetContentAdapter adapter in liveAdapters)
            {
                await adapter.ViewModel.ApplyExternalDataPushAsync(
                    stateUpdates,
                    collectionName,
                    collectionMode,
                    records);
            }
            return (true, config.Id, null);
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);
        CustomWidgetStorePayload payload = await store.LoadAsync();
        string? rawManifestJson = await store.TryReadExistingManifestJsonAsync();
        CustomWidgetManifest? diskManifest = !string.IsNullOrWhiteSpace(rawManifestJson)
            ? CustomWidgetManifestParser.Parse(rawManifestJson).Manifest
            : null;

        if (stateUpdates is not null)
        {
            foreach ((string k, string v) in stateUpdates)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    payload.State[k.StartsWith("state.", StringComparison.Ordinal) ? k["state.".Length..] : k] = v ?? string.Empty;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            string col = collectionName.Trim();
            if (!payload.Collections.TryGetValue(col, out List<CustomWidgetRecord>? list))
            {
                list = [];
                payload.Collections[col] = list;
            }

            int maxItems = diskManifest is not null &&
                           diskManifest.Collections.TryGetValue(col, out CustomWidgetCollectionSchema? schema)
                ? Math.Clamp(schema.MaxItems, 1, 200)
                : 200;

            string mode = (collectionMode ?? "prepend").Trim().ToLowerInvariant();
            if (mode is "clear" or "replace")
            {
                list.Clear();
            }

            if (records is { Count: > 0 } && mode != "clear")
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var rec = new CustomWidgetRecord
                    {
                        Id = records[i].TryGetValue("id", out string? idVal) && !string.IsNullOrWhiteSpace(idVal)
                            ? idVal.Trim()
                            : Guid.NewGuid().ToString("N")[..10],
                        CreatedAt = DateTimeOffset.Now
                    };
                    foreach ((string fk, string fv) in records[i])
                    {
                        if (!string.Equals(fk, "id", StringComparison.Ordinal))
                        {
                            rec.Fields[fk] = fv ?? string.Empty;
                        }
                    }

                    if (mode == "upsert")
                    {
                        int existingIdx = list.FindIndex(r => string.Equals(r.Id, rec.Id, StringComparison.Ordinal));
                        if (existingIdx >= 0)
                        {
                            rec.CreatedAtUtcTicks = list[existingIdx].CreatedAtUtcTicks;
                            rec.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                            list[existingIdx] = rec;
                            continue;
                        }
                        list.Insert(0, rec);
                    }
                    else if (mode is "append" or "replace")
                    {
                        list.Add(rec);
                    }
                    else
                    {
                        list.Insert(i < list.Count ? i : list.Count, rec);
                    }
                }

                if (list.Count > maxItems)
                {
                    list.RemoveRange(maxItems, list.Count - maxItems);
                }
            }
        }

        await store.SaveAsync(payload);
        return (true, config.Id, null);
    }

    public async Task<CustomWidgetLiveRuntimeSnapshot?> TryGetCustomWidgetLiveSnapshotAsync(
        string widgetIdOrTemplateId)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                TryGetCustomWidgetLiveSnapshotAsync(widgetIdOrTemplateId));
        }

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetIdOrTemplateId);
        if (config is null)
        {
            return null;
        }

        CustomWidgetContentAdapter? adapter = FindLiveCustomWidgetAdapters(config.Id).FirstOrDefault();
        if (adapter is null)
        {
            return null;
        }

        CustomWidgetViewModel vm = adapter.ViewModel;
        for (int waitIdx = 0; waitIdx < 12; waitIdx++)
        {
            if (!vm.State.TryGetValue("sourceStatus", out string? status) ||
                !string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(100);
        }

        var stateCopy = new Dictionary<string, string>(vm.State, StringComparer.Ordinal);
        var countsCopy = new Dictionary<string, int>(StringComparer.Ordinal);
        var samplesCopy = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);

        foreach ((string colName, List<CustomWidgetRecord> records) in vm.Collections)
        {
            countsCopy[colName] = records.Count;
            var sampleList = new List<Dictionary<string, string>>(Math.Min(3, records.Count));
            for (int i = 0; i < records.Count && i < 3; i++)
            {
                CustomWidgetRecord rec = records[i];
                var itemDict = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = rec.Id
                };
                foreach ((string fk, string fv) in rec.Fields)
                {
                    itemDict[fk] = fv;
                }
                sampleList.Add(itemDict);
            }
            samplesCopy[colName] = sampleList;
        }

        _contentWidgets.TryGetValue(config.Id, out Views.ContentWidgetWindow? hostWindow);
        string displayMode = hostWindow is null || hostWindow.CurrentCompactViewState == WidgetCompactViewState.Open
            ? "expanded"
            : "capsule";

        return new CustomWidgetLiveRuntimeSnapshot(
            LiveLoaded: true,
            DisplayMode: displayMode,
            State: stateCopy,
            CollectionCounts: countsCopy,
            SampleItems: samplesCopy);
    }

    public async Task<(byte[]? PngBytes, int Width, int Height, string? Error)> CaptureCustomWidgetPreviewPngAsync(
        string widgetIdOrTemplateId,
        string? themeOverride = null,
        string? mode = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                CaptureCustomWidgetPreviewPngAsync(widgetIdOrTemplateId, themeOverride, mode));
        }

        string effectiveMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "capsule" => "capsule",
            "live" => "live",
            _ => "expanded"
        };

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetIdOrTemplateId);

        // P2：Agent 调用 deskbox_upsert_widget 默认进入暂存区（尚未上桌面或尚未覆盖桌面实例）。
        // 此时调用 deskbox_capture_widget_preview 做视觉自检，必须优先渲染最新暂存包，
        // 否则新格子会报 not found，已有格子会截到修改前的旧画面。
        if (CustomWidgetAgentStageStore.TryFindLatestPendingBundle(widgetIdOrTemplateId, out AgentStageBundle? stagedBundle) &&
            stagedBundle is not null)
        {
            return await CaptureStagedBundlePreviewPngAsync(stagedBundle, config, themeOverride, effectiveMode);
        }

        if (config is null)
        {
            return (null, 0, 0, $"Custom widget '{widgetIdOrTemplateId}' not found.");
        }

        if (!_contentWidgets.TryGetValue(config.Id, out ContentWidgetWindow? window))
        {
            await ShowWidgetAsync(config.Id, reveal: false, autoRestoreOnReveal: false);
            _contentWidgets.TryGetValue(config.Id, out window);
        }

        if (window is null)
        {
            return (null, 0, 0, $"Custom widget window '{config.Id}' could not be materialized.");
        }

        await window.ContentReadyTask;

        CustomWidgetContentAdapter? targetAdapter = FindLiveCustomWidgetAdapters(config.Id).FirstOrDefault();
        bool isActiveGroupTab = window.CurrentContent is CustomWidgetContentAdapter currentAdapter &&
                                string.Equals(currentAdapter.WidgetId, config.Id, StringComparison.Ordinal);

        if (targetAdapter is not null)
        {
            for (int waitIdx = 0; waitIdx < 15; waitIdx++)
            {
                if (!targetAdapter.ViewModel.State.TryGetValue("sourceStatus", out string? st) ||
                    !string.Equals(st, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(100);
            }
        }

        UIElement? captureRoot = window.Content ?? window.CurrentContent?.View;
        if (captureRoot is not FrameworkElement fe)
        {
            return (null, 0, 0, "Widget visual tree is not ready.");
        }

        if (!fe.IsLoaded || fe.ActualWidth < 4 || fe.ActualHeight < 4)
        {
            await Task.Delay(80);
        }

        bool isDarkTheme = string.Equals(themeOverride, "light", StringComparison.OrdinalIgnoreCase)
            ? false
            : string.Equals(themeOverride, "dark", StringComparison.OrdinalIgnoreCase)
                ? true
                : fe.ActualTheme == ElementTheme.Dark ||
                  (fe.ActualTheme == ElementTheme.Default && Win32Helper.IsSystemDarkMode());

        bool isLiveCollapsed = window.CurrentCompactViewState != WidgetCompactViewState.Open || fe.ActualHeight < 96;
        if (targetAdapter is not null &&
            (!isActiveGroupTab ||
             (effectiveMode == "expanded" && (isLiveCollapsed || !fe.IsLoaded || fe.ActualWidth < 100)) ||
             (effectiveMode == "capsule" && !isLiveCollapsed)))
        {
            return await CaptureSyntheticCustomWidgetFramePngAsync(
                config,
                targetAdapter.ViewModel,
                renderCapsule: effectiveMode == "capsule",
                isDarkTheme: isDarkTheme);
        }

        ElementTheme previousTheme = fe.RequestedTheme;
        bool changedTheme = false;
        if (string.Equals(themeOverride, "light", StringComparison.OrdinalIgnoreCase) &&
            previousTheme != ElementTheme.Light)
        {
            fe.RequestedTheme = ElementTheme.Light;
            changedTheme = true;
        }
        else if (string.Equals(themeOverride, "dark", StringComparison.OrdinalIgnoreCase) &&
                 previousTheme != ElementTheme.Dark)
        {
            fe.RequestedTheme = ElementTheme.Dark;
            changedTheme = true;
        }

        try
        {
            if (changedTheme)
            {
                fe.UpdateLayout();
                await Task.Delay(40);
            }

            return await RenderElementToPngAsync(fe, isDarkTheme, compositeOpaqueBackdrop: true);
        }
        catch (Exception ex)
        {
            return (null, 0, 0, $"RenderTargetBitmap capture failed: {ex.Message}");
        }
        finally
        {
            if (changedTheme)
            {
                fe.RequestedTheme = previousTheme;
            }
        }
    }

    private async Task<(byte[]? PngBytes, int Width, int Height, string? Error)> CaptureStagedBundlePreviewPngAsync(
        AgentStageBundle stagedBundle,
        WidgetConfig? existingConfig,
        string? themeOverride,
        string effectiveMode)
    {
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(stagedBundle.ManifestJson);
        if (parsed.Manifest is null)
        {
            return (null, 0, 0, "Staged manifest could not be parsed.");
        }

        CustomWidgetDataStore? existingStore = existingConfig is not null
            ? CustomWidgetDataStore.ForWidget(existingConfig.Id)
            : null;
        string? logicJs = stagedBundle.HasLogicJs
            ? stagedBundle.LogicJs
            : existingStore?.ReadLogicJsOrNull();
        string effectivePreviewManifestJson = !string.IsNullOrWhiteSpace(logicJs)
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(stagedBundle.ManifestJson, logicJs)
            : stagedBundle.ManifestJson;
        CustomWidgetParseResult syncedParsed = !ReferenceEquals(effectivePreviewManifestJson, stagedBundle.ManifestJson)
            ? CustomWidgetManifestParser.Parse(effectivePreviewManifestJson)
            : parsed;
        CustomWidgetManifest baseManifest = syncedParsed.Manifest ?? parsed.Manifest;
        CustomWidgetLocaleCatalog? locales = stagedBundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(stagedBundle.LocalesJson)
            : (existingStore is not null && existingStore.ReadLocaleCatalog() is { HasAnyLocale: true } diskCat
                ? diskCat
                : null);

        CustomWidgetManifest effectiveManifest = locales is { HasAnyLocale: true }
            ? baseManifest.WithLocales(locales)
            : baseManifest;
        string title = existingConfig?.Name is { Length: > 0 } existingName
            ? existingName
            : effectiveManifest.ResolveLocalizedName(_localizationService.CurrentCultureName);

        var tempConfig = new WidgetConfig
        {
            Id = existingConfig?.Id ?? effectiveManifest.Id,
            Name = title,
            WidgetKind = WidgetKind.Custom,
            Width = stagedBundle.ResizeToDefault || existingConfig is null || existingConfig.Width <= 180
                ? effectiveManifest.DefaultSize.Width
                : existingConfig.Width,
            Height = stagedBundle.ResizeToDefault || existingConfig is null || existingConfig.Height <= 160
                ? effectiveManifest.DefaultSize.Height
                : existingConfig.Height
        };

        bool isDarkTheme = string.Equals(themeOverride, "light", StringComparison.OrdinalIgnoreCase)
            ? false
            : string.Equals(themeOverride, "dark", StringComparison.OrdinalIgnoreCase)
                ? true
                : Win32Helper.IsSystemDarkMode();

        var vm = new CustomWidgetViewModel(
            tempConfig,
            _localizationService,
            store: existingStore,
            dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            notificationServiceProvider: null,
            isPreviewOnly: true,
            settingsService: _settingsService)
        {
            IsUntrustedStagePreview = true
        };
        try
        {
            vm.ApplyPreviewBundle(effectivePreviewManifestJson, logicJs, locales, preserveRuntimeData: false);
            await vm.RefreshMusicNowAsync(triggerRebuild: false);
            await vm.RefreshTodoNowAsync(triggerRebuild: false);
            for (int waitIdx = 0; waitIdx < 15; waitIdx++)
            {
                if (!vm.State.TryGetValue("sourceStatus", out string? st) ||
                    !string.Equals(st, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(100);
            }

            return await CaptureSyntheticCustomWidgetFramePngAsync(
                tempConfig,
                vm,
                renderCapsule: effectiveMode == "capsule",
                isDarkTheme: isDarkTheme);
        }
        finally
        {
            vm.Dispose();
        }
    }

    private async Task<(byte[]? PngBytes, int Width, int Height, string? Error)> CaptureSyntheticCustomWidgetFramePngAsync(
        WidgetConfig config,
        CustomWidgetViewModel vm,
        bool renderCapsule,
        bool isDarkTheme)
    {
        var hostWindow = new Window();
        CustomWidgetNativeRenderer? tempRenderer = null;
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(hostWindow);
            WindowShellState.TryHideFromSwitchers(hostWindow.AppWindow);
            WindowShellState.TryApplyBorderlessOverlappedPresenter(hostWindow.AppWindow);

            int exStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLong(
                hwnd,
                Win32Helper.GWL_EXSTYLE,
                exStyle |
                Win32Helper.WS_EX_TOOLWINDOW |
                Win32Helper.WS_EX_NOACTIVATE |
                Win32Helper.WS_EX_LAYERED |
                Win32Helper.WS_EX_TRANSPARENT);
            Win32Helper.SetLayeredWindowAttributes(hwnd, 0, 0, Win32Helper.LWA_ALPHA);
            Win32Helper.SetWindowTheme(hwnd, isDarkTheme);

            double frameWidth = renderCapsule
                ? 348.0
                : Math.Clamp(config.Width > 180 ? config.Width : vm.Manifest.DefaultSize.Width, 240.0, 640.0);
            double frameHeight = renderCapsule
                ? 52.0
                : Math.Clamp(config.Height > 160 ? config.Height : vm.Manifest.DefaultSize.Height, 180.0, 900.0);

            Color surfaceBg = isDarkTheme
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 249, 249, 249);
            Color strokeColor = isDarkTheme
                ? Color.FromArgb(32, 255, 255, 255)
                : Color.FromArgb(22, 0, 0, 0);
            Color primaryFg = isDarkTheme
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(230, 0, 0, 0);
            Color secondaryFg = isDarkTheme
                ? Color.FromArgb(196, 255, 255, 255)
                : Color.FromArgb(156, 0, 0, 0);

            var frameBorder = new Border
            {
                Width = frameWidth,
                Height = frameHeight,
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
                CornerRadius = new CornerRadius(renderCapsule ? 26 : 8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(strokeColor),
                Background = new SolidColorBrush(surfaceBg),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            string emoji = CustomWidgetTemplates.IsEmojiIcon(vm.Manifest.Icon)
                ? vm.Manifest.Icon.Trim()
                : "\u2728";
            string resolvedTitle = !string.IsNullOrWhiteSpace(config.Name)
                ? config.Name
                : vm.Manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);

            if (renderCapsule)
            {
                var capsuleRow = new Grid
                {
                    Padding = new Thickness(16, 0, 16, 0),
                    ColumnSpacing = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };
                capsuleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                capsuleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                capsuleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                capsuleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                capsuleRow.Children.Add(new TextBlock
                {
                    Text = emoji,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var capTitle = new TextBlock
                {
                    Text = !string.IsNullOrWhiteSpace(vm.CapsuleTitle) ? vm.CapsuleTitle : resolvedTitle,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(primaryFg),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(capTitle, 1);
                capsuleRow.Children.Add(capTitle);

                var capSummary = new TextBlock
                {
                    Text = vm.CapsuleSummary ?? string.Empty,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(secondaryFg),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(capSummary, 2);
                capsuleRow.Children.Add(capSummary);

                if (!string.IsNullOrWhiteSpace(vm.CapsuleBadge))
                {
                    var badgeText = new TextBlock
                    {
                        Text = vm.CapsuleBadge,
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(primaryFg),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(badgeText, 3);
                    capsuleRow.Children.Add(badgeText);
                }

                frameBorder.Child = capsuleRow;
            }
            else
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var head = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Padding = new Thickness(12, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                head.Children.Add(new TextBlock
                {
                    Text = emoji,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
                head.Children.Add(new TextBlock
                {
                    Text = resolvedTitle,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(primaryFg),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    VerticalAlignment = VerticalAlignment.Center
                });
                grid.Children.Add(head);

                var divider = new Border { Background = new SolidColorBrush(strokeColor) };
                Grid.SetRow(divider, 1);
                grid.Children.Add(divider);

                tempRenderer = new CustomWidgetNativeRenderer(
                    vm,
                    frameBorder,
                    () => _settingsService.Settings.WidgetShell.TextSize);
                UIElement bodyElement = tempRenderer.BuildRootElement();
                if (bodyElement is FrameworkElement bodyFe)
                {
                    Grid.SetRow(bodyFe, 2);
                }
                grid.Children.Add(bodyElement);
                frameBorder.Child = grid;
            }

            var stageHost = new Grid
            {
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
                Width = frameWidth + 8,
                Height = frameHeight + 8,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            stageHost.Children.Add(frameBorder);

            var loadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            frameBorder.Loaded += (_, _) => loadedTcs.TrySetResult();

            hostWindow.Content = stageHost;
            double scale = Win32Helper.GetDpiScaleForPoint(80, 80);
            int physW = Math.Max(320, (int)Math.Ceiling((frameWidth + 48) * scale));
            int physH = Math.Max(160, (int)Math.Ceiling((frameHeight + 64) * scale));
            hostWindow.AppWindow.MoveAndResize(new RectInt32(80, 80, physW, physH));
            Win32Helper.ShowWindow(hwnd, Win32Helper.SW_SHOWNOACTIVATE);

            await Task.WhenAny(loadedTcs.Task, Task.Delay(350));
            frameBorder.UpdateLayout();
            await Task.Delay(60);
            frameBorder.UpdateLayout();

            return await RenderElementToPngAsync(frameBorder, isDarkTheme, compositeOpaqueBackdrop: false);
        }
        catch (Exception ex)
        {
            return (null, 0, 0, $"Offscreen expanded widget preview capture failed: {ex.Message}");
        }
        finally
        {
            tempRenderer?.ReleaseResources();
            try
            {
                hostWindow.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task<(byte[]? PngBytes, int Width, int Height, string? Error)> RenderElementToPngAsync(
        FrameworkElement fe,
        bool isDarkTheme,
        bool compositeOpaqueBackdrop)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(fe);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0)
        {
            return (null, 0, 0, "Captured bitmap was empty.");
        }

        IBuffer pixelBuffer = await rtb.GetPixelsAsync();
        byte[] bgraPixels = pixelBuffer.ToArray();
        if (compositeOpaqueBackdrop)
        {
            byte bgB = isDarkTheme ? (byte)34 : (byte)249;
            byte bgG = isDarkTheme ? (byte)32 : (byte)247;
            byte bgR = isDarkTheme ? (byte)32 : (byte)247;
            for (int i = 0; i + 3 < bgraPixels.Length; i += 4)
            {
                int a = bgraPixels[i + 3];
                if (a < 255)
                {
                    int invA = 255 - a;
                    bgraPixels[i] = (byte)Math.Min(255, bgraPixels[i] + ((bgB * invA + 127) / 255));
                    bgraPixels[i + 1] = (byte)Math.Min(255, bgraPixels[i + 1] + ((bgG * invA + 127) / 255));
                    bgraPixels[i + 2] = (byte)Math.Min(255, bgraPixels[i + 2] + ((bgR * invA + 127) / 255));
                    bgraPixels[i + 3] = 255;
                }
            }
        }
        using var outStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        double dpi = 96.0 * (fe.XamlRoot?.RasterizationScale ?? 1.0);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            dpi,
            dpi,
            bgraPixels);
        await encoder.FlushAsync();

        byte[] pngBytes = new byte[(int)outStream.Size];
        outStream.Seek(0);
        using Stream reader = outStream.AsStreamForRead();
        int offset = 0;
        while (offset < pngBytes.Length)
        {
            int read = await reader.ReadAsync(pngBytes.AsMemory(offset, pngBytes.Length - offset));
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }

        return (pngBytes, rtb.PixelWidth, rtb.PixelHeight, null);
    }
}
