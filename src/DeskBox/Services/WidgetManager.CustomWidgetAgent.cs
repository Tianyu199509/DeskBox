using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    IReadOnlyDictionary<string, JsonElement> StateJson,
    IReadOnlyDictionary<string, List<Dictionary<string, JsonElement>>> SampleItemsJson);

public sealed record CustomWidgetPreviewCaptureResult(
    byte[]? PngBytes,
    int Width,
    int Height,
    double MeasuredWidth,
    double MeasuredHeight,
    double VerticalOverflowPx,
    bool HasOverflow,
    string? Error,
    double DpiScale = 1.0);

public sealed partial class WidgetManager
{
    internal string ActiveCultureName => _localizationService.CurrentCultureName;
    internal Func<string?>? ResolveStudioAgentUpsertTargetWidgetId { get; set; }
    internal Action<string, string, string, string?, bool, CustomWidgetLocaleCatalog?, bool>? NotifyStudioAgentWidgetUpserted { get; set; }

    internal WidgetConfig? ResolveCustomWidgetConfig(
        string? widgetIdOrTemplateId,
        bool allowTemplateMetadataFallback = true)
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
        if (byId is not null || !allowTemplateMetadataFallback)
        {
            return byId;
        }

        string altNeedle = needle.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? needle["deskbox.".Length..]
            : $"deskbox.{needle}";

        WidgetConfig? byTemplate = _settingsService.Settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            w.WidgetKind == WidgetKind.Custom &&
            !IsDeleted(w.Id) &&
            w.Metadata.TryGetValue(CustomWidgetViewModel.TemplateMetadataKey, out string? tpl) &&
            (string.Equals(tpl, needle, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(tpl, altNeedle, StringComparison.OrdinalIgnoreCase)));
        if (byTemplate is not null)
        {
            return byTemplate;
        }

