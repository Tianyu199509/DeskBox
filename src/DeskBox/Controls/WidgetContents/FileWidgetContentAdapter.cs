using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Residency adapter for File widgets. The adapter owns the view model and
/// the lazy view, so a cached group member keeps its watcher and projection
/// while the leaf control stays a disposable view (roadmap P1-c). Host
/// callbacks that used to sit on the content itself — most notably the
/// confirm-extension dialog wiring — hang off the adapter.
/// </summary>
public sealed class FileWidgetContentAdapter :
    WidgetContentAdapterBase,
    ICancellableWidgetContent,
    IWidgetGroupContentCacheable,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetHostContextMenuSource,
    IWidgetTransientStateContent
{
    private readonly LocalizationService _localizationService;
    private IntPtr _hostWindowHandle;
    private bool _hasBeenWindowVisible;
    private bool _isWindowVisible = true;
    private bool _isWindowRevealCompleted;
    private bool _isCompactCollapsed;

    public FileWidgetContentAdapter(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Func<WidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            fileService,
            new WidgetViewModel(
                config,
                fileService,
                organizerService,
                settingsService,
                localizationService,
                dispatcherQueue),
            settingsService,
            localizationService,
            viewFactory)
    {
    }

    private FileWidgetContentAdapter(
        WidgetConfig config,
        FileService fileService,
        WidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<WidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new FileSurfaceContent(
                vm,
                fileService,
                settingsService,
                localizationService)))(viewModel))
    {
        ArgumentNullException.ThrowIfNull(fileService);

        _localizationService = localizationService;
        ViewModel = viewModel;
    }

    public WidgetViewModel ViewModel { get; }

    /// <summary>
    /// The materialized leaf, if the view has been attached. Reading this
    /// never constructs the leaf; group-cache probes rely on that.
    /// </summary>
    internal FileSurfaceContent? Surface => MaterializedView as FileSurfaceContent;

    /// <summary>
    /// The materialized leaf for hosts that must operate on the live view
    /// (QuickLook navigation, AOT smoke). Throws instead of returning null
    /// so a premature call cannot silently no-op.
    /// </summary>
    internal FileSurfaceContent RequireSurface()
    {
        return Surface ?? throw new InvalidOperationException(
            $"File widget '{WidgetId}' has no materialized surface leaf; " +
            "the host must attach the adapter before reaching its view.");
    }

    public bool IsReadyForReuse =>
        !IsDisposed &&
        (MaterializedView is FileSurfaceContent content
            ? content.IsReadyForReuse
            : ViewModel.IsInitialized);

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy =>
        MaterializedView is FileSurfaceContent { IsImportBusy: true };

    internal string? CurrentFolderPath =>
        MaterializedView is FileSurfaceContent content
            ? content.ViewModel.CurrentFolderPath
            : ViewModel.IsInitialized
                ? ViewModel.CurrentFolderPath
                : null;

    internal long? ImportBusyElapsedMilliseconds =>
        Surface?.ImportBusyElapsedMilliseconds;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is FileSurfaceContent content)
        {
            ViewModel.ResumeIconHydrationFromDormancy();
            content.FeedbackRequested += Content_FeedbackRequested;
            content.HostContextMenuOpening +=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged += Content_ImportBusyChanged;
            if (_hostWindowHandle != IntPtr.Zero)
            {
                content.SetHostWindowHandle(_hostWindowHandle);
            }

            content.OnWindowVisibilityChanged(_isWindowVisible);
            content.OnCompactStateChanged(_isCompactCollapsed);
            content.SyncFromResidentViewModel();
            content.QueueRenderWindowViewportCoverage();
            if (_isWindowVisible && _isWindowRevealCompleted)
            {
                content.OnWindowRevealCompleted();
            }
            else if (_isWindowVisible && ViewModel.IsInitialized)
            {
                ViewModel.HealReleasedItemIconsIfIdle();
            }
        }
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        if (view is FileSurfaceContent content)
        {
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.HostContextMenuOpening -=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged -= Content_ImportBusyChanged;
            content.DisposeLeafOnly();
            ViewModel.ReleaseAllItemIcons(resetRenderWindow: true);
        }
    }

    private void Content_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    private void Content_HostContextMenuOpening(
        object? sender,
        WidgetHostContextMenuOpeningEventArgs e)
    {
        HostContextMenuOpening?.Invoke(this, e);
    }

    private void Content_ImportBusyChanged(bool isBusy)
    {
        ImportBusyChanged?.Invoke(isBusy);
    }

    public override Task InitializeAsync()
    {
        return Surface is { } content
            ? content.InitializeAsync()
            : ViewModel.InitializeAsync();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Surface is { } content
            ? content.InitializeAsync(cancellationToken)
            : ViewModel.InitializeAsync(cancellationToken);
    }

    public override Task RefreshAsync()
    {
        if (Surface is { } content)
        {
            return content.RefreshAsync();
        }

        return ViewModel.IsInitialized
            ? ViewModel.RefreshFolderContentsAsync()
            : Task.CompletedTask;
    }

    // The picker-based add action is back: the native dialog pins
    // FOS_NODEREFERENCELINKS, so adding a shortcut no longer dereferences
    // and moves its target (issue 458, the reason this was stubbed).
    public Task AddFromTitleButtonAsync()
    {
        return AsContent(View).AddFromTitleButtonAsync();
    }

    public override void ApplyAppearance()
    {
        if (Surface is { } content)
        {
            content.ApplyAppearance();
        }
        else
        {
            ViewModel.ApplyAppearancePreview();
        }
    }

    public override void OnActivated()
    {
        Surface?.OnActivated();
    }

    public override void OnDeactivated()
    {
        Surface?.OnDeactivated();
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (visible)
        {
            _hasBeenWindowVisible = true;
            if (Surface is { } visibleContent)
            {
                ViewModel.ResumeIconHydrationFromDormancy();
                visibleContent.OnWindowVisibilityChanged(true);
                if (!_isCompactCollapsed)
                {
                    visibleContent.QueueRenderWindowViewportCoverage();
                    if (ViewModel.IsInitialized && !IsDisposed)
                    {
                        ViewModel.HealReleasedItemIconsIfIdle();
                    }
                }
            }
            else if (!_isCompactCollapsed && ViewModel.IsInitialized && !IsDisposed)
            {
                ViewModel.ResumeIconHydrationFromDormancy();
                ViewModel.HealReleasedItemIconsIfIdle();
            }

            return;
        }

        _isWindowRevealCompleted = false;
        if (Surface is { } hiddenContent)
        {
            hiddenContent.OnWindowVisibilityChanged(false);
        }
        else if (_hasBeenWindowVisible && !IsDisposed)
        {
            ViewModel.SuspendBackgroundActivity();
        }
    }

    public override void OnWindowRevealCompleted()
    {
        if (IsDisposed || !_isWindowVisible)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        if (Surface is { } content)
        {
            ViewModel.ResumeIconHydrationFromDormancy();
            content.OnWindowRevealCompleted();
        }
        else
        {
            ViewModel.ResumeBackgroundActivity();
            if (!_isCompactCollapsed && ViewModel.IsInitialized)
            {
                ViewModel.ResumeIconHydrationFromDormancy();
                ViewModel.HealReleasedItemIconsIfIdle();
            }
        }
    }

    public override void OnWindowLongHidden()
    {
        // Model-level only: nulling icons outside the render window keeps
        // every view warm for the next reveal. The file surface itself must
        // not grow a long-hidden teardown path (pinned by
        // WidgetVisualActivityContractTests), so this goes straight to the
        // view model instead of through the surface.
        ViewModel.ReleaseUnrenderedItemIcons();
        ViewModel.ReleaseAllItemIcons(resetRenderWindow: true);
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        if (!collapsed)
        {
            ViewModel.ResumeIconHydrationFromDormancy();
        }

        if (Surface is { } content)
        {
            content.OnCompactStateChanged(collapsed);
            if (!collapsed)
            {
                content.QueueRenderWindowViewportCoverage();
                if (ViewModel.IsInitialized && !IsDisposed)
                {
                    ViewModel.HealReleasedItemIconsIfIdle();
                }
            }
        }
        else if (!collapsed && ViewModel.IsInitialized && !IsDisposed)
        {
            ViewModel.HealReleasedItemIconsIfIdle();
        }
    }

    public override void OnCompactBoundsTransitionActiveChanged(bool isActive)
    {
        if (Surface is { } content)
        {
            content.OnCompactBoundsTransitionActiveChanged(isActive);
        }
        else if (isActive)
        {
            ViewModel.PauseIconHydrationForCompactTransition();
        }
        else
        {
            ViewModel.ResumeIconHydrationAfterCompactTransition();
        }
    }

    public void PrepareForReuse()
    {
        ViewModel.ResumeIconHydrationFromDormancy();
        if (Surface is { } content)
        {
            content.PrepareForReuse();
            content.QueueRenderWindowViewportCoverage();
        }
        else if (ViewModel.IsInitialized && !IsDisposed)
        {
            ViewModel.PrepareStackDisplayForReuse();
            ViewModel.HealReleasedItemIconsIfIdle();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        if (Surface is { } content)
        {
            return content.CaptureTransientState();
        }

        return HasHibernatedTransientState ? HibernatedTransientState : null;
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (Surface is { } content)
        {
            content.RestoreTransientState(state);
            return;
        }

        SaveHibernatedTransientState(state);
    }

    internal void SetHostWindowHandle(IntPtr windowHandle)
    {
        // The extension-change confirmation belongs to the adapter's view
        // model wiring, not to the leaf view: a cached member can outlive a
        // specific leaf, and the dialog only needs the owning HWND.
        _hostWindowHandle = windowHandle;
        Surface?.SetHostWindowHandle(windowHandle);

        ViewModel.ConfirmExtensionChangeHandler = ConfirmExtensionRename;
    }

    private bool ConfirmExtensionRename(string sourcePath, string destinationPath)
    {
        if (IsDisposed)
        {
            return false;
        }

        return Win32Helper.ConfirmExtensionChange(
            _hostWindowHandle,
            _localizationService.T("Widget.Rename.ExtensionChangeWarning"),
            _localizationService.T("Common.Rename"));
    }

    internal void RevealSavedItem(string itemPath)
    {
        if (View is FileSurfaceContent content)
        {
            content.RevealSavedItem(itemPath);
        }
    }

    internal void SetMigrationBusy(bool isBusy)
    {
        Surface?.SetMigrationBusy(isBusy);
    }

    internal void SetDesktopOrganizationBusy(bool isBusy)
    {
        Surface?.SetDesktopOrganizationBusy(isBusy);
    }

    internal void ClearItemSelection()
    {
        if (Surface is { } content)
        {
            content.ClearItemSelection();
            return;
        }

        foreach (WidgetItem item in ViewModel.Items)
        {
            if (item.IsCut)
            {
                item.IsCut = false;
            }
        }

        if (HasHibernatedTransientState &&
            HibernatedTransientState is FileWidgetTransientState)
        {
            SaveHibernatedTransientState(new FileWidgetTransientState([], []));
        }
    }

    internal Task ApplyFolderOpenBehaviorChangeAsync()
    {
        return Surface is { } content
            ? content.ApplyFolderOpenBehaviorChangeAsync()
            : ViewModel.RefreshFolderOpenBehaviorAsync();
    }

    internal void SuspendItemContainerTransitionsForHostSwitch()
    {
        Surface?.SuspendItemContainerTransitionsForHostSwitch();
    }

    internal void ResumeItemContainerTransitionsAfterHostSwitch()
    {
        Surface?.ResumeItemContainerTransitionsAfterHostSwitch();
    }

    internal void ClearDragSessionVisualState()
    {
        Surface?.ClearDragSessionVisualState();
    }

    internal bool CompleteReleasedDragSession()
    {
        return Surface?.CompleteReleasedDragSession() ?? true;
    }

    internal bool ShouldDeferReleasedDragSessionRecovery()
    {
        return Surface?.ShouldDeferReleasedDragSessionRecovery() ?? false;
    }

    internal void CaptureNativeDropInsertion(int screenX, int screenY)
    {
        Surface?.CaptureNativeDropInsertion(screenX, screenY);
    }

    internal void ClearPendingNativeDropInsertion()
    {
        Surface?.ClearPendingNativeDropInsertion();
    }

    internal void ObserveNativeDragPointer(
        int screenX,
        int screenY,
        bool hasFileData,
        IReadOnlyList<string>? pathHints = null,
        WidgetItem? nativeTarget = null,
        WidgetItem? launchTarget = null)
    {
        Surface?.ObserveNativeDragPointer(
            screenX,
            screenY,
            hasFileData,
            pathHints,
            nativeTarget,
            launchTarget);
    }

    internal void MarkNativeLaunchConsumed()
    {
        Surface?.MarkNativeLaunchConsumed();
    }

    internal bool WasLaunchConsumedRecently()
    {
        // Fail-safe direction: a null Surface means the consumed latch lives on
        // the previous surface while the content already switched to an
        // unmaterialized member. Reporting "not consumed" would re-arm the
        // legacy WM_DROPFILES import and the OLE double-launch paths; reporting
        // "consumed" only means the gesture is left unimported.
        return Surface?.WasLaunchConsumedRecently() ?? true;
    }

    internal void ShowShortcutLaunchRefusedFeedback(string? applicationName)
    {
        Surface?.ShowShortcutLaunchRefusedFeedback(applicationName);
    }

    internal void NotifyNativeDropBlockedUndisplayable(int undisplayableCount)
    {
        Surface?.NotifyNativeDropBlockedUndisplayable(undisplayableCount);
    }

    internal void NotifyNativeDropZeroResult()
    {
        Surface?.NotifyNativeDropZeroResult();
    }

    internal void NotifyNativeDropImportBusy()
    {
        Surface?.NotifyNativeDropImportBusy();
    }

    internal bool IsInternalReorderDrag(DataPackageView dataView)
    {
        return Surface?.IsInternalReorderDrag(dataView) ?? false;
    }

    internal bool SuppressesNativeShellDragVisual =>
        Surface?.SuppressesNativeShellDragVisual ?? false;

    internal bool IsStackPopoverBlockingSurfaceOpen =>
        Surface?.IsStackPopoverBlockingSurfaceOpen ?? false;

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped = null,
        WidgetItem? targetItem = null,
        FileDropIntent? forcedIntent = null,
        int? screenX = null,
        int? screenY = null,
        uint? allowedEffects = null)
    {
        return View is FileSurfaceContent content
            ? content.ImportNativeDroppedFilesAsync(
                paths,
                containsTemporaryFiles,
                copyWhenMapped,
                targetItem,
                forcedIntent,
                screenX,
                screenY,
                allowedEffects)
            : Task.FromResult(false);
    }

    internal void ApplyHostEdgeDragOverFeedback(DragEventArgs e)
    {
        Surface?.ApplyHostEdgeDragOverFeedback(e);
    }

    internal void HandleHostEdgeDrop(DragEventArgs e)
    {
        Surface?.HandleHostEdgeDrop(e);
    }

    internal Task<bool> TryHandleClipboardShortcutAsync(KeyRoutedEventArgs e)
    {
        return Surface is { } content
            ? content.TryHandleClipboardShortcutAsync(e)
            : Task.FromResult(false);
    }

    internal IReadOnlyList<string> GetQuickLookNavigationPaths()
    {
        return Surface?.GetQuickLookNavigationPaths() ?? [];
    }

    internal bool TrySelectQuickLookTarget(string path)
    {
        return Surface?.TrySelectQuickLookTarget(path) ?? false;
    }

    internal void FocusQuickLookNavigationTarget()
    {
        Surface?.FocusQuickLookNavigationTarget();
    }

    private FileSurfaceContent AsContent(FrameworkElement view)
    {
        return view as FileSurfaceContent ??
            throw new InvalidOperationException(
                "File widget content requires the surface leaf view.");
    }

    protected override void Dispose(bool disposing)
    {
        if (ViewModel.ConfirmExtensionChangeHandler == ConfirmExtensionRename)
        {
            ViewModel.ConfirmExtensionChangeHandler = null;
        }

        if (Surface is { } content)
        {
            // The leaf's dispose chain releases its shell surfaces and
            // disposes the view model, matching the pre-adapter ownership.
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.HostContextMenuOpening -=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged -= Content_ImportBusyChanged;
            content.Dispose();
        }
        else
        {
            ViewModel.Dispose();
        }
    }
}
