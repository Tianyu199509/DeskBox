using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("report.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf)]
    [InlineData("invoice.ofd", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf)]
    [InlineData("draft.wps", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Word)]
    [InlineData("config.json", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Text)]
    [InlineData("ebook.epub", DesktopOrganizationCategoryIds.Documents, null)]
    [InlineData("1.5.2虚拟视频优化.rp", DesktopOrganizationCategoryIds.Design, null)]
    [InlineData("roadmap.xmind", DesktopOrganizationCategoryIds.Design, null)]
    [InlineData("ui.fig", DesktopOrganizationCategoryIds.Design, null)]
    [InlineData("poster.psd", DesktopOrganizationCategoryIds.Design, null)]
    [InlineData("photo.webp", DesktopOrganizationCategoryIds.Images, null)]
    [InlineData("movie.mp4", DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Video)]
    [InlineData("music.flac", DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Audio)]
    [InlineData("app.lnk", DesktopOrganizationCategoryIds.Shortcuts, null)]
    [InlineData("AutoClaw.exe", DesktopOrganizationCategoryIds.Shortcuts, null)]
    [InlineData("CPU-Z_2.09_Portable.exe", DesktopOrganizationCategoryIds.Shortcuts, null)]
    [InlineData("WeChatSetup.exe", DesktopOrganizationCategoryIds.Packages, null)]
    [InlineData("python-3.12.6-amd64.exe", DesktopOrganizationCategoryIds.Packages, null)]
    [InlineData("setup.msix", DesktopOrganizationCategoryIds.Packages, null)]
    [InlineData("unknown.xyz", DesktopOrganizationCategoryIds.Other, null)]
    public void Classifier_UsesStableTypeIds(
        string fileName,
        string expectedCategory,
        string? expectedSubtype)
    {
        var result = new DesktopOrganizationClassifier().Classify(fileName);

        Assert.Equal(expectedCategory, result.CategoryId);
        Assert.Equal(expectedSubtype, result.SubtypeId);
    }

    [Fact]
    public void Classifier_ExposesTheFormatsShownByTheRuleEditor()
    {
        Assert.Contains(
            ".pdf",
            DesktopOrganizationClassifier.GetCategoryExtensions(
                DesktopOrganizationCategoryIds.Documents));
        Assert.Contains(
            ".epub",
            DesktopOrganizationClassifier.GetCategoryExtensions(
                DesktopOrganizationCategoryIds.Documents));
        Assert.Contains(
            ".rp",
            DesktopOrganizationClassifier.GetCategoryExtensions(
                DesktopOrganizationCategoryIds.Design));
        Assert.Contains(
            ".psd",
            DesktopOrganizationClassifier.GetCategoryExtensions(
                DesktopOrganizationCategoryIds.Design));
        Assert.Contains(
            ".docx",
            DesktopOrganizationClassifier.GetSubtypeExtensions(
                DesktopOrganizationSubtypeIds.Word));
        Assert.Contains(
            ".mp4",
            DesktopOrganizationClassifier.GetSubtypeExtensions(
                DesktopOrganizationSubtypeIds.Video));
        Assert.Empty(
            DesktopOrganizationClassifier.GetCategoryExtensions(
                DesktopOrganizationCategoryIds.Other));
    }

    [Fact]
    public async Task Scanner_IsTopLevelAndExcludesFoldersTemporaryAndSlowItems()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_root, "download.crdownload"), "partial");
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        using (var stream = File.Create(Path.Combine(_root, "large.bin")))
        {
            stream.SetLength(DesktopOrganizationScanner.SlowItemThresholdBytes + 1);
        }

        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => _root,
            () => string.Empty);

        DesktopOrganizationScanResult result = await scanner.ScanAsync();

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(1, result.EligibleCount);
        Assert.Contains(result.Items, item =>
            item.Name == "folder" &&
            item.ExclusionReason == DesktopOrganizationExclusionReason.Folder &&
            item.IsDirectory &&
            item.CanOptIn);
        Assert.Contains(result.Items, item =>
            item.Name == "download.crdownload" &&
            item.ExclusionReason == DesktopOrganizationExclusionReason.TemporaryOrDownloading);
        Assert.Contains(result.Items, item =>
            item.Name == "large.bin" &&
            item.ExclusionReason == DesktopOrganizationExclusionReason.SlowItem);
    }

    [Fact]
    public async Task Scanner_IncludesFilesAtThe100MiBBoundary()
    {
        Directory.CreateDirectory(_root);
        using (var stream = File.Create(Path.Combine(_root, "boundary.bin")))
        {
            stream.SetLength(DesktopOrganizationScanner.SlowItemThresholdBytes);
        }

        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => _root,
            () => string.Empty);

        DesktopOrganizationScanResult result = await scanner.ScanAsync();

        DesktopOrganizationFileSnapshot item = Assert.Single(result.Items);
        Assert.Equal(DesktopOrganizationScanner.SlowItemThresholdBytes, item.Size);
        Assert.Equal(DesktopOrganizationExclusionReason.None, item.ExclusionReason);
        Assert.Equal(1, result.EligibleCount);
    }

    [Fact]
    public async Task Scanner_DoesNotAllowHiddenFolderOptIn()
    {
        Directory.CreateDirectory(_root);
        string hiddenFolder = Directory.CreateDirectory(
            Path.Combine(_root, "hidden-folder")).FullName;
        File.SetAttributes(
            hiddenFolder,
            File.GetAttributes(hiddenFolder) | FileAttributes.Hidden);
        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => _root,
            () => string.Empty);

        DesktopOrganizationFileSnapshot item = Assert.Single(
            (await scanner.ScanAsync()).Items);

        Assert.True(item.IsDirectory);
        Assert.Equal(
            DesktopOrganizationExclusionReason.HiddenOrSystem,
            item.ExclusionReason);
        Assert.False(item.CanOptIn);
    }

    [Fact]
    public async Task Scanner_ExcludesCommonDownloadControlFiles()
    {
        Directory.CreateDirectory(_root);
        string[] names =
        [
            "download.opdownload",
            "download.aria2",
            "download.!ut",
            "download.bc!"
        ];
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(_root, name), "partial");
        }

        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => _root,
            () => string.Empty);

        DesktopOrganizationScanResult result = await scanner.ScanAsync();

        Assert.Equal(names.Length, result.TotalCount);
        Assert.All(
            result.Items,
            item => Assert.Equal(
                DesktopOrganizationExclusionReason.TemporaryOrDownloading,
                item.ExclusionReason));
    }

    [Fact]
    public async Task Scanner_LimitsQuickBatchItemCount()
    {
        Directory.CreateDirectory(_root);
        for (int index = 0; index < DesktopOrganizationScanner.QuickBatchItemLimit + 3; index++)
        {
            File.WriteAllText(Path.Combine(_root, $"{index:D3}.txt"), "x");
        }

        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => _root,
            () => string.Empty);

        DesktopOrganizationScanResult result = await scanner.ScanAsync();

        Assert.Equal(DesktopOrganizationScanner.QuickBatchItemLimit, result.EligibleCount);
        Assert.Equal(
            3,
            result.Items.Count(item =>
                item.ExclusionReason == DesktopOrganizationExclusionReason.BatchLimit));
    }

    [Fact]
    public void Resolver_FollowsStableWidgetIdAfterRename()
    {
        var widget = CreateWidget("renamed", Path.Combine(_root, "target"));
        var rule = new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        };
        var item = Snapshot("report.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf);
        var resolver = new DesktopOrganizationRuleResolver();

        DesktopOrganizationRule? before = resolver.Resolve(item, [rule], [widget]);
        widget.Name = "完全不同的名字";
        DesktopOrganizationRule? after = resolver.Resolve(item, [rule], [widget]);

        Assert.Same(rule, before);
        Assert.Same(rule, after);
    }

    [Fact]
    public void Resolver_PrefersExtensionOverSubtypeAndCategory()
    {
        var categoryWidget = CreateWidget("documents", Path.Combine(_root, "documents"));
        var subtypeWidget = CreateWidget("pdf", Path.Combine(_root, "pdf"));
        var extensionWidget = CreateWidget("specific", Path.Combine(_root, "specific"));
        var rules = new[]
        {
            new DesktopOrganizationRule
            {
                TargetWidgetId = categoryWidget.Id,
                CategoryIds = [DesktopOrganizationCategoryIds.Documents]
            },
            new DesktopOrganizationRule
            {
                TargetWidgetId = subtypeWidget.Id,
                SubtypeIds = [DesktopOrganizationSubtypeIds.Pdf]
            },
            new DesktopOrganizationRule
            {
                TargetWidgetId = extensionWidget.Id,
                Extensions = [".pdf"]
            }
        };

        DesktopOrganizationRule? result = new DesktopOrganizationRuleResolver().Resolve(
            Snapshot("report.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf),
            rules,
            [categoryWidget, subtypeWidget, extensionWidget]);

        Assert.Equal(extensionWidget.Id, result?.TargetWidgetId);
    }

    [Fact]
    public void AssignExtensionExclusively_TransfersOwnership()
    {
        var first = new DesktopOrganizationRule
        {
            TargetWidgetId = "first",
            Extensions = [".pdf"]
        };
        var second = new DesktopOrganizationRule
        {
            TargetWidgetId = "second"
        };
        var rules = new List<DesktopOrganizationRule> { first, second };

        new DesktopOrganizationRuleResolver()
            .AssignExtensionExclusively(rules, "second", "PDF");

        Assert.DoesNotContain(".pdf", first.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".pdf", second.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planner_ReusesRuleTargetAndLimitsNewWidgetsToCap()
    {
        Directory.CreateDirectory(_root);
        var existing = CreateWidget("Renamed", Path.Combine(_root, "existing"));
        var rule = new DesktopOrganizationRule
        {
            TargetWidgetId = existing.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        };
        var items = new List<DesktopOrganizationFileSnapshot>
        {
            Snapshot("one.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf),
            Snapshot("two.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf)
        };
        foreach (string category in new[]
                 {
                     DesktopOrganizationCategoryIds.Shortcuts,
                     DesktopOrganizationCategoryIds.Design,
                     DesktopOrganizationCategoryIds.Images,
                     DesktopOrganizationCategoryIds.Media,
                     DesktopOrganizationCategoryIds.Packages,
                     DesktopOrganizationCategoryIds.Other
                 })
        {
            items.Add(Snapshot($"{category}-1.dat", category, null));
            items.Add(Snapshot($"{category}-2.dat", category, null));
        }

        var scan = new DesktopOrganizationScanResult
        {
            DesktopPath = _root,
            Items = items
        };
        var planner = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver());

        DesktopOrganizationPlan plan = planner.CreatePlan(
            scan,
            Path.Combine(_root, "storage"),
            [existing],
            [rule]);

        Assert.Contains(plan.Targets, target =>
            !target.CreatesWidget &&
            target.TargetWidgetId == existing.Id &&
            target.Items.Count == 2);
        Assert.Equal(DesktopOrganizationPlanner.MaxNewWidgetCount, plan.NewWidgetCount);
        Assert.Equal(items.Count, plan.EligibleItemCount);
    }

    [Fact]
    public void Planner_KeepsSingleItemConcreteCategoriesSeparateWithinWidgetCap()
    {
        Directory.CreateDirectory(_root);
        var classifier = new DesktopOrganizationClassifier();
        DesktopOrganizationFileSnapshot ClassifySnapshot(string name)
        {
            var classified = classifier.Classify(name);
            return Snapshot(name, classified.CategoryId, classified.SubtypeId);
        }

        var items = new List<DesktopOrganizationFileSnapshot>
        {
            ClassifySnapshot("Antigravity Tools.lnk"),
            ClassifySnapshot("AutoClaw.exe"),
            ClassifySnapshot("1.5.2虚拟视频优化.rp"),
            ClassifySnapshot("1.5.4.rp"),
            ClassifySnapshot("朱-注销冷静.rp"),
            ClassifySnapshot("虚拟视频评分.rp"),
            ClassifySnapshot("166200.jpg"),
            ClassifySnapshot("当前报告.txt")
        };
        var scan = new DesktopOrganizationScanResult
        {
            DesktopPath = _root,
            Items = items
        };

        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver())
            .CreatePlan(scan, Path.Combine(_root, "storage"), [], []);

        Assert.Equal(
            [
                DesktopOrganizationCategoryIds.Shortcuts,
                DesktopOrganizationCategoryIds.Documents,
                DesktopOrganizationCategoryIds.Design,
                DesktopOrganizationCategoryIds.Images
            ],
            plan.Targets.Select(target => target.CategoryId).ToArray());
        Assert.DoesNotContain(plan.Targets, target => target.CategoryId == DesktopOrganizationCategoryIds.Other);
        Assert.Equal(8, plan.EligibleItemCount);
    }

    [Fact]
    public void PlacementPlanner_StartsAtTopRightAndAvoidsOccupiedBounds()
    {
        var plan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan { TargetWidgetId = "one", CreatesWidget = true },
                new DesktopOrganizationTargetPlan { TargetWidgetId = "two", CreatesWidget = true }
            ]
        };
        var occupied = new DesktopOrganizationRect(684, 16, 300, 400);

        bool succeeded = new DesktopOrganizationPlacementPlanner().TryAssignBounds(
            plan,
            new DesktopOrganizationRect(0, 0, 1000, 900),
            [occupied],
            300,
            400);

        Assert.True(succeeded);
        Assert.All(plan.Targets, target => Assert.NotNull(target.PlannedBounds));
        Assert.DoesNotContain(plan.Targets, target =>
            target.PlannedBounds!.Value.Intersects(occupied));
        Assert.False(plan.Targets[0].PlannedBounds!.Value.Intersects(
            plan.Targets[1].PlannedBounds!.Value));
    }

    [Fact]
    public void PlacementPlanner_UsesBestEffortPositionWhenDesktopIsFull()
    {
        var plan = new DesktopOrganizationPlan
        {
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetWidgetId = "new-widget",
                    CreatesWidget = true
                }
            ]
        };

        bool succeeded = new DesktopOrganizationPlacementPlanner().TryAssignBounds(
            plan,
            new DesktopOrganizationRect(0, 0, 1000, 900),
            [new DesktopOrganizationRect(0, 0, 1000, 900)],
            300,
            400);

        Assert.True(succeeded);
        DesktopOrganizationRect bounds = plan.Targets[0].PlannedBounds!.Value;
        Assert.Equal(684, bounds.X);
        Assert.Equal(16, bounds.Y);
        Assert.True(bounds.Right <= 1000 - DesktopOrganizationPlacementPlanner.DefaultEdgeMargin);
        Assert.True(bounds.Bottom <= 900 - DesktopOrganizationPlacementPlanner.DefaultEdgeMargin);
    }

    [Fact]
    public async Task Transaction_MovesBatchCreatesStableRulesAndSupportsUndo()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
        string sourceOne = Path.Combine(desktop, "one.pdf");
        string sourceTwo = Path.Combine(desktop, "two.pdf");
        File.WriteAllText(sourceOne, "one");
        File.WriteAllText(sourceTwo, "two");
        var classifier = new DesktopOrganizationClassifier();
        var scanner = new DesktopOrganizationScanner(classifier, () => desktop, () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan,
            storage,
            [],
            [],
            _ => "Documents");
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var fileService = new FileService();
        var recovery = new DesktopOrganizationRecoveryStore(
            Path.Combine(_root, "recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, fileService, recovery);
        var progressValues = new List<DesktopOrganizationProgress>();

        DesktopOrganizationExecutionResult result = await transaction.ExecuteAsync(
            plan,
            new InlineProgress<DesktopOrganizationProgress>(progressValues.Add));

        WidgetConfig widget = Assert.Single(result.CreatedWidgets);
        DesktopOrganizationRule rule = Assert.Single(settings.Settings.DesktopOrganizationRules);
        Assert.Equal(widget.Id, rule.TargetWidgetId);
        Assert.Equal(2, result.History.Items.Count);
        Assert.All(result.History.Items, item => Assert.True(File.Exists(item.DestinationPath)));
        Assert.False(File.Exists(sourceOne));
        Assert.False(File.Exists(sourceTwo));
        Assert.False(recovery.HasPendingJournal);
        Assert.Equal([1, 2], progressValues.Select(value => value.CompletedCount));
        Assert.All(progressValues, value => Assert.Equal(2, value.TotalCount));
        Assert.All(progressValues, value => Assert.False(string.IsNullOrWhiteSpace(value.TargetDisplayName)));

        var organizer = TestOrganizerServices.Create(settings, fileService, () => desktop);
        await organizer.UndoAsync(result.History.Id);

        Assert.True(File.Exists(sourceOne));
        Assert.True(File.Exists(sourceTwo));
        Assert.True(organizer.AutoOrganizationSuppressions.TryConsume(sourceOne));
        Assert.True(organizer.AutoOrganizationSuppressions.TryConsume(sourceTwo));
    }

    [Fact]
    public async Task Transaction_RetainsChangedItemAndContinuesTheRemainingBatch()
    {
        string desktop = Directory.CreateDirectory(
            Path.Combine(_root, "partial-desktop")).FullName;
        string storage = Directory.CreateDirectory(
            Path.Combine(_root, "partial-storage")).FullName;
        string retainedSource = Path.Combine(desktop, "changed.txt");
        string movedSource = Path.Combine(desktop, "stable.txt");
        File.WriteAllText(retainedSource, "before");
        File.WriteAllText(movedSource, "stable");
        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => desktop,
            () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan,
            storage,
            [],
            [],
            _ => "Documents");
        File.AppendAllText(retainedSource, "-changed-after-preview");
        var settings = new SettingsService(Path.Combine(_root, "partial-settings"));

        DesktopOrganizationExecutionResult result =
            await new DesktopOrganizationTransaction(
                settings,
                new FileService()).ExecuteAsync(plan);

        OrganizationHistoryItem moved = Assert.Single(result.History.Items);
        DesktopOrganizationRetainedItem retained = Assert.Single(result.RetainedItems);
        Assert.Equal("stable.txt", moved.Name);
        Assert.Equal("changed.txt", retained.Name);
        Assert.Equal(
            DesktopOrganizationRetentionReason.SourceChanged,
            retained.Reason);
        Assert.True(File.Exists(retainedSource));
        Assert.False(File.Exists(movedSource));
        Assert.True(File.Exists(moved.DestinationPath));
        Assert.Single(result.CreatedWidgets);
    }

    [Fact]
    public async Task Transaction_RetainsInUseItemAndContinuesTheRemainingBatch()
    {
        string desktop = Directory.CreateDirectory(
            Path.Combine(_root, "locked-desktop")).FullName;
        string storage = Directory.CreateDirectory(
            Path.Combine(_root, "locked-storage")).FullName;
        string lockedSource = Path.Combine(desktop, "locked.txt");
        string movedSource = Path.Combine(desktop, "available.txt");
        File.WriteAllText(lockedSource, "locked");
        File.WriteAllText(movedSource, "available");
        var scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => desktop,
            () => string.Empty);
        DesktopOrganizationScanResult scan = await scanner.ScanAsync();
        DesktopOrganizationPlan plan = new DesktopOrganizationPlanner(
            new DesktopOrganizationRuleResolver()).CreatePlan(
            scan,
            storage,
            [],
            [],
            _ => "Documents");
        var settings = new SettingsService(Path.Combine(_root, "locked-settings"));

        DesktopOrganizationExecutionResult result;
        using (File.Open(
                   lockedSource,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            result = await new DesktopOrganizationTransaction(
                settings,
                new FileService()).ExecuteAsync(plan);
        }

        Assert.Single(result.History.Items);
        DesktopOrganizationRetainedItem retained = Assert.Single(result.RetainedItems);
        Assert.Equal("locked.txt", retained.Name);
        Assert.Equal(DesktopOrganizationRetentionReason.InUse, retained.Reason);
        Assert.True(File.Exists(lockedSource));
        Assert.False(File.Exists(movedSource));
    }

    [Fact]
    public async Task Transaction_OptedInFolderMovesAndUndoRestoresItsContents()
    {
        string desktop = Directory.CreateDirectory(
            Path.Combine(_root, "folder-desktop")).FullName;
        string storage = Directory.CreateDirectory(
            Path.Combine(_root, "folder-storage")).FullName;
        string sourceFolder = Directory.CreateDirectory(
            Path.Combine(desktop, "Project")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "readme.txt"), "content");
        var directory = new DirectoryInfo(sourceFolder);
        var plan = new DesktopOrganizationPlan
        {
            DesktopPath = desktop,
            StorageRootPath = storage,
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = "category:Other",
                    TargetWidgetId = "folder-target",
                    CategoryId = DesktopOrganizationCategoryIds.Other,
                    SuggestedDisplayName = "Other",
                    TargetDirectoryPath = Path.Combine(storage, "Other"),
                    CreatesWidget = true,
                    Items =
                    [
                        new DesktopOrganizationFileSnapshot(
                            sourceFolder,
                            "Project",
                            string.Empty,
                            0,
                            directory.LastWriteTimeUtc,
                            DesktopOrganizationCategoryIds.Other,
                            null,
                            DesktopOrganizationExclusionReason.None,
                            IsDirectory: true)
                    ]
                }
            ]
        };
        var settings = new SettingsService(Path.Combine(_root, "folder-settings"));
        var fileService = new FileService();
        DesktopOrganizationExecutionResult result =
            await new DesktopOrganizationTransaction(settings, fileService)
                .ExecuteAsync(plan);

        string destinationFolder = Assert.Single(result.History.Items).DestinationPath;
        Assert.False(Directory.Exists(sourceFolder));
        Assert.True(File.Exists(Path.Combine(destinationFolder, "readme.txt")));

        await TestOrganizerServices.Create(settings, fileService, () => desktop)
            .UndoAsync(result.History.Id);

        Assert.True(File.Exists(Path.Combine(sourceFolder, "readme.txt")));
        Assert.False(Directory.Exists(destinationFolder));
    }

    [Fact]
    public async Task Transaction_NewTargetPersistsOnlyPrimaryCategoryAndNeverAutoBindsOther()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "merged-desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "merged-storage")).FullName;
        string documentPath = Path.Combine(desktop, "report.pdf");
        string imagePath = Path.Combine(desktop, "photo.webp");
        string designPath = Path.Combine(desktop, "proto.rp");
        File.WriteAllText(documentPath, "x");
        File.WriteAllText(imagePath, "x");
        File.WriteAllText(designPath, "x");
        var documentInfo = new FileInfo(documentPath);
        var imageInfo = new FileInfo(imagePath);
        var designInfo = new FileInfo(designPath);

        var plan = new DesktopOrganizationPlan
        {
            DesktopPath = desktop,
            StorageRootPath = storage,
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetWidgetId = "design-target",
                    CategoryId = DesktopOrganizationCategoryIds.Design,
                    SuggestedDisplayName = "Design",
                    TargetDirectoryPath = Path.Combine(storage, "Design"),
                    CreatesWidget = true,
                    Items =
                    [
                        Snapshot("proto.rp", DesktopOrganizationCategoryIds.Design, null, designPath, designInfo.Length, designInfo.LastWriteTimeUtc)
                    ]
                },
                new DesktopOrganizationTargetPlan
                {
                    TargetWidgetId = "merged-target",
                    CategoryId = DesktopOrganizationCategoryIds.Other,
                    SuggestedDisplayName = "Other",
                    TargetDirectoryPath = Path.Combine(storage, "Other"),
                    CreatesWidget = true,
                    Items =
                    [
                        Snapshot("report.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf, documentPath, documentInfo.Length, documentInfo.LastWriteTimeUtc),
                        Snapshot("photo.webp", DesktopOrganizationCategoryIds.Images, null, imagePath, imageInfo.Length, imageInfo.LastWriteTimeUtc)
                    ]
                }
            ]
        };
        var settings = new SettingsService(Path.Combine(_root, "merged-settings"));
        var transaction = new DesktopOrganizationTransaction(settings, new FileService());

        await transaction.ExecuteAsync(plan);

        DesktopOrganizationRule rule = Assert.Single(settings.Settings.DesktopOrganizationRules);
        Assert.Equal("design-target", rule.TargetWidgetId);
        Assert.Equal([DesktopOrganizationCategoryIds.Design], rule.CategoryIds);
    }

    [Fact]
    public async Task Settings_KeepsAutoOrganizationWhenUserDisablesLastRule()
    {
        // Only a truly deleted target (a widget that entered DeletedWidgetIds)
        // may close the master switch programmatically. A user disabling the
        // last rule is not a deletion: the switch and baseline stay as they
        // are, and re-enabling the rule resumes organization (feedback
        // 323/370/405 contract).
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "auto-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "auto-settings"));
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents],
            IsEnabled = false
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        DateTimeOffset baseline = DateTimeOffset.UtcNow;
        settings.Settings.DesktopAutoOrganizationBaselineUtc = baseline;

        await settings.SaveAsync();

        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.Equal(baseline, settings.Settings.DesktopAutoOrganizationBaselineUtc);
    }

    [Fact]
    public async Task Settings_KeepsAutoOrganizationWhenMappedPathIsNotBackfilledYet()
    {
        // Boot race: load-time normalize runs before WidgetViewModel backfills
        // MappedFolderPath. The rule must be treated as pending there - the
        // switch stays on, the rule stays enabled, the baseline is kept, and
        // nothing is disabled on disk (feedback 323/370/405).
        string settingsDir = Path.Combine(_root, "pending-settings");
        var settings = new SettingsService(settingsDir);
        WidgetConfig widget = new()
        {
            Id = "pending-widget",
            Name = "Documents",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = "",
            FollowsDefaultStoragePath = false
        };
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        DateTimeOffset baseline = DateTimeOffset.UtcNow;
        settings.Settings.DesktopAutoOrganizationBaselineUtc = baseline;

        await settings.SaveAsync();

        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(
            settings.Settings.DesktopOrganizationRules.Single(rule =>
                rule.TargetWidgetId == widget.Id).IsEnabled,
            "a not-yet-backfilled mapped path must suspend the rule, not disable it");
        Assert.Equal(baseline, settings.Settings.DesktopAutoOrganizationBaselineUtc);

        var reloaded = new SettingsService(settingsDir);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(
            reloaded.Settings.DesktopOrganizationRules.Single(rule =>
                rule.TargetWidgetId == widget.Id).IsEnabled);
        Assert.NotNull(reloaded.Settings.DesktopAutoOrganizationBaselineUtc);
    }

    [Fact]
    public async Task Settings_BackfillsDefaultPathWidgetAndKeepsItsRulePending()
    {
        // A default-storage widget's path backfill happens later in the same
        // normalize pass than the rule validity check: at decision time the
        // path is still empty, so the rule must survive the same pass as
        // pending.
        var settings = new SettingsService(Path.Combine(_root, "backfill-settings"));
        WidgetConfig widget = new()
        {
            Id = "backfill-widget",
            Name = "Documents",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = "",
            FollowsDefaultStoragePath = true,
            ManagedFolderName = "Documents"
        };
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;

        await settings.SaveAsync();

        Assert.NotEmpty(settings.Settings.Widgets[0].MappedFolderPath);
        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(settings.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task EnabledRulesSurviveRestartOnTheAdoptedLayoutStore()
    {
        // Steady state after the device-layer split: settings.json holds the
        // rules but no widgets (the writer strips layout keys once the
        // layout file is authoritative). The organizer normalize must judge
        // rule targets only against the merged widget list — running it
        // before the layout merge treated every enabled rule's target as
        // deleted and disabled the rule plus the master switch on every
        // boot (feedback #553 family). This pin doubles as the load-order
        // pin: moving the organizer normalize back ahead of the merge turns
        // the second boot's assertions red.
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "restart-rule")).FullName;
        string settingsDir = Path.Combine(_root, "restart-settings");

        var first = new SettingsService(settingsDir);
        await first.LoadAsync();
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        first.Settings.Widgets.Add(widget);
        first.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        first.Settings.DesktopAutoOrganizationEnabled = true;
        await first.SaveAsync();

        // Second boot reads the stripped settings.json plus the
        // authoritative widget-layout.json — the state every real restart
        // sees after adoption.
        var second = new SettingsService(settingsDir);
        await second.LoadAsync();
        Assert.True(second.Settings.DesktopAutoOrganizationEnabled);
        Assert.Single(second.Settings.DesktopOrganizationRules);
        Assert.True(second.Settings.DesktopOrganizationRules[0].IsEnabled);

        await second.SaveAsync();
        Assert.True(second.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(second.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task EnabledRulesSurviveFirstBootFromLegacySettings()
    {
        // The upgrade-path shape: settings.json still carries the legacy
        // widgets key (no widget-layout.json yet — adoption happens during
        // this very load). The organizer normalize must judge the rules
        // against the adopted list and keep them enabled; the pre-merge
        // widget list is exactly what the legacy key provides, so this pin
        // guards the DefaultMissing adoption branch of the merged normalize.
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "legacy-first-boot")).FullName;
        string settingsDir = Path.Combine(_root, "legacy-first-settings");

        var legacy = new SettingsService(settingsDir);
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        legacy.Settings.Widgets.Add(widget);
        legacy.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        legacy.Settings.DesktopAutoOrganizationEnabled = true;
        // Saving before any load keeps the layout keys inside settings.json
        // (the store is not authoritative yet) — the legacy on-disk shape.
        await legacy.SaveAsync();

        var upgraded = new SettingsService(settingsDir);
        await upgraded.LoadAsync();
        Assert.True(upgraded.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(upgraded.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task EnabledRulesSurviveSessionWithCorruptLayoutStore()
    {
        // The untrusted-topology branch: settings.json is stripped (rules
        // only) and BOTH layout files are corrupt. The load falls to
        // DefaultAfterFailure, the merged flag stays false, and neither the
        // load-time nor the save-time organizer normalize may disable rules
        // against the untrusted (empty) widget list.
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "corrupt-layout")).FullName;
        string settingsDir = Path.Combine(_root, "corrupt-layout-settings");

        var first = new SettingsService(settingsDir);
        await first.LoadAsync();
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        first.Settings.Widgets.Add(widget);
        first.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        first.Settings.DesktopAutoOrganizationEnabled = true;
        await first.SaveAsync();

        // Corrupt the durable pair so the second boot cannot recover it.
        string layoutPath = Path.Combine(settingsDir, "widget-layout.json");
        string backupPath = layoutPath + ".bak";
        Assert.True(File.Exists(layoutPath));
        await File.WriteAllTextAsync(layoutPath, "{ not json");
        if (File.Exists(backupPath))
        {
            await File.WriteAllTextAsync(backupPath, "{ not json");
        }

        var second = new SettingsService(settingsDir);
        await second.LoadAsync();
        Assert.True(second.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(second.Settings.DesktopOrganizationRules[0].IsEnabled);

        // Saves inside the untrusted session must keep the rules too.
        await second.SaveAsync();
        Assert.True(second.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(second.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task Settings_ClosesAutoOrganizationAndKeepsBaselineWhenLastTargetIsDeleted()
    {
        // A truly deleted target (widget in DeletedWidgetIds) remains the only
        // path that may close the master switch automatically, but the
        // baseline is preserved: the watcher reconciles with
        // BaselineUtc ?? now, and clearing it would widen the organization
        // window after the feature is turned back on (feedback 323/370/405
        // contract).
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "deleted-target")).FullName;
        string settingsDir = Path.Combine(_root, "deleted-settings");
        var settings = new SettingsService(settingsDir);
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;
        DateTimeOffset baseline = DateTimeOffset.UtcNow;
        settings.Settings.DesktopAutoOrganizationBaselineUtc = baseline;

        settings.RemoveWidget(widget.Id);
        await settings.SaveAsync();

        Assert.False(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.Equal(baseline, settings.Settings.DesktopAutoOrganizationBaselineUtc);
        var reloaded = new SettingsService(settingsDir);
        await reloaded.LoadAsync();
        Assert.False(reloaded.Settings.DesktopAutoOrganizationEnabled);
        Assert.Equal(baseline, reloaded.Settings.DesktopAutoOrganizationBaselineUtc);
        Assert.False(reloaded.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task Settings_DisablesOnlyTheDeletedTargetsRuleAndKeepsSwitchAlive()
    {
        string survivorFolder = Directory.CreateDirectory(
            Path.Combine(_root, "survivor-target")).FullName;
        string removedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "removed-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "partial-settings"));
        WidgetConfig survivor = CreateWidget("Images", survivorFolder);
        WidgetConfig removed = CreateWidget("Documents", removedFolder);
        settings.Settings.Widgets.Add(survivor);
        settings.Settings.Widgets.Add(removed);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = survivor.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Images]
        });
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = removed.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;

        settings.RemoveWidget(removed.Id);
        await settings.SaveAsync();

        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(settings.Settings.DesktopOrganizationRules.Single(rule =>
            rule.TargetWidgetId == survivor.Id).IsEnabled);
        Assert.False(settings.Settings.DesktopOrganizationRules.Single(rule =>
            rule.TargetWidgetId == removed.Id).IsEnabled);
    }

    [Fact]
    public async Task Settings_KeepsRulePendingWhenTargetWidgetIsDisabled()
    {
        // A disabled widget is a recoverable state: re-enabling the widget
        // resumes organization, so its rule must not be killed.
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_root, "disabled-target")).FullName;
        var settings = new SettingsService(Path.Combine(_root, "disabled-settings"));
        WidgetConfig widget = CreateWidget("Documents", mappedFolder);
        widget.IsDisabled = true;
        settings.Settings.Widgets.Add(widget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = widget.Id,
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.Settings.DesktopAutoOrganizationEnabled = true;

        await settings.SaveAsync();

        Assert.True(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.True(settings.Settings.DesktopOrganizationRules[0].IsEnabled);
    }

    [Fact]
    public async Task Recovery_RestoresMovedFilesAndRemovesUncommittedWidgets()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "recovery-desktop")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(_root, "recovery-target")).FullName;
        string sourcePath = Path.Combine(desktop, "report.pdf");
        string destinationPath = Path.Combine(target, "report.pdf");
        File.WriteAllText(destinationPath, "content");
        var settings = new SettingsService(Path.Combine(_root, "recovery-settings"));
        WidgetConfig temporaryWidget = CreateWidget("Temporary", target);
        temporaryWidget.Id = "temporary";
        settings.Settings.Widgets.Add(temporaryWidget);
        settings.Settings.DesktopOrganizationRules.Add(new DesktopOrganizationRule
        {
            TargetWidgetId = "temporary",
            CategoryIds = [DesktopOrganizationCategoryIds.Documents]
        });
        settings.OrganizationHistory.Entries.Add(new OrganizationHistoryEntry
        {
            Id = "transaction",
            ActionType = OrganizationActionType.DesktopOrganization
        });
        var store = new DesktopOrganizationRecoveryStore(
            Path.Combine(_root, "pending-recovery.json"));
        var item = new DesktopOrganizationRecoveryItem
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            TargetWidgetId = "temporary",
            Completed = true
        };
        // A real crash after RecordCompleted persists the destination object
        // identity alongside the receipt.
        DesktopOrganizationTransaction.RecordDestinationIdentity(item, destinationPath);
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "transaction",
            CreatedWidgetIds = ["temporary"],
            Items = [item]
        });

        int restored = await new DesktopOrganizationTransaction(
            settings,
            new FileService(),
            store).RecoverPendingAsync();

        Assert.Equal(1, restored);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
        Assert.DoesNotContain(settings.Settings.Widgets, widget => widget.Id == "temporary");
        Assert.DoesNotContain(settings.Settings.DesktopOrganizationRules, rule => rule.TargetWidgetId == "temporary");
        Assert.DoesNotContain(settings.OrganizationHistory.Entries, entry => entry.Id == "transaction");
        Assert.False(store.HasPendingJournal);

    }

    [Fact]
    public async Task RecoverPendingAsync_KeepsItemsWithoutRecordedIdentity()
    {
        // Items without a destination identity (legacy journals, or the rare
        // crash between the physical move and the receipt write) have no
        // automatic restore authority: the file stays at the destination and
        // the journal keeps the record.
        string root = Directory.CreateDirectory(Path.Combine(_root, "identity-none")).FullName;
        string desktopPath = Path.Combine(root, "desktop");
        string storagePath = Path.Combine(root, "storage");
        Directory.CreateDirectory(desktopPath);
        Directory.CreateDirectory(storagePath);
        string destinationPath = Path.Combine(storagePath, "moved.txt");
        File.WriteAllText(destinationPath, "already moved");
        var settings = new SettingsService(Path.Combine(root, "settings"));
        var store = new DesktopOrganizationRecoveryStore(
            Path.Combine(root, "pending-recovery.json"));
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "transaction",
            CreatedWidgetIds = ["temporary"],
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = Path.Combine(desktopPath, "moved.txt"),
                    DestinationPath = destinationPath,
                    TargetWidgetId = "temporary",
                    Completed = true,
                    DestinationIdentity = null
                }
            ]
        });

        int restored = await new DesktopOrganizationTransaction(
            settings,
            new FileService(),
            store).RecoverPendingAsync();

        Assert.Equal(0, restored);
        Assert.True(
            File.Exists(destinationPath),
            "no identity means no automatic move");
    }

    [Fact]
    public async Task Undo_PartialFailureRetryMovesOnlyTheRemainingItem()
    {
        // A retry after a partial undo must not touch the already-restored
        // item: its undo target is gone and re-running it would relocate
        // whatever reappeared at that path.
        string root = Directory.CreateDirectory(Path.Combine(_root, "undo-retry")).FullName;
        string desktopPath = Path.Combine(root, "desktop");
        string widgetPath = Path.Combine(root, "widget");
        Directory.CreateDirectory(desktopPath);
        Directory.CreateDirectory(widgetPath);
        string firstSource = Path.Combine(desktopPath, "first.txt");
        string secondSource = Path.Combine(desktopPath, "second.txt");
        File.WriteAllText(firstSource, "first");
        File.WriteAllText(secondSource, "second");
        // The widget-side objects must exist before their identities are
        // captured as undo receipts (a real move records them on arrival).
        File.WriteAllText(Path.Combine(widgetPath, "first.txt"), "first");
        File.WriteAllText(Path.Combine(widgetPath, "second.txt"), "second");
        var settings = new SettingsService(Path.Combine(root, "settings"));
        var history = new OrganizationHistoryEntry
        {
            Id = "undo-retry",
            ActionType = OrganizationActionType.MoveBackToDesktop,
            CanUndo = true,
            Items =
            [
                new OrganizationHistoryItem
                {
                    Name = "first.txt",
                    SourcePath = firstSource,
                    DestinationPath = Path.Combine(widgetPath, "first.txt"),
                    DestinationIdentity = FileService.CaptureUndoReceiptIdentity(
                        Path.Combine(widgetPath, "first.txt"))
                },
                new OrganizationHistoryItem
                {
                    Name = "second.txt",
                    SourcePath = secondSource,
                    DestinationPath = Path.Combine(widgetPath, "second.txt"),
                    DestinationIdentity = FileService.CaptureUndoReceiptIdentity(
                        Path.Combine(widgetPath, "second.txt"))
                }
            ]
        };
        settings.OrganizationHistory.Entries.Add(history);
        await settings.SaveAsync(notifySubscribers: false);
        // Simulate the state after a partial undo: the first item was already
        // restored to the desktop (its data still sits at the widget path,
        // which is exactly the trap — moving it again would duplicate), the
        // second is still in the widget.
        File.WriteAllText(firstSource, "first");
        history.Items[0].IsRestored = true;
        history.Items[0].RestoredPath = firstSource;
        await settings.SaveAsync(notifySubscribers: false);

        var service = TestOrganizerServices.Create(
            settings,
            new FileService(),
            () => desktopPath);
        await service.UndoAsync("undo-retry");

        Assert.True(history.IsUndone);
        Assert.Equal(
            "second",
            await File.ReadAllTextAsync(secondSource));
        Assert.False(
            File.Exists(Path.Combine(desktopPath, "first (2).txt")),
            "the retry must never duplicate the restored item");
        Assert.Single(Directory.GetFiles(desktopPath, "first*.txt"));
    }

    [Fact]
    public async Task Undo_PersistsRestoredFlagsToHistoryStoreAcrossReload()
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, "undo-persist")).FullName;
        string desktopPath = Directory.CreateDirectory(Path.Combine(root, "desktop")).FullName;
        string widgetPath = Directory.CreateDirectory(Path.Combine(root, "widget")).FullName;
        string settingsDir = Path.Combine(root, "settings");
        string movedFile = Path.Combine(widgetPath, "note.txt");
        File.WriteAllText(movedFile, "hello");

        var settings = new SettingsService(settingsDir);
        await settings.LoadAsync();
        var entry = new OrganizationHistoryEntry
        {
            Id = "persist-undo-entry",
            ActionType = OrganizationActionType.MoveBackToDesktop,
            CanUndo = true,
            Items =
            [
                new OrganizationHistoryItem
                {
                    Name = "note.txt",
                    SourcePath = Path.Combine(desktopPath, "note.txt"),
                    DestinationPath = movedFile,
                    DestinationIdentity = FileService.CaptureUndoReceiptIdentity(movedFile)
                }
            ]
        };
        settings.OrganizationHistory.Entries.Add(entry);
        await settings.OrganizationHistory.SaveCheckedAsync();

        var organizer = TestOrganizerServices.Create(settings, new FileService(), () => desktopPath);
        await organizer.UndoAsync(entry.Id);

        var reloaded = new SettingsService(settingsDir);
        await reloaded.LoadAsync();
        OrganizationHistoryEntry reloadedEntry = Assert.Single(reloaded.OrganizationHistory.Entries);
        Assert.True(reloadedEntry.IsUndone);
        OrganizationHistoryItem reloadedItem = Assert.Single(reloadedEntry.Items);
        Assert.True(reloadedItem.IsRestored);
        Assert.Equal(Path.Combine(desktopPath, "note.txt"), reloadedItem.RestoredPath);
    }

    [Fact]
    public async Task RecoverPendingAsync_RestoresShortcutInterruptedDuringAtomicRebase()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "lnk-recovery-desktop")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(_root, "lnk-recovery-target")).FullName;
        string sourcePath = Path.Combine(desktop, "App.lnk");
        string destinationPath = Path.Combine(target, "App.lnk");
        string orphanTempPath = destinationPath + ".orphan.deskbox-tmp";

        byte[] initialBytes = new byte[96];
        BitConverter.GetBytes(0x0000004Cu).CopyTo(initialBytes, 0);
        File.WriteAllBytes(destinationPath, initialBytes);

        var settings = new SettingsService(Path.Combine(_root, "lnk-recovery-settings"));
        WidgetConfig temporaryWidget = CreateWidget("Shortcuts", target);
        temporaryWidget.Id = "temp-shortcuts";
        settings.Settings.Widgets.Add(temporaryWidget);

        var item = new DesktopOrganizationRecoveryItem
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            TargetWidgetId = "temp-shortcuts",
            Completed = true,
            ShortcutRebaseInProgress = true,
            Size = initialBytes.Length,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(destinationPath)
        };
        DesktopOrganizationTransaction.RecordDestinationIdentity(item, destinationPath);

        // Simulate atomic File.Replace completing right before a crash: the .lnk
        // now has a new file index, updated length, and updated timestamp, plus
        // an orphaned .deskbox-tmp sibling.
        string replacementTemp = destinationPath + ".commit.deskbox-tmp";
        byte[] rebasedBytes = new byte[128];
        BitConverter.GetBytes(0x0000004Cu).CopyTo(rebasedBytes, 0);
        File.WriteAllBytes(replacementTemp, rebasedBytes);
        File.Replace(replacementTemp, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        File.SetLastWriteTimeUtc(destinationPath, item.LastWriteTimeUtc!.Value.AddMinutes(5));
        File.WriteAllBytes(orphanTempPath, [1, 2, 3]);

        var store = new DesktopOrganizationRecoveryStore(Path.Combine(_root, "lnk-recovery.json"));
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "lnk-tx",
            CreatedWidgetIds = ["temp-shortcuts"],
            Items = [item]
        });

        int restored = await new DesktopOrganizationTransaction(settings, new FileService(), store)
            .RecoverPendingAsync();

        Assert.Equal(1, restored);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
        Assert.False(File.Exists(orphanTempPath));
        Assert.False(store.HasPendingJournal);
        Assert.DoesNotContain(settings.Settings.Widgets, widget => widget.Id == "temp-shortcuts");
    }

    private DesktopOrganizationFileSnapshot Snapshot(
        string name,
        string category,
        string? subtype,
        string? sourcePath = null,
        long size = 1,
        DateTime? lastWriteTimeUtc = null)
    {
        return new DesktopOrganizationFileSnapshot(
            sourcePath ?? Path.Combine(_root, name),
            name,
            DesktopOrganizationClassifier.NormalizeExtension(Path.GetExtension(name)),
            size,
            lastWriteTimeUtc ?? DateTime.UtcNow,
            category,
            subtype,
            DesktopOrganizationExclusionReason.None);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static WidgetConfig CreateWidget(string name, string path) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        WidgetKind = WidgetKind.File,
        MappedFolderPath = path,
        FollowsDefaultStoragePath = true,
        ManagedFolderName = Path.GetFileName(path)
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
