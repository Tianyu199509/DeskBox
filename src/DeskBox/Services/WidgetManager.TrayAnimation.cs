// Copyright (c) DeskBox. All rights reserved.

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
/// Partial class containing TrayAnimation logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private const double OffscreenAnimationPadding = 16.0;
    private long _trayRaiseBatchGeneration;

    // Single shared driver for batch tray animations: one clock and one
    // atomic DeferWindowPos commit per frame, so all windows slide in
    // lockstep instead of staggering per-window (the "wave" effect).
    private readonly WidgetTrayBatchAnimationDriver _trayBatchAnimationDriver = new(App.LogVerbose);

    /// <summary>
    /// Bring desktop widgets to the front of the normal Z-order from the tray.
    /// </summary>
    public async Task<bool?> RaiseWidgetsFromTrayAsync()
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RaiseWidgetsFromTray");
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            App.LogVerbose("[TrayBatch] Raise redirected to desktop-pinned show");
            await SetAllWidgetsVisibleAsync(true);
            return false;
        }

        var now = DateTime.UtcNow;
        double sinceLastToggleMs = (now - _lastTrayLayerToggleUtc).TotalMilliseconds;
        App.LogVerbose(
            $"[TrayBatch] Raise requested raised={_widgetsRaisedFromTray} toggling={_isTogglingWidgetsDesktopLayer} " +
            $"sinceLastMs={sinceLastToggleMs:F0} loadedFile={_widgets.Count} loadedQuick={_quickCaptureWidgets.Count} loadedContent={_contentWidgets.Count}");
        // ⭐ 移除 320ms 节流限制，确保即时响应
        if (_isTogglingWidgetsDesktopLayer)
        {
            App.LogVerbose("[TrayBatch] Raise ignored reason=busy");
            return null;
        }

        _isTogglingWidgetsDesktopLayer = true;
        _lastTrayLayerToggleUtc = now;
        try
        {
            _trayBatchAnimationDriver.Cancel();
            var candidates = _settingsService.Settings.Widgets
                .Where(IsSessionCandidate)
                .ToList();
            App.LogVerbose($"[TrayBatch] Raise candidates={candidates.Count} widgets={FormatWidgetList(candidates)}");

            var windowsToRaise = new List<IDesktopWidgetWindow>();
            foreach (var widget in candidates)
            {
                try
                {
                    var window = await PrepareWidgetForBatchShowAsync(widget, showRaisedWhileInitializing: true);
                    if (window is null)
                    {
                        continue;
                    }

                    windowsToRaise.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget for tray raise '{widget.Name}' ({widget.Id}): {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] Raise prepared={windowsToRaise.Count}/{candidates.Count}");
            var windowsToAnimate = windowsToRaise
                .Where(window => !window.Visible)
                .ToList();
            PrepareTrayShowAnimations(windowsToAnimate);

            _widgetsRaisedFromTray = windowsToRaise.Count > 0;
            var shownWindows = new List<IDesktopWidgetWindow>();
            foreach (var window in windowsToRaise)
            {
                try
                {
                    if (window.Visible)
                    {
                        window.EnsureRaisedFromTrayTopMost();
                    }
                    else
                    {
                        window.ShowPreparedRaisedFromTray(persistVisibility: false);
                    }

                    shownWindows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to show prepared widget from tray {FormatHostWindow(window)}: {ex}");
                }
            }

            _ = Win32Helper.HasMouseButtonActivity();
            _foregroundAtRaiseTime = Win32Helper.GetForegroundWindow();
            _suppressTrayLayerRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(160);
            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SetWidgetsRaisedFromTray(shownWindows.Count > 0);
            // Release the raised group once the foreground leaves DeskBox (e.g. the
            // user clicks another app window). Without the monitor the widgets stay
            // topmost until the next toggle, covering whatever the user clicks.
            StartTrayLayerRestoreMonitor(shownWindows.Count > 0);
            QueueTrayRaiseTopMostConfirmation(shownWindows);
            ActivateLastRaisedWindow(shownWindows);
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] Raise completed raised={_widgetsRaisedFromTray} prepared={windowsToRaise.Count} shown={shownWindows.Count} animated={windowsToAnimate.Count}");
            return _widgetsRaisedFromTray;
        }
        finally
        {
            _isTogglingWidgetsDesktopLayer = false;
        }
    }

    private async Task<IDesktopWidgetWindow?> PrepareWidgetForBatchShowAsync(
        WidgetConfig config,
        bool showRaisedWhileInitializing = false)
    {
        if (IsDeleted(config.Id))
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=deleted widget={FormatWidget(config)}");
            return null;
        }

        if (config.IsDisabled)
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=disabled widget={FormatWidget(config)}");
            return null;
        }

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            if (!GetFeatureWidgetEnabledState(WidgetKind.QuickCapture))
            {
                App.LogVerbose($"[TrayBatch] Prepare skipped reason=quick-capture-disabled widget={FormatWidget(config)}");
                return null;
            }

            if (_quickCaptureWidgets.TryGetValue(config.Id, out var existingQuickCapture))
            {
                App.LogVerbose($"[TrayBatch] Prepare useLoaded widget={FormatWidget(config)} {FormatHostWindow(existingQuickCapture.Window)}");
                existingQuickCapture.Window.RestoreBoundsForCurrentTopology();
                if (!existingQuickCapture.Window.Visible)
                {
                    existingQuickCapture.Window.PrepareTrayShowAnimation();
                }
                return existingQuickCapture.Window;
            }

            App.LogVerbose($"[TrayBatch] Prepare createQuick widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
            var quickCaptureWindow = await CreateRegisteredWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation: true,
                showRaisedWhileInitializing: showRaisedWhileInitializing);
            return quickCaptureWindow;
        }

        if (config.WidgetKind != WidgetKind.File)
        {
            if (IsContentFeatureWidgetKind(config.WidgetKind))
            {
                if (!GetFeatureWidgetEnabledState(config.WidgetKind))
                {
                    App.LogVerbose($"[TrayBatch] Prepare skipped reason=feature-disabled widget={FormatWidget(config)}");
                    return null;
                }

                if (_contentWidgets.TryGetValue(config.Id, out var existingContent))
                {
                    App.LogVerbose($"[TrayBatch] Prepare useLoaded content widget={FormatWidget(config)} {FormatHostWindow(existingContent)}");
                    existingContent.RestoreBoundsForCurrentTopology();
                    if (!existingContent.Visible)
                    {
                        existingContent.PrepareTrayShowAnimation();
                    }

                    return existingContent;
                }

                App.LogVerbose($"[TrayBatch] Prepare createContent widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
                return await CreateRegisteredWidgetFromConfigAsync(
                    config,
                    keepPreparedForAnimation: true,
                    showRaisedWhileInitializing: showRaisedWhileInitializing);
            }

            App.LogVerbose($"[TrayBatch] Prepare skipped reason=unsupported-kind widget={FormatWidget(config)}");
            return null;
        }

        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            App.LogVerbose($"[TrayBatch] Prepare useLoaded widget={FormatWidget(config)} {FormatHostWindow(existing.Window)}");
            existing.Window.RestoreBoundsForCurrentTopology();
            if (!existing.Window.Visible)
            {
                existing.Window.PrepareTrayShowAnimation();
            }
            return existing.Window;
        }

        App.LogVerbose($"[TrayBatch] Prepare createFile widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
        var window = await CreateRegisteredWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: true,
            showRaisedWhileInitializing: showRaisedWhileInitializing);
        return window;
    }

    private void PlayPreparedTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        App.LogVerbose($"[TrayBatch] Starting batch show for {windows.Count} widgets...");
        
        // ⭐ 统一驱动：同一时钟 + DeferWindowPos 原子批量提交，所有窗口锁步滑动
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dispatcher.TryEnqueue(() =>
        {
            try
            {
                // Step 1: 在同一帧内完成所有偏移量设置
                ApplyTrayAnimationGroupOffset(windows);

                // Step 2: 收集所有窗口的共享动画条目（窗口自身的 Opacity/Scale
                // 仍由各自的 Composition 动画驱动）
                var entries = new List<WidgetTrayBatchAnimationEntry>(windows.Count);
                foreach (var window in windows)
                {
                    try
                    {
                        var entry = window.BeginSharedTrayShowAnimation();
                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[WidgetManager] Failed to play widget show animation {window}: {ex}");
                    }
                }

                // Step 3: 单批启动；等待 1 帧让新显示的窗口先提交首帧表面
                var options = WidgetAnimationSettings.From(_settingsService.Settings);
                _trayBatchAnimationDriver.Start(
                    entries,
                    options.DurationMs,
                    _settingsService.Settings.WidgetAnimationEasingIntensity,
                    isShowing: true,
                    startDelayFrames: 1);
            }
            catch (Exception ex)
            {
                App.Log($"[TrayBatch] Error during batch animation: {ex}");
            }
        });
    }

    private void PrepareTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PrepareTrayShowAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to prepare widget show animation {FormatHostWindow(window)}: {ex}");
            }
        }
    }

    private void PlayPreparedTrayHideAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        App.LogVerbose($"[TrayBatch] Starting batch hide for {windows.Count} widgets...");
        
        // ⭐ 与批量显示相同：统一驱动 + DeferWindowPos 原子批量提交
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dispatcher.TryEnqueue(() =>
        {
            try
            {
                // Step 1: 在同一帧内完成所有偏移量设置
                ApplyTrayAnimationGroupOffset(windows);

                // Step 2: 收集所有窗口的共享隐藏动画条目
                var entries = new List<WidgetTrayBatchAnimationEntry>(windows.Count);
                foreach (var window in windows)
                {
                    try
                    {
                        var entry = window.BeginSharedTrayHideAnimation();
                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[WidgetManager] Failed to play widget hide animation {FormatHostWindow(window)}: {ex}");
                    }
                }

                // Step 3: 单批启动，内容已渲染无需等待
                var options = WidgetAnimationSettings.From(_settingsService.Settings);
                _trayBatchAnimationDriver.Start(
                    entries,
                    options.DurationMs,
                    _settingsService.Settings.WidgetAnimationEasingIntensity,
                    isShowing: false,
                    startDelayFrames: 0);
            }
            catch (Exception ex)
            {
                App.Log($"[TrayBatch] Error during batch hide animation: {ex}");
            }
        });
    }

    private void ApplyTrayAnimationGroupOffset(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        foreach (var window in windows)
        {
            window.SetTrayAnimationOffsetOverride(null, null);
        }

        var options = WidgetAnimationSettings.From(_settingsService.Settings);
        if (!options.UsesGroupOffset)
        {
            return;
        }

        string effect = options.Effect;
        string direction = effect switch
        {
            SettingsService.WidgetAnimationEffectSlideLeft or
            SettingsService.WidgetAnimationEffectSlideLeftFade =>
                SettingsService.WidgetAnimationSlideDirectionLeft,
            SettingsService.WidgetAnimationEffectSlideUp or
            SettingsService.WidgetAnimationEffectSlideUpFade =>
                SettingsService.WidgetAnimationSlideDirectionUp,
            SettingsService.WidgetAnimationEffectSlideDown or
            SettingsService.WidgetAnimationEffectSlideDownFade =>
                SettingsService.WidgetAnimationSlideDirectionDown,
            SettingsService.WidgetAnimationEffectSlideRight or
            SettingsService.WidgetAnimationEffectSlideRightFade =>
                SettingsService.WidgetAnimationSlideDirectionRight,
            SettingsService.WidgetAnimationEffectSlideFade or
            SettingsService.WidgetAnimationEffectScaleSlide =>
                options.SlideDirection,
            _ => SettingsService.WidgetAnimationSlideDirectionNone
        };

        if (direction == SettingsService.WidgetAnimationSlideDirectionNone)
        {
            return;
        }

        foreach (var group in windows.GroupBy(GetAnimationWorkAreaKey))
        {
            var groupWindows = group.ToList();
            if (groupWindows.Count == 0)
            {
                continue;
            }

            var workArea = GetAnimationWorkArea(groupWindows[0]);
            // Use resting bounds: during prepare/play the HWNDs are physically
            // displaced offscreen, which would collapse the group offset to ~0
            // and leave windows parked at their final position when uncloaked.
            double groupLeft = groupWindows.Min(window => window.RestingAnimationBounds.Left);
            double groupTop = groupWindows.Min(window => window.RestingAnimationBounds.Top);
            double groupRight = groupWindows.Max(window => window.RestingAnimationBounds.Right);
            double groupBottom = groupWindows.Max(window => window.RestingAnimationBounds.Bottom);

            double offsetX = 0;
            double offsetY = 0;
            switch (direction)
            {
                case SettingsService.WidgetAnimationSlideDirectionLeft:
                    offsetX = -(groupRight - workArea.X + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationSlideDirectionUp:
                    offsetY = -(groupBottom - workArea.Y + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationSlideDirectionDown:
                    offsetY = workArea.Y + workArea.Height - groupTop + OffscreenAnimationPadding;
                    break;

                case SettingsService.WidgetAnimationSlideDirectionRight:
                default:
                    offsetX = workArea.X + workArea.Width - groupLeft + OffscreenAnimationPadding;
                    break;
            }

            foreach (var window in groupWindows)
            {
                window.SetTrayAnimationOffsetOverride(offsetX, offsetY);
            }
        }
    }

    private static string GetAnimationWorkAreaKey(IDesktopWidgetWindow window)
    {
        var workArea = GetAnimationWorkArea(window);
        return $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";
    }

    private static Windows.Graphics.RectInt32 GetAnimationWorkArea(IDesktopWidgetWindow window)
    {
        var point = new Windows.Graphics.PointInt32(
            (int)Math.Round(window.RestingAnimationBounds.Left),
            (int)Math.Round(window.RestingAnimationBounds.Top));
        var displayArea = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        return displayArea.WorkArea;
    }

    private static void ActivateLastRaisedWindow(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.LastOrDefault() is not { } window)
        {
            return;
        }

        try
        {
            window.ActivateRaisedFromTrayBatch();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetManager] Failed to activate raised widget {FormatHostWindow(window)}: {ex}");
        }
    }

    private void QueueTrayRaiseTopMostConfirmation(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        long generation = ++_trayRaiseBatchGeneration;
        ConfirmTrayRaiseTopMost(windows, generation);
    }

    private void ConfirmTrayRaiseTopMost(IReadOnlyList<IDesktopWidgetWindow> windows, long generation)
    {
        if (generation != _trayRaiseBatchGeneration || !_widgetsRaisedFromTray)
        {
            return;
        }

        var visibleWindows = windows.Where(window => window.Visible).ToList();
        IntPtr activeHandle = visibleWindows.LastOrDefault()?.WindowHandle ?? IntPtr.Zero;
        WidgetLayerService.BringGroupTemporarilyToFront(
            visibleWindows.Select(window => window.WindowHandle).ToList(),
            activeHandle);
    }

    private void SaveBatchVisibilityState()
    {
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

}
