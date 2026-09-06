using System.Numerics;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan FolderNavigationLoadingDelay =
        TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _folderNavigationLoadingDelayCancellation;
    private bool _isFolderNavigationOperationActive;
    private bool _folderNavigationVisualPrepared;
    private Visual? _folderNavigationAnimatedVisual;

    private void InitializeFolderNavigationPresentation()
    {
        string upLabel = T("Widget.FolderNavigation.Up");
        string rootLabel = T("Widget.FolderNavigation.ReturnToRoot");
        AutomationProperties.SetName(FolderNavigationUpButton, upLabel);
        ToolTipService.SetToolTip(FolderNavigationUpButton, upLabel);
        AutomationProperties.SetName(FolderNavigationRootButton, rootLabel);
        ToolTipService.SetToolTip(FolderNavigationRootButton, rootLabel);
        AutomationProperties.SetName(
            FolderNavigationLoadingRing,
            T("Widget.FolderNavigation.Loading"));
    }

    private async Task ActivateItemAsync(WidgetItem item)
    {
        if (TryBlockTransferOpen(item))
        {
            return;
        }

        bool isStackPopoverItem = IsItemInStackPopover(item);
        bool isFolderShortcut =
            FolderNavigationPathPolicy.IsFolderShortcutCandidate(item);
        bool isExternalFolderShortcut =
            isFolderShortcut &&
            !string.IsNullOrWhiteSpace(item.TargetPath) &&
            ShortcutTargetProbe.Classify(item.TargetPath) is
                ShortcutTargetKind.Unc or
                ShortcutTargetKind.NetworkDrive or
                ShortcutTargetKind.UriOrShellNamespace or
                ShortcutTargetKind.Unknown;
        bool shouldNavigateInside =
            ViewModel.IsEmbeddedFolderNavigationEnabled &&
            !isExternalFolderShortcut &&
            (isFolderShortcut || (!isStackPopoverItem && item.IsFolder));
        if (!shouldNavigateInside)
        {
            await OpenFileItemAsync(item);
            return;
        }

        if (_isFolderNavigationOperationActive)
        {
            return;
        }

        string? previousFolderPath = ViewModel.CurrentFolderPath;
        bool navigated = await RunFolderNavigationOperationAsync(
            beforeItemsReplaced => isFolderShortcut
                ? ViewModel.NavigateIntoFolderShortcutAsync(
                    item,
                    beforeItemsReplaced: () =>
                    {
                        if (isStackPopoverItem)
                        {
                            // Only hide the independent popover when the
                            // embedded navigation is about to replace the
                            // main surface. An external or unverifiable
                            // shortcut remains a normal Shell-open item.
                            CloseStackPopover();
                        }

                        beforeItemsReplaced();
                    })
                : ViewModel.NavigateIntoFolderAsync(
                    item,
                    beforeItemsReplaced),
            navigatingUp: false);
        if (!navigated)
        {
            RestoreFolderNavigationVisuals();
            if (isFolderShortcut)
            {
                // Broken, non-filesystem, external-root and unverifiable
                // shortcuts retain the normal Windows Shell behavior.
                await OpenFileItemAsync(item);
                return;
            }

            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.FolderNavigation.Unavailable"),
                WidgetFeedbackSeverity.Warning,
                "folder-navigation-unavailable"));
            return;
        }

        if (FolderNavigationPathPolicy.ArePathsEqual(
                previousFolderPath,
                ViewModel.CurrentFolderPath))
        {
            RestoreFolderNavigationVisuals();
            if (isFolderShortcut)
            {
                // A shortcut back to the directory already displayed cannot
                // produce an in-widget navigation change. Preserve a visible
                // activation result by handing the original .lnk to Shell.
                await OpenFileItemAsync(item);
            }

            return;
        }

        CompleteFolderNavigationVisuals(navigatingUp: false);
    }

    private async void FolderNavigationUpButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await NavigateUpFromSurfaceAsync();
    }

    private async Task NavigateUpFromSurfaceAsync()
    {
        if (_isFolderNavigationOperationActive)
        {
            return;
        }

        string? exitedFolderPath = ViewModel.CurrentFolderPath;
        if (!await RunFolderNavigationOperationAsync(
                ViewModel.NavigateUpAsync,
                navigatingUp: true))
        {
            RestoreFolderNavigationVisuals();
            return;
        }

        CompleteFolderNavigationVisuals(navigatingUp: true);
        if (!string.IsNullOrWhiteSpace(exitedFolderPath))
        {
            DispatcherQueue.TryEnqueue(() =>
                ScrollExitedFolderIntoView(exitedFolderPath));
        }
    }

    private async void FolderNavigationRootButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isFolderNavigationOperationActive)
        {
            return;
        }

        bool navigated = await RunFolderNavigationOperationAsync(
            ViewModel.ResetFolderNavigationToMappedRootAsync,
            navigatingUp: true);
        if (!navigated)
        {
            RestoreFolderNavigationVisuals();
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.FolderNavigation.Unavailable"),
                WidgetFeedbackSeverity.Warning,
                "folder-navigation-root-unavailable"));
            return;
        }

        CompleteFolderNavigationVisuals(navigatingUp: true);
    }

    internal async Task ApplyFolderOpenBehaviorChangeAsync()
    {
        string? previousPath = ViewModel.CurrentFolderPath;
        await RunFolderNavigationOperationAsync(
            async beforeItemsReplaced =>
            {
                await ViewModel.RefreshFolderOpenBehaviorAsync(
                    beforeItemsReplaced);
                return true;
            },
            navigatingUp: true);
        UpdateEmptyState();
        if (!string.Equals(
                previousPath,
                ViewModel.CurrentFolderPath,
                StringComparison.OrdinalIgnoreCase))
        {
            AnimateFolderNavigation(navigatingUp: true);
            return;
        }

        RestoreFolderNavigationVisuals();
    }

    private async Task<bool> RunFolderNavigationOperationAsync(
        Func<Action, Task<bool>> operation,
        bool navigatingUp)
    {
        if (_isDisposed || _isFolderNavigationOperationActive)
        {
            return false;
        }

        _isFolderNavigationOperationActive = true;
        FileItemsViewport.IsHitTestVisible = false;
        FolderNavigationBar.IsHitTestVisible = false;

        var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _folderNavigationLoadingDelayCancellation,
            delayCancellation);
        previous?.Cancel();
        _ = ShowDelayedFolderNavigationLoadingAsync(delayCancellation);

        try
        {
            return await operation(
                () => PrepareFolderNavigationVisuals(navigatingUp));
        }
        finally
        {
            delayCancellation.Cancel();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _folderNavigationLoadingDelayCancellation,
                        null,
                        delayCancellation),
                    delayCancellation))
            {
                SetFolderNavigationLoadingVisible(false);
            }

            delayCancellation.Dispose();
            FileItemsViewport.IsHitTestVisible = true;
            FolderNavigationBar.IsHitTestVisible = true;
            _isFolderNavigationOperationActive = false;
        }
    }

    private async Task ShowDelayedFolderNavigationLoadingAsync(
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(
                FolderNavigationLoadingDelay,
                delayCancellation.Token);
            if (_isDisposed ||
                !ReferenceEquals(
                    _folderNavigationLoadingDelayCancellation,
                    delayCancellation))
            {
                return;
            }

            SetFolderNavigationLoadingVisible(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetFolderNavigationLoadingVisible(bool visible)
    {
        FolderNavigationLoadingRing.IsActive = visible;
        FolderNavigationLoadingOverlay.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CompleteFolderNavigationVisuals(bool navigatingUp)
    {
        ClearSelection();
        _cutClipboardPaths = [];
        ApplyCutState();
        UpdateEmptyState();
        AnimateFolderNavigation(navigatingUp);
    }

    private void ScrollExitedFolderIntoView(string exitedFolderPath)
    {
        if (_isDisposed || _isFolderNavigationOperationActive)
        {
            return;
        }

        WidgetItem? folder = ViewModel.Items.FirstOrDefault(item =>
            string.Equals(
                item.Path,
                exitedFolderPath,
                StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        if (activeView.Items.Contains(folder))
        {
            // Keep the return location visible without selecting the folder
            // again or overwriting a selection made after navigation finished.
            activeView.ScrollIntoView(folder);
        }
    }

    private void AnimateFolderNavigation(bool navigatingUp)
    {
        if (!AreSystemAnimationsEnabled())
        {
            RestoreFolderNavigationVisuals();
            return;
        }

        FrameworkElement activeView = GetActiveItemsView();
        ElementCompositionPreview.SetIsTranslationEnabled(
            activeView,
            true);
        Visual contentVisual =
            ElementCompositionPreview.GetElementVisual(activeView);
        StopPreviousFolderNavigationVisual(contentVisual);
        _folderNavigationAnimatedVisual = contentVisual;
        Compositor compositor = contentVisual.Compositor;
        if (!_folderNavigationVisualPrepared)
        {
            SetFolderNavigationStartState(contentVisual, navigatingUp);
        }

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = TimeSpan.FromMilliseconds(190);
        slide.InsertKeyFrame(
            1,
            Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.8f),
                new Vector2(0.2f, 1f)));
        contentVisual.StartAnimation("Translation", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(140);
        fade.InsertKeyFrame(1, 1);
        contentVisual.StartAnimation("Opacity", fade);
        _folderNavigationVisualPrepared = false;
    }

    private void PrepareFolderNavigationVisuals(bool navigatingUp)
    {
        if (!AreSystemAnimationsEnabled())
        {
            return;
        }

        FrameworkElement activeView = GetActiveItemsView();
        ElementCompositionPreview.SetIsTranslationEnabled(activeView, true);
        Visual contentVisual =
            ElementCompositionPreview.GetElementVisual(activeView);
        StopPreviousFolderNavigationVisual(contentVisual);
        _folderNavigationAnimatedVisual = contentVisual;
        contentVisual.StopAnimation("Translation");
        contentVisual.StopAnimation("Opacity");
        SetFolderNavigationStartState(contentVisual, navigatingUp);
        _folderNavigationVisualPrepared = true;
    }

    private static void SetFolderNavigationStartState(
        Visual contentVisual,
        bool navigatingUp)
    {
        contentVisual.Properties.InsertVector3(
            "Translation",
            new Vector3(0, navigatingUp ? -16 : 16, 0));
        contentVisual.Opacity = 0;
    }

    private void RestoreFolderNavigationVisuals()
    {
        FrameworkElement activeView = GetActiveItemsView();
        ElementCompositionPreview.SetIsTranslationEnabled(activeView, true);
        Visual contentVisual =
            ElementCompositionPreview.GetElementVisual(activeView);
        Visual? previousVisual = _folderNavigationAnimatedVisual;
        if (previousVisual is not null &&
            !ReferenceEquals(previousVisual, contentVisual))
        {
            ResetFolderNavigationVisual(previousVisual);
        }
        _folderNavigationAnimatedVisual = contentVisual;
        contentVisual.StopAnimation("Translation");
        contentVisual.StopAnimation("Opacity");
        contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        contentVisual.Opacity = 1;
        _folderNavigationVisualPrepared = false;
    }

    private void StopPreviousFolderNavigationVisual(Visual currentVisual)
    {
        if (_folderNavigationAnimatedVisual is { } previousVisual &&
            !ReferenceEquals(previousVisual, currentVisual))
        {
            ResetFolderNavigationVisual(previousVisual);
        }
    }

    private static void ResetFolderNavigationVisual(Visual visual)
    {
        try
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1;
        }
        catch (Exception)
        {
            // The visual can be torn down between Unloaded and the lifecycle
            // callback. There is no retained managed state to clean in that
            // case, so leaving the native visual alone is safe.
        }
    }

    private void StopFolderNavigationVisuals()
    {
        if (_folderNavigationAnimatedVisual is { } visual)
        {
            ResetFolderNavigationVisual(visual);
        }

        _folderNavigationAnimatedVisual = null;
        _folderNavigationVisualPrepared = false;
    }

    private static bool AreSystemAnimationsEnabled() =>
        WindowsCompatibilityService.AreAnimationsEnabled;
}
