using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Module-boundary ratchet tests — the "legislation step" of
/// docs/architecture/module-boundary-roadmap-20260918.md. Laws are declared
/// before any physical code moves: exact violation manifests pin today's
/// offenders file-by-file and may only shrink, never grow — so a cleanup in
/// one file cannot launder a regression in another. Hard-zero laws stay
/// dormant while their target namespaces do not exist and start enforcing
/// the day they appear.
/// </summary>
public sealed class ModuleBoundaryContractTests
{
    // Exact violation manifests measured on 2026-09-18: file → call-site count.
    // Entries may only shrink or disappear — a file that has to grow, or a new
    // file that has to appear, means new boundary violations were added, which
    // is exactly what these tests exist to reject. A bare total budget would
    // let one file's cleanup pay for another file's regression; the manifest
    // closes that substitution gap. Tighten an entry in the same commit that
    // removes its violations — the manifest is the ratchet's memory.
    private static readonly IReadOnlyDictionary<string, int> PlatformInteropExpectedViolations =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/DeskBox/App.xaml.cs"] = 10,
        ["src/DeskBox/Controls/NativeShellFileDragProvider.cs"] = 4,
        ["src/DeskBox/Helpers/BoundedStaOperationRunner.cs"] = 2,
        ["src/DeskBox/Helpers/ChineseTextConverter.cs"] = 1,
        ["src/DeskBox/Helpers/ElevatedFileLauncher.cs"] = 7,
        ["src/DeskBox/Helpers/ExplorerShellLaunchService.cs"] = 1,
        ["src/DeskBox/Helpers/IconHelper.cs"] = 6,
        ["src/DeskBox/Helpers/NativeDropDescriptionWriter.cs"] = 7,
        ["src/DeskBox/Helpers/NativeDropImageManager.cs"] = 1,
        ["src/DeskBox/Helpers/NativeDropTarget.cs"] = 12,
        ["src/DeskBox/Helpers/NativeDropTargetComInterop.cs"] = 2,
        ["src/DeskBox/Helpers/NaturalStringComparer.cs"] = 1,
        ["src/DeskBox/Helpers/ShellClipboardHelper.cs"] = 12,
        ["src/DeskBox/Helpers/ShellContextMenuHelper.cs"] = 1,
        ["src/DeskBox/Helpers/ShellContextMenuProxy.cs"] = 1,
        ["src/DeskBox/Helpers/ShellDataObjectBuilder.cs"] = 5,
        ["src/DeskBox/Helpers/ShellDesktopDropTarget.cs"] = 1,
        ["src/DeskBox/Helpers/ShellDropDelegator.cs"] = 1,
        ["src/DeskBox/Helpers/ShortcutHelper.cs"] = 1,
        ["src/DeskBox/Helpers/ShortcutNativeBackend.cs"] = 1,
        ["src/DeskBox/Helpers/StorageBusTypeHelper.cs"] = 3,
        ["src/DeskBox/Services/AppDistributionService.cs"] = 1,
        ["src/DeskBox/Services/AppLifecycleRecoveryWatcher.cs"] = 2,
        ["src/DeskBox/Services/DesktopAutoOrganizationWatcher.cs"] = 1,
        ["src/DeskBox/Services/DesktopBlankHitTest.cs"] = 6,
        ["src/DeskBox/Services/DesktopOrganizationCoordinator.cs"] = 2,
        ["src/DeskBox/Services/DirectStartupTaskXmlReader.cs"] = 3,
        ["src/DeskBox/Services/DragDropPermissionService.cs"] = 13,
        ["src/DeskBox/Services/EverythingInstallationDetector.cs"] = 4,
        ["src/DeskBox/Services/FileMetaService.cs"] = 2,
        ["src/DeskBox/Services/FileService.ShellTransfer.cs"] = 5,
        ["src/DeskBox/Services/FileService.TransferProgress.cs"] = 1,
        ["src/DeskBox/Services/FileService.cs"] = 6,
        ["src/DeskBox/Services/JumpListService.cs"] = 4,
        ["src/DeskBox/Services/QuickLookPreviewService.cs"] = 4,
        ["src/DeskBox/Services/SystemFontCatalogService.cs"] = 3,
        ["src/DeskBox/Services/WidgetTopologyLayoutService.cs"] = 1,
        ["src/DeskBox/Views/ContentWidgetWindow.AotNativeDropSmoke.cs"] = 4,
    };

    private static readonly IReadOnlyDictionary<string, int> DestructiveFileOpExpectedViolations =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/DeskBox/App.AotHotkeySmoke.cs"] = 1,
        ["src/DeskBox/App.AotLocalFilePersistenceSmoke.cs"] = 3,
        ["src/DeskBox/App.AotManagedUiSmoke.cs"] = 1,
        ["src/DeskBox/App.AotMusicVolumeMutationSmoke.cs"] = 3,
        ["src/DeskBox/App.AotMusicVolumeReadSmoke.cs"] = 2,
        ["src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs"] = 3,
        ["src/DeskBox/App.AotNativeDropSmoke.cs"] = 4,
        ["src/DeskBox/App.AotQuickAccessMutationSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShellMoveSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShellSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShortcutSmoke.cs"] = 5,
        ["src/DeskBox/App.AotTodoNotificationActivationSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs"] = 1,
        ["src/DeskBox/App.xaml.cs"] = 3,
        ["src/DeskBox/Helpers/NativeDropTarget.cs"] = 5,
        ["src/DeskBox/Services/AotShellMoveFixture.cs"] = 1,
        ["src/DeskBox/Services/AppUpdateService.cs"] = 5,
        ["src/DeskBox/Services/AttachmentStorageService.cs"] = 1,
        // Cloud backup orchestrator cleans up its own %TEMP% upload staging
        // directory — it never touches the data root (that stays inside
        // DeskBoxDataBackupService's owned surface).
        ["src/DeskBox/Services/CloudBackupService.cs"] = 1,
        // +2: scoped cloud restore deletes+copies domain files inside the
        // data directory it already owns (ApplyScopedRestoreCoreAsync).
        // +1: Directory.Move inside the scoped-restore staging dir remaps an
        // orphaned todo store onto a live widget id — confined to staging.
        ["src/DeskBox/Services/DeskBoxDataBackupService.cs"] = 12,
        ["src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs"] = 2,
        ["src/DeskBox/Services/DeskBoxDragData.cs"] = 3,
        ["src/DeskBox/Services/DesktopOrganizationCoordinator.cs"] = 1,
        ["src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs"] = 2,
        ["src/DeskBox/Services/DesktopOrganizationTransaction.cs"] = 1,
        ["src/DeskBox/Services/DirectStartupTaskBackend.cs"] = 2,
        ["src/DeskBox/Services/FeedbackService.cs"] = 1,
        ["src/DeskBox/Services/FileService.CaseOnlyRename.cs"] = 2,
        ["src/DeskBox/Services/FileService.TransferProgress.cs"] = 2,
        ["src/DeskBox/Services/FileService.cs"] = 8,
        ["src/DeskBox/Services/GlanceImageService.cs"] = 3,
        ["src/DeskBox/Services/GlanceWidgetStore.cs"] = 1,
        ["src/DeskBox/Services/LegacySearchIndexCleanupService.cs"] = 1,
        ["src/DeskBox/Services/ManagedStorageDesktopShortcutService.cs"] = 1,
        ["src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs"] = 6,
        ["src/DeskBox/Services/QuickCaptureService.cs"] = 8,
        ["src/DeskBox/Services/ReleaseNotesService.cs"] = 1,
        ["src/DeskBox/Services/ResilientJsonStore.cs"] = 7,
        ["src/DeskBox/Services/TodoWidgetStore.cs"] = 3,
        ["src/DeskBox/Services/VirtualDropFileNameResolver.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.Storage.cs"] = 3,
        ["src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs"] = 1,
        ["src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"] = 1,
        ["src/DeskBox/Views/SearchPopupWindow.xaml.cs"] = 2,
        ["src/DeskBox/Views/SettingsWindow.Feedback.cs"] = 1,
    };

    private static readonly string[] LegacyModelsUiExpectedFiles =
    {
        "src/DeskBox/Models/GlanceWidgetData.cs",
        "src/DeskBox/Models/SearchModels.cs",
        "src/DeskBox/Models/SettingsOption.cs",
        "src/DeskBox/Models/WeatherData.cs",
        "src/DeskBox/Models/WidgetItem.AotBindableProperties.cs",
        "src/DeskBox/Models/WidgetItem.cs",
    };

    private static readonly Regex PlatformInteropAttribute = new(
        @"\b(?:DllImport|LibraryImport)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DestructiveFileOperation = new(
        @"(?<![A-Za-z_])(?:File|Directory)\.(?:Move|Delete|Copy|Replace)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex UiFrameworkDependency = new(
        @"Microsoft\.UI|Windows\.Foundation|WinRT\.|Microsoft\.Graphics",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceDeclaration = new(
        @"namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    private static readonly Regex FeatureNamespace = new(
        @"^DeskBox\.Features\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly Regex FeatureUsing = new(
        @"using\s+DeskBox\.Features\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    [Fact]
    public void PlatformInterop_StaysInsideThePlatformDomain()
    {
        // DllImport/LibraryImport may only appear under DeskBox.Platform.*.
        // That namespace does not exist yet, so every current call site counts
        // against the budget and the count may only shrink as P/Invoke migrates
        // in (new P/Invoke must land in Platform from today).
        var offenders = CountMatchesOutsideNamespaces(
            PlatformInteropAttribute,
            namespacePrefix => namespacePrefix.StartsWith("DeskBox.Platform", StringComparison.Ordinal))
            .ToArray();

        AssertViolationManifest(
            offenders,
            PlatformInteropExpectedViolations,
            "New P/Invoke belongs in Platform:");
    }

    [Fact]
    public void DestructiveFileOperations_StayInsideOwnedDomains()
    {
        // File/Directory Move/Delete/Copy/Replace may only appear in the domains
        // that own file mutation: DeskBox.FileSafety (user-file policy), the
        // future DeskBox.Core.Persistence (local persistence machinery), and
        // DeskBox.Platform (mechanism wrappers). None exist yet, so all current
        // call sites sit in the budget and any new scatter fails the ratchet.
        var offenders = CountMatchesOutsideNamespaces(
            DestructiveFileOperation,
            namespacePrefix =>
                namespacePrefix.StartsWith("DeskBox.FileSafety", StringComparison.Ordinal) ||
                namespacePrefix.StartsWith("DeskBox.Core.Persistence", StringComparison.Ordinal) ||
                namespacePrefix.StartsWith("DeskBox.Platform", StringComparison.Ordinal))
            .ToArray();

        AssertViolationManifest(
            offenders,
            DestructiveFileOpExpectedViolations,
            "Route new file mutation through the file-safety kernel:");
    }

    [Fact]
    public void LegacyModelsNamespace_UiDependenciesDoNotGrow()
    {
        // Semantic law: Core.Models / FileSafety.Models / Sync.Contracts are
        // UI-free; UI.Models / ViewModels may hold WinUI types. The legacy
        // DeskBox.Models namespace is unsorted, so its UI-dependent files are
        // capped at today's count until the namespace is split by semantics.
        string[] offenders = FilesMatching(
                source => UiFrameworkDependency.IsMatch(source),
                namespacePrefix => namespacePrefix.StartsWith("DeskBox.Models", StringComparison.Ordinal))
            .ToArray();

        string[] unexpected = offenders
            .Where(path => !LegacyModelsUiExpectedFiles.Contains(path, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            "New UI-bound models belong in a UI namespace:\n" +
            string.Join('\n', unexpected.Select(path => $"  NEW {path}")));
    }

    [Fact]
    public void RestrictedNamespaces_StayUiFree()
    {
        // Hard-zero law, dormant until these namespaces exist: the domain,
        // file-safety and sync model layers must never take WinUI/WASDK types.
        string[] restrictedPrefixes =
        {
            "DeskBox.Core.Models",
            "DeskBox.FileSafety.Models",
            "DeskBox.Sync"
        };

        string[] offenders = FilesMatching(
                source => UiFrameworkDependency.IsMatch(source),
                namespacePrefix => restrictedPrefixes.Any(prefix =>
                    namespacePrefix.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Restricted namespaces must stay free of UI-framework dependencies:\n" +
            string.Join('\n', offenders.Select(path => $"  {path}")));
    }

    [Fact]
    public void FeatureNamespaces_DoNotCrossReference()
    {
        // Hard-zero law, dormant until DeskBox.Features.* exists: a feature may
        // use its own namespace and DeskBox.Contracts, never a sibling feature.
        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            string[] declaredRoots = DeclaredNamespaces(source)
                .Select(prefix => FeatureNamespace.Match(prefix))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (declaredRoots.Length == 0)
            {
                continue;
            }

            foreach (Match match in FeatureUsing.Matches(source))
            {
                string referencedRoot = match.Groups[1].Value;
                if (!declaredRoots.Contains(referencedRoot, StringComparer.Ordinal))
                {
                    violations.Add($"{path} uses DeskBox.Features.{referencedRoot}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Features must reach sibling features only through Contracts:\n" +
            string.Join('\n', violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void SyncNamespace_ReachesOtherDomainsOnlyThroughContracts()
    {
        // Hard-zero law, dormant until DeskBox.Sync exists: the sync engine may
        // not touch FileSafety (local transactional data), concrete Features
        // implementations, or Platform — it reads the sync domain via Contracts.
        string[] forbiddenPrefixes = { "DeskBox.FileSafety", "DeskBox.Features", "DeskBox.Platform" };

        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            bool isSync = DeclaredNamespaces(source).Any(prefix =>
                prefix.StartsWith("DeskBox.Sync", StringComparison.Ordinal));
            if (!isSync)
            {
                continue;
            }

            foreach (string forbidden in forbiddenPrefixes.Where(source.Contains))
            {
                violations.Add($"{path} references {forbidden}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "DeskBox.Sync must reach other domains only through Contracts:\n" +
            string.Join('\n', violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void FileSafetyNamespace_UsesContractsNotNativeMechanism()
    {
        // Hard-zero law, dormant until DeskBox.FileSafety exists: the policy
        // layer (identity, done-is-done, WAL) talks to mechanism only through
        // contracts such as IFileSystemPrimitives — never P/Invoke directly.
        string[] offenders = FilesMatching(
                source => PlatformInteropAttribute.IsMatch(source),
                namespacePrefix => namespacePrefix.StartsWith("DeskBox.FileSafety", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "DeskBox.FileSafety must not carry P/Invoke; route mechanism through contracts:\n" +
            string.Join('\n', offenders.Select(path => $"  {path}")));
    }

    private static void AssertViolationManifest(
        (string Path, int Count)[] actual,
        IReadOnlyDictionary<string, int> expected,
        string guidance)
    {
        List<string> violations = new();
        foreach ((string path, int count) in actual)
        {
            if (!expected.TryGetValue(path, out int budget))
            {
                violations.Add($"  NEW {path}: {count}");
            }
            else if (count > budget)
            {
                violations.Add($"  GREW {path}: {count} (manifest {budget})");
            }
        }

        Assert.True(
            violations.Count == 0,
            guidance + "\n" + string.Join('\n', violations));
    }

    private static IEnumerable<(string Path, int Count)> CountMatchesOutsideNamespaces(
        Regex pattern,
        Func<string, bool> isExemptNamespace)
    {
        foreach ((string path, string source) in ProductionSource())
        {
            if (DeclaredNamespaces(source).Any(isExemptNamespace))
            {
                continue;
            }

            int count = pattern.Matches(source).Count;
            if (count > 0)
            {
                yield return (path, count);
            }
        }
    }

    private static IEnumerable<string> FilesMatching(
        Func<string, bool> sourceMatches,
        Func<string, bool> namespaceMatches)
    {
        foreach ((string path, string source) in ProductionSource())
        {
            if (DeclaredNamespaces(source).Any(namespaceMatches) && sourceMatches(source))
            {
                yield return path;
            }
        }
    }

    private static string[] DeclaredNamespaces(string source) =>
        NamespaceDeclaration.Matches(source)
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static IEnumerable<(string Path, string Source)> ProductionSource()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => (RepositoryRelativePath(path), File.ReadAllText(path)));
    }

    private static string RepositoryRelativePath(string path) =>
        Path.GetRelativePath(TestPaths.FromRepository("."), path)
            .Replace(Path.DirectorySeparatorChar, '/');
}