        foreach (WidgetConfig w in _settingsService.Settings.WidgetLayout.Widgets)
        {
            if (w.WidgetKind != WidgetKind.Custom || IsDeleted(w.Id) || string.IsNullOrWhiteSpace(w.Id))
            {
                continue;
            }

            try
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(w.Id);
                if (!File.Exists(store.ManifestPath))
                {
                    continue;
                }

                using var fs = new FileStream(store.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument doc = JsonDocument.Parse(fs);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("forkedFromId", out JsonElement forkEl) &&
                    forkEl.ValueKind == JsonValueKind.String)
                {
                    string? forkedFrom = forkEl.GetString();
                    if (!string.IsNullOrWhiteSpace(forkedFrom) &&
                        (string.Equals(forkedFrom, needle, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(forkedFrom, altNeedle, StringComparison.OrdinalIgnoreCase)))
                    {
                        return w;
                    }
                }

                if (doc.RootElement.TryGetProperty("id", out JsonElement manifestIdEl) &&
                    manifestIdEl.ValueKind == JsonValueKind.String)
                {
                    string? manifestId = manifestIdEl.GetString();
                    if (!string.IsNullOrWhiteSpace(manifestId) &&
                        (string.Equals(manifestId, needle, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(manifestId, altNeedle, StringComparison.OrdinalIgnoreCase)))
                    {
                        return w;
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public async Task<GlanceWidgetInstanceInfo> UpdateCustomWidgetManifestAsync(
        string widgetId,
        string manifestJson,
        bool resizeToDefault = false,
        bool reveal = false,
        string? logicJs = null,
        bool updateLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = false,
        bool trustedHostWrite = true)
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
                    updateLocales,
                    trustedHostWrite));
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

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetId, allowTemplateMetadataFallback: false);
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
                        else if (prevManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
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

        // Channel-scoped executable-surface fingerprint: Studio saves (Local/Official
        // origin) extend trust silently; Agent/Community overwrites with a changed
        // surface revoke positive privileged overrides.
        string? effectiveSurfaceLogicJs = updateLogicJs
            ? logicJs
            : (File.Exists(store.LogicJsPath)
                ? await File.ReadAllTextAsync(store.LogicJsPath)
                : null);
        bool isExternalOrigin =
            string.Equals(manifest.Origin, CustomWidgetOriginKinds.Agent, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(manifest.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase);
        CustomWidgetTrustStore.Shared.RecordExecutableSurfaceAfterWrite(
            config.Id,
            stampedJson,
            effectiveSurfaceLogicJs,
            trustedLocalAuthoring: trustedHostWrite,
            preserveExistingTrustedStateOnHostWrite: trustedHostWrite && isExternalOrigin);

        if (File.Exists(Path.Combine(store.WidgetDirectory, "AGENTS.md")))
        {
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, config.Id);
        }

        CustomWidgetLocaleCatalog? effectiveLocales = updateLocales
            ? locales
            : await store.ReadLazyRuntimeLocalesAsync(_localizationService.CurrentCultureName);
        if (effectiveLocales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(effectiveLocales);
        }

        config.Metadata[CustomWidgetViewModel.TemplateMetadataKey] = manifest.Id;
        if (!string.IsNullOrWhiteSpace(manifest.Name) &&
            !CustomWidgetTemplates.IsKnownBuiltInTemplate(manifest.Id))
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

        ApplyCustomWidgetRevealOnUpdate(
            config,
            reveal,
            () => GetFeatureWidgetEnabledState(WidgetKind.Custom),
            () => SetFeatureWidgetEnabledState(WidgetKind.Custom, true));

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

    internal static void ApplyCustomWidgetRevealOnUpdate(
        WidgetConfig config,
        bool reveal,
        Func<bool> isFeatureEnabled,
        Action enableFeature)
    {
        if (!reveal)
        {
            return;
        }

        if (!isFeatureEnabled())
        {
            enableFeature();
        }

        config.Metadata.Remove(CustomParkedByUserMetadataKey);
        config.IsDisabled = false;
        config.IsVisible = true;
    }

    public async Task<GlanceWidgetInstanceInfo?> UpdateCustomWidgetIconOnlyAsync(
        string widgetId,
        string? emojiIcon)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => UpdateCustomWidgetIconOnlyAsync(widgetId, emojiIcon));
        }

        WidgetConfig? config = ResolveCustomWidgetConfig(widgetId, allowTemplateMetadataFallback: false);
        if (config is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(emojiIcon) && !CustomWidgetTemplates.IsEmojiIcon(emojiIcon))
        {
            throw new InvalidDataException("Invalid emoji icon.");
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);
        string? existingManifestJson = await store.TryReadExistingManifestJsonAsync();
        if (string.IsNullOrWhiteSpace(existingManifestJson))
        {
            if (config.Metadata.TryGetValue(CustomWidgetViewModel.TemplateMetadataKey, out string? tpl) &&
                CustomWidgetTemplates.IsKnownBuiltInTemplate(tpl))
            {
                existingManifestJson = await store.ReadOrCreateManifestJsonAsync(tpl);
            }
            else
            {
                return null;
            }
        }

        string updatedManifestJson;
        if (!string.IsNullOrEmpty(emojiIcon))
        {
            string trimmedEmoji = emojiIcon.Trim();
            updatedManifestJson = CustomWidgetTemplates.WithUpdatedIconJson(existingManifestJson, trimmedEmoji);
            WidgetTitleIconCustomization.SetEmojiOverride(config, trimmedEmoji);
        }
        else
        {
            updatedManifestJson = ClearEmojiIconPropertiesJson(existingManifestJson);
            WidgetTitleIconCustomization.SetEmojiOverride(config, null);
        }

        await store.WriteManifestJsonAsync(updatedManifestJson);

        _settingsService.UpdateWidget(config);
        await _settingsService.SaveAsync();

        IReadOnlyList<CustomWidgetContentAdapter> liveAdapters = FindLiveCustomWidgetAdapters(config.Id);
        foreach (CustomWidgetContentAdapter adapter in liveAdapters)
        {
            await adapter.ViewModel.ReloadManifestFromDiskAsync(notifyFeedback: true);
        }

        return new GlanceWidgetInstanceInfo(
            config.Id,
            config.Name,
            GetFeatureWidgetEnabledState(WidgetKind.Custom) && !config.IsDisabled);
    }

