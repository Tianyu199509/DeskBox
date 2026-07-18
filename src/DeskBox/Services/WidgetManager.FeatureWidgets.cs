﻿// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing FeatureWidgets logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private readonly Dictionary<WidgetKind, bool> _lastFeatureWidgetEnabledStates = new();
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

    public async Task<QuickCaptureWidgetWindow> CreateOrShowQuickCaptureWidgetAsync(bool reveal = true)
    {
        SetFeatureWidgetEnabledState(WidgetKind.QuickCapture, true);
        RestoreDeletedQuickCaptureConfigs();

        var config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.QuickCapture);

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

        var window = await CreateQuickCaptureWidgetFromConfigAsync(config);
        if (reveal)
        {
            window.RevealFromTray(autoRestore: false);
        }

        return window;
    }

    public async Task<ContentWidgetWindow> CreateTodoWidgetAsync(string? name = null)
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

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateContentWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public async Task ShowTodoReminderTargetAsync(string? widgetId, string? itemId, bool preferTodayFilter)
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
                await ShowContentWidgetAsync(config, reveal: true);
                _contentWidgets.TryGetValue(config.Id, out window);
            }
        }

        window ??= await CreateTodoWidgetAsync();
        if (window.CurrentContent?.View is TodoWidgetContent todoContent)
        {
            todoContent.RevealReminderItem(itemId, preferTodayFilter);
        }
    }

    private string GetDefaultFeatureWidgetTitle(WidgetKind kind, WidgetContentDescriptor descriptor)
    {
        string key = kind switch
        {
            WidgetKind.Todo => "Todo.Title",
            WidgetKind.Music => "Music.Title",
            WidgetKind.Weather => "Weather.Title",
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
                _ => Math.Max(_settingsService.Settings.DefaultWidgetWidth, 320)
            },
            Height = kind switch
            {
                WidgetKind.Music => 190,
                WidgetKind.Weather => 200,
                _ => Math.Max(_settingsService.Settings.DefaultWidgetHeight, 360)
            }
        };

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateContentWidgetFromConfigAsync(config, revealAfterCreate: true);
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
        SetFeatureWidgetEnabledState(WidgetKind.QuickCapture, enabled);

        if (enabled)
        {
            await CreateOrShowQuickCaptureWidgetAsync(reveal);
            return;
        }

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
        foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
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
            if (_widgets.TryGetValue(targetWidgetId, out var targetEntry))
            {
                await targetEntry.ViewModel.RefreshFromConfigAsync();
                targetEntry.Window.RevealSavedItem(destinationPath);
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
            if (config.WidgetKind == WidgetKind.File) continue;
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

    internal IDesktopWidgetWindow? GetFeatureWidget(WidgetKind kind)
    {
        if (kind == WidgetKind.QuickCapture)
        {
            return _quickCaptureWidgets.Values
                .Select(entry => (IDesktopWidgetWindow)entry.Window)
                .FirstOrDefault(window => window.Config.WidgetKind == kind);
        }

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

        if (_featureWidgetHandlers.TryGetValue(kind, out var handler))
        {
            await handler.SetEnabledAsync(enabled, reveal);
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

            SetFeatureWidgetEnabledState(kind, false);
            var config = configs.FirstOrDefault(widget => !IsDeleted(widget.Id)) ??
                         configs.FirstOrDefault();

            foreach (var duplicate in configs.Where(widget => !ReferenceEquals(widget, config)).ToList())
            {
                _settingsService.Settings.Widgets.Remove(duplicate);
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
        config.IsDisabled = false;
        config.IsPositionLocked = false;
        config.IsSizeLocked = false;
        config.Metadata ??= [];
        config.Metadata.Clear();
        config.MappedFolderPath = null;
        config.FollowsDefaultStoragePath = false;
        config.ManagedFolderName = null;
        config.SortMode = WidgetSortMode.Name;
        config.SortDescending = false;
        config.Items ??= [];
        config.Items.Clear();
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
        SetFeatureWidgetEnabledState(WidgetKind.Todo, enabled);

        if (enabled)
        {
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
        SetFeatureWidgetEnabledState(kind, enabled);

        if (enabled)
        {
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

        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == kind &&
                     !IsDeleted(widget.Id)))
        {
            config.IsVisible = false;
            config.IsDisabled = false;
        }

        HideAndCloseFeatureWidgetAsync(kind);
        await _settingsService.SaveAsync();
    }

    private Task SetWeatherFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Weather, enabled, reveal);
    }

    private bool GetFeatureWidgetEnabledState(WidgetKind? kind)
    {
        return kind is { } featureKind &&
               FeatureWidgetSettings.IsFeatureWidget(featureKind) &&
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, featureKind);
    }

    private static bool IsContentFeatureWidgetKind(WidgetKind kind)
    {
        return FeatureWidgetSettings.IsFeatureWidget(kind) &&
               kind != WidgetKind.QuickCapture;
    }

    private void SetFeatureWidgetEnabledState(WidgetKind kind, bool enabled)
    {
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
        _lastFeatureWidgetEnabledStates[kind] = enabled;
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

        if (window.Config.WidgetKind == WidgetKind.QuickCapture &&
            _quickCaptureWidgets.TryGetValue(window.Config.Id, out var quickCaptureEntry) &&
            ReferenceEquals(quickCaptureEntry.Window, window))
        {
            _quickCaptureWidgets.Remove(window.Config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);
            quickCaptureEntry.ViewModel.Dispose();
        }
        else if (window.Config.WidgetKind == WidgetKind.File &&
                 _widgets.TryGetValue(window.Config.Id, out var fileEntry) &&
                 ReferenceEquals(fileEntry.Window, window))
        {
            _widgets.Remove(window.Config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);
            fileEntry.ViewModel.Dispose();
        }
        else if (_contentWidgets.TryGetValue(window.Config.Id, out var contentWindow) &&
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
    }

    private async Task<QuickCaptureWidgetWindow> CreateQuickCaptureWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (_quickCaptureWidgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.WidgetKind = WidgetKind.QuickCapture;
        config.Name = string.IsNullOrWhiteSpace(config.Name)
            ? _localizationService.T("QuickCapture.Name")
            : config.Name;
        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new QuickCaptureWidgetViewModel(
            config,
            _quickCaptureService,
            _settingsService,
            _localizationService,
            dispatcherQueue);
        var window = new QuickCaptureWidgetWindow(viewModel, _settingsService, _localizationService);

        _themeService.TrackWindow(window);
        _quickCaptureWidgets[config.Id] = (window, viewModel);
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) =>
        {
            if (_quickCaptureWidgets.TryGetValue(config.Id, out var currentEntry) &&
                ReferenceEquals(currentEntry.Window, window))
            {
                _quickCaptureWidgets.Remove(config.Id);
            }

            _widgetWindowHandles.Remove(window.WindowHandle);
            if (IsDeleted(config.Id) || FindConfig(config.Id) is null)
            {
                return;
            }

            if (_suppressClosedVisibilityPersistence.Contains(config.Id))
            {
                return;
            }

            if (_quickCaptureWidgets.ContainsKey(config.Id))
            {
                return;
            }

            config.IsVisible = false;
            _settingsService.SaveDebounced();
        };

        try
        {
            window.PrepareTrayShowAnimation();
            if (!keepPreparedForAnimation)
            {
                window.Activate();
                window.PushToBottom();
            }
            else if (showRaisedWhileInitializing)
            {
                QueueDeferredQuickCaptureInitialization(config, window, viewModel);
                return window;
            }

            await viewModel.InitializeAsync();
            if (!keepPreparedForAnimation)
            {
                window.CompleteTrayShowWithoutAnimation();
                if (revealAfterCreate)
                {
                    window.RevealFromTray(autoRestore: false);
                }
            }
        }
        catch
        {
            _quickCaptureWidgets.Remove(config.Id);
            viewModel.Dispose();

            try
            {
                window.Close();
            }
            catch
            {
            }

            throw;
        }

        return window;
    }

    private void QueueDeferredQuickCaptureInitialization(
        WidgetConfig config,
        QuickCaptureWidgetWindow window,
        QuickCaptureWidgetViewModel viewModel)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to initialize quick capture widget '{config.Name}' ({config.Id}) after show: {ex}");
                if (_quickCaptureWidgets.TryGetValue(config.Id, out var entry) &&
                    ReferenceEquals(entry.Window, window))
                {
                    _quickCaptureWidgets.Remove(config.Id);
                    viewModel.Dispose();
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
            }
        });
    }

}
