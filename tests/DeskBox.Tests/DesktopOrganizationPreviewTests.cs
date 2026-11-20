using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Windows.Foundation;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationPreviewTests
{
    [Fact]
    public void SummaryOnlyCountsSelectedSourcesAndIncludesManualExclusions()
    {
        var personal = Item("personal.txt");
        var publicItem = Item("public.lnk") with { SourceScope = DesktopOrganizationSourceScope.Public };
        var folder = Item("folder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var plan = new DesktopOrganizationPlan
        {
            SourceItems = [personal, publicItem, folder],
            Targets = [Target("docs", [personal])],
            ExcludedItems = [publicItem with { ExclusionReason = DesktopOrganizationExclusionReason.SourceNotSelected }, folder]
        };
        var selections = new Dictionary<string, DesktopOrganizationTargetSelection>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, excluded);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(1, summary.RetainedCount);
        excluded.Add(personal.SourcePath.ToUpperInvariant());
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, excluded);
        Assert.Equal(0, summary.SelectedCount);
        Assert.Equal(2, summary.RetainedCount);
        Assert.Equal(0, summary.DestinationCount);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void SummaryScopeAndSelectionAlwaysUseTheSamePopulation(bool personal, bool shared, int count)
    {
        var items = new List<DesktopOrganizationFileSnapshot>
        { Item("a.txt"), Item("b.txt") with { SourceScope = DesktopOrganizationSourceScope.Public } };
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = personal, IncludePublicDesktop = shared,
            SourceItems = items, Targets = [Target("all", items)]
        };
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, new Dictionary<string, DesktopOrganizationTargetSelection>(), new HashSet<string>());
        Assert.Equal(count, summary.TotalCount);
        Assert.Equal(count, summary.SelectedCount);
        Assert.Equal(0, summary.RetainedCount);
    }

    [Fact]
    public void RescanKeepsChoicesRemovesMissingPathsAndRequiresReviewForNewFiles()
    {
        var excluded = Item("excluded.txt");
        var folder = Item("folder") with { ExclusionReason = DesktopOrganizationExclusionReason.Folder, IsDirectory = true };
        var removed = Item("removed.txt");
        var added = Item("new.txt");
        var result = DesktopOrganizationPreviewReconciliation.Calculate(
            [excluded, folder, removed], [excluded, folder, added],
            [excluded.SourcePath.ToUpperInvariant(), removed.SourcePath], [folder.SourcePath], []);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.True(result.ExcludedPaths.SetEquals([excluded.SourcePath, added.SourcePath]));
        Assert.True(result.OptionalPaths.SetEquals([folder.SourcePath]));
        Assert.True(result.NewPaths.SetEquals([added.SourcePath]));
        var again = DesktopOrganizationPreviewReconciliation.Calculate(
            [excluded, folder, added], [excluded, folder, added],
            result.ExcludedPaths, result.OptionalPaths, result.NewPaths);
        Assert.Equal(0, again.AddedCount);
        Assert.Contains(added.SourcePath, again.NewPaths);
        Assert.Contains(added.SourcePath, again.ExcludedPaths);
    }

    [Fact]
    public void RescanDoesNotCarryAnOptInAcrossAFileBecomingProtected()
    {
        var folder = Item("folder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var protectedFolder = folder with { ExclusionReason = DesktopOrganizationExclusionReason.ReparsePoint };
        var result = DesktopOrganizationPreviewReconciliation.Calculate([folder], [protectedFolder], [], [folder.SourcePath], []);
        Assert.Empty(result.OptionalPaths);
        Assert.Empty(result.NewPaths);
    }

    [Fact]
    public void TwoCategoriesSentToSameExistingWidgetCountAsOneDestination()
    {
        var plan = new DesktopOrganizationPlan
        {
            Targets = [Target("docs", [Item("a.txt")]), Target("apps", [Item("b.lnk")])]
        };
        var selections = plan.Targets.ToDictionary(target => target.SourceBucketId, target => new DesktopOrganizationTargetSelection
        {
            SourceBucketId = target.SourceBucketId, IsSelected = true,
            DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget, ExistingWidgetId = "work"
        });
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(2, summary.SelectedCount);
        Assert.Equal(1, summary.DestinationCount);
        Assert.Equal(0, summary.NewDestinationCount);
        selections["apps"].DestinationMode = DesktopOrganizationDestinationMode.Dynamic;
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(2, summary.DestinationCount);
        Assert.Equal(1, summary.NewDestinationCount);
        selections["apps"].IsSelected = false;
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(1, summary.RetainedCount);
    }

    [Fact]
    public void PreviewGeometryUsesConfiguredIconTextSpacingAndDestinationOverride()
    {
        var settings = new AppSettings { IconSize = 30, TextSize = 13, FileNameLineCount = 2 };
        var original = FileWidgetIconLayout.Calculate(settings);
        Assert.Equal(30, original.ImageSize);
        Assert.Equal(13, original.LabelFontSize);
        Assert.Equal(2, original.LabelMaxLines);
        Assert.True(original.ShowLabel);
        var overridden = FileWidgetIconLayout.Calculate(settings, iconSizeOverride: 48);
        Assert.Equal(48, overridden.ImageSize);
        Assert.Equal(30, settings.IconSize);
        Assert.True(overridden.TileWidth >= original.TileWidth);
        settings.HorizontalSpacingScale = SettingsService.MaxSpacingScale;
        settings.VerticalSpacingScale = SettingsService.MaxSpacingScale;
        settings.FileNameWidthScale = SettingsService.MaxSpacingScale;
        var spacious = FileWidgetIconLayout.Calculate(settings);
        Assert.True(spacious.CellWidth > original.CellWidth);
        Assert.True(spacious.CellHeight > original.CellHeight);
        Assert.True(spacious.LabelMaxWidth > original.LabelMaxWidth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void ConfiguredNameLinesFitAtSystemTextScale(double textScale)
    {
        var settings = new AppSettings { IconSize = 48, TextSize = 14, FileNameLineCount = 2, VerticalSpacingScale = 0 };
        var twoLines = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        double contentHeight = twoLines.TilePadding.Top + twoLines.TilePadding.Bottom + twoLines.ImageSize +
            twoLines.ContentSpacing + Math.Ceiling(14 * 1.4 * textScale) * 2;
        Assert.True(twoLines.TileHeight >= contentHeight);
        settings.FileNameLineCount = 1;
        var oneLine = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        Assert.True(oneLine.TileHeight < twoLines.TileHeight);
        settings.FileNameLineCount = SettingsService.HiddenFileNameLineCount;
        var hidden = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        Assert.False(hidden.ShowLabel);
        Assert.True(hidden.TileHeight < oneLine.TileHeight);
    }

    private static DesktopOrganizationFileSnapshot Item(string name) => new(
        "C:\\Desktop\\" + name, name, Path.GetExtension(name), 12, DateTime.UnixEpoch,
        DesktopOrganizationCategoryIds.Documents, null, DesktopOrganizationExclusionReason.None);

    private static DesktopOrganizationTargetPlan Target(string id, List<DesktopOrganizationFileSnapshot> items) => new()
    {
        SourceBucketId = id, TargetWidgetId = id, CreatesWidget = true, Items = items
    };

    [Fact]
    public void WorkbenchSession_DoesNotMutateBasePlanAcrossEditsAndReset()
    {
        var doc = Item("doc.txt");
        var img = Item("pic.png") with { CategoryId = DesktopOrganizationCategoryIds.Images };
        var folder = Item("Projects") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var basePlan = BuildSessionPlan([doc, img, folder], Array.Empty<string>());
        var session = CreateWorkbenchSession();

        session.SetInitialPlan(basePlan);
        session.ExcludeItems([doc.SourcePath]);
        session.IncludeItems([folder.SourcePath]);
        var customBox = session.CreateCustomBox([img.SourcePath], requestedTitle: "Media Drop");
        Assert.NotNull(customBox);
        Assert.True(session.RenameBox(customBox.WidgetId, "Renamed Media"));
        session.SetTargetDestinationSelection("docs", new DesktopOrganizationTargetSelection
        {
            SourceBucketId = "docs",
            DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget,
            ExistingWidgetId = "existing-1"
        });

        Assert.Single(basePlan.Targets.First(t => t.SourceBucketId == "docs").Items);
        Assert.Single(basePlan.Targets.First(t => t.SourceBucketId == "images").Items);
        Assert.Single(basePlan.ExcludedItems);

        session.ResetCustomizations();

        Assert.False(session.HasActiveUserCustomizations);
        Assert.Equal(2, session.EffectivePlan!.EligibleItemCount);
        Assert.Contains(session.EffectivePlan.ExcludedItems, item => item.SourcePath == folder.SourcePath);
    }

    [Fact]
    public void WorkbenchSession_ExcludingOptedInItemRemovesOptInWithoutPollutingExclusions()
    {
        var doc = Item("doc.txt");
        var folder = Item("WorkFolder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var large = Item("archive.zip") with { ExclusionReason = DesktopOrganizationExclusionReason.SlowItem, Size = 200_000_000 };
        var capped = Item("extra.txt") with { ExclusionReason = DesktopOrganizationExclusionReason.BatchLimit };
        var basePlan = BuildSessionPlan([doc, folder, large, capped], Array.Empty<string>());
        var session = CreateWorkbenchSession();
        session.SetInitialPlan(basePlan);

        Assert.Equal(3, session.IncludeItems([folder.SourcePath, large.SourcePath, capped.SourcePath]).MovedCount);
        Assert.True(session.HasActiveUserCustomizations);
        Assert.Equal(3, session.OptionalIncludedPaths.Count);

        Assert.Equal(3, session.ExcludeItems([folder.SourcePath, large.SourcePath, capped.SourcePath]).MovedCount);
        Assert.Empty(session.OptionalIncludedPaths);
        Assert.Empty(session.ExcludedSourcePaths);
        Assert.False(session.HasActiveUserCustomizations);
        Assert.Contains(session.EffectivePlan!.ExcludedItems, item => item.SourcePath == folder.SourcePath && item.ExclusionReason == DesktopOrganizationExclusionReason.Folder);
    }

    [Fact]
    public void WorkbenchSession_RestoreManualExclusionsKeepsOptInEligibleFoldersRetained()
    {
        var doc = Item("doc.txt");
        var folder = Item("UntouchedFolder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var basePlan = BuildSessionPlan([doc, folder], Array.Empty<string>());
        var session = CreateWorkbenchSession();
        session.SetInitialPlan(basePlan);

        session.ExcludeItems([doc.SourcePath]);
        Assert.Single(session.ExcludedSourcePaths);

        int restored = session.RestoreManualExclusions();
        Assert.Equal(1, restored);
        Assert.Empty(session.ExcludedSourcePaths);
        Assert.Empty(session.OptionalIncludedPaths);
        Assert.Contains(session.EffectivePlan!.Targets.SelectMany(t => t.Items), i => i.SourcePath == doc.SourcePath);
        Assert.Contains(session.EffectivePlan.ExcludedItems, i => i.SourcePath == folder.SourcePath);
    }

    [Fact]
    public void WorkbenchSession_EmptyCustomBoxPersistsUntilPopulatedOrRemovedAndPrunesAfterEmptied()
    {
        var doc = Item("doc.txt");
        var basePlan = BuildSessionPlan([doc], Array.Empty<string>());
        var session = CreateWorkbenchSession();
        session.SetInitialPlan(basePlan);

        var emptyBox = session.CreateEmptyCustomBox("New");
        Assert.Contains(session.EffectivePlan!.Targets, t => t.SourceBucketId == emptyBox.WidgetId && t.Items.Count == 0);

        Assert.Equal(1, session.MoveItemsToBucket([doc.SourcePath], emptyBox.WidgetId).MovedCount);
        Assert.Contains(session.EffectivePlan.Targets, t => t.SourceBucketId == emptyBox.WidgetId && t.Items.Count == 1);

        Assert.Equal(1, session.ExcludeItems([doc.SourcePath]).MovedCount);
        Assert.DoesNotContain(session.EffectivePlan.Targets, t => t.SourceBucketId == emptyBox.WidgetId);

        var secondEmpty = session.CreateEmptyCustomBox("New");
        Assert.Contains(session.EffectivePlan!.Targets, t => t.SourceBucketId == secondEmpty.WidgetId);
        session.RemoveEmptyCustomBox(secondEmpty.WidgetId);
        Assert.DoesNotContain(session.EffectivePlan.Targets, t => t.SourceBucketId == secondEmpty.WidgetId);
    }

    [Fact]
    public void WorkbenchSession_RenameBoxSanitizesCollisionsAndDisablesAutoRuleBinding()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "DeskBox.PreviewTests." + Guid.NewGuid().ToString("N"));
        string storageRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Storage")).FullName;
        string existingFolder = Directory.CreateDirectory(Path.Combine(storageRoot, "Projects")).FullName;
        try
        {
            var doc = Item("doc.txt");
            var basePlan = new DesktopOrganizationPlan
            {
                StorageRootPath = storageRoot,
                SourceItems = [doc],
                Targets =
                [
                    new DesktopOrganizationTargetPlan
                    {
                        SourceBucketId = "docs",
                        CategoryId = DesktopOrganizationCategoryIds.Documents,
                        TargetWidgetId = "docs",
                        SuggestedDisplayName = "Documents",
                        TargetDirectoryPath = Path.Combine(storageRoot, "Documents"),
                        CreatesWidget = true,
                        AutoBindCategoryRule = true,
                        Items = [doc]
                    }
                ]
            };

            var widgets = new Dictionary<string, WidgetConfig>(StringComparer.Ordinal)
            {
                ["existing-projects"] = new WidgetConfig
                {
                    Id = "existing-projects",
                    Name = "Projects",
                    WidgetKind = WidgetKind.File,
                    MappedFolderPath = existingFolder
                }
            };

            var session = new DesktopOrganizationWorkbenchSession(
                (plan, optional) => BuildSessionPlan(plan.SourceItems, optional, storageRoot),
                () => widgets);
            session.SetInitialPlan(basePlan);

            Assert.True(session.RenameBox("docs", "CON/Projects:*?"));
            DesktopOrganizationTargetPlan renamed = Assert.Single(session.EffectivePlan!.Targets);
            Assert.False(renamed.AutoBindCategoryRule);
            Assert.StartsWith(storageRoot, renamed.TargetDirectoryPath, StringComparison.OrdinalIgnoreCase);

            Assert.True(session.RenameBox("docs", "Projects"));
            DesktopOrganizationTargetPlan collided = Assert.Single(session.EffectivePlan.Targets);
            Assert.False(collided.AutoBindCategoryRule);
            Assert.NotEqual(Path.GetFullPath(existingFolder), Path.GetFullPath(collided.TargetDirectoryPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkbenchSession_RoutingTwoBucketsToSameExistingWidgetMergesTargetsWithoutDuplicates()
    {
        var doc = Item("doc.txt");
        var img = Item("pic.png") with { CategoryId = DesktopOrganizationCategoryIds.Images };
        var basePlan = BuildSessionPlan([doc, img], Array.Empty<string>());
        var widgets = new Dictionary<string, WidgetConfig>(StringComparer.Ordinal)
        {
            ["w-existing"] = new WidgetConfig
            {
                Id = "w-existing",
                Name = "WorkHub",
                WidgetKind = WidgetKind.File,
                MappedFolderPath = "C:\\Storage\\WorkHub"
            }
        };

        var session = new DesktopOrganizationWorkbenchSession(
            (plan, optional) => BuildSessionPlan(plan.SourceItems, optional),
            () => widgets);
        session.SetInitialPlan(basePlan);

        session.SetTargetDestinationSelection("docs", new DesktopOrganizationTargetSelection
        {
            SourceBucketId = "docs",
            DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget,
            ExistingWidgetId = "w-existing"
        });
        session.SetTargetDestinationSelection("images", new DesktopOrganizationTargetSelection
        {
            SourceBucketId = "images",
            DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget,
            ExistingWidgetId = "w-existing"
        });

        DesktopOrganizationTargetPlan merged = Assert.Single(session.EffectivePlan!.Targets);
        Assert.Equal("w-existing", merged.TargetWidgetId);
        Assert.False(merged.CreatesWidget);
        Assert.Equal(2, merged.Items.Count);
    }

    [Fact]
    public void WorkbenchSession_ApplyRescanPlanDropsOverridesForNewlyLockedItems()
    {
        var folder = Item("Vault") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var doc = Item("keep.txt");
        var initialPlan = BuildSessionPlan([folder, doc], Array.Empty<string>());
        var session = CreateWorkbenchSession();
        session.SetInitialPlan(initialPlan);
        session.IncludeItems([folder.SourcePath]);
        session.ExcludeItems([doc.SourcePath]);

        var protectedFolder = folder with { ExclusionReason = DesktopOrganizationExclusionReason.ReparsePoint };
        var hiddenDoc = doc with { ExclusionReason = DesktopOrganizationExclusionReason.HiddenOrSystem };
        var rescanned = BuildSessionPlan([protectedFolder, hiddenDoc], Array.Empty<string>());

        session.ApplyRescanPlan(rescanned);

        Assert.DoesNotContain(folder.SourcePath, session.OptionalIncludedPaths);
        Assert.DoesNotContain(doc.SourcePath, session.ExcludedSourcePaths);
    }

    [Fact]
    public void WorkbenchSession_TileClickSupportsSingleToggleCtrlShiftRangeAndSelectAll()
    {
        var a = Item("a.txt");
        var b = Item("b.txt");
        var c = Item("c.txt");
        string[] ordered = [a.SourcePath, b.SourcePath, c.SourcePath];
        var session = CreateWorkbenchSession();
        session.SetInitialPlan(BuildSessionPlan([a, b, c], Array.Empty<string>()));

        session.HandleTileClick(a.SourcePath, "docs", ordered, isCtrlDown: false, isShiftDown: false);
        Assert.True(session.SelectedSourcePaths.SetEquals([a.SourcePath]));

        session.HandleTileClick(c.SourcePath, "docs", ordered, isCtrlDown: false, isShiftDown: true);
        Assert.True(session.SelectedSourcePaths.SetEquals(ordered));
        Assert.Equal(3, session.GetDragPayloadForTargetItem(b.SourcePath).Count);

        session.HandleTileClick(b.SourcePath, "docs", ordered, isCtrlDown: true, isShiftDown: false);
        Assert.True(session.SelectedSourcePaths.SetEquals([a.SourcePath, c.SourcePath]));

        session.HandleTileClick(a.SourcePath, "docs", ordered, isCtrlDown: false, isShiftDown: false);
        session.HandleTileClick(a.SourcePath, "docs", ordered, isCtrlDown: false, isShiftDown: false);
        Assert.Empty(session.SelectedSourcePaths);

        session.SelectAllInBucket(ordered, "docs");
        Assert.Equal(3, session.SelectedSourcePaths.Count);
    }

    [Fact]
    public void PointerDragController_SmallMovementInvokesClickWithoutActivatingDrag()
    {
        using var controller = new DesktopOrganizationPointerDragController();
        bool started = false;
        bool completed = false;
        bool clicked = false;
        controller.DragStarted += (_, _) => started = true;
        controller.DragCompleted += (_, _) => completed = true;

        controller.BeginCandidateCore(
            1,
            DesktopOrganizationDragOrigin.PreviewCard,
            "docs",
            new Point(10, 10),
            () => ["C:\\Desktop\\a.txt"],
            () => clicked = true);

        Assert.False(controller.UpdatePointerCore(1, new Point(13, 14), primaryButtonStillDown: true));
        Assert.False(controller.IsDragging);

        controller.ReleasePointerCore(1, new Point(13, 14));
        Assert.True(clicked);
        Assert.False(started);
        Assert.False(completed);
    }

    [Fact]
    public void PointerDragController_ThresholdCrossingStartsDragAndReleaseCompletesDrop()
    {
        using var controller = new DesktopOrganizationPointerDragController();
        DesktopOrganizationDragStartedEventArgs? started = null;
        int movedCount = 0;
        DesktopOrganizationDragCompletedEventArgs? completed = null;
        bool canceled = false;

        controller.DragStarted += (_, args) => started = args;
        controller.DragMoved += (_, _) => movedCount++;
        controller.DragCompleted += (_, args) => completed = args;
        controller.DragCanceled += (_, _) => canceled = true;

        controller.BeginCandidateCore(
            7,
            DesktopOrganizationDragOrigin.PreviewCard,
            "docs",
            new Point(10, 10),
            () => ["C:\\Desktop\\a.txt"]);

        Assert.True(controller.UpdatePointerCore(7, new Point(25, 10), primaryButtonStillDown: true));
        Assert.True(controller.IsDragging);
        Assert.NotNull(started);
        Assert.Equal(1, movedCount);

        controller.SetPointerCaptureStateForTest(true);
        controller.ReleasePointerCore(7, new Point(300, 120));

        Assert.NotNull(completed);
        Assert.Equal(new Point(300, 120), completed.RootPosition);
        Assert.False(canceled);
        Assert.False(controller.IsDragging);
    }

    [Fact]
    public void PointerDragController_UnexpectedCaptureLossCancelsAndBlocksReleaseDrop()
    {
        using var controller = new DesktopOrganizationPointerDragController();
        bool completed = false;
        bool canceled = false;
        controller.DragCompleted += (_, _) => completed = true;
        controller.DragCanceled += (_, _) => canceled = true;

        controller.BeginCandidateCore(
            9,
            DesktopOrganizationDragOrigin.StayOnDesktopTray,
            string.Empty,
            new Point(50, 50),
            () => ["C:\\Desktop\\folder"]);

        Assert.True(controller.UpdatePointerCore(9, new Point(70, 50), primaryButtonStillDown: true));
        controller.SetPointerCaptureStateForTest(true);
        controller.HandleCaptureLossCore(9);

        Assert.True(canceled);
        Assert.False(controller.IsDragging);

        controller.ReleasePointerCore(9, new Point(120, 80));
        Assert.False(completed);
    }

    [Fact]
    public void PointerDragController_CancelActiveDragFiresCanceledAndClearsState()
    {
        using var controller = new DesktopOrganizationPointerDragController();
        bool canceled = false;
        controller.DragCanceled += (_, _) => canceled = true;

        controller.BeginCandidateCore(
            3,
            DesktopOrganizationDragOrigin.PreviewCard,
            "docs",
            new Point(0, 0),
            () => ["C:\\Desktop\\a.txt"]);
        Assert.True(controller.UpdatePointerCore(3, new Point(0, 12), primaryButtonStillDown: true));

        Assert.True(controller.CancelActiveDrag());
        Assert.True(canceled);
        Assert.False(controller.IsDragging);
        Assert.False(controller.HasPendingCandidate);
    }

    private static DesktopOrganizationWorkbenchSession CreateWorkbenchSession() =>
        new(
            (plan, optional) => BuildSessionPlan(plan.SourceItems, optional),
            () => new Dictionary<string, WidgetConfig>(StringComparer.Ordinal));

    private static DesktopOrganizationPlan BuildSessionPlan(
        IReadOnlyList<DesktopOrganizationFileSnapshot> sourceItems,
        IReadOnlyCollection<string> optionalIncludedPaths,
        string storageRoot = "C:\\Storage")
    {
        var optedIn = optionalIncludedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetsByBucket = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.Ordinal);
        var excluded = new List<DesktopOrganizationFileSnapshot>();

        foreach (var raw in sourceItems)
        {
            var effective = raw.CanOptIn && optedIn.Contains(raw.SourcePath)
                ? raw with { ExclusionReason = DesktopOrganizationExclusionReason.None }
                : raw;

            if (!effective.IsEligible)
            {
                excluded.Add(effective);
                continue;
            }

            string bucketId = effective.CategoryId == DesktopOrganizationCategoryIds.Images ? "images" : "docs";
            if (!targetsByBucket.TryGetValue(bucketId, out var target))
            {
                target = new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = bucketId,
                    CategoryId = effective.CategoryId,
                    TargetWidgetId = bucketId,
                    SuggestedDisplayName = bucketId == "images" ? "Images" : "Documents",
                    TargetDirectoryPath = Path.Combine(storageRoot, bucketId),
                    CreatesWidget = true,
                    Items = []
                };
                targetsByBucket.Add(bucketId, target);
            }

            target.Items.Add(effective);
        }

        return new DesktopOrganizationPlan
        {
            StorageRootPath = storageRoot,
            SourceItems = sourceItems.ToList(),
            Targets = targetsByBucket.Values.ToList(),
            ExcludedItems = excluded
        };
    }
}
