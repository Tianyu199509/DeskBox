using System.Text;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.Helpers;

internal readonly record struct LegacyDropFilesPayload(
    IReadOnlyList<string> StagedPaths,
    IReadOnlyList<DroppedItemOriginKind> ItemOrigins,
    bool ContainsTemporaryFiles,
    int? ScreenX,
    int? ScreenY);

internal interface INativeDropHostBridge : IDisposable
{
    IntPtr HostWindowHandle { get; }

    IReadOnlyCollection<NativeDropTarget> Targets { get; }

    bool HasActiveShellDragWindow { get; }

    long MaxActiveGestureId { get; }

    void EnsureRegistered();
}

/// <summary>
/// Reusable native inbound drop bridge for top-level DeskBox host windows (such
/// as <see cref="DeskBox.Views.StackPopoverHostWindow"/>). Subclasses the host
/// HWND for UIPI <c>WM_DROPFILES</c> (preserving <c>DragQueryPoint</c> screen
/// coordinates and staging transient paths before <c>DragFinish</c>) and
/// registers <see cref="NativeDropTarget"/> on both the outer HWND and its
/// inner <c>Microsoft.UI.Content.DesktopChildSiteBridge</c> child islands.
/// </summary>
internal sealed class NativeDropHostBridge : INativeDropHostBridge
{
    private const string ContentBridgeWindowClass =
        "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const uint WmParentNotify = 0x0210;
    private const uint WmCreateChildEvent = 0x0001;
    private const int MaxRegistrationAttempts = 40;

    private readonly IntPtr _hostWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly UIntPtr _subclassId;
    private readonly Func<IntPtr, NativeDropTarget> _targetFactory;
    private readonly Action<NativeDropTarget>? _targetDetacher;
    private readonly Func<IReadOnlyList<string>, bool>? _shouldSkipLegacyDropFiles;
    private readonly Action<LegacyDropFilesPayload>? _legacyDropFilesHandler;
    private readonly Dictionary<IntPtr, NativeDropTarget> _targets = [];
    private readonly Win32Helper.SubclassProc _subclassProc;
    private DispatcherQueueTimer? _registrationTimer;
    private int _registrationAttempts;
    private bool _subclassInstalled;
    private bool _disposed;

    public NativeDropHostBridge(
        IntPtr hostWindowHandle,
        DispatcherQueue dispatcherQueue,
        UIntPtr subclassId,
        Func<IntPtr, NativeDropTarget> targetFactory,
        Action<NativeDropTarget>? targetDetacher = null,
        Func<IReadOnlyList<string>, bool>? shouldSkipLegacyDropFiles = null,
        Action<LegacyDropFilesPayload>? legacyDropFilesHandler = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        ArgumentNullException.ThrowIfNull(targetFactory);

        _hostWindowHandle = hostWindowHandle;
        _dispatcherQueue = dispatcherQueue;
        _subclassId = subclassId;
        _targetFactory = targetFactory;
        _targetDetacher = targetDetacher;
        _shouldSkipLegacyDropFiles = shouldSkipLegacyDropFiles;
        _legacyDropFilesHandler = legacyDropFilesHandler;
        _subclassProc = CallbackDepth.WrapSubclassProc(SubclassProc);

        if (_hostWindowHandle != IntPtr.Zero)
        {
            Win32Helper.AllowShellDragDropMessages(_hostWindowHandle);
            _subclassInstalled = Win32Helper.SetWindowSubclass(
                _hostWindowHandle,
                _subclassProc,
                _subclassId,
                UIntPtr.Zero);
        }
    }

    public IntPtr HostWindowHandle => _hostWindowHandle;

    public IReadOnlyCollection<NativeDropTarget> Targets => _targets.Values;

