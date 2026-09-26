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
        MaterializedView is FileSurfaceContent content &&
        content.IsReadyForReuse;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy =>
        MaterializedView is FileSurfaceContent { IsImportBusy: true };

    internal long? ImportBusyElapsedMilliseconds =>
        Surface?.ImportBusyElapsedMilliseconds;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is FileSurfaceContent content)
        {
            content.FeedbackRequested += Content_FeedbackRequested;
            content.HostContextMenuOpening +=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged += Content_ImportBusyChanged;
            if (_hostWindowHandle != IntPtr.Zero)
            {
                content.SetHostWindowHandle(_hostWindowHandle);
            }
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
        return AsContent(View).InitializeAsync();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return AsContent(View).InitializeAsync(cancellationToken);
    }

    public override Task RefreshAsync()
    {
        return Surface is { } content
            ? content.RefreshAsync()
            : Task.CompletedTask;
    }

    public Task AddFromTitleButtonAsync()
    {
        return AsContent(View).AddFromTitleButtonAsync();
    }

    public override void ApplyAppearance()
    {
        Surface?.ApplyAppearance();
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
        Surface?.OnWindowVisibilityChanged(visible);
    }

    public override void OnWindowRevealCompleted()
    {
        Surface?.OnWindowRevealCompleted();
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        Surface?.OnCompactStateChanged(collapsed);
    }

    public override void OnCompactBoundsTransitionActiveChanged(bool isActive)
    {
        Surface?.OnCompactBoundsTransitionActiveChanged(isActive);
    }

    public void PrepareForReuse()
    {
        Surface?.PrepareForReuse();
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        return Surface?.CaptureTransientState();
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        Surface?.RestoreTransientState(state);
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
        Surface?.RevealSavedItem(itemPath);
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
        Surface?.ClearItemSelection();
    }

    internal Task ApplyFolderOpenBehaviorChangeAsync()
    {
        return Surface is { } content
            ? content.ApplyFolderOpenBehaviorChangeAsync()
            : Task.CompletedTask;
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
        return Surface?.WasLaunchConsumedRecently() ?? false;
    }

    internal void ShowShortcutLaunchRefusedFeedback(string? applicationName)
    {
        Surface?.ShowShortcutLaunchRefusedFeedback(applicationName);
    }

    internal void NotifyNativeDropBlockedUndisplayable(int undisplayableCount)
    {
        Surface?.NotifyNativeDropBlockedUndisplayable(undisplayableCount);
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
        int? screenY = null)
    {
        return Surface is { } content
            ? content.ImportNativeDroppedFilesAsync(
                paths,
                containsTemporaryFiles,
                copyWhenMapped,
                targetItem,
                forcedIntent,
                screenX,
                screenY)
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
