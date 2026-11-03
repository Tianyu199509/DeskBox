using System.Diagnostics;
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
    private static readonly long LaunchConsumptionWindowTicks = Stopwatch.Frequency;

    private readonly FileService _fileService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private Func<bool>? _hostHasActiveShellDragWindowProbe;
    private long _lastLaunchConsumedTicks;
    private int _hibernatedImportBusy;
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
        DispatcherQueue? dispatcherQueue = null,
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

        _fileService = fileService;
        _settingsService = settingsService;
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

    public bool HasActiveOutboundDrag =>
        !IsDisposed &&
        MaterializedView is FileSurfaceContent { HasActiveOutboundDrag: true };

    public event EventHandler? OutboundDragCompleted;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy =>
        MaterializedView is FileSurfaceContent { IsImportBusy: true } ||
        Volatile.Read(ref _hibernatedImportBusy) != 0;

    internal Func<bool>? HostHasActiveShellDragWindowProbe
    {
        get => _hostHasActiveShellDragWindowProbe;
        set
        {
            _hostHasActiveShellDragWindowProbe = value;
            if (Surface is { } content)
            {
                content.HostHasActiveShellDragWindowProbe = value;
            }
        }
    }

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
            content.OutboundDragCompleted += Content_OutboundDragCompleted;
            content.HostHasActiveShellDragWindowProbe = _hostHasActiveShellDragWindowProbe;
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
            if (content.WasLaunchConsumedRecently())
            {
                Interlocked.Exchange(ref _lastLaunchConsumedTicks, Stopwatch.GetTimestamp());
            }
            content.HostHasActiveShellDragWindowProbe = null;
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.HostContextMenuOpening -=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged -= Content_ImportBusyChanged;
            content.OutboundDragCompleted -= Content_OutboundDragCompleted;
            content.DisposeLeafOnly();
            ViewModel.ReleaseAllItemIcons(resetRenderWindow: true);
        }
    }

    private void Content_OutboundDragCompleted(object? sender, EventArgs e)
    {
        OutboundDragCompleted?.Invoke(this, e);
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
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(_settingsService.Settings);
        if (!_isWindowVisible &&
            string.Equals(
                performance.HiddenCacheCleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                StringComparison.Ordinal))
        {
            ViewModel.ReleaseAllItemIcons(resetRenderWindow: true);
        }
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
        Interlocked.Exchange(ref _lastLaunchConsumedTicks, Stopwatch.GetTimestamp());
        Surface?.MarkNativeLaunchConsumed();
    }

    internal bool WasLaunchConsumedRecently()
    {
        if (Surface is { } content && content.WasLaunchConsumedRecently())
        {
            return true;
        }

        long consumedTicks = Interlocked.Read(ref _lastLaunchConsumedTicks);
        return consumedTicks != 0 &&
            Stopwatch.GetTimestamp() - consumedTicks < LaunchConsumptionWindowTicks;
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

    internal IntPtr ActiveStackPopoverWindowHandle =>
        Surface?.ActiveStackPopoverWindowHandle ?? IntPtr.Zero;

    internal void CloseOpenStackPopover()
    {
        Surface?.CloseOpenStackPopover();
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped = null,
        WidgetItem? targetItem = null,
        FileDropIntent? forcedIntent = null,
        int? screenX = null,
        int? screenY = null,
        uint? allowedEffects = null,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null,
        int? preferredStackMemberIndex = null)
    {
        return MaterializedView is FileSurfaceContent content
            ? content.ImportNativeDroppedFilesAsync(
                paths,
                containsTemporaryFiles,
                copyWhenMapped,
                targetItem,
                forcedIntent,
                screenX,
                screenY,
                allowedEffects,
                itemOrigins,
                preferredStackMemberIndex)
            : ImportHibernatedNativeDroppedFilesAsync(
                paths,
                containsTemporaryFiles,
                copyWhenMapped,
                forcedIntent,
                allowedEffects,
                itemOrigins);
    }

    private async Task<bool> ImportHibernatedNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped,
        FileDropIntent? forcedIntent,
        uint? allowedEffects,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins)
    {
        if (paths.Count == 0 || IsDisposed)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _hibernatedImportBusy, 1, 0) != 0)
        {
            RaiseHibernatedFeedback(
                _localizationService.T("Widget.ImportInProgress"),
                WidgetFeedbackSeverity.Info,
                "file-hibernated-import-busy");
            return false;
        }

        ImportBusyChanged?.Invoke(true);
        try
        {
            if (!ViewModel.IsInitialized)
            {
                await ViewModel.InitializeAsync();
            }

            DroppedFilePath[] droppedFiles = await Task.Run(() =>
            {
                var resolved = new List<DroppedFilePath>(paths.Count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < paths.Count; index++)
                {
                    string rawPath = paths[index];
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(rawPath);
                    }
                    catch
                    {
                        continue;
                    }

                    if ((!File.Exists(fullPath) && !Directory.Exists(fullPath)) ||
                        !seen.Add(fullPath))
                    {
                        continue;
                    }

                    DroppedItemOriginKind originKind =
                        itemOrigins is not null && index < itemOrigins.Count
                            ? itemOrigins[index]
                            : (containsTemporaryFiles ||
                               NativeDropTarget.ContainsVirtualDropStagedPaths([fullPath])
                                ? DroppedItemOriginKind.MaterializedVirtualStream
                                : DroppedItemOriginKind.RealFileSystemPath);
                    bool itemForceCopy = itemOrigins is not null && index < itemOrigins.Count
                        ? originKind != DroppedItemOriginKind.RealFileSystemPath
                        : (containsTemporaryFiles ||
                           originKind != DroppedItemOriginKind.RealFileSystemPath);
                    resolved.Add(new DroppedFilePath(
                        fullPath,
                        Path.GetFileName(
                            fullPath.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar)),
                        ForceManagedCopy: itemForceCopy,
                        OriginKind: originKind));
                }

                return resolved.ToArray();
            });
            if (droppedFiles.Length == 0)
            {
                return false;
            }

            string destinationPath = TransferIntentEngine.ResolveProspectiveManagedDestination(
                ViewModel.CurrentFolderPath,
                ViewModel.MappedFolderPath,
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            bool hasProspectiveFolder = !string.IsNullOrWhiteSpace(destinationPath);
            string[] droppedPaths = droppedFiles.Select(static f => f.Path).ToArray();

            if (!string.IsNullOrWhiteSpace(destinationPath) &&
                await Task.Run(() => FileService.IsUnsafeDirectoryTransfer(droppedPaths, destinationPath)))
            {
                RaiseHibernatedFeedback(
                    _localizationService.T("Widget.Error.UnsafeFolderTransfer"),
                    WidgetFeedbackSeverity.Error,
                    "file-hibernated-unsafe-folder");
                return false;
            }

            bool allProviderTemporary = droppedFiles.All(static f => f.IsProviderTemporary);
            bool sameVolume = PathOriginClassifier.AreAllOnSameVolume(droppedPaths, destinationPath);
            bool isDeskBoxOwnedSource = ActiveDeskBoxDragRegistry.TryMatch(
                droppedPaths,
                out _,
                out string? sourceWidgetId,
                out _);
            if (!string.IsNullOrWhiteSpace(sourceWidgetId) &&
                string.Equals(sourceWidgetId, WidgetId, StringComparison.Ordinal))
            {
                return false;
            }

            SourceOriginSnapshot originSnapshot = PathOriginClassifier.ClassifySources(droppedPaths);
            bool protectDefaultMoveAsCopy = originSnapshot.IsProtectedCopyByDefault;
            bool sourceCanCopy = allowedEffects is null ||
                (allowedEffects & NativeDropEffectPolicy.Copy) != 0 ||
                isDeskBoxOwnedSource;
            bool sourceCanMove = !allProviderTemporary &&
                !originSnapshot.IsMoveProtectedSystemOrSharedSource &&
                (isDeskBoxOwnedSource ||
                 allowedEffects is null ||
                 (allowedEffects & NativeDropEffectPolicy.Move) != 0);
            bool followWindows = string.Equals(
                _settingsService.Settings.FileWidget.ManagedDropAction,
                SettingsService.ManagedDropActionFollowWindows,
                StringComparison.Ordinal);

            TransferDomain dropDomain = isDeskBoxOwnedSource
                ? TransferDomain.InternalDeskBox
                : TransferDomain.ExternalInbound;
            FileDropIntent intent = forcedIntent ??
                (followWindows
                    ? FileDropIntentPolicy.ResolveMappedTransfer(
                        hasMappedFolder: hasProspectiveFolder,
                        forceCopy: allProviderTemporary,
                        controlDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
                        shiftDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
                        defaultMove: true,
                        canCopy: sourceCanCopy,
                        canMove: sourceCanMove,
                        altDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
                        followWindows: true,
                        sameVolume: sameVolume,
                        protectDefaultMoveAsCopy: protectDefaultMoveAsCopy,
                        sourceOrigin: originSnapshot,
                        domain: dropDomain)
                    : copyWhenMapped switch
                    {
                        true => FileDropIntent.Copy,
                        false => FileDropIntent.Move,
                        _ => FileDropIntentPolicy.ResolveMappedTransfer(
                            hasMappedFolder: hasProspectiveFolder,
                            forceCopy: allProviderTemporary,
                            controlDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
                            shiftDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
                            defaultMove: string.Equals(
                                _settingsService.Settings.FileWidget.ManagedDropAction,
                                SettingsService.ManagedDropActionMove,
                                StringComparison.Ordinal),
                            canCopy: sourceCanCopy,
                            canMove: sourceCanMove,
                            altDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
                            followWindows: false,
                            sameVolume: sameVolume,
                            protectDefaultMoveAsCopy: protectDefaultMoveAsCopy,
                            sourceOrigin: originSnapshot,
                            domain: dropDomain)
                    });
            if (intent == FileDropIntent.None)
            {
                return false;
            }

            bool? moveWhenMapped = FileSurfaceContent.ResolveMoveWhenMapped(hasProspectiveFolder, intent);
            var movedSourcePaths = new List<string>();
            int importedCount = 0;

            string[] regularPaths = droppedFiles
                .Where(static f => !f.ForceManagedCopy)
                .Select(static f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (regularPaths.Length > 0)
            {
                if (intent == FileDropIntent.Shortcut)
                {
                    string? shortcutDest = ViewModel.CurrentFolderPath ?? ViewModel.MappedFolderPath;
                    if (string.IsNullOrWhiteSpace(shortcutDest))
                    {
                        shortcutDest = ViewModel.EnsureManagedFolderForDrop();
                    }

                    if (!string.IsNullOrWhiteSpace(shortcutDest))
                    {
                        IReadOnlyList<string> created = await FileSurfaceContent.CreateShortcutFilesAsync(
                            droppedFiles.Where(static f => !f.ForceManagedCopy).ToArray(),
                            shortcutDest,
                            _localizationService.T("Widget.CreateShortcut"),
                            CancellationToken.None);
                        importedCount += created.Count;
                        if (created.Count > 0 && !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                        {
                            await ViewModel.RefreshFromConfigAsync(clearIconCacheBeforeHydration: false);
                        }
                    }
                }
                else
                {
                    IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                        regularPaths,
                        moveWhenMapped,
                        useShellProgress: true,
                        ownerWindowHandle: _hostWindowHandle);
                    importedCount += completed.Count;
                    if (moveWhenMapped == true)
                    {
                        movedSourcePaths.AddRange(completed);
                    }
                }
            }

            string[] managedCopyPaths = droppedFiles
                .Where(static f => f.ForceManagedCopy)
                .Select(static f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (managedCopyPaths.Length > 0)
            {
                IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                    managedCopyPaths,
                    moveWhenMapped: false,
                    useShellProgress: false,
                    ownerWindowHandle: _hostWindowHandle);
                importedCount += completed.Count;
            }

            if (movedSourcePaths.Count > 0 &&
                !string.IsNullOrWhiteSpace(sourceWidgetId) &&
                !string.Equals(sourceWidgetId, WidgetId, StringComparison.Ordinal) &&
                App.Current?.WidgetManager is { } manager)
            {
                await manager.NotifyItemsMovedOutAsync(sourceWidgetId, movedSourcePaths);
            }

            if (importedCount <= 0)
            {
                return false;
            }

            RaiseHibernatedFeedback(
                _localizationService.Format("Widget.FilesImported", importedCount),
                WidgetFeedbackSeverity.Success,
                "file-hibernated-drop");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Hibernated File drop failed id={WidgetId}: {ex}");
            RaiseHibernatedFeedback(
                _localizationService.T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "file-hibernated-drop-error");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _hibernatedImportBusy, 0);
            ImportBusyChanged?.Invoke(false);
        }
    }

    private void RaiseHibernatedFeedback(
        string message,
        WidgetFeedbackSeverity severity,
        string? deduplicationKey)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey)));
    }

    internal void ApplyHostEdgeDragOverFeedback(DragEventArgs e)
    {
        if (Surface is { } content)
        {
            content.ApplyHostEdgeDragOverFeedback(e);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        DataPackageOperation supported = DeskBoxDragData.GetFileTransferOperations(
            e.DataView,
            e.AllowedOperations);
        bool isInternal = DeskBoxDragData.IsInternalFileDrag(e.DataView);
        string[] sourcePaths = isInternal
            ? DeskBoxDragData.GetRawInternalSourcePaths(e.DataView).ToArray()
            : [];
        string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
            CurrentFolderPath,
            ViewModel.MappedFolderPath,
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        bool sameVolume = sourcePaths.Length == 0 ||
            (!string.IsNullOrWhiteSpace(destination) &&
             PathOriginClassifier.AreAllOnSameVolume(
                 sourcePaths,
                 destination,
                 TimeSpan.FromMilliseconds(80)));
        string action = _settingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        uint allowedEffects = Views.ContentWidgetWindow.ToNativeAllowedEffects(supported);
        FileDropIntent intent = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: isInternal ? TransferDomain.InternalDeskBox : TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: !string.IsNullOrWhiteSpace(destination),
            ControlDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
            ShiftDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
            AltDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
            AllowedEffects: allowedEffects,
            NoOperationMetadata: supported == DataPackageOperation.None,
            DefaultMove: string.Equals(action, SettingsService.ManagedDropActionMove, StringComparison.Ordinal),
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).Intent;
        DataPackageOperation resolved = intent switch
        {
            FileDropIntent.Copy => DataPackageOperation.Copy,
            FileDropIntent.Move => DataPackageOperation.Move,
            FileDropIntent.Shortcut or FileDropIntent.Reference => DataPackageOperation.Link,
            _ => DataPackageOperation.None,
        };
        e.AcceptedOperation = DeskBoxDragData.ResolveFileDragFeedbackOperation(
            e.DataView,
            resolved,
            e.AllowedOperations);
        e.DragUIOverride.IsContentVisible = isInternal;
        e.DragUIOverride.IsGlyphVisible = isInternal && e.AcceptedOperation != DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = isInternal && e.AcceptedOperation != DataPackageOperation.None;
        e.Handled = true;
    }

    internal async void HandleHostEdgeDrop(DragEventArgs e)
    {
        if (Surface is { } content)
        {
            content.HandleHostEdgeDrop(e);
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        DataPackageOperation allowedOperations = e.AllowedOperations;
        bool ctrlDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool shiftDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        bool altDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu);
        bool isInternal = DeskBoxDragData.IsInternalFileDrag(e.DataView);
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            if (batch.Files.Count == 0)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            DataPackageOperation supported = DeskBoxDragData.GetFileTransferOperations(
                e.DataView,
                allowedOperations);
            uint effects = Views.ContentWidgetWindow.ToNativeAllowedEffects(supported);
            bool allTemp = batch.Files.All(static f => f.IsProviderTemporary);
            bool anyTemp = batch.Files.Any(static f => f.IsProviderTemporary);
            string[] paths = batch.Files.Select(static f => f.Path).ToArray();
            DroppedItemOriginKind[] origins = batch.Files.Select(static f => f.OriginKind).ToArray();
            FileDropIntent? forcedIntent = Views.ContentWidgetWindow.ResolveSnapshotForcedIntent(
                ctrlDown,
                shiftDown,
                altDown,
                effects,
                allTemp,
                paths);
            bool imported = await ImportHibernatedNativeDroppedFilesAsync(
                paths,
                anyTemp,
                copyWhenMapped: null,
                forcedIntent: forcedIntent,
                allowedEffects: effects,
                itemOrigins: origins);
            int nonTempCount = batch.Files.Count(static f => !f.IsProviderTemporary);
            DataPackageOperation requested = forcedIntent switch
            {
                FileDropIntent.Copy => DataPackageOperation.Copy,
                FileDropIntent.Move => DataPackageOperation.Move,
                FileDropIntent.Shortcut or FileDropIntent.Reference => DataPackageOperation.Link,
                _ => supported.HasFlag(DataPackageOperation.Copy)
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None,
            };
            e.AcceptedOperation = imported
                ? FileSurfaceContent.ResolveSafeDropCompletionOperation(
                    requested,
                    isInternal,
                    requestedMoveCount: nonTempCount,
                    completedMoveCount: nonTempCount,
                    canReportCopy: allowedOperations.HasFlag(DataPackageOperation.Copy))
                : DataPackageOperation.None;
            e.AcceptedOperation = DeskBoxDragData.ResolveFileDragFeedbackOperation(
                e.DataView,
                e.AcceptedOperation);
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal WidgetItem? TryResolveFolderNavigationDropTarget(int screenX, int screenY) =>
        Surface?.TryResolveFolderNavigationDropTarget(screenX, screenY, out _);

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

        _hostHasActiveShellDragWindowProbe = null;
        if (Surface is { } content)
        {
            // The leaf's dispose chain releases its shell surfaces and
            // disposes the view model, matching the pre-adapter ownership.
            content.HostHasActiveShellDragWindowProbe = null;
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.HostContextMenuOpening -=
                Content_HostContextMenuOpening;
            content.ImportBusyChanged -= Content_ImportBusyChanged;
            content.OutboundDragCompleted -= Content_OutboundDragCompleted;
            content.Dispose();
        }
        else
        {
            ViewModel.Dispose();
        }
    }
}
