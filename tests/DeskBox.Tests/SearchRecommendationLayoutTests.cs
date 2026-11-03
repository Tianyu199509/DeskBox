using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchRecommendationLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox-RecLayout-" + Guid.NewGuid().ToString("N"));

    public SearchRecommendationLayoutTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Deduplicator_CollapsesDuplicateDisplayNamesAndSameExecutable_KeepingFirstPriority()
    {
        var deduplicator = new AppRecommendationDeduplicator();

        var widgetApp = CreateApp("Visual Studio Code.lnk", @"C:\Users\simon\Desktop\Visual Studio Code.lnk");
        var startMenuDuplicate = CreateApp("Visual Studio Code.lnk", @"C:\ProgramData\Start Menu\Visual Studio Code.lnk");
        var directExe = CreateApp("Code.exe", @"C:\Apps\VSCode\Code.exe");
        var secondDirectExe = CreateApp("Code.exe", @"c:\apps\vscode\code.exe");

        Assert.True(deduplicator.TryAccept(widgetApp.DetailPath, widgetApp.Title, out string primaryKey));
        Assert.False(string.IsNullOrWhiteSpace(primaryKey));
        Assert.False(deduplicator.TryAccept(startMenuDuplicate.DetailPath, startMenuDuplicate.Title, out _));

        Assert.True(deduplicator.TryAccept(directExe.DetailPath, directExe.Title, out _));
        Assert.False(deduplicator.TryAccept(secondDirectExe.DetailPath, secondDirectExe.Title, out _));
    }

    [Fact]
    public void Identity_ExtractsSquirrelTargetAndDistinguishesDistinctProxyPwas()
    {
        string? squirrelKey = AppRecommendationIdentity.ResolveShortcutMetadataTargetKey(
            @"C:\Users\simon\AppData\Local\Discord\Update.exe",
            "--processStart Discord.exe");
        Assert.Equal("squirrel:discord.exe", squirrelKey);

        string? pwaOneKey = AppRecommendationIdentity.ResolveShortcutMetadataTargetKey(
            @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
            "--profile-directory=Default --app-id=aaaa1111");
        string? pwaTwoKey = AppRecommendationIdentity.ResolveShortcutMetadataTargetKey(
            @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
            "--profile-directory=Default --app-id=bbbb2222");

        Assert.NotNull(pwaOneKey);
        Assert.NotNull(pwaTwoKey);
        Assert.NotEqual(pwaOneKey, pwaTwoKey);
    }

    [Fact]
    public void HideAndUnhide_FiltersVisibleEntriesAndPersistsAcrossReload()
    {
        string storePath = Path.Combine(_root, "search-recommendations.json");
        var service = new SearchRecommendationLayoutService(storePath);

        SearchResultItem[] apps =
        [
            CreateApp("Edge.exe", @"C:\Apps\Edge.exe"),
            CreateApp("Notepad.exe", @"C:\Apps\Notepad.exe"),
            CreateApp("Calc.exe", @"C:\Apps\Calc.exe")
        ];

        string[] allKeys = apps.Select(AppRecommendationIdentity.GetCanonicalKey).ToArray();
        string notepadKey = allKeys[1];
        string calcKey = allKeys[2];

        Assert.True(service.HideApp(notepadKey, allKeys));
        Assert.True(service.HideApp(calcKey, allKeys));

        var reloaded = new SearchRecommendationLayoutService(storePath);
        SearchRecommendationProjection projection = reloaded.BuildProjection(apps, "文件夹");

        Assert.Single(projection.VisibleEntries);
        Assert.Equal("Edge", projection.VisibleEntries[0].Label);
        Assert.Equal(2, projection.HiddenItems.Count);
        Assert.Equal("Notepad", projection.HiddenItems[0].AppDisplayName);
        Assert.Equal("Calc", projection.HiddenItems[1].AppDisplayName);

        Assert.True(reloaded.UnhideApp(notepadKey));
        projection = reloaded.BuildProjection(apps, "文件夹");
        Assert.Equal(2, projection.VisibleEntries.Count);
        Assert.Single(projection.HiddenItems);

        Assert.Equal(1, reloaded.UnhideAllApps());
        projection = reloaded.BuildProjection(apps, "文件夹");
        Assert.Equal(3, projection.VisibleEntries.Count);
        Assert.Empty(projection.HiddenItems);
    }

    [Fact]
    public void ReorderMergeRenameAndAutoDissolve_MatchWin11StartMenuSemantics()
    {
        string storePath = Path.Combine(_root, "layout-folders.json");
        var service = new SearchRecommendationLayoutService(storePath);

        SearchResultItem[] apps =
        [
            CreateApp("AppA.exe", @"C:\Apps\AppA.exe"),
            CreateApp("AppB.exe", @"C:\Apps\AppB.exe"),
            CreateApp("AppC.exe", @"C:\Apps\AppC.exe"),
            CreateApp("AppD.exe", @"C:\Apps\AppD.exe")
        ];

        string[] allKeys = apps.Select(AppRecommendationIdentity.GetCanonicalKey).ToArray();

        // 1. Move AppD (index 3) to index 0 -> [AppD, AppA, AppB, AppC]
        Assert.True(service.MoveTopLevelEntry($"app:{allKeys[3]}", 0, allKeys));
        SearchRecommendationProjection reordered = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "AppD", "AppA", "AppB", "AppC" },
            reordered.VisibleEntries.Select(e => e.Label).ToArray());

        // 2. Merge AppB onto AppA -> creates 2-item folder [AppA, AppB] at AppA's position
        string? folderId = service.MergeIntoFolder(
            allKeys[1],
            $"app:{allKeys[0]}",
            "文件夹",
            allKeys);
        Assert.False(string.IsNullOrWhiteSpace(folderId));

        SearchRecommendationProjection mergedTwo = service.BuildProjection(apps, "文件夹");
        Assert.Equal(3, mergedTwo.VisibleEntries.Count);
        Assert.True(mergedTwo.VisibleEntries[1].IsFolder);
        Assert.Equal("文件夹", mergedTwo.VisibleEntries[1].Label);
        Assert.Equal(
            new[] { "AppA", "AppB" },
            mergedTwo.VisibleEntries[1].FolderItems.Select(i => i.AppDisplayName).ToArray());

        // 3. Merge AppC into the existing folder & rename it -> [AppA, AppB, AppC]
        string? sameFolderId = service.MergeIntoFolder(
            allKeys[2],
            $"folder:{folderId}",
            "文件夹",
            allKeys);
        Assert.Equal(folderId, sameFolderId);
        Assert.True(service.RenameFolder(folderId!, "开发工具", "文件夹"));

        // 4. Reorder AppC to index 0 inside the folder -> [AppC, AppA, AppB]
        Assert.True(service.ReorderInsideFolder(folderId!, allKeys[2], 0));

        var reloaded = new SearchRecommendationLayoutService(storePath);
        SearchRecommendationProjection reloadedProjection = reloaded.BuildProjection(apps, "文件夹");
        Assert.Equal(2, reloadedProjection.VisibleEntries.Count);
        RecommendedGridEntry folderEntry = reloadedProjection.VisibleEntries[1];
        Assert.True(folderEntry.IsFolder);
        Assert.Equal("开发工具", folderEntry.Label);
        Assert.Equal(
            new[] { "AppC", "AppA", "AppB" },
            folderEntry.FolderItems.Select(i => i.AppDisplayName).ToArray());

        // 5. Removing 1 of 3 items keeps the folder (2 left) and places extracted item after folder
        Assert.True(reloaded.RemoveFromFolder(folderId!, allKeys[2], allKeys, out bool stillExistsAfterFirstRemove));
        Assert.True(stillExistsAfterFirstRemove);

        // 6. Removing 1 of the remaining 2 items auto-dissolves the folder into standalone apps
        Assert.True(reloaded.RemoveFromFolder(folderId!, allKeys[1], allKeys, out bool stillExistsAfterSecondRemove));
        Assert.False(stillExistsAfterSecondRemove);

        SearchRecommendationProjection afterDissolve = reloaded.BuildProjection(apps, "文件夹");
        Assert.All(afterDissolve.VisibleEntries, entry => Assert.False(entry.IsFolder));
        Assert.Equal(
            new[] { "AppD", "AppA", "AppB", "AppC" },
            afterDissolve.VisibleEntries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void EverythingPromptDismissal_PersistsAcrossReloadAndRearmsOnToggleOrConnect()
    {
        string storePath = Path.Combine(_root, "everything-banner.json");
        var service = new SearchRecommendationLayoutService(storePath);

        Assert.False(service.IsEverythingPromptDismissed(currentEverythingEnabled: false));
        service.DismissEverythingPrompt(currentEverythingEnabled: false);
        Assert.True(service.IsEverythingPromptDismissed(currentEverythingEnabled: false));

        var reloaded = new SearchRecommendationLayoutService(storePath);
        Assert.True(reloaded.IsEverythingPromptDismissed(currentEverythingEnabled: false));

        // Toggling EverythingEnabled in settings re-arms the prompt if connection fails.
        Assert.False(reloaded.IsEverythingPromptDismissed(currentEverythingEnabled: true));

        // Connecting Everything resets the dismissal flag completely.
        reloaded.ResetEverythingPromptDismissal();
        var afterConnectReload = new SearchRecommendationLayoutService(storePath);
        Assert.False(afterConnectReload.IsEverythingPromptDismissed(currentEverythingEnabled: false));
    }

    [Fact]
    public void BatchMergeReorderAndHide_SupportMultiSelectedEditModeWorkflow()
    {
        string storePath = Path.Combine(_root, "batch-edit-mode.json");
        var service = new SearchRecommendationLayoutService(storePath);

        SearchResultItem[] apps =
        [
            CreateApp("App1.exe", @"C:\Apps\App1.exe"),
            CreateApp("App2.exe", @"C:\Apps\App2.exe"),
            CreateApp("App3.exe", @"C:\Apps\App3.exe"),
            CreateApp("App4.exe", @"C:\Apps\App4.exe"),
            CreateApp("App5.exe", @"C:\Apps\App5.exe"),
            CreateApp("App6.exe", @"C:\Apps\App6.exe")
        ];

        string[] allKeys = apps.Select(AppRecommendationIdentity.GetCanonicalKey).ToArray();

        // 1. Batch move {App1, App2} after App4 (slotIndex = 4) -> [App3, App4, App1, App2, App5, App6]
        Assert.True(service.MoveTopLevelEntries(
            [$"app:{allKeys[0]}", $"app:{allKeys[1]}"],
            targetVisibleIndex: 4,
            allKeys));
        SearchRecommendationProjection afterBatchMove = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "App3", "App4", "App1", "App2", "App5", "App6" },
            afterBatchMove.VisibleEntries.Select(e => e.Label).ToArray());

        // 2. Batch merge {App1, App2, App5} onto standalone App3 -> creates 4-item folder [App3, App1, App2, App5]
        string? folderId = service.MergeAppsIntoFolder(
            [allKeys[0], allKeys[1], allKeys[4]],
            $"app:{allKeys[2]}",
            "工具集",
            allKeys);
        Assert.False(string.IsNullOrWhiteSpace(folderId));

        SearchRecommendationProjection afterBatchMerge = service.BuildProjection(apps, "文件夹");
        Assert.Equal(3, afterBatchMerge.VisibleEntries.Count);
        Assert.True(afterBatchMerge.VisibleEntries[0].IsFolder);
        Assert.Equal("工具集", afterBatchMerge.VisibleEntries[0].Label);
        Assert.Equal(
            new[] { "App3", "App1", "App2", "App5" },
            afterBatchMerge.VisibleEntries[0].FolderItems.Select(i => i.AppDisplayName).ToArray());

        // 3. Batch merge {App4, App6} into the existing folder -> 6-item folder
        string? updatedFolderId = service.MergeAppsIntoFolder(
            [allKeys[3], allKeys[5]],
            $"folder:{folderId}",
            "工具集",
            allKeys);
        Assert.Equal(folderId, updatedFolderId);

        SearchRecommendationProjection afterFolderAppend = service.BuildProjection(apps, "文件夹");
        Assert.Single(afterFolderAppend.VisibleEntries);
        Assert.Equal(
            new[] { "App3", "App1", "App2", "App5", "App4", "App6" },
            afterFolderAppend.VisibleEntries[0].FolderItems.Select(i => i.AppDisplayName).ToArray());

        // 4. Batch hide 5 of the 6 apps -> remaining 1-item folder auto-dissolves to standalone App6!
        Assert.Equal(
            5,
            service.HideApps(
                [allKeys[2], allKeys[0], allKeys[1], allKeys[4], allKeys[3]],
                allKeys));
        SearchRecommendationProjection afterBatchHide = service.BuildProjection(apps, "文件夹");
        Assert.Single(afterBatchHide.VisibleEntries);
        Assert.False(afterBatchHide.VisibleEntries[0].IsFolder);
        Assert.Equal("App6", afterBatchHide.VisibleEntries[0].Label);
        Assert.Equal(5, afterBatchHide.HiddenItems.Count);
    }

    [Fact]
    public void Deduplicator_ReusesPrecomputedIdentityKeys_WithoutRescanning()
    {
        var firstPass = new AppRecommendationDeduplicator();
        Assert.True(firstPass.TryAcceptWithKeys(
            @"C:\Users\simon\Desktop\Chrome.lnk",
            "Google Chrome.lnk",
            precomputedKeys: null,
            out AppRecommendationIdentity.Keys cachedKeys,
            out string firstCanonicalKey));
        Assert.Equal(cachedKeys.CanonicalKey, firstCanonicalKey);

        var secondPass = new AppRecommendationDeduplicator();
        Assert.True(secondPass.TryAccept(
            @"C:\Users\simon\Desktop\Chrome.lnk",
            "Google Chrome.lnk",
            cachedKeys,
            out string canonicalKey));
        Assert.Equal(cachedKeys.CanonicalKey, canonicalKey);

        // Same display name from Start Menu is still deduplicated against precomputed keys.
        Assert.False(secondPass.TryAccept(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Google Chrome.lnk",
            "Google Chrome.lnk",
            out _));
    }

    [Fact]
    public void EditModeVisuals_UseFluentCheckboxAndSubtleHideWithoutJiggle()
    {
        string repoRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repoRoot, "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(repoRoot, "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        // Edit mode must never apply rotation/jiggle animations to recommendation cards.
        Assert.DoesNotContain("RotationAngleInDegrees", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CardJiggleHost", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CardJiggleHost", code, StringComparison.Ordinal);

        // Fluent rounded-square checkbox + eye-slash subtle hide button + footer batch-hide action.
        Assert.Contains("x:Key=\"EditCardQuickActionButtonStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CardSelectionCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CardSelectionCheckedBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CardSelectionUncheckedBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BatchHideSelectedButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SingleIconHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CardLabelTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HideSelectedRecommendations", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveTopLevelEntries_SupportsMixedFolderAndAppBatchAndPrunesDuplicates()
    {
        string storePath = Path.Combine(_root, "mixed-folder-app-batch.json");
        var service = new SearchRecommendationLayoutService(storePath);

        SearchResultItem[] apps =
        [
            CreateApp("ThisPC.lnk", @"C:\Apps\ThisPC.lnk"),
            CreateApp("Recycle.lnk", @"C:\Apps\Recycle.lnk"),
            CreateApp("MemberA.exe", @"C:\Apps\MemberA.exe"),
            CreateApp("MemberB.exe", @"C:\Apps\MemberB.exe"),
            CreateApp("ControlPanel.lnk", @"C:\Apps\ControlPanel.lnk"),
            CreateApp("Chrome.exe", @"C:\Apps\Chrome.exe")
        ];

        string[] allKeys = apps.Select(AppRecommendationIdentity.GetCanonicalKey).ToArray();
        string? folderId = service.MergeIntoFolder(allKeys[3], $"app:{allKeys[2]}", "文件夹", allKeys);
        Assert.False(string.IsNullOrWhiteSpace(folderId));

        // Initial visible order: [0: ThisPC, 1: Recycle, 2: 文件夹(MemberA, MemberB), 3: ControlPanel, 4: Chrome]
        SearchRecommendationProjection initial = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "ThisPC", "Recycle", "文件夹", "ControlPanel", "Chrome" },
            initial.VisibleEntries.Select(e => e.Label).ToArray());

        // Contiguous selection {Recycle(1), 文件夹(2)} dropped at slot 1 or 3 is a no-op -> returns false.
        Assert.False(service.MoveTopLevelEntries(
            [$"app:{allKeys[1]}", $"folder:{folderId}"],
            targetVisibleIndex: 1,
            allKeys));
        Assert.False(service.MoveTopLevelEntries(
            [$"app:{allKeys[1]}", $"folder:{folderId}"],
            targetVisibleIndex: 3,
            allKeys));

        // Mixed batch move {Recycle(1), 文件夹(2)} to end (slot 5) -> [ThisPC, ControlPanel, Chrome, Recycle, 文件夹]
        Assert.True(service.MoveTopLevelEntries(
            [$"app:{allKeys[1]}", $"folder:{folderId}"],
            targetVisibleIndex: 5,
            allKeys));
        SearchRecommendationProjection afterEndMove = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "ThisPC", "ControlPanel", "Chrome", "Recycle", "文件夹" },
            afterEndMove.VisibleEntries.Select(e => e.Label).ToArray());

        // Scattered batch move {ControlPanel(1), 文件夹(4)} before ThisPC (slot 0) -> [ControlPanel, 文件夹, ThisPC, Chrome, Recycle]
        Assert.True(service.MoveTopLevelEntries(
            [$"app:{allKeys[4]}", $"folder:{folderId}"],
            targetVisibleIndex: 0,
            allKeys));
        SearchRecommendationProjection afterFrontMove = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "ControlPanel", "文件夹", "ThisPC", "Chrome", "Recycle" },
            afterFrontMove.VisibleEntries.Select(e => e.Label).ToArray());

        // Single folder<->app interleaved moves stay 1:1 with visible indices.
        Assert.True(service.MoveTopLevelEntry($"folder:{folderId}", targetVisibleIndex: 3, allKeys));
        SearchRecommendationProjection afterSingleFolderMove = service.BuildProjection(apps, "文件夹");
        Assert.Equal(
            new[] { "ControlPanel", "ThisPC", "Chrome", "文件夹", "Recycle" },
            afterSingleFolderMove.VisibleEntries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void ReconcileRecommendedEntries_NeverEmitsMoveAndPreservesReusableInstances()
    {
        SearchResultItem appA = CreateApp("A.exe", @"C:\Apps\A.exe");
        SearchResultItem appB = CreateApp("B.exe", @"C:\Apps\B.exe");
        SearchResultItem appC = CreateApp("C.exe", @"C:\Apps\C.exe");
        SearchResultItem appD = CreateApp("D.exe", @"C:\Apps\D.exe");

        var entryA = Models.RecommendedGridEntry.CreateApp("a", appA);
        var folderBC = Models.RecommendedGridEntry.CreateFolder("f1", "文件夹", [appB, appC]);
        var entryD = Models.RecommendedGridEntry.CreateApp("d", appD);

        var current = new System.Collections.ObjectModel.ObservableCollection<Models.RecommendedGridEntry>
        {
            entryA,
            folderBC,
            entryD
        };

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, args) => actions.Add(args.Action);

        Models.RecommendedGridEntry[] incoming =
        [
            Models.RecommendedGridEntry.CreateApp("d", appD),
            Models.RecommendedGridEntry.CreateApp("a", appA),
            Models.RecommendedGridEntry.CreateFolder("f1", "文件夹", [appB, appC])
        ];

        ViewModels.SearchPopupViewModel.ReconcileRecommendedEntries(current, incoming);

        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Move, actions);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { "app:d", "app:a", "folder:f1" }, current.Select(e => e.EntryKey).ToArray());
        Assert.Same(entryD, current[0]);
        Assert.Same(entryA, current[1]);
        Assert.Same(folderBC, current[2]);
    }

    [Fact]
    public void WouldBatchReorderChangeOrder_DetectsNoOpContiguousDropsAndAllowsScatteredGather()
    {
        SearchResultItem app0 = CreateApp("0.exe", @"C:\Apps\0.exe");
        SearchResultItem app1 = CreateApp("1.exe", @"C:\Apps\1.exe");
        SearchResultItem app2 = CreateApp("2.exe", @"C:\Apps\2.exe");
        SearchResultItem app3 = CreateApp("3.exe", @"C:\Apps\3.exe");

        Models.RecommendedGridEntry[] entries =
        [
            Models.RecommendedGridEntry.CreateApp("0", app0),
            Models.RecommendedGridEntry.CreateFolder("f1", "文件夹", [app1]),
            Models.RecommendedGridEntry.CreateApp("2", app2),
            Models.RecommendedGridEntry.CreateApp("3", app3)
        ];

        var contiguous = new HashSet<string>(["folder:f1", "app:2"], StringComparer.OrdinalIgnoreCase);
        Assert.False(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, contiguous, 1));
        Assert.False(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, contiguous, 2));
        Assert.False(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, contiguous, 3));
        Assert.True(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, contiguous, 0));
        Assert.True(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, contiguous, 4));

        var scattered = new HashSet<string>(["app:0", "app:2"], StringComparer.OrdinalIgnoreCase);
        // Dropping right after app:0 (slot 1) or right before app:2 (slot 2) gathers {app:0, app:2}!
        Assert.True(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, scattered, 1));
        Assert.True(Views.SearchPopupWindow.WouldBatchReorderChangeOrder(entries, scattered, 2));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }

    private static SearchResultItem CreateApp(string title, string detailPath) => new()
    {
        Kind = SearchResultKind.File,
        Title = title,
        Subtitle = Path.GetDirectoryName(detailPath) ?? string.Empty,
        DetailPath = detailPath,
        Glyph = "\uE7AC"
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
