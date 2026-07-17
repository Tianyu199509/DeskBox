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
        if (_isTogglingWidgetsDesktopLayer || now - _lastTrayLayerToggleUtc < TimeSpan.FromMilliseconds(320))
        {
            App.LogVerbose("[TrayBatch] Raise ignored reason=busy-or-throttled");
            return null;
        }

        _isTogglingWidgetsDesktopLayer = true;
        _lastTrayLayerToggleUtc = now;
        try
        {
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
            _suppressTrayLayerRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(160);
            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SetWidgetsRaisedFromTray(shownWindows.Count > 0);
            QueueTrayRaiseTopMostConfirmation(shownWindows);
            ActivateLastRaisedWindow(shownWindows);
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] Raise completed raised={_widgetsRaisedFromTray} prepared={windowsToRaise.Count} shown={shownWindows.Count}");
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
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PlayTrayShowAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to play widget show animation {FormatHostWindow(window)}: {ex}");
            }
        }
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
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PlayPreparedTrayHideAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to play widget hide animation {FormatHostWindow(window)}: {ex}");
            }
        }
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
            double groupLeft = groupWindows.Min(window => window.AnimationBounds.Left);
            double groupTop = groupWindows.Min(window => window.AnimationBounds.Top);
            double groupRight = groupWindows.Max(window => window.AnimationBounds.Right);
            double groupBottom = groupWindows.Max(window => window.AnimationBounds.Bottom);

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
            (int)Math.Round(window.AnimationBounds.Left),
            (int)Math.Round(window.AnimationBounds.Top));
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
