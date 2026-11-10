using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal const int FolderNavigationSpringBackDelayMs = 500;
    private static readonly TimeSpan FolderNavigationLoadingDelay =
        TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _folderNavigationLoadingDelayCancellation;
    private DispatcherTimer? _folderNavigationSpringBackTimer;
    private Button? _highlightedFolderNavigationButton;
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
        slide.Duration = TimeSpan.FromMilliseconds(WidgetMotion.SpatialMilliseconds);
        slide.InsertKeyFrame(
            1,
            Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.8f),
                new Vector2(0.2f, 1f)));
        contentVisual.StartAnimation("Translation", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(WidgetMotion.TransitionMilliseconds);
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

    internal WidgetItem? TryResolveFolderNavigationDropTarget(
        int screenX,
        int screenY,
        out bool isUpButton)
    {
        isUpButton = false;
        if (_isDisposed || !ViewModel.CanNavigateUp ||
            FolderNavigationBar.Visibility != Visibility.Visible)
        {
            return null;
        }

        if (IsScreenPointInsideElement(FolderNavigationRootButton, screenX, screenY) &&
            ViewModel.TryGetRootNavigationFolderTarget(out string rootPath, out string rootName))
        {
            return new WidgetItem
            {
                Name = rootName,
                Path = rootPath,
                IsFolder = true,
            };
        }

        if (IsScreenPointInsideElement(FolderNavigationUpButton, screenX, screenY) &&
            ViewModel.TryGetParentNavigationFolderTarget(out string parentPath, out string parentName))
        {
            isUpButton = true;
            return new WidgetItem
            {
                Name = parentName,
                Path = parentPath,
                IsFolder = true,
            };
        }

        return null;
    }

    private void ObserveFolderNavigationSpringBack(bool hoveringUpButton)
    {
        if (!hoveringUpButton || !ViewModel.CanNavigateUp || _isFolderNavigationOperationActive)
        {
            CancelFolderNavigationSpringBack();
            return;
        }

        SetHighlightedFolderNavigationButton(FolderNavigationUpButton);
        if (_folderNavigationSpringBackTimer is null)
        {
            _folderNavigationSpringBackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(FolderNavigationSpringBackDelayMs),
            };
            _folderNavigationSpringBackTimer.Tick += FolderNavigationSpringBackTimer_Tick;
        }

        if (!_folderNavigationSpringBackTimer.IsEnabled)
        {
            _folderNavigationSpringBackTimer.Start();
        }
    }

    private async void FolderNavigationSpringBackTimer_Tick(object? sender, object e)
    {
        _folderNavigationSpringBackTimer?.Stop();
        if (_isDisposed || !ViewModel.CanNavigateUp || _isFolderNavigationOperationActive ||
            !Win32Helper.IsAnyMouseButtonDown() ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) ||
            !IsScreenPointInsideElement(FolderNavigationUpButton, cursor.X, cursor.Y))
        {
            ClearHighlightedFolderNavigationButton();
            return;
        }

        await NavigateUpFromSurfaceAsync();
        // If still deeper than root and user keeps hovering over Up, re-arm next level.
        if (!_isDisposed && ViewModel.CanNavigateUp &&
            Win32Helper.IsAnyMouseButtonDown() &&
            Win32Helper.GetCursorPos(out Win32Helper.POINT afterCursor) &&
            IsScreenPointInsideElement(FolderNavigationUpButton, afterCursor.X, afterCursor.Y))
        {
            _folderNavigationSpringBackTimer?.Start();
        }
        else
        {
            ClearHighlightedFolderNavigationButton();
        }
    }

    private void CancelFolderNavigationSpringBack()
    {
        _folderNavigationSpringBackTimer?.Stop();
        ClearHighlightedFolderNavigationButton();
    }

    private void SetHighlightedFolderNavigationButton(Button? button)
    {
        if (ReferenceEquals(_highlightedFolderNavigationButton, button))
        {
            return;
        }

        if (_highlightedFolderNavigationButton is { } previous)
        {
            previous.BorderBrush = null;
            previous.BorderThickness = new Thickness(0);
        }

        _highlightedFolderNavigationButton = button;
        if (button is not null)
        {
            button.BorderBrush = ResolveBrush("AccentFillColorDefaultBrush");
            button.BorderThickness = new Thickness(1.5);
        }
    }

    private void ClearHighlightedFolderNavigationButton() =>
        SetHighlightedFolderNavigationButton(null);

    private void FolderNavigationUpButton_DragOver(object sender, DragEventArgs e)
    {
        HandleFolderNavigationButtonDragOver(e, isRootTarget: false);
    }

    private void FolderNavigationRootButton_DragOver(object sender, DragEventArgs e)
    {
        HandleFolderNavigationButtonDragOver(e, isRootTarget: true);
    }

    private void HandleFolderNavigationButtonDragOver(DragEventArgs e, bool isRootTarget)
    {
        e.Handled = true;
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ResetExternalDropPreview();

        bool resolved = isRootTarget
            ? ViewModel.TryGetRootNavigationFolderTarget(out string targetPath, out string targetName)
            : ViewModel.TryGetParentNavigationFolderTarget(out targetPath, out targetName);
        if (!resolved || _isImportBusy)
        {
            CancelFolderNavigationSpringBack();
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!string.IsNullOrWhiteSpace(payload.StackReorderKey))
        {
            // Whole-stack tile headers are projections, not filesystem transfers;
            // still allow 500ms spring-back on Up without advertising a file drop.
            ObserveFolderNavigationSpringBack(!isRootTarget);
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        IReadOnlyList<string> hoverPaths = GetEffectiveHoverPaths(payload);
        if (!payload.HasSurfacePathData ||
            HasTransferConflict(hoverPaths, targetPath) ||
            IsUnsafeFolderDrop(hoverPaths, targetPath) ||
            AreAllSourcesAlreadyInDestinationLexically(hoverPaths, targetPath))
        {
            CancelFolderNavigationSpringBack();
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (isRootTarget)
        {
            _folderNavigationSpringBackTimer?.Stop();
            SetHighlightedFolderNavigationButton(FolderNavigationRootButton);
        }
        else
        {
            ObserveFolderNavigationSpringBack(hoveringUpButton: true);
        }

        FileDropIntent intent = payload.IsInternalReorder
            ? (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu)
                ? FileDropIntent.Shortcut
                : Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                    ? FileDropIntent.Copy
                    : FileDropIntent.Move)
            : ResolveSurfaceDropIntent(
                payload.DataView,
                e.AllowedOperations,
                destinationFolderPath: targetPath,
                sourcePathsOverride: hoverPaths);
        DataPackageOperation op = ToDataPackageOperation(intent);
        if (payload.IsDeskBoxFileDrag)
        {
            ApplyDeskBoxFileDragFeedback(e, op, FormatDropCaption(intent, targetName));
        }
        else if (!HasActiveHostShellDragWindow())
        {
            ApplyExternalDragOperationVisuals(e, op, FormatDropCaption(intent, targetName));
        }
        else
        {
            SuppressExternalDragOperationBadge(e);
            e.AcceptedOperation = op;
        }
    }

    private void FolderNavigationTarget_DragLeave(object sender, DragEventArgs e)
    {
        CancelFolderNavigationSpringBack();
    }

    private async void FolderNavigationUpButton_Drop(object sender, DragEventArgs e)
    {
        await HandleFolderNavigationButtonDropAsync(e, isRootTarget: false);
    }

    private async void FolderNavigationRootButton_Drop(object sender, DragEventArgs e)
    {
        await HandleFolderNavigationButtonDropAsync(e, isRootTarget: true);
    }

    private async Task HandleFolderNavigationButtonDropAsync(DragEventArgs e, bool isRootTarget)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        CancelFolderNavigationSpringBack();
        StopInteractiveAutoScroll();

        bool resolved = isRootTarget
            ? ViewModel.TryGetRootNavigationFolderTarget(out string targetPath, out string targetName)
            : ViewModel.TryGetParentNavigationFolderTarget(out targetPath, out targetName);
        if (!resolved || _isImportBusy)
        {
            ResetDragPayloadCache();
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!payload.HasSurfacePathData || !string.IsNullOrWhiteSpace(payload.StackReorderKey))
        {
            ResetDragPayloadCache();
            return;
        }

        _activeDragHandledAsStackMembership = payload.IsInternalReorder;
        var targetFolder = new WidgetItem
        {
            Name = targetName,
            Path = targetPath,
            IsFolder = true,
        };
        var deferral = e.GetDeferral();
        BeginTrackedImport();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            if (batch.Files.Count == 0)
            {
                return;
            }

            bool containsTemp = batch.Files.Any(static f => f.ForceManagedCopy);
            FileDropIntent intent = payload.IsInternalReorder
                ? (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu)
                    ? FileDropIntent.Shortcut
                    : Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                        ? FileDropIntent.Copy
                        : FileDropIntent.Move)
                : ResolveSurfaceDropIntent(
                    e.DataView,
                    e.AllowedOperations,
                    forceCopy: containsTemp,
                    destinationFolderPath: targetPath,
                    sourcePathsOverride: batch.Files.Select(static f => f.Path));
            if (intent == FileDropIntent.None)
            {
                return;
            }

            bool move = intent == FileDropIntent.Move;
            bool imported = await ImportNativeDroppedFilesIntoFolderAsync(
                batch.Files,
                targetFolder,
                move: move,
                intentOverride: intent == FileDropIntent.Shortcut ? FileDropIntent.Shortcut : null);
            if (imported)
            {
                string? sourceWidgetId = TryGetString(e.DataView.Properties, DeskBoxDragData.SourceWidgetIdProperty);
                if (move && !string.IsNullOrWhiteSpace(sourceWidgetId) &&
                    !string.Equals(sourceWidgetId, WidgetId, StringComparison.OrdinalIgnoreCase) &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        batch.Files.Select(static f => f.Path).ToArray());
                }

                DataPackageOperation desiredOp = ToDataPackageOperation(intent);
                int reqCount = batch.Files.Count(static f => !f.ForceManagedCopy);
                e.AcceptedOperation = ResolveSafeDropCompletionOperation(
                    desiredOp,
                    payload.IsDeskBoxFileDrag,
                    reqCount,
                    reqCount,
                    e.AllowedOperations.HasFlag(DataPackageOperation.Copy));
                e.AcceptedOperation = DeskBoxDragData.ResolveFileDragFeedbackOperation(
                    e.DataView,
                    e.AcceptedOperation);
            }
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }

            ResetDragPayloadCache();
            deferral.Complete();
        }
    }

    private void StopFolderNavigationVisuals()
    {
        CancelFolderNavigationSpringBack();
        if (_folderNavigationSpringBackTimer is not null)
        {
            _folderNavigationSpringBackTimer.Tick -= FolderNavigationSpringBackTimer_Tick;
            _folderNavigationSpringBackTimer = null;
        }

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
