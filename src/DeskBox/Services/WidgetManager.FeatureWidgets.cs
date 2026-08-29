﻿// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

public sealed record GlanceWidgetInstanceInfo(
    string Id,
    string Name,
    bool IsEnabled);

internal sealed record TodoReminderTargetPresentationResult(
    string WidgetId,
    string? ItemId,
    long WindowHandle,
    bool Visible,
    bool HasXamlRoot,
    bool ItemPresented,
    bool TargetPresented);

/// <summary>
/// Partial class containing FeatureWidgets logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private readonly Dictionary<WidgetKind, bool> _lastFeatureWidgetEnabledStates = new();
    private readonly Dictionary<WidgetKind, SemaphoreSlim> _featureWidgetUpdateLocks =
        FeatureWidgetSettings.FeatureKinds.ToDictionary(
            kind => kind,
            _ => new SemaphoreSlim(1, 1));
    private readonly Dictionary<WidgetKind, FeatureWidgetHandler> _featureWidgetHandlers;
    private readonly Dictionary<WidgetKind, WidgetWindowProvider> _windowProviders;
    private bool _isApplyingAppearancePreview;

    private void ApplyFeatureWidgetEnabledState(WidgetKind kind, bool enabled)
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => ApplyFeatureWidgetEnabledState(kind, enabled));
            return;
        }

        if (!enabled)
        {
            if (_featureWidgetHandlers.TryGetValue(kind, out var handler))
            {
                handler.HideLoaded();
            }
            else
            {
                HideAndCloseFeatureWidgetAsync(kind);
            }

            return;
        }

        CreateOrShowFeatureWidgetAsync(kind).ContinueWith(
            task =>
            {
                if (task.Exception is not null)
                {
                    App.Log($"[WidgetManager] Failed to show feature widget after enabling kind={kind}: {task.Exception}");
                }
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task<ContentWidgetWindow> CreateOrShowQuickCaptureWidgetAsync(bool reveal = true, bool focusNewInput = false)
    {
        SetFeatureWidgetEnabledState(WidgetKind.QuickCapture, true);
        RestoreDeletedQuickCaptureConfigs();

        var config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.QuickCapture);
        bool isNewConfig = config is null;

        if (config is null)
        {
            config = new WidgetConfig
            {
                Name = _localizationService.T("QuickCapture.Name"),
                WidgetKind = WidgetKind.QuickCapture,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                Width = _settingsService.Settings.DefaultWidgetWidth,
                Height = _settingsService.Settings.DefaultWidgetHeight
            };
            _settingsService.Settings.Widgets.Add(config);
        }

        config.IsDisabled = false;
        config.IsVisible = true;
        await _settingsService.SaveAsync();

        if (isNewConfig)
        {
            await SeedQuickCaptureGuideAsync();
        }

        var window = await CreateContentWidgetFromConfigAsync(config);
        if (reveal)
        {
            window.RevealFromTray(autoRestore: false);
        }

        if (focusNewInput)
        {
            window.TriggerAddAction();
        }

        return window;
    }

    public async Task<ContentWidgetWindow> CreateTodoWidgetAsync(string? name = null, bool focusNewInput = false)
    {
        SetFeatureWidgetEnabledState(WidgetKind.Todo, true);

        // Single-instance: show existing Todo if one exists
        var existingConfig = _settingsService.Settings.Widgets
            .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !IsDeleted(w.Id));
        if (existingConfig is not null)
        {
            await ShowWidgetAsync(existingConfig.Id, reveal: true, autoRestoreOnReveal: false);
            if (_contentWidgets.TryGetValue(existingConfig.Id, out var existing))
            {
                if (focusNewInput)
                {
                    existing.TriggerAddAction();
                }

                return existing;
            }
        }

        name = string.IsNullOrWhiteSpace(name)
            ? _localizationService.T("Todo.Title")
            : name;

        var config = new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.Todo,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = Math.Max(_settingsService.Settings.DefaultWidgetWidth, 320),
            Height = Math.Max(_settingsService.Settings.DefaultWidgetHeight, 420)
        };

        MarkNeedsInitialPlacementIfDisplayUnusable(config);
        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();
        await SeedTodoGuideAsync(config);

        var window = await CreateContentWidgetFromConfigAsync(config, revealAfterCreate: true);
        if (focusNewInput)
        {
            window.TriggerAddAction();
        }

        return window;
    }

    private async Task SeedQuickCaptureGuideAsync()
    {
        try
        {
            await WidgetFirstRunGuideFactory.EnsureQuickCaptureGuideAsync(
                _quickCaptureService,
                _localizationService);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetManager] Failed to seed Quick Capture guide: {ex}");
        }
    }

    private async Task SeedTodoGuideAsync(WidgetConfig config)
    {
        try
        {
            await WidgetFirstRunGuideFactory.EnsureTodoGuideAsync(
                new TodoWidgetStore(config.Id),
                _localizationService);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetManager] Failed to seed Todo guide: {ex}");
        }
    }

    internal async Task<TodoReminderTargetPresentationResult> ShowTodoReminderTargetAsync(
        string? widgetId,
        string? itemId,
        bool preferTodayFilter)
    {
        ContentWidgetWindow? window = null;
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            var config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
                widget.WidgetKind == WidgetKind.Todo &&
                string.Equals(widget.Id, widgetId, StringComparison.Ordinal) &&
                !IsDeleted(widget.Id));

            if (config is not null)
            {
                SetFeatureWidgetEnabledState(WidgetKind.Todo, true);
                await ShowWidgetAsync(
                    config.Id,
                    reveal: true,
                    autoRestoreOnReveal: false);
                _contentWidgets.TryGetValue(config.Id, out window);
            }
        }

        window ??= await CreateTodoWidgetAsync();
        await window.ContentReadyTask;
        if (window.CurrentContent?.View is TodoWidgetContent todoContent)
        {
            bool surfaceLoaded = await WaitForTodoReminderSurfaceLoadedAsync(
                todoContent);
            bool itemPresented = todoContent.RevealReminderItem(
                itemId,
                preferTodayFilter);
            bool surfaceReady = surfaceLoaded &&
                await WaitForTodoReminderSurfaceCommitAsync(todoContent);
            bool requiresItem = !string.IsNullOrWhiteSpace(itemId);
            bool targetPresented = window.Visible &&
                surfaceReady &&
                (!requiresItem || itemPresented);
            return new TodoReminderTargetPresentationResult(
                window.Identity.WidgetId,
                itemId,
                window.WindowHandle.ToInt64(),
                window.Visible,
                HasXamlRoot: surfaceReady,
                ItemPresented: itemPresented,
                TargetPresented: targetPresented);
        }

        return new TodoReminderTargetPresentationResult(
            window.Identity.WidgetId,
            itemId,
            window.WindowHandle.ToInt64(),
            window.Visible,
            HasXamlRoot: false,
            ItemPresented: false,
            TargetPresented: false);
    }

    private static async Task<bool> WaitForTodoReminderSurfaceLoadedAsync(
        TodoWidgetContent content)
    {
        if (content.IsLoaded && content.XamlRoot is not null)
        {
            return true;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler loaded = (_, _) => completion.TrySetResult();
        content.Loaded += loaded;
        try
        {
            if (content.IsLoaded && content.XamlRoot is not null)
            {
                return true;
            }

            Task completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(TimeSpan.FromSeconds(3)));
            return ReferenceEquals(completed, completion.Task) &&
                content.IsLoaded &&
                content.XamlRoot is not null;
        }
        finally
        {
            content.Loaded -= loaded;
        }
    }

    internal static async Task<bool> WaitForTodoReminderSurfaceCommitAsync(
        TodoWidgetContent content)
    {
        if (!content.IsLoaded || content.XamlRoot is null)
        {
            return false;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int committedFrameCount = 0;
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            if (!content.IsLoaded || content.XamlRoot is null)
            {
                committedFrameCount = 0;
                return;
            }

            if (++committedFrameCount >= 2)
            {
                completion.TrySetResult();
            }
        };

        CompositionTarget.Rendering += rendering;
        try
        {
            Task completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(TimeSpan.FromSeconds(3)));
            return ReferenceEquals(completed, completion.Task) &&
                content.IsLoaded &&
                content.XamlRoot is not null;
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }
    }

    private string GetDefaultFeatureWidgetTitle(WidgetKind kind, WidgetContentDescriptor descriptor)
    {
        string key = kind switch
        {
            WidgetKind.Todo => "Todo.Title",
            WidgetKind.Music => "Music.Title",
            WidgetKind.Weather => "Weather.Title",
            WidgetKind.Search => "Search.Title",
            WidgetKind.Glance => "Glance.Title",
            WidgetKind.Tags => "Tags.Title",
            WidgetKind.SystemMonitor => "SystemMonitor.Title",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(key))
        {
            string localized = _localizationService.T(key);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        return descriptor.DefaultTitle;
    }

    private async Task<ContentWidgetWindow> CreateSingletonContentFeatureWidgetAsync(WidgetKind kind)
    {
        if (!IsContentFeatureWidgetKind(kind))
        {
            throw new NotSupportedException($"Widget kind '{kind}' is not a content feature widget.");
        }

        SetFeatureWidgetEnabledState(kind, true);

        var existingConfig = _settingsService.Settings.Widgets
            .FirstOrDefault(w => w.WidgetKind == kind && !IsDeleted(w.Id));
        if (existingConfig is not null)
        {
            await ShowWidgetAsync(existingConfig.Id, reveal: true, autoRestoreOnReveal: false);
            if (_contentWidgets.TryGetValue(existingConfig.Id, out var existing))
            {
                return existing;
            }
        }

        var descriptor = new WidgetContentFactory(_localizationService).GetDescriptor(kind);
        var config = new WidgetConfig
        {
            Name = GetDefaultFeatureWidgetTitle(kind, descriptor),
            WidgetKind = kind,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = kind switch
            {
                WidgetKind.Music => 380,
                WidgetKind.Weather => 200,
                WidgetKind.Search => 280,
                WidgetKind.Glance => 360,
                _ => Math.Max(_settingsService.Settings.DefaultWidgetWidth, 320)
            },
            Height = kind switch
            {
                WidgetKind.Music => 190,
                WidgetKind.Weather => 200,
                WidgetKind.Search => 90,
                WidgetKind.Glance => 260,
                _ => Math.Max(_settingsService.Settings.DefaultWidgetHeight, 360)
            }
        };
        ApplyDefaultFeatureWidgetChromeMode(config, kind);

        MarkNeedsInitialPlacementIfDisplayUnusable(config);
        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateContentWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetGlanceWidgetInstances()
    {
        bool featureEnabled = GetFeatureWidgetEnabledState(WidgetKind.Glance);
        return _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Glance && !IsDeleted(widget.Id))
            .Select(widget => new GlanceWidgetInstanceInfo(
                widget.Id,
                widget.Name,
                featureEnabled && !widget.IsDisabled))
            .ToList();
    }

    public bool IsGlanceFeatureEnabled =>
        GetFeatureWidgetEnabledState(WidgetKind.Glance);

    public async Task<GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(
        string? sourceWidgetId = null)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => CreateGlanceWidgetAsync(sourceWidgetId));
        }

        WidgetConfig? sourceConfig = string.IsNullOrWhiteSpace(sourceWidgetId)
            ? null
            : _settingsService.Settings.Widgets.FirstOrDefault(widget =>
                widget.WidgetKind == WidgetKind.Glance &&
                string.Equals(widget.Id, sourceWidgetId, StringComparison.Ordinal) &&
                !IsDeleted(widget.Id));
        if (!string.IsNullOrWhiteSpace(sourceWidgetId) && sourceConfig is null)
        {
            throw new InvalidOperationException($"Glance widget '{sourceWidgetId}' was not found.");
        }

        bool featureEnabled = GetFeatureWidgetEnabledState(WidgetKind.Glance);
        WidgetConfig? placementSource = sourceConfig ?? _settingsService.Settings.Widgets
            .LastOrDefault(widget => widget.WidgetKind == WidgetKind.Glance && !IsDeleted(widget.Id));
        var config = new WidgetConfig
        {
            Name = GetUniqueGlanceWidgetName(),
            IsDefaultTitle = placementSource is null,
            WidgetKind = WidgetKind.Glance,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = (placementSource?.X ?? 100) + (placementSource is null ? 0 : 24),
            Y = (placementSource?.Y ?? 100) + (placementSource is null ? 0 : 24),
            Width = sourceConfig?.Width ?? 360,
            Height = sourceConfig?.Height ?? 260,
            ViewMode = sourceConfig?.ViewMode ?? ViewMode.Icon,
            IsVisible = featureEnabled,
            IsDisabled = !featureEnabled,
            Metadata = sourceConfig?.Metadata.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal) ?? []
        };
        ApplyDefaultFeatureWidgetChromeMode(config, WidgetKind.Glance);

        GlanceWidgetData data = sourceConfig is null
            ? new GlanceWidgetData()
            : await GlanceWidgetStore.ForWidget(sourceConfig.Id).LoadAsync();
        await GlanceWidgetStore.ForWidget(config.Id).SaveAsync(data);

        MarkNeedsInitialPlacementIfDisplayUnusable(config);
        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        if (featureEnabled)
        {
            await CreateContentWidgetFromConfigAsync(config, revealAfterCreate: true);
        }

        App.Log(
            $"[WidgetManager] Glance instance created id={config.Id} " +
            $"source={sourceConfig?.Id ?? "default"} enabled={featureEnabled}");
        return new GlanceWidgetInstanceInfo(config.Id, config.Name, featureEnabled);
    }

    public async Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled)
    {
        if (!HasUiThreadAccess())
        {
            await RunOnUiThreadAsync(() => SetGlanceWidgetInstanceEnabledAsync(widgetId, enabled));
            return;
        }

        WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.Glance &&
            string.Equals(widget.Id, widgetId, StringComparison.Ordinal) &&
            !IsDeleted(widget.Id));
        if (config is null)
        {
            return;
        }

        if (enabled && !GetFeatureWidgetEnabledState(WidgetKind.Glance))
        {
            App.Log($"[WidgetManager] Ignored Glance instance toggle while master is off id={widgetId}");
            return;
        }

        if (enabled)
        {
            config.IsDisabled = false;
            config.IsVisible = true;
            await _settingsService.SaveAsync();
            await ShowWidgetAsync(config.Id, reveal: true, autoRestoreOnReveal: false);
        }
        else
        {
            await RemoveWidgetFromGroupAsync(config.Id, revealStandalone: false);
            config.IsDisabled = true;
            config.IsVisible = false;
            if (_contentWidgets.TryGetValue(config.Id, out ContentWidgetWindow? window))
            {
                CloseFeatureWidgetInstance(window);
            }

            await _settingsService.SaveAsync();
        }

        App.Log($"[WidgetManager] Glance instance enabled={enabled} id={widgetId}");
    }

    public async Task<bool> LocateGlanceWidgetAsync(string widgetId)
    {
        if (!GetFeatureWidgetEnabledState(WidgetKind.Glance))
        {
            return false;
        }

        return await ShowWidgetAsync(widgetId, reveal: true, autoRestoreOnReveal: false);
    }

    public Task RemoveGlanceWidgetAsync(string widgetId) => RemoveWidgetAsync(widgetId);

    private string GetUniqueGlanceWidgetName()
    {
        string baseName = GetDefaultFeatureWidgetTitle(
            WidgetKind.Glance,
            new WidgetContentFactory(_localizationService).GetDescriptor(WidgetKind.Glance));
        HashSet<string> existingNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Glance && !IsDeleted(widget.Id))
            .Select(widget => widget.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        for (int index = 2; ; index++)
        {
            string candidate = $"{baseName} {index}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<IDesktopWidgetWindow?> CreateOrShowGlanceWidgetsAsync(bool reveal)
    {
        SetFeatureWidgetEnabledState(WidgetKind.Glance, true);
        List<WidgetConfig> configs = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Glance && !IsDeleted(widget.Id))
            .ToList();
        if (configs.Count == 0)
        {
            GlanceWidgetInstanceInfo created = await CreateGlanceWidgetAsync();
            return _contentWidgets.GetValueOrDefault(created.Id);
        }

        ApplyGlanceMasterState(configs, enabled: true);

        await _settingsService.SaveAsync();
        if (reveal)
        {
            foreach (WidgetConfig config in configs)
            {
                await ShowWidgetAsync(config.Id, reveal: true, autoRestoreOnReveal: false);
            }
        }

        return configs
            .Select(config => _contentWidgets.GetValueOrDefault(config.Id))
            .FirstOrDefault(window => window is not null);
    }

    private void RestoreDeletedQuickCaptureConfigs()
    {
        var quickCaptureIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.QuickCapture)
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (quickCaptureIds.Count == 0)
        {
            return;
        }

        _deletedWidgetIds.RemoveWhere(quickCaptureIds.Contains);
        _settingsService.Settings.DeletedWidgetIds.RemoveAll(quickCaptureIds.Contains);
    }

    public async Task SetQuickCaptureEnabledAsync(bool enabled, bool reveal = true)
    {
        if (enabled)
        {
            SetFeatureWidgetEnabledState(WidgetKind.QuickCapture, true);
            await CreateOrShowQuickCaptureWidgetAsync(reveal);
            return;
        }

        await DetachFeatureWidgetsFromGroupsAsync(WidgetKind.QuickCapture);
        SetFeatureWidgetEnabledState(WidgetKind.QuickCapture, false);
        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == WidgetKind.QuickCapture &&
                     !IsDeleted(widget.Id)))
        {
            config.IsVisible = false;
            config.IsDisabled = false;
        }

        CloseLoadedQuickCaptureWidgets();
        await _settingsService.SaveAsync();
    }

    private void CloseLoadedQuickCaptureWidgets()
    {
        foreach (ContentWidgetWindow window in _contentWidgets.Values
                     .Where(window => window.Config.WidgetKind == WidgetKind.QuickCapture)
                     .DistinctBy(window => window.WindowHandle)
                     .ToList())
        {
            CloseFeatureWidgetInstance(window);
        }
    }

    public IReadOnlyList<QuickCaptureFileWidgetTarget> GetQuickCaptureFileWidgetTargets()
    {
        return _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             !widget.IsDisabled &&
                             !IsDeleted(widget.Id) &&
                             TryGetFileWidgetFolderPath(widget, out _))
            .Select(widget =>
            {
                TryGetFileWidgetFolderPath(widget, out string folderPath);
                return new QuickCaptureFileWidgetTarget(widget.Id, widget.Name, folderPath);
            })
            .ToList();
    }

    public QuickCaptureFileWidgetTarget? GetLastQuickCaptureFileWidgetTarget()
    {
        string lastTargetId = _settingsService.Settings.LastQuickCaptureFileWidgetId;
        if (string.IsNullOrWhiteSpace(lastTargetId))
        {
            return null;
        }

        return GetQuickCaptureFileWidgetTargets()
            .FirstOrDefault(target => string.Equals(target.WidgetId, lastTargetId, StringComparison.Ordinal));
    }

    public async Task<string?> SaveQuickCaptureItemToFileWidgetAsync(
        QuickCaptureItem item,
        string targetWidgetId,
        string? imageFileNamePrefix = null)
    {
        if (item.IsDeleted ||
            string.IsNullOrWhiteSpace(targetWidgetId) ||
            FindConfig(targetWidgetId) is not { } targetConfig ||
            targetConfig.WidgetKind != WidgetKind.File ||
            targetConfig.IsDisabled ||
            IsDeleted(targetWidgetId) ||
            !TryGetFileWidgetFolderPath(targetConfig, out string targetFolderPath))
        {
            return null;
        }

        Directory.CreateDirectory(targetFolderPath);
        string? destinationPath = item.Type switch
        {
            QuickCaptureItemType.Image => await SaveQuickCaptureImageToFolderAsync(item, targetFolderPath, imageFileNamePrefix),
            QuickCaptureItemType.Link => await SaveQuickCaptureLinkToFolderAsync(item, targetFolderPath),
            _ => await SaveQuickCaptureTextToFolderAsync(item, targetFolderPath)
        };

        if (!string.IsNullOrWhiteSpace(destinationPath))
        {
            RememberLastQuickCaptureFileWidgetTarget(targetWidgetId);
            if (_fileWidgets.TryGetValue(targetWidgetId, out var targetEntry))
            {
                await targetEntry.ViewModel.RefreshFromConfigAsync();
                targetEntry.RevealSavedItem(destinationPath);
            }
            else
            {
                ContentWidgetWindow? contentWindow = _contentWidgets.Values
                    .Distinct()
                    .FirstOrDefault(window =>
                        window.CurrentContent is FileSurfaceContent surface &&
                        string.Equals(
                            surface.WidgetId,
                            targetWidgetId,
                            StringComparison.Ordinal));
                if (contentWindow?.CurrentContent is FileSurfaceContent fileSurface)
                {
                    await fileSurface.ViewModel.RefreshFromConfigAsync();
                    fileSurface.RevealSavedItem(destinationPath);
                }
            }
        }

        return destinationPath;
    }

    private void RememberLastQuickCaptureFileWidgetTarget(string widgetId)
    {
        if (string.Equals(_settingsService.Settings.LastQuickCaptureFileWidgetId, widgetId, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.LastQuickCaptureFileWidgetId = widgetId;
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    private async Task<string?> SaveQuickCaptureImageToFolderAsync(
        QuickCaptureItem item,
        string targetFolderPath,
        string? imageFileNamePrefix)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            return null;
        }

        string fileName = QuickCaptureService.BuildImageExportFileName(
            imageFileNamePrefix,
            item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt,
            item.ImagePath);
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await Task.Run(() => File.Copy(item.ImagePath, destinationPath));
        return destinationPath;
    }

    private async Task<string?> SaveQuickCaptureTextToFolderAsync(QuickCaptureItem item, string targetFolderPath)
    {
        string body = item.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string fileName = BuildQuickCaptureContentFileName(
            body,
            _localizationService.T("QuickCapture.TextFileNamePrefix"),
            ".txt");
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await File.WriteAllTextAsync(destinationPath, body);
        return destinationPath;
    }

    private async Task<string?> SaveQuickCaptureLinkToFolderAsync(QuickCaptureItem item, string targetFolderPath)
    {
        string url = string.IsNullOrWhiteSpace(item.Url) ? item.Body?.Trim() ?? string.Empty : item.Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return await SaveQuickCaptureTextToFolderAsync(item, targetFolderPath);
        }

        string baseText = string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsoluteUri : uri.Host;
        string fileName = BuildQuickCaptureContentFileName(
            baseText,
            _localizationService.T("QuickCapture.LinkFileNamePrefix"),
            ".url");
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await File.WriteAllTextAsync(destinationPath, $"[InternetShortcut]{Environment.NewLine}URL={uri.AbsoluteUri}{Environment.NewLine}");
        return destinationPath;
    }

    private static string BuildQuickCaptureContentFileName(string? body, string fallbackName, string extension)
    {
        string firstLine = body?
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        string baseName = FileService.SanitizeFileSystemName(firstLine);
        if (baseName.Length > 36)
        {
            baseName = baseName[..36].Trim().TrimEnd('.');
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = FileService.SanitizeFileSystemName(fallbackName);
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Quick Capture";
        }

        return baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + extension;
    }

    private bool TryGetFileWidgetFolderPath(WidgetConfig widget, out string folderPath)
    {
        folderPath = string.Empty;
        if (widget.WidgetKind != WidgetKind.File)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            folderPath = Path.GetFullPath(widget.MappedFolderPath);
            return true;
        }

        if (!widget.FollowsDefaultStoragePath || string.IsNullOrWhiteSpace(widget.ManagedFolderName))
        {
            return false;
        }

        folderPath = Path.Combine(GetManagedStorageRootPath(), widget.ManagedFolderName);
        return true;
    }

    internal int RepairLegacyContentFeatureFileShells()
    {
        if (!FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music))
        {
            return 0;
        }

        bool hasMusicConfig = _settingsService.Settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.Music &&
            !IsDeleted(widget.Id));
        if (!hasMusicConfig)
        {
            return 0;
        }

        var fileShells = _settingsService.Settings.Widgets
            .Where(IsLegacyEmptyContentFeatureFileShell)
            .ToList();
        if (fileShells.Count == 0)
        {
            return 0;
        }

        foreach (var shell in fileShells)
        {
            _settingsService.Settings.Widgets.Remove(shell);
            if (!_settingsService.Settings.DeletedWidgetIds.Contains(shell.Id))
            {
                _settingsService.Settings.DeletedWidgetIds.Add(shell.Id);
            }

            App.Log($"[WidgetManager] Repaired legacy empty Music file shell: {FormatWidget(shell)}");
        }

        _settingsService.SaveDebounced();
        return fileShells.Count;
    }

    private bool IsLegacyEmptyContentFeatureFileShell(WidgetConfig widget)
    {
        return widget.WidgetKind == WidgetKind.File &&
               string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
               !widget.FollowsDefaultStoragePath &&
               string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
               widget.Items.Count == 0 &&
               IsDefaultMusicTitle(widget.Name);
    }

    private bool IsDefaultMusicTitle(string title)
    {
        string normalized = title.Trim();
        return string.Equals(normalized, "Music", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "\u97F3\u4E50", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, _localizationService.T("Music.Title"), StringComparison.OrdinalIgnoreCase);
    }

    private void DeduplicateFeatureWidgets()
    {
        var seen = new HashSet<WidgetKind>();
        var toRemove = new List<string>();

        foreach (var config in _settingsService.Settings.Widgets.ToList())
        {
            if (!RequiresSingletonFeatureWidgetConfig(config.WidgetKind)) continue;
            if (IsDeleted(config.Id)) continue;

            if (!seen.Add(config.WidgetKind))
            {
                toRemove.Add(config.Id);
                App.Log($"[WidgetManager] Dedup: removing duplicate {config.WidgetKind} widget {config.Id}");
            }
        }

        if (toRemove.Count > 0)
        {
            foreach (var id in toRemove)
            {
                _settingsService.Settings.Widgets.RemoveAll(w => w.Id == id);
                _settingsService.Settings.DeletedWidgetIds.Add(id);
            }
            _settingsService.SaveDebounced();
        }
    }

    internal static bool RequiresSingletonFeatureWidgetConfig(WidgetKind kind)
    {
        return FeatureWidgetSettings.IsFeatureWidget(kind) &&
               kind != WidgetKind.Glance;
    }

    internal IDesktopWidgetWindow? GetFeatureWidget(WidgetKind kind)
    {
        return _contentWidgets.Values
            .FirstOrDefault(w => w.Config.WidgetKind == kind);
    }

    internal bool IsFeatureWidgetEnabled(WidgetKind kind)
    {
        return FeatureWidgetSettings.IsFeatureWidget(kind)
            ? GetFeatureWidgetEnabledState(kind)
            : GetFeatureWidget(kind)?.Visible == true;
    }

    internal async Task<IDesktopWidgetWindow?> CreateOrShowFeatureWidgetAsync(WidgetKind kind)
    {
        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => CreateOrShowFeatureWidgetAsync(kind));
        }

        if (_featureWidgetHandlers.TryGetValue(kind, out var handler))
        {
            return await handler.CreateOrShowAsync(true);
        }

        App.Log($"[WidgetManager] CreateOrShowFeatureWidget: unsupported kind={kind}");
        return null;
    }

    public async Task SetFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled, bool reveal = true)
    {
        if (!HasUiThreadAccess())
        {
            await RunOnUiThreadAsync(() => SetFeatureWidgetEnabledAsync(kind, enabled, reveal));
            return;
        }

        if (_featureWidgetHandlers.TryGetValue(kind, out var handler) &&
            _featureWidgetUpdateLocks.TryGetValue(kind, out var updateLock))
        {
            await updateLock.WaitAsync();
            try
            {
                await handler.SetEnabledAsync(enabled, reveal);
            }
            finally
            {
                updateLock.Release();
            }

            return;
        }

        App.Log($"[WidgetManager] SetFeatureWidgetEnabled: unsupported kind={kind}");
    }

    public async Task ResetFeatureWidgetAsync(WidgetKind kind)
    {
        if (!HasUiThreadAccess())
        {
            await RunOnUiThreadAsync(() => ResetFeatureWidgetAsync(kind));
            return;
        }

        if (!FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            App.Log($"[WidgetManager] ResetFeatureWidget: unsupported kind={kind}");
            return;
        }

        await DetachFeatureWidgetsFromGroupsAsync(kind);
        var suppressedClosedIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == kind)
            .Select(widget => widget.Id)
            .ToList();
        foreach (string id in suppressedClosedIds)
        {
            _suppressClosedVisibilityPersistence.Add(id);
        }

        try
        {
            CloseLoadedFeatureWidgetWindows(kind);

            var configs = _settingsService.Settings.Widgets
                .Where(widget => widget.WidgetKind == kind)
                .ToList();
            foreach (WidgetConfig existingConfig in configs)
            {
                ClearWidgetGroupTransientState(existingConfig.Id);
            }

            if (kind == WidgetKind.QuickCapture)
            {
                await _quickCaptureService.ClearAsync();
            }
            else if (kind == WidgetKind.Todo)
            {
                foreach (var todoConfig in configs)
                {
                    await new TodoWidgetStore(todoConfig.Id).ClearAsync();
                }
            }
            else if (kind == WidgetKind.Glance)
            {
                foreach (WidgetConfig glanceConfig in configs)
                {
                    await GlanceWidgetStore.ForWidget(glanceConfig.Id).ResetAsync();
                }
                await new GlanceImageService().ClearCacheAsync();
            }

            SetFeatureWidgetEnabledState(kind, false);
            var config = configs.FirstOrDefault(widget => !IsDeleted(widget.Id)) ??
                         configs.FirstOrDefault();

            foreach (var duplicate in configs.Where(widget => !ReferenceEquals(widget, config)).ToList())
            {
                _settingsService.Settings.Widgets.Remove(duplicate);
                if (kind == WidgetKind.Glance)
                {
                    await GlanceWidgetStore.DeleteForWidgetAsync(duplicate.Id);
                }
                if (!_settingsService.Settings.DeletedWidgetIds.Contains(duplicate.Id))
                {
                    _settingsService.Settings.DeletedWidgetIds.Add(duplicate.Id);
                }

                _deletedWidgetIds.Remove(duplicate.Id);
                App.Log($"[WidgetManager] ResetFeatureWidget removed duplicate kind={kind} id={duplicate.Id}");
            }

            if (config is null)
            {
                config = CreateDefaultFeatureWidgetConfig(kind, isEnabled: false);
                _settingsService.Settings.Widgets.Add(config);
            }
            else
            {
                ResetFeatureWidgetConfig(config, kind, isEnabled: false);
            }

            _settingsService.Settings.DeletedWidgetIds.RemoveAll(id =>
                string.Equals(id, config.Id, StringComparison.Ordinal));
            _deletedWidgetIds.Remove(config.Id);

            if (kind == WidgetKind.QuickCapture)
            {
                await SeedQuickCaptureGuideAsync();
            }
            else if (kind == WidgetKind.Todo)
            {
                await SeedTodoGuideAsync(config);
            }
            else if (kind == WidgetKind.Glance)
            {
                await GlanceWidgetStore.ForWidget(config.Id).ResetAsync();
            }

            await _settingsService.SaveAsync();
            App.Log($"[WidgetManager] ResetFeatureWidget kind={kind} enabled=false id={config.Id}");
        }
        finally
        {
            foreach (string id in suppressedClosedIds)
            {
                _suppressClosedVisibilityPersistence.Remove(id);
            }
        }
    }

    private WidgetConfig CreateDefaultFeatureWidgetConfig(WidgetKind kind, bool isEnabled)
    {
        var config = new WidgetConfig();
        ResetFeatureWidgetConfig(config, kind, isEnabled);
        return config;
    }

    private void ResetFeatureWidgetConfig(WidgetConfig config, WidgetKind kind, bool isEnabled)
    {
        var descriptor = new WidgetContentFactory(_localizationService).GetDescriptor(kind);
        config.WidgetKind = kind;
        config.Name = kind == WidgetKind.QuickCapture
            ? _localizationService.T("QuickCapture.Name")
            : GetDefaultFeatureWidgetTitle(kind, descriptor);
        config.IsDefaultTitle = true;
        config.X = 100;
        config.Y = 100;
        config.PositionAnchor = null;
        config.PositionMarginX = 0;
        config.PositionMarginY = 0;
        config.PositionMonitorKey = null;
        config.PositionMonitorDeviceName = null;
        config.PositionMonitorWasPrimary = null;
        config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        (config.Width, config.Height) = GetDefaultFeatureWidgetSize(kind);
        config.ViewMode = ViewMode.Icon;
        config.IsVisible = isEnabled;
        config.IsDisabled = kind == WidgetKind.Glance && !isEnabled;
        config.IsPositionLocked = false;
        config.IsSizeLocked = false;
        config.Metadata ??= [];
        config.Metadata.Clear();
        ApplyDefaultFeatureWidgetChromeMode(config, kind);
        config.MappedFolderPath = null;
        config.FollowsDefaultStoragePath = false;
        config.ManagedFolderName = null;
        config.SortMode = WidgetSortMode.Name;
        config.SortDescending = false;
        config.Items ??= [];
        config.Items.Clear();
    }

    internal static void ApplyDefaultFeatureWidgetChromeMode(WidgetConfig config, WidgetKind kind)
    {
        if (kind == WidgetKind.Search)
        {
            WidgetChromeModeNames.SetOverrideMode(config, WidgetChromeMode.Overlay);
        }
    }

    private void CloseLoadedFeatureWidgetWindows(WidgetKind kind)
    {
        if (kind == WidgetKind.QuickCapture)
        {
            CloseLoadedQuickCaptureWidgets();
            return;
        }

        foreach (var window in _contentWidgets.Values
                     .Where(window => window.Config.WidgetKind == kind)
                     .ToList())
        {
            CloseFeatureWidgetInstance(window);
        }
    }

    public async Task SetTodoEnabledAsync(bool enabled, bool reveal = true)
    {
        if (enabled)
        {
            SetFeatureWidgetEnabledState(WidgetKind.Todo, true);
            if (reveal)
            {
                await CreateTodoWidgetAsync();
            }
            else
            {
                var config = _settingsService.Settings.Widgets
                    .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !IsDeleted(w.Id));
                if (config is not null)
                {
                    config.IsDisabled = false;
                    config.IsVisible = true;
                }

                await _settingsService.SaveAsync();
            }

            return;
        }

        await DetachFeatureWidgetsFromGroupsAsync(WidgetKind.Todo);
        SetFeatureWidgetEnabledState(WidgetKind.Todo, false);
        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == WidgetKind.Todo &&
                     !IsDeleted(widget.Id)))
        {
            config.IsVisible = false;
            config.IsDisabled = false;
        }

        HideAndCloseFeatureWidgetAsync(WidgetKind.Todo);
        await _settingsService.SaveAsync();
    }

    private async Task SetContentFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled, bool reveal = true)
    {
        if (enabled)
        {
            SetFeatureWidgetEnabledState(kind, true);
            if (reveal)
            {
                await CreateSingletonContentFeatureWidgetAsync(kind);
            }
            else
            {
                var config = _settingsService.Settings.Widgets
                    .FirstOrDefault(w => w.WidgetKind == kind && !IsDeleted(w.Id));
                if (config is not null)
                {
                    config.IsDisabled = false;
                    config.IsVisible = true;
                }

                await _settingsService.SaveAsync();
            }

            return;
        }

        await DetachFeatureWidgetsFromGroupsAsync(kind);
        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == kind &&
                     !IsDeleted(widget.Id)))
        {
            config.IsVisible = false;
            config.IsDisabled = false;
        }

        HideAndCloseFeatureWidgetAsync(kind);
        // Close the content window while feature-owned services are still
        // available. Search content, for example, must unsubscribe from the
        // exact SearchHistoryService instance before that service is released.
        SetFeatureWidgetEnabledState(kind, false);
        await _settingsService.SaveAsync();
    }

    private async Task DetachFeatureWidgetsFromGroupsAsync(WidgetKind kind)
    {
        List<string> groupedIds = _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == kind &&
                WidgetGroupSettings.FindByMember(_settingsService.Settings, widget.Id) is not null)
            .Select(widget => widget.Id)
            .ToList();

        foreach (string widgetId in groupedIds)
        {
            await RemoveWidgetFromGroupAsync(widgetId, revealStandalone: false);
        }
    }

    private Task SetWeatherFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Weather, enabled, reveal);
    }

    private Task SetSearchFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Search, enabled, reveal);
    }

    private Task SetGlanceFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetGlanceFeatureWidgetEnabledCoreAsync(enabled, reveal);
    }

    private async Task SetGlanceFeatureWidgetEnabledCoreAsync(bool enabled, bool reveal)
    {
        if (enabled)
        {
            await CreateOrShowGlanceWidgetsAsync(reveal);
            return;
        }

        await DetachFeatureWidgetsFromGroupsAsync(WidgetKind.Glance);
        List<WidgetConfig> configs = _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.Glance &&
                !IsDeleted(widget.Id))
            .ToList();
        ApplyGlanceMasterState(configs, enabled: false);

        CloseLoadedFeatureWidgetWindows(WidgetKind.Glance);
        SetFeatureWidgetEnabledState(WidgetKind.Glance, false);
        await _settingsService.SaveAsync();
    }

    internal static void ApplyGlanceMasterState(
        IEnumerable<WidgetConfig> configs,
        bool enabled)
    {
        foreach (WidgetConfig config in configs)
        {
            config.IsVisible = enabled;
            config.IsDisabled = !enabled;
        }
    }

    private bool GetFeatureWidgetEnabledState(WidgetKind? kind)
    {
        return kind is { } featureKind &&
               FeatureWidgetSettings.IsFeatureWidget(featureKind) &&
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, featureKind);
    }

    private static bool IsContentFeatureWidgetKind(WidgetKind kind)
    {
        return FeatureWidgetSettings.IsFeatureWidget(kind);
    }

    private void SetFeatureWidgetEnabledState(WidgetKind kind, bool enabled)
    {
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
        _lastFeatureWidgetEnabledStates[kind] = enabled;
        switch (kind)
        {
            case WidgetKind.Search:
                App.Current.SetSearchFeatureEnabled(enabled);
                break;
            case WidgetKind.QuickCapture:
                App.Current.RefreshQuickCaptureClipboardService();
                break;
            case WidgetKind.Todo:
                App.Current.RefreshTodoReminderService();
                break;
        }
    }

    public void HideAndCloseFeatureWidgetAsync(WidgetKind kind)
    {
        var existing = GetFeatureWidget(kind);
        if (existing is not null)
        {
            CloseFeatureWidgetInstance(existing);
        }
    }

    private void CloseFeatureWidgetInstance(IDesktopWidgetWindow window)
    {
        if (!HasUiThreadAccess())
        {
            _ = RunOnUiThreadAsync(() =>
            {
                CloseFeatureWidgetInstance(window);
                return Task.CompletedTask;
            });
            return;
        }

        window.Config.IsVisible = false;

        if (window.Config.WidgetKind == WidgetKind.File &&
                 _fileWidgets.TryGetValue(window.Config.Id, out var fileEntry) &&
                 ReferenceEquals(fileEntry.Host, window))
        {
            _fileWidgets.Remove(window.Config.Id);
        }

        if (_contentWidgets.TryGetValue(window.Config.Id, out var contentWindow) &&
            ReferenceEquals(contentWindow, window))
        {
            _contentWidgets.Remove(window.Config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);
        }

        try
        {
            window.CloseWindow();
        }
        catch
        {
        }

        _settingsService.SaveDebounced();
        if (FeatureWidgetSettings.IsFeatureWidget(window.Config.WidgetKind))
        {
            App.ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        }
    }

}