    public bool HasActiveShellDragWindow
    {
        get
        {
            foreach (NativeDropTarget target in _targets.Values)
            {
                if (target.HasActiveShellDragWindow)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public long MaxActiveGestureId
    {
        get
        {
            long max = 0;
            foreach (NativeDropTarget target in _targets.Values)
            {
                if (target.ActiveGestureId > max)
                {
                    max = target.ActiveGestureId;
                }
            }

            return max;
        }
    }

    public void EnsureRegistered()
    {
        if (_disposed || _hostWindowHandle == IntPtr.Zero)
        {
            return;
        }

        InstallTargets();
        if (HasRegisteredContentBridgeTarget() || _registrationTimer is not null)
        {
            return;
        }

        _registrationAttempts = 0;
        _registrationTimer = _dispatcherQueue.CreateTimer();
        _registrationTimer.Interval = TimeSpan.FromMilliseconds(75);
        _registrationTimer.IsRepeating = true;
        _registrationTimer.Tick += RegistrationTimer_Tick;
        _registrationTimer.Start();
    }

    private void RegistrationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !Win32Helper.IsWindow(_hostWindowHandle))
        {
            ReleaseRegistrationTimer();
            return;
        }

        _registrationAttempts++;
        InstallTargets();
        if (HasRegisteredContentBridgeTarget() ||
            _registrationAttempts >= MaxRegistrationAttempts)
        {
            ReleaseRegistrationTimer();
        }
    }

    private void InstallTargets()
    {
        if (_disposed || !Win32Helper.IsWindow(_hostWindowHandle))
        {
            return;
        }

        PruneDestroyedTargets();
        foreach (IntPtr targetWindow in EnumerateDropWindowHandles())
        {
            if (_targets.TryGetValue(targetWindow, out NativeDropTarget? existing))
            {
                existing.Register();
                continue;
            }

            try
            {
                NativeDropTarget created = _targetFactory(targetWindow);
                created.Register();
                _targets[targetWindow] = created;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DropTarget] NativeDropHostBridge failed to register " +
                    $"hwnd=0x{targetWindow.ToInt64():X}: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<IntPtr> EnumerateDropWindowHandles()
    {
        var handles = new List<IntPtr> { _hostWindowHandle };
        _ = Win32Helper.EnumChildWindows(
            _hostWindowHandle,
            (childWindow, _) =>
            {
                var className = new StringBuilder(128);
                int length = Win32Helper.GetClassName(
                    childWindow,
                    className,
                    className.Capacity);
                if (length > 0 &&
                    string.Equals(
                        className.ToString(),
                        ContentBridgeWindowClass,
                        StringComparison.Ordinal))
                {
                    handles.Add(childWindow);
                }

                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private bool HasRegisteredContentBridgeTarget()
    {
        foreach ((IntPtr window, NativeDropTarget target) in _targets)
        {
            if (window != _hostWindowHandle &&
                Win32Helper.IsWindow(window) &&
                target.IsRegistered)
            {
                return true;
            }
        }

        return false;
    }

    private void PruneDestroyedTargets()
    {
        if (_targets.Count == 0)
        {
            return;
        }

        List<IntPtr>? stale = null;
        foreach ((IntPtr window, NativeDropTarget target) in _targets)
        {
            if (window != _hostWindowHandle && !Win32Helper.IsWindow(window))
            {
                stale ??= [];
                stale.Add(window);
                _targetDetacher?.Invoke(target);
                target.Dispose();
            }
        }

        if (stale is not null)
        {
            foreach (IntPtr window in stale)
            {
                _targets.Remove(window);
            }
        }
    }

    private IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == Win32Helper.WM_DROPFILES && !_disposed)
        {
            IReadOnlyList<string> paths =
                Win32Helper.GetDroppedFilePathsAndScreenPoint(
                    (IntPtr)wParam,
                    hWnd,
                    out int? screenX,
                    out int? screenY);
            if (paths.Count > 0 &&
                _legacyDropFilesHandler is not null &&
                (_shouldSkipLegacyDropFiles?.Invoke(paths) != true))
            {
                IReadOnlyList<string> stagedPaths =
                    DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                        paths,
                        hasShellIdList: false,
                        out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
                        out bool stagedAny);
                var payload = new LegacyDropFilesPayload(
                    stagedPaths,
                    itemOrigins,
                    stagedAny,
                    screenX,
                    screenY);
                UiDispatch.PostAfterCallback(() =>
                {
                    if (_disposed)
                    {
                        if (payload.ContainsTemporaryFiles)
                        {
                            NativeDropMaterializationWorker.CleanupVirtualDropDirectories(payload.StagedPaths);
                        }

                        return;
                    }

                    _legacyDropFilesHandler(payload);
                });
            }

            return IntPtr.Zero;
        }

        if (message == WmParentNotify &&
            ((uint)wParam & 0xFFFFu) == WmCreateChildEvent &&
            !_disposed &&
            !HasRegisteredContentBridgeTarget())
        {
            _ = _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    InstallTargets();
                    if (HasRegisteredContentBridgeTarget())
                    {
                        ReleaseRegistrationTimer();
                    }
                }
            });
        }

        return Win32Helper.DefSubclassProc(
            hWnd,
            message,
            wParam,
            lParam);
    }

    private void ReleaseRegistrationTimer()
    {
        if (_registrationTimer is null)
        {
            return;
        }

        _registrationTimer.Stop();
        _registrationTimer.Tick -= RegistrationTimer_Tick;
        _registrationTimer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseRegistrationTimer();

        if (_subclassInstalled && _hostWindowHandle != IntPtr.Zero)
        {
            _ = Win32Helper.RemoveWindowSubclass(
                _hostWindowHandle,
                _subclassProc,
                _subclassId);
            _subclassInstalled = false;
        }

        foreach (NativeDropTarget target in _targets.Values)
        {
            _targetDetacher?.Invoke(target);
            target.Dispose();
        }

        _targets.Clear();
    }
}
