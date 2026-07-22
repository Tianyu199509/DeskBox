using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    public WidgetTrayBatchAnimationEntry? BeginSharedTrayShowAnimation()
    {
        long generation = TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;

        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"SharedShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return null;
        }

        LogTrayWindow($"SharedShow gen={generation} durationMs={profile.DurationMs}");
        return TrayAnimation.BeginSharedAnimate(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            0,
            0,
            profile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            profile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            profile.DurationMs,
            true,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                TrayAnimation.RestoreVisualState();
                TrayAnimation.RestoreWindowPosition();
            });
    }

    public WidgetTrayBatchAnimationEntry? BeginSharedTrayHideAnimation()
    {
        if (!_isHidePrepared || !IsHideAnimationRunning)
        {
            return null;
        }

        long generation = TrayAnimation.Generation;
        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"SharedHide skipped reason=animation-disabled gen={generation}");
            CompleteTrayHideAnimation();
            return null;
        }

        LogTrayWindow($"SharedHide gen={generation} durationMs={profile.DurationMs}");
        return TrayAnimation.BeginSharedAnimate(
            0,
            0,
            profile.HideOffsetX,
            profile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            profile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            profile.HideEndScale,
            profile.DurationMs,
            false,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                if (!Visible)
                {
                    CompleteTrayHideAnimation();
                }
            });
    }

    private void PlayTrayRaiseAnimation()
    {
        long generation = TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;

        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={generation} durationMs={profile.DurationMs}");
        TrayAnimation.Animate(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            0,
            0,
            profile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            profile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            profile.DurationMs,
            true,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                TrayAnimation.RestoreVisualState();
                TrayAnimation.RestoreWindowPosition();
            });
    }

    private void PlayTrayRaiseAnimationAfterFirstFrame()
    {
        if (Visible)
        {
            TrayAnimation.PlayAfterContentReady(PlayTrayRaiseAnimation);
        }
    }

    private void PlayTrayHideAnimation(Action completed)
    {
        long generation = TrayAnimation.Generation;
        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={generation}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={generation} durationMs={profile.DurationMs}");
        TrayAnimation.Animate(
            0,
            0,
            profile.HideOffsetX,
            profile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            profile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            profile.HideEndScale,
            profile.DurationMs,
            false,
            generation,
            SettingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                if (!Visible)
                {
                    completed();
                }
            });
    }

    private void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            LogTrayWindow("CompleteHide skipped reason=visible-again");
            return;
        }

        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        TrayAnimation.Stop();
        WidgetLayerService.ClearTopMost(HWnd);
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_HIDE);
        AppWindow.Hide();
        TrayAnimation.RevealWindowForTrayShow();
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        _contentHost.OnWindowVisibilityChanged(false);
        LogTrayWindow("CompleteHide");
    }

    // ── Title bar & layout ─────────────────────────────────────
}