    private static string ClearEmojiIconPropertiesJson(string rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return rawManifestJson;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return rawManifestJson;
            }

            bool stripTopLevelIcon = doc.RootElement.TryGetProperty("icon", out JsonElement topIconEl) &&
                                     (topIconEl.ValueKind == JsonValueKind.Null ||
                                      (topIconEl.ValueKind == JsonValueKind.String &&
                                       (string.IsNullOrWhiteSpace(topIconEl.GetString()) ||
                                        CustomWidgetTemplates.IsEmojiIcon(topIconEl.GetString()))));
            bool stripCapsuleEmojiGlyph = doc.RootElement.TryGetProperty("capsule", out JsonElement capsuleEl) &&
                                          capsuleEl.ValueKind == JsonValueKind.Object &&
                                          capsuleEl.TryGetProperty("glyph", out JsonElement capGlyphEl) &&
                                          capGlyphEl.ValueKind == JsonValueKind.String &&
                                          CustomWidgetTemplates.IsEmojiIcon(capGlyphEl.GetString());
            if (!stripTopLevelIcon && !stripCapsuleEmojiGlyph)
            {
                return rawManifestJson;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (stripTopLevelIcon && string.Equals(prop.Name, "icon", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (stripCapsuleEmojiGlyph &&
                        string.Equals(prop.Name, "capsule", StringComparison.Ordinal) &&
                        prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("capsule");
                        writer.WriteStartObject();
                        foreach (JsonProperty capProp in prop.Value.EnumerateObject())
                        {
                            if (string.Equals(capProp.Name, "glyph", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            capProp.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                        continue;
                    }

                    prop.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return rawManifestJson;
        }
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

        WidgetConfig? resolvedExisting = null;
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            resolvedExisting = ResolveCustomWidgetConfig(widgetId, allowTemplateMetadataFallback: false);
        }

        CustomWidgetParseResult incomingParsed = CustomWidgetManifestParser.Parse(manifestJson);
        bool incomingIsCommunity = string.Equals(
            incomingParsed.Manifest?.Origin,
            CustomWidgetOriginKinds.Community,
            StringComparison.OrdinalIgnoreCase);

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
            CustomWidgetTrustStore.Shared.RegisterWidgetProvenance(
                resolvedExisting.Id,
                trustedLocalAuthoring: false);
            upserted = await UpdateCustomWidgetManifestAsync(
                resolvedExisting.Id,
                agentStampedJson,
                resizeToDefault,
                reveal,
                logicJs,
                updateLogicJs,
                locales,
                updateLocales,
                trustedHostWrite: false);
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
        IReadOnlyList<Dictionary<string, string>>? records = null,
        IReadOnlyDictionary<string, JsonElement>? stateJsonUpdates = null,
        IReadOnlyList<Dictionary<string, JsonElement>>? jsonRecords = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                PushCustomWidgetDataAsync(
                    widgetIdOrTemplateId,
                    stateUpdates,
                    collectionName,
                    collectionMode,
                    records,
                    stateJsonUpdates,
                    jsonRecords));
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
                    records,
                    stateJsonUpdates,
                    jsonRecords);
            }
            return (true, config.Id, null);
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);
        CustomWidgetStorePayload payload = await store.LoadAsync();
        string? rawManifestJson = await store.TryReadExistingManifestJsonAsync();
        CustomWidgetManifest? diskManifest = !string.IsNullOrWhiteSpace(rawManifestJson)
            ? CustomWidgetManifestParser.Parse(rawManifestJson).Manifest
            : null;

