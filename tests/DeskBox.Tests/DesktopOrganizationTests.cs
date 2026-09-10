using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("report.pdf", DesktopOrganizationCategoryIds.Documents, DesktopOrganizationSubtypeIds.Pdf)]
    [InlineData("photo.webp", DesktopOrganizationCategoryIds.Images, null)]
    [InlineData("movie.mp4", DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Video)]
    [InlineData("music.flac", DesktopOrganizationCategoryIds.Media, DesktopOrganizationSubtypeIds.Audio)]
    [InlineData("app.lnk", DesktopOrganizationCategoryIds.Shortcuts, null)]
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
    public void Planner_ReusesRuleTargetAndLimitsNewWidgetsToFour()
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
        Assert.True(plan.NewWidgetCount <= DesktopOrganizationPlanner.MaxNewWidgetCount);
        Assert.Equal(items.Count, plan.EligibleItemCount);
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

        var organizer = new OrganizerService(settings, fileService, () => desktop);
        await organizer.UndoAsync(result.History.Id);

        Assert.True(File.Exists(sourceOne));
        Assert.True(File.Exists(sourceTwo));
        Assert.True(organizer.AutoOrganizationSuppressions.TryConsume(sourceOne));
        Assert.True(organizer.AutoOrganizationSuppressions.TryConsume(sourceTwo));
    }

    [Fact]
    public async Task Transaction_ProgressCountsOnlyCompletedItems()
    {
        string desktop = Directory.CreateDirectory(
            Path.Combine(_root, "progress-desktop")).FullName;
        string storage = Directory.CreateDirectory(
            Path.Combine(_root, "progress-storage")).FullName;
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
        var settings = new SettingsService(Path.Combine(_root, "progress-settings"));
        var progressValues = new List<DesktopOrganizationProgress>();

        DesktopOrganizationExecutionResult result = await new DesktopOrganizationTransaction(
                settings,
                new FileService())
            .ExecuteAsync(
                plan,
                new InlineProgress<DesktopOrganizationProgress>(progressValues.Add));

        // Two items planned, one retained by revalidation. One personal
        // item is transferred per batch, so the two reports must never count
        // the retained item as completed; the running total ends at 1.
        Assert.Single(result.RetainedItems);
        Assert.Equal(2, progressValues.Count);
        Assert.All(progressValues, value => Assert.Equal(2, value.TotalCount));
        Assert.Equal(1, progressValues[^1].CompletedCount);
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

        await new OrganizerService(settings, fileService, () => desktop)
            .UndoAsync(result.History.Id);

        Assert.True(File.Exists(Path.Combine(sourceFolder, "readme.txt")));
        Assert.False(Directory.Exists(destinationFolder));
    }

    [Fact]
    public async Task Transaction_PersistsAllSourceCategoriesForMergedTarget()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_root, "merged-desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_root, "merged-storage")).FullName;
        string documentPath = Path.Combine(desktop, "report.pdf");
        string imagePath = Path.Combine(desktop, "photo.webp");
        File.WriteAllText(documentPath, "x");
        File.WriteAllText(imagePath, "x");
        var documentInfo = new FileInfo(documentPath);
        var imageInfo = new FileInfo(imagePath);

        var plan = new DesktopOrganizationPlan
        {
            DesktopPath = desktop,
            StorageRootPath = storage,
            Targets =
            [
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
        Assert.Equal(
            [DesktopOrganizationCategoryIds.Documents, DesktopOrganizationCategoryIds.Images],
            rule.CategoryIds);
    }

    [Fact]
    public async Task Settings_DisablesAutomaticOrganizationWhenLastEffectiveRuleIsRemoved()
    {
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
        settings.Settings.DesktopAutoOrganizationBaselineUtc = DateTimeOffset.UtcNow;

        await settings.SaveAsync();

        Assert.False(settings.Settings.DesktopAutoOrganizationEnabled);
        Assert.Null(settings.Settings.DesktopAutoOrganizationBaselineUtc);
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
        settings.Settings.RecentOrganizationHistory.Add(new OrganizationHistoryEntry
        {
            Id = "transaction",
            ActionType = OrganizationActionType.DesktopOrganization
        });
        var store = new DesktopOrganizationRecoveryStore(
            Path.Combine(_root, "pending-recovery.json"));
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "transaction",
            CreatedWidgetIds = ["temporary"],
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    TargetWidgetId = "temporary",
                    Completed = false
                }
            ]
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
        Assert.DoesNotContain(settings.Settings.RecentOrganizationHistory, entry => entry.Id == "transaction");
        Assert.False(store.HasPendingJournal);

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
