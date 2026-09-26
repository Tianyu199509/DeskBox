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
    IWidgetContent,
    ICancellableWidgetContent,
    IWidgetGroupContentCacheable,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetHostContextMenuSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private readonly Func<WidgetViewModel, FrameworkElement> _viewFactory;
    private readonly WidgetViewModel _viewModel;
    private readonly FileService _fileService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private FrameworkElement? _view;
    private IntPtr _hostWindowHandle;
    private bool _isDisposed;

    public FileWidgetContentAdapter(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Func<WidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(fileService);

        Config = config;
        _fileService = fileService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _viewModel = new WidgetViewModel(
            config,
            fileService,
            organizerService,
            settingsService,
            localizationService,
            dispatcherQueue);
        _viewFactory = viewFactory ?? (viewModel => new FileSurfaceContent(
            viewModel,
            fileService,
            settingsService,
            localizationService));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public WidgetViewModel ViewModel => _viewModel;

    public FrameworkElement View
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_view is null)
            {
                _view = _viewFactory(_viewModel);
                if (_view is FileSurfaceContent content)
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

            return _view;
        }
    }

    /// <summary>
    /// The materialized leaf, if the view has been attached. Reading this
    /// never constructs the leaf; group-cache probes rely on that.
    /// </summary>
    internal FileSurfaceContent? Surface => _view as FileSurfaceContent;

    public bool IsReadyForReuse =>
        _view is FileSurfaceContent content &&
        content.IsReadyForReuse;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy =>
        _view is FileSurfaceContent { IsImportBusy: true };

    internal long? ImportBusyElapsedMilliseconds =>
        (_view as FileSurfaceContent)?.ImportBusyElapsedMilliseconds;

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

    public Task InitializeAsync()
    {
        return AsContent(View).InitializeAsync();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return AsContent(View).InitializeAsync(cancellationToken);
    }

    public Task RefreshAsync()
    {
        return _view is FileSurfaceContent content
            ? content.RefreshAsync()
            : Task.CompletedTask;
    }

    public Task AddFromTitleButtonAsync()
    {
        return AsContent(View).AddFromTitleButtonAsync();
    }

    public void ApplyAppearance()
    {
        if (_view is FileSurfaceContent content)
        {
            content.ApplyAppearance();
        }
    }

    public void OnActivated()
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnActivated();
        }
    }

    public void OnDeactivated()
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnDeactivated();
        }
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnWindowRevealCompleted();
        }
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnCompactStateChanged(collapsed);
        }
    }

    public void OnCompactBoundsTransitionActiveChanged(bool isActive)
    {
        if (_view is FileSurfaceContent content)
        {
            content.OnCompactBoundsTransitionActiveChanged(isActive);
        }
    }

    public void PrepareForReuse()
    {
        if (_view is FileSurfaceContent content)
        {
            content.PrepareForReuse();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        return (_view as FileSurfaceContent)?.CaptureTransientState();
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        (_view as FileSurfaceContent)?.RestoreTransientState(state);
    }

    internal void SetHostWindowHandle(IntPtr windowHandle)
    {
        // The extension-change confirmation belongs to the adapter's view
        // model wiring, not to the leaf view: a cached member can outlive a
        // specific leaf, and the dialog only needs the owning HWND.
        _hostWindowHandle = windowHandle;
        if (_view is FileSurfaceContent content)
        {
            content.SetHostWindowHandle(windowHandle);
        }

        _viewModel.ConfirmExtensionChangeHandler = ConfirmExtensionRename;
    }

    private bool ConfirmExtensionRename(string sourcePath, string destinationPath)
    {
        if (_isDisposed)
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
        (_view as FileSurfaceContent)?.RevealSavedItem(itemPath);
    }

    internal void SetMigrationBusy(bool isBusy)
    {
        (_view as FileSurfaceContent)?.SetMigrationBusy(isBusy);
    }

    internal void SetDesktopOrganizationBusy(bool isBusy)
    {
        (_view as FileSurfaceContent)?.SetDesktopOrganizationBusy(isBusy);
    }

    internal void ClearItemSelection()
    {
        (_view as FileSurfaceContent)?.ClearItemSelection();
    }

    internal Task ApplyFolderOpenBehaviorChangeAsync()
    {
        return _view is FileSurfaceContent content
            ? content.ApplyFolderOpenBehaviorChangeAsync()
            : Task.CompletedTask;
    }

    internal void SuspendItemContainerTransitionsForHostSwitch()
    {
        (_view as FileSurfaceContent)?.SuspendItemContainerTransitionsForHostSwitch();
    }

    internal void ResumeItemContainerTransitionsAfterHostSwitch()
    {
        (_view as FileSurfaceContent)?.ResumeItemContainerTransitionsAfterHostSwitch();
    }

    internal void ClearDragSessionVisualState()
    {
        (_view as FileSurfaceContent)?.ClearDragSessionVisualState();
    }

    internal bool CompleteReleasedDragSession()
    {
        return (_view as FileSurfaceContent)?.CompleteReleasedDragSession() ?? true;
    }

    internal bool ShouldDeferReleasedDragSessionRecovery()
    {
        return (_view as FileSurfaceContent)?
            .ShouldDeferReleasedDragSessionRecovery() ?? false;
    }

    internal void CaptureNativeDropInsertion(int screenX, int screenY)
    {
        (_view as FileSurfaceContent)?.CaptureNativeDropInsertion(screenX, screenY);
    }

    internal void ClearPendingNativeDropInsertion()
    {
        (_view as FileSurfaceContent)?.ClearPendingNativeDropInsertion();
    }

    internal void ObserveNativeDragPointer(
        int screenX,
        int screenY,
        bool hasFileData,
        IReadOnlyList<string>? pathHints = null,
        WidgetItem? nativeTarget = null,
        WidgetItem? launchTarget = null)
    {
        (_view as FileSurfaceContent)?.ObserveNativeDragPointer(
            screenX,
            screenY,
            hasFileData,
            pathHints,
            nativeTarget,
            launchTarget);
    }

    internal void MarkNativeLaunchConsumed()
    {
        (_view as FileSurfaceContent)?.MarkNativeLaunchConsumed();
    }

    internal bool WasLaunchConsumedRecently()
    {
        return (_view as FileSurfaceContent)?.WasLaunchConsumedRecently() ?? false;
    }

    internal void ShowShortcutLaunchRefusedFeedback(string? applicationName)
    {
        (_view as FileSurfaceContent)?.ShowShortcutLaunchRefusedFeedback(
            applicationName);
    }

    internal void NotifyNativeDropBlockedUndisplayable(int undisplayableCount)
    {
        (_view as FileSurfaceContent)?.NotifyNativeDropBlockedUndisplayable(
            undisplayableCount);
    }

    internal bool IsInternalReorderDrag(DataPackageView dataView)
    {
        return (_view as FileSurfaceContent)?.IsInternalReorderDrag(dataView) ?? false;
    }

    internal bool SuppressesNativeShellDragVisual =>
        (_view as FileSurfaceContent)?.SuppressesNativeShellDragVisual ?? false;

    internal bool IsStackPopoverBlockingSurfaceOpen =>
        (_view as FileSurfaceContent)?.IsStackPopoverBlockingSurfaceOpen ?? false;

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped = null,
        WidgetItem? targetItem = null,
        FileDropIntent? forcedIntent = null,
        int? screenX = null,
        int? screenY = null)
    {
        return _view is FileSurfaceContent content
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
        (_view as FileSurfaceContent)?.ApplyHostEdgeDragOverFeedback(e);
    }

    internal void HandleHostEdgeDrop(DragEventArgs e)
    {
        (_view as FileSurfaceContent)?.HandleHostEdgeDrop(e);
    }

    internal Task<bool> TryHandleClipboardShortcutAsync(KeyRoutedEventArgs e)
    {
        return _view is FileSurfaceContent content
            ? content.TryHandleClipboardShortcutAsync(e)
            : Task.FromResult(false);
    }

    internal IReadOnlyList<string> GetQuickLookNavigationPaths()
    {
        return (_view as FileSurfaceContent)?.GetQuickLookNavigationPaths() ?? [];
    }

    internal bool TrySelectQuickLookTarget(string path)
    {
        return (_view as FileSurfaceContent)?.TrySelectQuickLookTarget(path) ?? false;
    }

    internal void FocusQuickLookNavigationTarget()
    {
        (_view as FileSurfaceContent)?.FocusQuickLookNavigationTarget();
    }

    private FileSurfaceContent AsContent(FrameworkElement view)
    {
        return view as FileSurfaceContent ??
            throw new InvalidOperationException(
                "File widget content requires the surface leaf view.");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_viewModel.ConfirmExtensionChangeHandler == ConfirmExtensionRename)
        {
            _viewModel.ConfirmExtensionChangeHandler = null;
        }

        if (_view is FileSurfaceContent content)
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
            _viewModel.Dispose();
        }
    }
}