        bool mutated = CustomWidgetStoreMutator.ApplyExternalDataPushToPayload(
            payload,
            diskManifest,
            stateUpdates,
            collectionName,
            collectionMode,
            records,
            stateJsonUpdates,
            jsonRecords);

        if (mutated || !File.Exists(store.StorePath))
        {
            await store.SaveAsync(payload);
        }

        return (true, config.Id, null);
    }

    internal async Task<bool> TrySeedLiveCreatedWidgetAsync(
        string widgetId,
        IReadOnlyDictionary<string, string>? userState,
        IReadOnlyDictionary<string, JsonElement>? userStateJson,
        IReadOnlyDictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>? userCollections,
        IReadOnlyDictionary<string, string>? sampleState,
        IReadOnlyDictionary<string, JsonElement>? sampleStateJson,
        IReadOnlyDictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>? sampleCollections)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                TrySeedLiveCreatedWidgetAsync(
                    widgetId,
                    userState,
                    userStateJson,
                    userCollections,
                    sampleState,
                    sampleStateJson,
                    sampleCollections));
        }

        IReadOnlyList<CustomWidgetContentAdapter> liveAdapters = FindLiveCustomWidgetAdapters(widgetId);
        if (liveAdapters.Count == 0)
        {
            return false;
        }

        foreach (CustomWidgetContentAdapter adapter in liveAdapters)
        {
            CustomWidgetViewModel vm = adapter.ViewModel;
            for (int waitIdx = 0; waitIdx < 5; waitIdx++)
            {
                if (!vm.State.TryGetValue("sourceStatus", out string? status) ||
                    !string.Equals(status, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(40);
            }

            var mergedState = new Dictionary<string, string>(StringComparer.Ordinal);
            var mergedStateJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (sampleState is not null)
            {
                foreach ((string k, string v) in sampleState)
                {
                    if (!IsTransientRuntimeStateKey(k) &&
                        (!vm.State.TryGetValue(k, out string? existing) || string.IsNullOrWhiteSpace(existing)))
                    {
                        mergedState[k] = v;
                        if (sampleStateJson is not null && sampleStateJson.TryGetValue(k, out JsonElement jv))
                        {
                            mergedStateJson[k] = jv.Clone();
                        }
                    }
                }
            }

            if (userState is not null)
            {
                foreach ((string k, string v) in userState)
                {
                    if (!IsTransientRuntimeStateKey(k) &&
                        (!string.IsNullOrWhiteSpace(v) || !vm.State.TryGetValue(k, out string? existing) || string.IsNullOrWhiteSpace(existing)))
                    {
                        mergedState[k] = v;
                        if (userStateJson is not null && userStateJson.TryGetValue(k, out JsonElement jv))
                        {
                            mergedStateJson[k] = jv.Clone();
                        }
                    }
                }
            }

            var collectionsToApply = new Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>(StringComparer.Ordinal);
            if (sampleCollections is not null)
            {
                foreach ((string col, var tuple) in sampleCollections)
                {
                    if (tuple.Items.Count > 0 &&
                        (!vm.Collections.TryGetValue(col, out List<CustomWidgetRecord>? existing) || existing.Count == 0))
                    {
                        collectionsToApply[col] = tuple;
                    }
                }
            }

            if (userCollections is not null)
            {
                foreach ((string col, var tuple) in userCollections)
                {
                    if (tuple.Items.Count > 0 ||
                        !vm.Collections.TryGetValue(col, out List<CustomWidgetRecord>? existing) ||
                        existing.Count == 0)
                    {
                        collectionsToApply[col] = tuple;
                    }
                }
            }

            if (collectionsToApply.Count == 0)
            {
                if (mergedState.Count > 0)
                {
                    await vm.ApplyExternalDataPushAsync(mergedState, null, null, null, mergedStateJson, null);
                }
                continue;
            }

            bool stateSent = false;
            foreach ((string col, var tuple) in collectionsToApply)
            {
                await vm.ApplyExternalDataPushAsync(
                    stateSent ? null : (mergedState.Count > 0 ? mergedState : null),
                    col,
                    "replace",
                    tuple.Items,
                    stateSent ? null : (mergedStateJson.Count > 0 ? mergedStateJson : null),
                    tuple.JsonItems);
                stateSent = true;
            }
        }

        return true;
    }

    internal static bool IsTransientRuntimeStateKey(string key) =>
        string.Equals(key, "sourceStatus", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "lastError", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "updatedAt", StringComparison.OrdinalIgnoreCase);

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
        var stateJsonCopy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string sk, JsonElement sv) in vm.StateJson)
        {
            if (sv.ValueKind != JsonValueKind.Undefined)
            {
                stateJsonCopy[sk] = sv.Clone();
            }
        }
        var countsCopy = new Dictionary<string, int>(StringComparer.Ordinal);
        var samplesJsonCopy = new Dictionary<string, List<Dictionary<string, JsonElement>>>(StringComparer.Ordinal);

        foreach ((string colName, List<CustomWidgetRecord> records) in vm.Collections)
        {
            countsCopy[colName] = records.Count;
            var sampleJsonList = new List<Dictionary<string, JsonElement>>(Math.Min(3, records.Count));
            for (int i = 0; i < records.Count && i < 3; i++)
            {
                CustomWidgetRecord rec = records[i];
                var itemJsonDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["id"] = CustomWidgetScriptEngine.CreateStringJsonElement(rec.Id)
                };
                foreach ((string fk, JsonElement fv) in rec.FieldsJson)
                {
                    if (!string.Equals(fk, "id", StringComparison.Ordinal) &&
                        fv.ValueKind != JsonValueKind.Undefined)
                    {
                        itemJsonDict[fk] = fv.Clone();
                    }
                }
                foreach ((string fk, string fv) in rec.Fields)
                {
                    if (!itemJsonDict.ContainsKey(fk))
                    {
                        itemJsonDict[fk] = CustomWidgetScriptEngine.CreateStringJsonElement(fv);
                    }
                }
                sampleJsonList.Add(itemJsonDict);
            }
            samplesJsonCopy[colName] = sampleJsonList;
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
            StateJson: stateJsonCopy,
            SampleItemsJson: samplesJsonCopy);
    }

    public async Task<(byte[]? PngBytes, int Width, int Height, string? Error)> CaptureCustomWidgetPreviewPngAsync(
        string widgetIdOrTemplateId,
        string? themeOverride = null,
        string? mode = null)
    {
        CustomWidgetPreviewCaptureResult result = await CaptureCustomWidgetPreviewWithTelemetryAsync(
            widgetIdOrTemplateId,
            themeOverride,
            mode,
            stageId: null);
        return (result.PngBytes, result.Width, result.Height, result.Error);
    }

    public async Task<CustomWidgetPreviewCaptureResult> CaptureCustomWidgetPreviewWithTelemetryAsync(
        string widgetIdOrTemplateId,
        string? themeOverride = null,
        string? mode = null,
        string? stageId = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() =>
                CaptureCustomWidgetPreviewWithTelemetryAsync(widgetIdOrTemplateId, themeOverride, mode, stageId));
        }

        string effectiveMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "capsule" => "capsule",
            "live" => "live",
            _ => "expanded"
        };

        WidgetConfig? config = !string.IsNullOrWhiteSpace(widgetIdOrTemplateId)
            ? ResolveCustomWidgetConfig(widgetIdOrTemplateId)
            : null;

        if (!string.IsNullOrWhiteSpace(stageId))
        {
            if (CustomWidgetAgentStageStore.TryReadBundle(
                    stageId.Trim(),
                    out AgentStageBundle? explicitStage,
                    out string stageErr,
                    _localizationService.CurrentCultureName) &&
                explicitStage is not null)
            {
                if (config is null && !string.IsNullOrWhiteSpace(explicitStage.WidgetId))
                {
                    config = ResolveCustomWidgetConfig(explicitStage.WidgetId);
                }
                return await CaptureStagedBundlePreviewWithTelemetryAsync(explicitStage, config, themeOverride, effectiveMode);
            }

            if (string.IsNullOrWhiteSpace(widgetIdOrTemplateId))
            {
                return new CustomWidgetPreviewCaptureResult(null, 0, 0, 0, 0, 0, false, stageErr);
            }
        }

        // P2：Agent 调用 deskbox_upsert_widget 默认进入暂存区（尚未上桌面或尚未覆盖桌面实例）。
        // 此时调用 deskbox_capture_widget_preview 做视觉自检，必须优先渲染最新暂存包，
        // 否则新格子会报 not found，已有格子会截到修改前的旧画面。
        if (!string.IsNullOrWhiteSpace(widgetIdOrTemplateId) &&
            CustomWidgetAgentStageStore.TryFindLatestPendingBundle(widgetIdOrTemplateId, out AgentStageBundle? stagedBundle) &&
            stagedBundle is not null)
        {
            return await CaptureStagedBundlePreviewWithTelemetryAsync(stagedBundle, config, themeOverride, effectiveMode);
        }

        if (config is null)
        {
            return new CustomWidgetPreviewCaptureResult(
                null, 0, 0, 0, 0, 0, false,
                $"Custom widget '{widgetIdOrTemplateId}' not found.");
        }

        if (!_contentWidgets.TryGetValue(config.Id, out ContentWidgetWindow? window))
        {
            await ShowWidgetAsync(config.Id, reveal: false, autoRestoreOnReveal: false);
            _contentWidgets.TryGetValue(config.Id, out window);
        }

        if (window is null)
        {
            return new CustomWidgetPreviewCaptureResult(
                null, 0, 0, 0, 0, 0, false,
                $"Custom widget window '{config.Id}' could not be materialized.");
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
            return new CustomWidgetPreviewCaptureResult(null, 0, 0, 0, 0, 0, false, "Widget visual tree is not ready.");
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

            (byte[]? pngBytes, int pxW, int pxH, string? err) =
                await RenderElementToPngAsync(fe, isDarkTheme, compositeOpaqueBackdrop: true);

            double measuredW = Math.Round(fe.ActualWidth > 0 ? fe.ActualWidth : config.Width, 1);
            double measuredH = Math.Round(fe.ActualHeight > 0 ? fe.ActualHeight : config.Height, 1);
            double overflowPx = 0;

            if (effectiveMode != "capsule" && targetAdapter is not null)
            {
                double frameW = Math.Clamp(config.Width > 180 ? config.Width : targetAdapter.ViewModel.Manifest.DefaultSize.Width, 200.0, 640.0);
                double frameH = Math.Clamp(config.Height > 60 ? config.Height : targetAdapter.ViewModel.Manifest.DefaultSize.Height, 64.0, 900.0);
                double availBodyH = Math.Max(48.0, frameH - 38.0);
                bool hasScrollRoot = CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(targetAdapter.ViewModel.Manifest.View);

                if (targetAdapter.View is FrameworkElement liveBodyFe && liveBodyFe.ActualWidth > 20)
                {
                    double restoreW = liveBodyFe.ActualWidth;
                    double restoreH = liveBodyFe.ActualHeight;
                    liveBodyFe.Measure(new Windows.Foundation.Size(Math.Max(120.0, frameW - 2.0), double.PositiveInfinity));
                    measuredW = Math.Round(liveBodyFe.DesiredSize.Width, 1);
                    measuredH = Math.Round(liveBodyFe.DesiredSize.Height, 1);
                    liveBodyFe.InvalidateMeasure();
                    if (restoreW > 0 && restoreH > 0)
                    {
                        liveBodyFe.Measure(new Windows.Foundation.Size(restoreW, restoreH));
                    }
                }
                else
                {
                    double estH = CustomWidgetHealthAnalyzer.EstimateManifestLayoutHeight(
                        targetAdapter.ViewModel.Manifest,
                        out _,
                        out _);
                    measuredW = Math.Round(frameW, 1);
                    measuredH = Math.Round(estH, 1);
                }

                if (!hasScrollRoot && measuredH > availBodyH + 24.0)
                {
                    overflowPx = Math.Round(measuredH - availBodyH, 1);
                }
            }

            double liveScale = fe.XamlRoot?.RasterizationScale is double rs && rs > 0 ? rs : 1.0;
            return new CustomWidgetPreviewCaptureResult(
                pngBytes,
                pxW,
                pxH,
                measuredW,
                measuredH,
                overflowPx,
                overflowPx > 0,
                err,
                liveScale);
        }
        catch (Exception ex)
        {
            return new CustomWidgetPreviewCaptureResult(
                null, 0, 0, 0, 0, 0, false,
                $"RenderTargetBitmap capture failed: {ex.Message}");
        }
        finally
        {
            if (changedTheme)
            {
                fe.RequestedTheme = previousTheme;
            }
        }
    }

    private async Task<CustomWidgetPreviewCaptureResult> CaptureStagedBundlePreviewWithTelemetryAsync(
        AgentStageBundle stagedBundle,
        WidgetConfig? existingConfig,
        string? themeOverride,
        string effectiveMode)
    {
        string rawStageManifestJson = stagedBundle.ManifestJson;
        CustomWidgetDataStore? existingStore = existingConfig is not null
            ? CustomWidgetDataStore.ForWidget(existingConfig.Id)
            : null;
        string? logicJs = stagedBundle.HasLogicJs
            ? stagedBundle.LogicJs
            : existingStore?.ReadLogicJsOrNull();

        CustomWidgetLocaleCatalog? locales = stagedBundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(stagedBundle.LocalesJson)
            : (existingStore is not null && existingStore.ReadLocaleCatalog() is { HasAnyLocale: true } diskCat
                ? diskCat
                : null);

        // Apply AST compiler normalization in memory so staged previews match Studio / commit rendering.
        var normEngine = new StudioWorkspaceEngine(rawStageManifestJson, logicJs, locales);
        if (normEngine.HasManifest)
        {
            normEngine.NormalizeDomInPlace();
            rawStageManifestJson = normEngine.GetManifestJson(indented: true) ?? rawStageManifestJson;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawStageManifestJson);
        if (parsed.Manifest is null)
        {
            return new CustomWidgetPreviewCaptureResult(
                null, 0, 0, 0, 0, 0, false,
                "Staged manifest could not be parsed.");
        }

        string effectivePreviewManifestJson = !string.IsNullOrWhiteSpace(logicJs)
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(rawStageManifestJson, logicJs)
            : rawStageManifestJson;
        CustomWidgetParseResult syncedParsed = !ReferenceEquals(effectivePreviewManifestJson, rawStageManifestJson)
            ? CustomWidgetManifestParser.Parse(effectivePreviewManifestJson)
            : parsed;
        CustomWidgetManifest baseManifest = syncedParsed.Manifest ?? parsed.Manifest;

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

    private async Task<CustomWidgetPreviewCaptureResult> CaptureSyntheticCustomWidgetFramePngAsync(
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
            Win32Helper.TrySetWindowCloaked(hwnd, cloaked: true);
            Win32Helper.SetWindowTheme(hwnd, isDarkTheme);

            double frameWidth = renderCapsule
                ? 348.0
                : Math.Clamp(config.Width > 180 ? config.Width : vm.Manifest.DefaultSize.Width, 200.0, 640.0);
            double frameHeight = renderCapsule
                ? 52.0
                : Math.Clamp(config.Height > 60 ? config.Height : vm.Manifest.DefaultSize.Height, 64.0, 900.0);

            string resolvedTitle = !string.IsNullOrWhiteSpace(config.Name)
                ? config.Name
                : vm.Manifest.ResolveLocalizedName(_localizationService.CurrentCultureName);

            var bodyPlaceholder = new Border
            {
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };
            UIElement? bodyElement = null;
            FrameworkElement? bodyFeForMeasure = null;
            if (!renderCapsule)
            {
                tempRenderer = new CustomWidgetNativeRenderer(
                    vm,
                    bodyPlaceholder,
                    () => _settingsService.Settings.WidgetShell.TextSize);
                bodyElement = tempRenderer.BuildRootElement();
                bodyFeForMeasure = bodyElement as FrameworkElement;
            }

            Color accentColor = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
            Border frameBorder = Controls.Studio.StudioPreviewFrameThemeRenderer.CreateStandaloneCaptureFrame(
                vm,
                bodyElement,
                resolvedTitle,
                frameWidth,
                frameHeight,
                renderCapsule,
                isDarkTheme,
                accentColor,
                _settingsService.Settings.WidgetShell.TextSize,
                32.0);

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
            RectInt32? primaryWork = Microsoft.UI.Windowing.DisplayArea.Primary?.WorkArea;
            int dpiRefX = primaryWork is { } pw ? pw.X + 48 : 80;
            int dpiRefY = primaryWork is { } py ? py.Y + 48 : 80;
            double scale = Win32Helper.GetDpiScaleForPoint(dpiRefX, dpiRefY);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            int physW = Math.Max(240, (int)Math.Ceiling((frameWidth + 48) * scale));
            int physH = Math.Max(96, (int)Math.Ceiling((frameHeight + 64) * scale));
            hostWindow.AppWindow.Move(new PointInt32(-32000, -32000));
            hostWindow.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, physW, physH));
            Win32Helper.ShowWindow(hwnd, Win32Helper.SW_SHOWNOACTIVATE);

            await Task.WhenAny(loadedTcs.Task, Task.Delay(350));
            frameBorder.UpdateLayout();
            await Task.Delay(60);
            frameBorder.UpdateLayout();

            (byte[]? pngBytes, int pxW, int pxH, string? err) =
                await RenderElementToPngAsync(frameBorder, isDarkTheme, compositeOpaqueBackdrop: false);

            double measuredW = Math.Round(frameWidth, 1);
            double measuredH = Math.Round(frameHeight, 1);
            double overflowPx = 0;
            if (!renderCapsule && bodyFeForMeasure is not null)
            {
                double availBodyW = Math.Max(120.0, frameWidth - 2.0);
                double availBodyH = Math.Max(48.0, frameHeight - 38.0);
                bodyFeForMeasure.Measure(new Windows.Foundation.Size(availBodyW, double.PositiveInfinity));
                measuredW = Math.Round(bodyFeForMeasure.DesiredSize.Width, 1);
                measuredH = Math.Round(bodyFeForMeasure.DesiredSize.Height, 1);
                bool hasScrollRoot = CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(vm.Manifest.View);
                if (!hasScrollRoot && measuredH > availBodyH + 24.0)
                {
                    overflowPx = Math.Round(measuredH - availBodyH, 1);
                }
            }

            double offscreenScale = frameBorder.XamlRoot?.RasterizationScale is double rs && rs > 0 ? rs : scale;
            return new CustomWidgetPreviewCaptureResult(
                pngBytes,
                pxW,
                pxH,
                measuredW,
                measuredH,
                overflowPx,
                overflowPx > 0,
                err,
                offscreenScale);
        }
        catch (Exception ex)
        {
            return new CustomWidgetPreviewCaptureResult(
                null, 0, 0, 0, 0, 0, false,
                $"Offscreen expanded widget preview capture failed: {ex.Message}");
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
