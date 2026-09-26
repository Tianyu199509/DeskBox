using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Views;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private readonly QuickLookPreviewService _quickLookPreviewService = new();
    private readonly SemaphoreSlim _quickLookPreviewSendGate = new(1, 1);
    private FileSurfaceContent? _quickLookPreviewSurface;
    private string? _quickLookPreviewPath;
    private long _quickLookPreviewGeneration;
    private bool _quickLookPreviewSessionActive;
    private bool _isSynchronizingQuickLookSelection;

    internal async Task<bool> TryToggleQuickLookPreviewAsync(
        FileSurfaceContent surface,
        string path)
    {
        if (!_quickLookPreviewService.CanPreview(path))
        {
            return false;
        }

        long generation = ++_quickLookPreviewGeneration;
        await _quickLookPreviewSendGate.WaitAsync();
        try
        {
            if (generation != _quickLookPreviewGeneration)
            {
                return false;
            }

            if (!await _quickLookPreviewService.TryToggleAsync(path))
            {
                return false;
            }

            bool closesCurrentPreview =
                _quickLookPreviewSessionActive &&
                ReferenceEquals(_quickLookPreviewSurface, surface) &&
                string.Equals(
                    _quickLookPreviewPath,
                    path,
                    StringComparison.OrdinalIgnoreCase);
            if (closesCurrentPreview)
            {
                EndQuickLookPreviewSession();
            }
            else
            {
                _quickLookPreviewSessionActive = true;
                _quickLookPreviewSurface = surface;
                _quickLookPreviewPath = path;
            }

            return true;
        }
        finally
        {
            _quickLookPreviewSendGate.Release();
        }
    }

    internal bool IsCurrentQuickLookPreviewTarget(
        FileSurfaceContent surface,
        string path) =>
        _quickLookPreviewSessionActive &&
        ReferenceEquals(_quickLookPreviewSurface, surface) &&
        string.Equals(
            _quickLookPreviewPath,
            path,
            StringComparison.OrdinalIgnoreCase);

    internal Task FollowQuickLookSelectionAsync(
        FileSurfaceContent surface,
        string path)
    {
        if (!_quickLookPreviewSessionActive ||
            _isSynchronizingQuickLookSelection ||
            string.IsNullOrWhiteSpace(path) ||
            IsCurrentQuickLookPreviewTarget(surface, path))
        {
            return Task.CompletedTask;
        }

        _quickLookPreviewSurface = surface;
        _quickLookPreviewPath = path;
        long generation = ++_quickLookPreviewGeneration;
        return SwitchQuickLookPreviewAsync(path, generation);
    }

    internal async Task ContinueQuickLookNavigationAfterNativeAsync(
        FileSurfaceContent source,
        string originalPath,
        VirtualKey key)
    {
        if (!IsCurrentQuickLookPreviewTarget(source, originalPath) ||
            source.GetPrimaryQuickLookSelection() is not { } selected ||
            !string.Equals(
                selected.Path,
                originalPath,
                StringComparison.OrdinalIgnoreCase) ||
            !TryMapDirection(key, out QuickLookNavigationDirection direction))
        {
            return;
        }

        IReadOnlyList<QuickLookSurfaceHost> hosts =
            GetVisibleQuickLookSurfaceHosts();
        IReadOnlyList<QuickLookSurfaceNavigationSnapshot> snapshots = hosts
            .Select(host =>
            {
                Windows.Foundation.Rect bounds =
                    host.Host.RestingAnimationBounds;
                return new QuickLookSurfaceNavigationSnapshot(
                    host.Surface.WidgetId,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    host.Surface.GetQuickLookNavigationPaths()
                        .Where(QuickLookPreviewService.IsPreviewablePath)
                        .ToArray());
            })
            .ToArray();
        QuickLookNavigationTarget? target =
            QuickLookNavigationPolicy.ResolveAdjacentSurface(
                snapshots,
                source.WidgetId,
                direction);
        if (target is not { } resolvedTarget)
        {
            return;
        }

        QuickLookSurfaceHost? targetHost = hosts.FirstOrDefault(host =>
            string.Equals(
                host.Surface.WidgetId,
                resolvedTarget.SurfaceId,
                StringComparison.Ordinal));
        if (targetHost is null)
        {
            return;
        }

        bool selectedTarget;
        _isSynchronizingQuickLookSelection = true;
        try
        {
            ClearSelectionsExcept(targetHost.Surface.WidgetId);
            selectedTarget = targetHost.Surface.TrySelectQuickLookTarget(
                resolvedTarget.Path);
            if (selectedTarget)
            {
                targetHost.Host.ActivateQuickLookNavigationTarget(
                    targetHost.Adapter);
            }
        }
        finally
        {
            _isSynchronizingQuickLookSelection = false;
        }

        if (!selectedTarget)
        {
            return;
        }

        _quickLookPreviewSurface = targetHost.Surface;
        _quickLookPreviewPath = resolvedTarget.Path;
        long generation = ++_quickLookPreviewGeneration;
        await SwitchQuickLookPreviewAsync(resolvedTarget.Path, generation);
    }

    internal void EndQuickLookPreviewSession()
    {
        _quickLookPreviewGeneration++;
        _quickLookPreviewSessionActive = false;
        _quickLookPreviewSurface = null;
        _quickLookPreviewPath = null;
    }

    internal async Task CloseQuickLookPreviewAsync()
    {
        if (!_quickLookPreviewSessionActive)
        {
            return;
        }

        EndQuickLookPreviewSession();
        long closeGeneration = _quickLookPreviewGeneration;
        await _quickLookPreviewSendGate.WaitAsync();
        try
        {
            if (_quickLookPreviewSessionActive ||
                closeGeneration != _quickLookPreviewGeneration)
            {
                return;
            }

            await _quickLookPreviewService.TryCloseAsync();
        }
        finally
        {
            _quickLookPreviewSendGate.Release();
        }
    }

    internal void NotifyQuickLookSurfaceUnavailable(FileSurfaceContent surface)
    {
        if (ReferenceEquals(_quickLookPreviewSurface, surface))
        {
            EndQuickLookPreviewSession();
        }
    }

    private async Task SwitchQuickLookPreviewAsync(
        string path,
        long generation)
    {
        await _quickLookPreviewSendGate.WaitAsync();
        try
        {
            if (!_quickLookPreviewSessionActive ||
                generation != _quickLookPreviewGeneration)
            {
                return;
            }

            if (!await _quickLookPreviewService.TrySwitchAsync(path) &&
                generation == _quickLookPreviewGeneration)
            {
                EndQuickLookPreviewSession();
            }
        }
        finally
        {
            _quickLookPreviewSendGate.Release();
        }
    }

    private IReadOnlyList<QuickLookSurfaceHost>
        GetVisibleQuickLookSurfaceHosts()
    {
        var hosts = new List<QuickLookSurfaceHost>();
        hosts.AddRange(_fileWidgets.Values.Select(session =>
            new QuickLookSurfaceHost(
                session.Host,
                session.Content,
                session.Content.RequireSurface())));
        hosts.AddRange(_contentWidgets.Values
            .Distinct()
            .Where(window => window.CurrentContent is FileWidgetContentAdapter
                {
                    Surface: not null
                })
            .Select(window => new QuickLookSurfaceHost(
                window,
                (FileWidgetContentAdapter)window.CurrentContent!,
                ((FileWidgetContentAdapter)window.CurrentContent!).RequireSurface())));

        return hosts
            .Where(host => host.Host.Visible && host.Surface.IsLoaded)
            .GroupBy(host => host.Surface)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool TryMapDirection(
        VirtualKey key,
        out QuickLookNavigationDirection direction)
    {
        direction = key switch
        {
            VirtualKey.Left => QuickLookNavigationDirection.Left,
            VirtualKey.Up => QuickLookNavigationDirection.Up,
            VirtualKey.Right => QuickLookNavigationDirection.Right,
            VirtualKey.Down => QuickLookNavigationDirection.Down,
            _ => default
        };
        return key is VirtualKey.Left or VirtualKey.Up or
            VirtualKey.Right or VirtualKey.Down;
    }

    private sealed record QuickLookSurfaceHost(
        ContentWidgetWindow Host,
        FileWidgetContentAdapter Adapter,
        FileSurfaceContent Surface);
}
