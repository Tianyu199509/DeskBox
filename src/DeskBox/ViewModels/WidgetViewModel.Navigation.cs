using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private readonly SemaphoreSlim _folderNavigationGate = new(1, 1);
    private CancellationTokenSource? _folderNavigationCancellation;
    private string? _currentFolderPath;
    private string? _mappedFolderTraversalPath;

    public string? CurrentFolderPath => _currentFolderPath ?? MappedFolderPath;

    public bool IsEmbeddedFolderNavigationEnabled =>
        string.Equals(
            FileWidgetFolderOpenBehaviorNames.Resolve(
                _settingsService.Settings,
                Config),
            FileWidgetFolderOpenBehaviorNames.Embedded,
            StringComparison.Ordinal);

    public bool IsAtMappedRoot =>
        PathsEqual(
            CurrentFolderPath,
            _mappedFolderTraversalPath ?? MappedFolderPath);

    public bool CanNavigateUp =>
        IsEmbeddedFolderNavigationEnabled && !IsAtMappedRoot;

    public Visibility FolderNavigationVisibility =>
        IsEmbeddedFolderNavigationEnabled && !IsAtMappedRoot
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string CurrentFolderDisplayName => IsAtMappedRoot
        ? GetFolderDisplayName(MappedFolderPath)
        : GetFolderDisplayName(CurrentFolderPath);

    public string CurrentFolderRelativePath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MappedFolderPath) ||
                string.IsNullOrWhiteSpace(CurrentFolderPath))
            {
                return CurrentFolderDisplayName;
            }

            try
            {
                string rootPath = _mappedFolderTraversalPath ?? MappedFolderPath;
                string relative = Path.GetRelativePath(
                    rootPath,
                    CurrentFolderPath);
                if (relative == ".")
                {
                    return GetFolderDisplayName(MappedFolderPath);
                }

                return string.Join(
                    " › ",
                    new[] { GetFolderDisplayName(MappedFolderPath) }
                        .Concat(relative.Split(
                            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                            StringSplitOptions.RemoveEmptyEntries)));
            }
            catch
            {
                return CurrentFolderPath;
            }
        }
    }

    public Task<bool> NavigateIntoFolderAsync(
        WidgetItem item,
        Action? beforeItemsReplaced = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsFolder && IsEmbeddedFolderNavigationEnabled
            ? NavigateToFolderAsync(
                item.Path,
                beforeItemsReplaced: beforeItemsReplaced)
            : Task.FromResult(false);
    }

    public Task<bool> NavigateUpAsync(Action? beforeItemsReplaced = null)
    {
        if (!CanNavigateUp ||
            string.IsNullOrWhiteSpace(CurrentFolderPath) ||
            string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return Task.FromResult(false);
        }

        string rootPath = GetMappedFolderTraversalPath();
        DirectoryInfo? parent = Directory.GetParent(
            Path.GetFullPath(CurrentFolderPath));
        if (parent is null ||
            !FileService.IsPathUnderDirectory(parent.FullName, rootPath))
        {
            return Task.FromResult(false);
        }

        return NavigateToFolderAsync(
            parent.FullName,
            beforeItemsReplaced: beforeItemsReplaced);
    }

    public async Task RefreshFolderOpenBehaviorAsync(
        Action? beforeItemsReplaced = null)
    {
        if (!IsEmbeddedFolderNavigationEnabled && !IsAtMappedRoot)
        {
            await ResetFolderNavigationToMappedRootAsync(beforeItemsReplaced);
        }

        UpdateFolderNavigationPresentation();
    }

    public Task<bool> ResetFolderNavigationToMappedRootAsync(
        Action? beforeItemsReplaced = null)
    {
        return string.IsNullOrWhiteSpace(MappedFolderPath)
            ? Task.FromResult(false)
            : NavigateToFolderAsync(
                MappedFolderPath,
                allowWhenEmbeddedNavigationDisabled: true,
                beforeItemsReplaced: beforeItemsReplaced);
    }

    private async Task<bool> NavigateToFolderAsync(
        string folderPath,
        bool allowWhenEmbeddedNavigationDisabled = false,
        Action? beforeItemsReplaced = null)
    {
        if (_isDisposed ||
            (!allowWhenEmbeddedNavigationDisabled &&
             !IsEmbeddedFolderNavigationEnabled) ||
            string.IsNullOrWhiteSpace(MappedFolderPath) ||
            string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        string requestedPath;
        string rootPath;
        string targetPath;
        bool mappedRootRequested;
        try
        {
            requestedPath = Path.GetFullPath(folderPath);
            mappedRootRequested = PathsEqual(
                requestedPath,
                MappedFolderPath);
            rootPath = mappedRootRequested
                ? ResolveMappedFolderTraversalPath()
                : GetMappedFolderTraversalPath();
            if (mappedRootRequested)
            {
                targetPath = rootPath;
            }
            else if (!FileService.TryResolveExistingPathForTraversal(
                         requestedPath,
                         out targetPath))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(targetPath) ||
            !FileService.TryIsPathUnderDirectoryResolved(
                targetPath,
                rootPath,
                out bool isUnderMappedRoot) ||
            !isUnderMappedRoot)
        {
            return false;
        }

        if (PathsEqual(CurrentFolderPath, targetPath))
        {
            if (PathsEqual(requestedPath, MappedFolderPath))
            {
                _mappedFolderTraversalPath = rootPath;
                UpdateFolderNavigationPresentation();
            }

            return true;
        }

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous =
            Interlocked.Exchange(
                ref _folderNavigationCancellation,
                cancellation);
        previous?.Cancel();

        bool gateEntered = false;
        string previousPath = CurrentFolderPath ?? rootPath;
        string? previousMappedTraversalPath = _mappedFolderTraversalPath;
        try
        {
            await _folderNavigationGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            cancellation.Token.ThrowIfCancellationRequested();

            if (mappedRootRequested)
            {
                // ConfigureFolderWatchersAsync will pass the logical path to
                // FolderWatcherService, allowing a future reconnect to follow
                // a newly selected version of the junction.
                _mappedFolderTraversalPath = rootPath;
            }

            await ConfigureFolderWatchersAsync(
                targetPath,
                cancellation.Token);
            bool loaded = await ReloadFolderContentsAsync(
                targetPath,
                cancellationToken: cancellation.Token,
                beforeItemsReplaced: () =>
                {
                    beforeItemsReplaced?.Invoke();
                    if (PathsEqual(requestedPath, MappedFolderPath))
                    {
                        _mappedFolderTraversalPath = rootPath;
                    }

                    SetCurrentFolderPath(targetPath);
                },
                allowFolderPathTransition: true);
            if (!loaded)
            {
                _mappedFolderTraversalPath = previousMappedTraversalPath;
                await ConfigureFolderWatchersAsync(previousPath);
                return false;
            }

            UpdateDependentProperties();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_isDisposed &&
                ReferenceEquals(_folderNavigationCancellation, cancellation))
            {
                _mappedFolderTraversalPath = previousMappedTraversalPath;
                SetCurrentFolderPath(previousPath);
                try
                {
                    await ConfigureFolderWatchersAsync(previousPath);
                }
                catch
                {
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FolderNavigation] Failed widget={Config.Id} " +
                $"target='{targetPath}': {ex}");
            _mappedFolderTraversalPath = previousMappedTraversalPath;
            SetCurrentFolderPath(previousPath);
            try
            {
                await ConfigureFolderWatchersAsync(previousPath);
            }
            catch
            {
            }

            return false;
        }
        finally
        {
            if (gateEntered)
            {
                _folderNavigationGate.Release();
            }

            Interlocked.CompareExchange(
                ref _folderNavigationCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private string ResolveCurrentFolderForMappedRoot()
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return string.Empty;
        }

        bool wasAtMappedRoot = PathsEqual(
            _currentFolderPath,
            _mappedFolderTraversalPath ?? MappedFolderPath);
        string rootPath;
        if (TryResolveMappedFolderTraversalPath(out string resolvedRootPath))
        {
            rootPath = resolvedRootPath;
            _mappedFolderTraversalPath = rootPath;
        }
        else
        {
            // Keep a previously resolved target during a transient provider
            // failure. Falling back to the logical junction here would make
            // the subsequent watcher/read attempt hit RedirectionGuard again.
            rootPath = _mappedFolderTraversalPath ?? Path.GetFullPath(MappedFolderPath);
        }

        if (wasAtMappedRoot)
        {
            return rootPath;
        }

        if (!string.IsNullOrWhiteSpace(_currentFolderPath) &&
            Directory.Exists(_currentFolderPath) &&
            FileService.TryIsPathUnderDirectoryResolved(
                _currentFolderPath,
                rootPath,
                out bool isUnderRoot) &&
            isUnderRoot)
        {
            return Path.GetFullPath(_currentFolderPath);
        }

        return rootPath;
    }

    private string ResolveMappedFolderTraversalPath()
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return string.Empty;
        }

        string logicalPath = Path.GetFullPath(MappedFolderPath);
        return TryResolveMappedFolderTraversalPath(out string traversalPath)
            ? traversalPath
            : logicalPath;
    }

    private bool TryResolveMappedFolderTraversalPath(
        out string traversalPath)
    {
        traversalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return false;
        }

        try
        {
            return FileService.TryResolveExistingPathForTraversal(
                Path.GetFullPath(MappedFolderPath),
                out traversalPath);
        }
        catch
        {
            return false;
        }
    }

    private string GetMappedFolderTraversalPath()
    {
        if (!string.IsNullOrWhiteSpace(_mappedFolderTraversalPath))
        {
            return _mappedFolderTraversalPath;
        }

        _mappedFolderTraversalPath = ResolveMappedFolderTraversalPath();
        return _mappedFolderTraversalPath;
    }

    private async Task<string?> RefreshMappedRootTraversalPathAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentFolderPath) || !IsAtMappedRoot)
        {
            return CurrentFolderPath;
        }

        if (!TryResolveMappedFolderTraversalPath(out string refreshedRoot))
        {
            // A transient ACL/offline failure must not replace a still usable
            // physical target with the logical junction path and reintroduce
            // the guarded traversal on the next refresh.
            App.LogVerbose(
                $"[FolderNavigation] Mapped root could not be resolved; retaining '{CurrentFolderPath}'");
            return CurrentFolderPath;
        }

        _mappedFolderTraversalPath = refreshedRoot;
        if (!PathsEqual(CurrentFolderPath, refreshedRoot))
        {
            SetCurrentFolderPath(refreshedRoot);
            await ConfigureFolderWatchersAsync(
                refreshedRoot,
                cancellationToken);
        }
        else
        {
            UpdateFolderNavigationPresentation();
        }

        return CurrentFolderPath;
    }

    private void SetCurrentFolderPath(string? folderPath)
    {
        string? normalized = string.IsNullOrWhiteSpace(folderPath)
            ? null
            : Path.GetFullPath(folderPath);
        if (string.Equals(
                _currentFolderPath,
                normalized,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentFolderPath = normalized;
        UpdateFolderNavigationPresentation();
    }

    private void UpdateFolderNavigationPresentation()
    {
        OnPropertyChanged(nameof(CurrentFolderPath));
        OnPropertyChanged(nameof(IsEmbeddedFolderNavigationEnabled));
        OnPropertyChanged(nameof(IsAtMappedRoot));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(FolderNavigationVisibility));
        OnPropertyChanged(nameof(CurrentFolderDisplayName));
        OnPropertyChanged(nameof(CurrentFolderRelativePath));
    }

    private string GetFolderDisplayName(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return _localizationService.T("Common.CurrentLocation");
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (PathsEqual(folderPath, userDesktop) ||
            PathsEqual(folderPath, publicDesktop))
        {
            return _localizationService.T("Common.Desktop");
        }

        string normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(folderPath));
        string name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? normalized : name;
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
