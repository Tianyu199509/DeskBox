#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private static readonly string[] AotLocalFileBaselineSurfaceNames =
    [
        AotLocalFileSurfaceFixture.NestedDirectoryName,
        "baseline"
    ];

    private static readonly string[] AotLocalFileMutationSurfaceNames =
    [
        AotLocalFileSurfaceFixture.NestedDirectoryName,
        "baseline",
        "copied-renamed",
        "move-source",
        "watcher-created"
    ];

    private static readonly string[] AotLocalFileBaselineFixtureFiles =
    [
        "sources/copy-source.txt",
        "sources/move-source.txt",
        "widget-root/baseline.txt",
        "widget-root/nested/nested.txt"
    ];

    private static readonly string[] AotLocalFileMutationFixtureFiles =
    [
        "sources/copy-source.txt",
        "widget-root/baseline.txt",
        "widget-root/copied-renamed.txt",
        "widget-root/move-source.txt",
        "widget-root/nested/nested.txt",
        "widget-root/watcher-created.txt"
    ];

    private async Task CaptureAotManagedUiLocalFilePersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotLocalFileSurfaceHost host =
            await manager.GetAotLocalFileSurfaceHostAsync();
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "LocalFileSurfaceHostReady",
            "The real File Widget HWND or XamlRoot is unavailable.");

        AotLocalFileFixturePaths paths =
            AotLocalFileSurfaceFixture.GetOwnedPaths(DeskBoxDataPathService.Current);
        RequireAotManagedUi(
            result,
            IsAotManagedUiPathEqual(
                host.ViewModel.MappedFolderPath ?? string.Empty,
                paths.WidgetRoot),
            "LocalFileOwnedRootVerified",
            "The real File Widget is not mapped to the exact owned preview directory.");

        AotManagedUiLocalFilePersistenceEvidence evidence =
            result.LocalFilePersistence ??
            throw new InvalidOperationException(
                "Local-file persistence evidence is unavailable.");
        evidence.WindowHandle = host.WindowHandle;
        evidence.HasXamlRoot = host.HasXamlRoot;
        evidence.Visible = host.Visible;

        bool beforeMutation = phase == "VerifyRestore";
        evidence.Before = await CaptureAotLocalFileStateAsync(
            host,
            paths,
            expectMutation: beforeMutation);
        RequireAotLocalFileState(
            result,
            evidence.Before,
            beforeMutation,
            beforeMutation
                ? "LocalFileRestartMutationVerified"
                : "LocalFileBaselineVerified");

        switch (phase)
        {
            case "Mutate":
                evidence.Operations = await ApplyAotLocalFileMutationAsync(
                    result,
                    host,
                    paths);
                evidence.After = await CaptureAotLocalFileStateAsync(
                    host,
                    paths,
                    expectMutation: true);
                RequireAotLocalFileState(
                    result,
                    evidence.After,
                    expectMutation: true,
                    "LocalFileMutationApplied");
                break;

            case "VerifyRestore":
                evidence.Operations = await RestoreAotLocalFileBaselineAsync(
                    result,
                    host,
                    paths);
                evidence.After = await CaptureAotLocalFileStateAsync(
                    host,
                    paths,
                    expectMutation: false);
                RequireAotLocalFileState(
                    result,
                    evidence.After,
                    expectMutation: false,
                    "LocalFileBaselineRestored");
                break;

            case "Postflight":
                evidence.After = await CaptureAotLocalFileStateAsync(
                    host,
                    paths,
                    expectMutation: false);
                RequireAotLocalFileState(
                    result,
                    evidence.After,
                    expectMutation: false,
                    "LocalFilePostflightVerified");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported local-file persistence phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "LocalFilePersistenceFlushed",
            "The local-file persistence phase did not flush successfully.");
    }

    private async Task<AotManagedUiLocalFileOperationsEvidence>
        ApplyAotLocalFileMutationAsync(
            AotManagedUiSmokeResult result,
            AotLocalFileSurfaceHost host,
            AotLocalFileFixturePaths paths)
    {
        WidgetItem nested = host.ViewModel.Items.Single(item =>
            item.IsFolder &&
            string.Equals(
                item.Name,
                AotLocalFileSurfaceFixture.NestedDirectoryName,
                StringComparison.Ordinal));
        bool navigatedIntoFolder = await host.ViewModel.NavigateIntoFolderAsync(nested);
        AotLocalFileSurfaceSnapshot nestedSurface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.NestedDirectory,
                ["nested"],
                expectAtMappedRoot: false);
        bool navigatedUp = await host.ViewModel.NavigateUpAsync();
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            AotLocalFileBaselineSurfaceNames,
            expectAtMappedRoot: true);
        RequireAotManagedUi(
            result,
            navigatedIntoFolder &&
            navigatedUp &&
            nestedSurface.CanNavigateUp &&
            nestedSurface.NavigationBarVisible &&
            nestedSurface.ProjectedItemCount == 1,
            "LocalFileNavigationCycleCompleted",
            "The real embedded folder navigation cycle did not project its child surface.");

        IReadOnlyList<string> copiedSources = await host.ViewModel.ImportPathsAsync(
            [paths.CopySourceFile],
            moveWhenMapped: false,
            useShellProgress: false);
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            [
                AotLocalFileSurfaceFixture.NestedDirectoryName,
                "baseline",
                "copy-source"
            ],
            expectAtMappedRoot: true);
        bool copyCompleted = copiedSources.Count == 1 &&
            IsAotManagedUiPathEqual(copiedSources[0], paths.CopySourceFile) &&
            File.Exists(paths.CopySourceFile) &&
            File.Exists(paths.CopiedFile);
        RequireAotManagedUi(
            result,
            copyCompleted,
            "LocalFileCopyCompleted",
            "The product copy path did not retain its source and create its owned destination.");

        IReadOnlyList<string> movedSources = await host.ViewModel.ImportPathsAsync(
            [paths.MoveSourceFile],
            moveWhenMapped: true,
            useShellProgress: false);
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            [
                AotLocalFileSurfaceFixture.NestedDirectoryName,
                "baseline",
                "copy-source",
                "move-source"
            ],
            expectAtMappedRoot: true);
        bool moveCompleted = movedSources.Count == 1 &&
            IsAotManagedUiPathEqual(movedSources[0], paths.MoveSourceFile) &&
            !File.Exists(paths.MoveSourceFile) &&
            File.Exists(paths.MovedFile);
        RequireAotManagedUi(
            result,
            moveCompleted,
            "LocalFileMoveCompleted",
            "The product move path did not remove its source and create its owned destination.");

        WidgetItem copiedItem = host.ViewModel.Items.Single(item =>
            IsAotManagedUiPathEqual(item.Path, paths.CopiedFile));
        await host.ViewModel.RenameItemAsync(copiedItem, "copied-renamed");
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            [
                AotLocalFileSurfaceFixture.NestedDirectoryName,
                "baseline",
                "copied-renamed",
                "move-source"
            ],
            expectAtMappedRoot: true);
        bool renameCompleted = !File.Exists(paths.CopiedFile) &&
            File.Exists(paths.RenamedCopyFile) &&
            IsAotManagedUiPathEqual(copiedItem.Path, paths.RenamedCopyFile);
        RequireAotManagedUi(
            result,
            renameCompleted,
            "LocalFileRenameCompleted",
            "The product rename path did not update both disk and the real surface item.");

        bool conflictRejected = false;
        string conflictExceptionType = string.Empty;
        string conflictMessage = string.Empty;
        try
        {
            await host.ViewModel.RenameItemAsync(copiedItem, "baseline");
        }
        catch (IOException ex)
        {
            conflictRejected = true;
            conflictExceptionType = ex.GetType().FullName ?? ex.GetType().Name;
            conflictMessage = ex.Message;
        }
        bool conflictStatePreserved =
            File.Exists(paths.BaselineFile) &&
            File.Exists(paths.RenamedCopyFile) &&
            IsAotManagedUiPathEqual(copiedItem.Path, paths.RenamedCopyFile);
        RequireAotManagedUi(
            result,
            conflictRejected &&
            conflictStatePreserved &&
            !string.IsNullOrWhiteSpace(conflictMessage),
            "LocalFileRenameConflictRejected",
            "The rename conflict did not fail without changing the owned files.");

        await File.WriteAllTextAsync(
            paths.WatcherCreatedFile,
            "DeskBox AOT 5B-4C1A watcher stimulus.\n");
        AotLocalFileSurfaceSnapshot watcherSurface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                AotLocalFileMutationSurfaceNames,
                expectAtMappedRoot: true);
        bool watcherObserved = watcherSurface.Items.Any(item =>
            string.Equals(
                item.Name,
                "watcher-created",
                StringComparison.Ordinal) &&
            item.NameProjected);
        RequireAotManagedUi(
            result,
            watcherObserved,
            "LocalFileWatcherObservedExternalCreate",
            "The product folder watcher did not project an external owned-file creation.");

        return new AotManagedUiLocalFileOperationsEvidence
        {
            NavigatedIntoFolder = navigatedIntoFolder,
            NestedSurfaceProjected = nestedSurface.ProjectedItemCount == 1,
            NavigatedUp = navigatedUp,
            CopyCompleted = copyCompleted,
            CopySourceRetained = File.Exists(paths.CopySourceFile),
            MoveCompleted = moveCompleted,
            MoveSourceRemoved = !File.Exists(paths.MoveSourceFile),
            RenameCompleted = renameCompleted,
            ConflictRejected = conflictRejected,
            ConflictStatePreserved = conflictStatePreserved,
            ConflictExceptionType = conflictExceptionType,
            ConflictMessage = conflictMessage,
            WatcherObserved = watcherObserved,
            ShellProgressRequested = false
        };
    }

    private async Task<AotManagedUiLocalFileOperationsEvidence>
        RestoreAotLocalFileBaselineAsync(
            AotManagedUiSmokeResult result,
            AotLocalFileSurfaceHost host,
            AotLocalFileFixturePaths paths)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(paths.MovedFile) || File.Exists(paths.MoveSourceFile))
            {
                throw new InvalidOperationException(
                    "The moved fixture file was not in its expected mutation location.");
            }

            File.Move(paths.MovedFile, paths.MoveSourceFile);
            File.Delete(paths.RenamedCopyFile);
            File.Delete(paths.WatcherCreatedFile);
        });

        AotLocalFileSurfaceSnapshot restoredSurface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                AotLocalFileBaselineSurfaceNames,
                expectAtMappedRoot: true);
        bool cleanupCompleted =
            File.Exists(paths.MoveSourceFile) &&
            !File.Exists(paths.MovedFile) &&
            !File.Exists(paths.RenamedCopyFile) &&
            !File.Exists(paths.WatcherCreatedFile);
        bool watcherRemovalObserved =
            restoredSurface.ProjectedItemCount ==
                AotLocalFileBaselineSurfaceNames.Length;
        RequireAotManagedUi(
            result,
            cleanupCompleted && watcherRemovalObserved,
            "LocalFileOwnedFixtureCleanupCompleted",
            "The owned harness cleanup did not restore the baseline through watcher observation.");

        return new AotManagedUiLocalFileOperationsEvidence
        {
            OwnedFixtureCleanupCompleted = cleanupCompleted,
            WatcherRemovalObserved = watcherRemovalObserved,
            ShellProgressRequested = false
        };
    }

    private async Task<AotManagedUiLocalFileStateEvidence>
        CaptureAotLocalFileStateAsync(
            AotLocalFileSurfaceHost host,
            AotLocalFileFixturePaths paths,
            bool expectMutation)
    {
        string[] expectedSurfaceNames = expectMutation
            ? AotLocalFileMutationSurfaceNames
            : AotLocalFileBaselineSurfaceNames;
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedSurfaceNames,
                expectAtMappedRoot: true);
        AotManagedUiLocalFileDiskEvidence disk = await Task.Run(() =>
            CaptureAotLocalFileDisk(paths));

        return new AotManagedUiLocalFileStateEvidence
        {
            FixtureRoot = paths.FixtureRoot,
            MappedFolderPath = host.ViewModel.MappedFolderPath ?? string.Empty,
            CurrentFolderPath = host.ViewModel.CurrentFolderPath ?? string.Empty,
            IsInitialized = host.ViewModel.IsInitialized,
            IsAtMappedRoot = host.ViewModel.IsAtMappedRoot,
            Surface = MapAotLocalFileSurface(surface),
            Disk = disk
        };
    }

    private static AotManagedUiLocalFileSurfaceEvidence MapAotLocalFileSurface(
        AotLocalFileSurfaceSnapshot snapshot)
    {
        return new AotManagedUiLocalFileSurfaceEvidence
        {
            IsLoaded = snapshot.IsLoaded,
            HasXamlRoot = snapshot.HasXamlRoot,
            DataContextMatchesViewModel = snapshot.DataContextMatchesViewModel,
            ActualWidth = snapshot.ActualWidth,
            ActualHeight = snapshot.ActualHeight,
            ViewModelInitialized = snapshot.ViewModelInitialized,
            MappedFolderPath = snapshot.MappedFolderPath,
            CurrentFolderPath = snapshot.CurrentFolderPath,
            IsAtMappedRoot = snapshot.IsAtMappedRoot,
            CanNavigateUp = snapshot.CanNavigateUp,
            NavigationBarVisibility = snapshot.NavigationBarVisibility.ToString(),
            NavigationBarVisible = snapshot.NavigationBarVisible,
            NavigationText = snapshot.NavigationText,
            ViewModelItemCount = snapshot.ViewModelItemCount,
            VisibleItemCount = snapshot.VisibleItemCount,
            XamlItemCount = snapshot.XamlItemCount,
            RealizedContainerCount = snapshot.RealizedContainerCount,
            ProjectedItemCount = snapshot.ProjectedItemCount,
            EmptyStateVisible = snapshot.EmptyStateVisible,
            ActiveViewVisible = snapshot.ActiveViewVisible,
            ViewMode = snapshot.ViewMode,
            Items = snapshot.Items
                .Select(item => new AotManagedUiLocalFileItemEvidence
                {
                    Name = item.Name,
                    Path = item.Path,
                    IsFolder = item.IsFolder,
                    ContainerRealized = item.ContainerRealized,
                    DataContextMatches = item.DataContextMatches,
                    ProjectedName = item.ProjectedName,
                    NameProjected = item.NameProjected
                })
                .ToList()
        };
    }

    private static AotManagedUiLocalFileDiskEvidence CaptureAotLocalFileDisk(
        AotLocalFileFixturePaths paths)
    {
        string[] directories = Directory
            .EnumerateDirectories(paths.FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeAotLocalFileRelativePath(
                paths.FixtureRoot,
                path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        List<AotManagedUiLocalFileDiskEntryEvidence> files = Directory
            .EnumerateFiles(paths.FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                using FileStream stream = File.OpenRead(path);
                return new AotManagedUiLocalFileDiskEntryEvidence
                {
                    RelativePath = NormalizeAotLocalFileRelativePath(
                        paths.FixtureRoot,
                        path),
                    Length = stream.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stream))
                };
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();

        return new AotManagedUiLocalFileDiskEvidence
        {
            Directories = directories.ToList(),
            Files = files
        };
    }

    private static string NormalizeAotLocalFileRelativePath(
        string root,
        string path)
    {
        return Path.GetRelativePath(root, path).Replace(
            Path.DirectorySeparatorChar,
            '/');
    }

    private static void RequireAotLocalFileState(
        AotManagedUiSmokeResult result,
        AotManagedUiLocalFileStateEvidence state,
        bool expectMutation,
        string step)
    {
        string[] expectedSurfaceNames = expectMutation
            ? AotLocalFileMutationSurfaceNames
            : AotLocalFileBaselineSurfaceNames;
        string[] expectedFixtureFiles = expectMutation
            ? AotLocalFileMutationFixtureFiles
            : AotLocalFileBaselineFixtureFiles;
        string[] actualSurfaceNames = state.Surface.Items
            .Select(item => item.Name)
            .ToArray();
        string[] actualFixtureFiles = state.Disk.Files
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        bool valid =
            state.IsInitialized &&
            state.IsAtMappedRoot &&
            IsAotManagedUiPathEqual(state.MappedFolderPath, state.CurrentFolderPath) &&
            IsAotManagedUiPathEqual(
                state.Surface.MappedFolderPath,
                state.MappedFolderPath) &&
            IsAotManagedUiPathEqual(
                state.Surface.CurrentFolderPath,
                state.CurrentFolderPath) &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.ActualWidth > 0 &&
            state.Surface.ActualHeight > 0 &&
            state.Surface.ViewModelInitialized &&
            state.Surface.IsAtMappedRoot &&
            !state.Surface.CanNavigateUp &&
            !state.Surface.NavigationBarVisible &&
            state.Surface.NavigationBarVisibility == "Collapsed" &&
            state.Surface.ViewMode == nameof(ViewMode.Icon) &&
            state.Surface.ViewModelItemCount == expectedSurfaceNames.Length &&
            state.Surface.VisibleItemCount == expectedSurfaceNames.Length &&
            state.Surface.XamlItemCount == expectedSurfaceNames.Length &&
            state.Surface.RealizedContainerCount == expectedSurfaceNames.Length &&
            state.Surface.ProjectedItemCount == expectedSurfaceNames.Length &&
            !state.Surface.EmptyStateVisible &&
            state.Surface.ActiveViewVisible &&
            state.Surface.Items.All(item =>
                item.ContainerRealized &&
                item.DataContextMatches &&
                item.NameProjected &&
                item.IsFolder == string.Equals(
                    item.Name,
                    AotLocalFileSurfaceFixture.NestedDirectoryName,
                    StringComparison.Ordinal) &&
                string.Equals(item.Name, item.ProjectedName, StringComparison.Ordinal) &&
                AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                    state.MappedFolderPath,
                    item.Path)) &&
            actualSurfaceNames.SequenceEqual(
                expectedSurfaceNames,
                StringComparer.OrdinalIgnoreCase) &&
            actualFixtureFiles.SequenceEqual(
                expectedFixtureFiles,
                StringComparer.Ordinal) &&
            state.Disk.Directories.SequenceEqual(
                ["sources", "widget-root", "widget-root/nested"],
                StringComparer.Ordinal) &&
            state.Disk.Files.All(file =>
                file.Length > 0 &&
                file.Sha256.Length == 64);

        RequireAotManagedUi(
            result,
            valid,
            step,
            expectMutation
                ? "The real local-file surface or owned disk tree did not project the mutation."
                : "The real local-file surface or owned disk tree did not project the baseline.");
    }
}

internal sealed class AotManagedUiLocalFilePersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool Visible { get; set; }
    public AotManagedUiLocalFileStateEvidence Before { get; set; } = new();
    public AotManagedUiLocalFileStateEvidence After { get; set; } = new();
    public AotManagedUiLocalFileOperationsEvidence Operations { get; set; } = new();
}

internal sealed class AotManagedUiLocalFileStateEvidence
{
    public string FixtureRoot { get; set; } = string.Empty;
    public string MappedFolderPath { get; set; } = string.Empty;
    public string CurrentFolderPath { get; set; } = string.Empty;
    public bool IsInitialized { get; set; }
    public bool IsAtMappedRoot { get; set; }
    public AotManagedUiLocalFileSurfaceEvidence Surface { get; set; } = new();
    public AotManagedUiLocalFileDiskEvidence Disk { get; set; } = new();
}

internal sealed class AotManagedUiLocalFileSurfaceEvidence
{
    public bool IsLoaded { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool DataContextMatchesViewModel { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public bool ViewModelInitialized { get; set; }
    public string MappedFolderPath { get; set; } = string.Empty;
    public string CurrentFolderPath { get; set; } = string.Empty;
    public bool IsAtMappedRoot { get; set; }
    public bool CanNavigateUp { get; set; }
    public string NavigationBarVisibility { get; set; } = string.Empty;
    public bool NavigationBarVisible { get; set; }
    public string NavigationText { get; set; } = string.Empty;
    public int ViewModelItemCount { get; set; }
    public int VisibleItemCount { get; set; }
    public int XamlItemCount { get; set; }
    public int RealizedContainerCount { get; set; }
    public int ProjectedItemCount { get; set; }
    public bool EmptyStateVisible { get; set; }
    public bool ActiveViewVisible { get; set; }
    public string ViewMode { get; set; } = string.Empty;
    public List<AotManagedUiLocalFileItemEvidence> Items { get; set; } = [];
}

internal sealed class AotManagedUiLocalFileItemEvidence
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool ContainerRealized { get; set; }
    public bool DataContextMatches { get; set; }
    public string ProjectedName { get; set; } = string.Empty;
    public bool NameProjected { get; set; }
}

internal sealed class AotManagedUiLocalFileDiskEvidence
{
    public List<string> Directories { get; set; } = [];
    public List<AotManagedUiLocalFileDiskEntryEvidence> Files { get; set; } = [];
}

internal sealed class AotManagedUiLocalFileDiskEntryEvidence
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class AotManagedUiLocalFileOperationsEvidence
{
    public bool NavigatedIntoFolder { get; set; }
    public bool NestedSurfaceProjected { get; set; }
    public bool NavigatedUp { get; set; }
    public bool CopyCompleted { get; set; }
    public bool CopySourceRetained { get; set; }
    public bool MoveCompleted { get; set; }
    public bool MoveSourceRemoved { get; set; }
    public bool RenameCompleted { get; set; }
    public bool ConflictRejected { get; set; }
    public bool ConflictStatePreserved { get; set; }
    public string ConflictExceptionType { get; set; } = string.Empty;
    public string ConflictMessage { get; set; } = string.Empty;
    public bool WatcherObserved { get; set; }
    public bool OwnedFixtureCleanupCompleted { get; set; }
    public bool WatcherRemovalObserved { get; set; }
    public bool ShellProgressRequested { get; set; }
}
#endif
