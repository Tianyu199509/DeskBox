using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class TodoQuickCaptureUxUpgradeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _widgetsDataRoot;
    private readonly string _quickCaptureRoot;

    public TodoQuickCaptureUxUpgradeTests()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests.UxUpgrade", Guid.NewGuid().ToString("N"));
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
        _quickCaptureRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "quick-capture")).FullName;
    }

    [Fact]
    public void TodoNaturalDateParser_ParsesChineseAndEnglishDatesTimesAndRecurrence_WithoutStrippingSource()
    {
        var baseMonday = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.FromHours(8));

        Assert.True(TodoNaturalDateParser.TryParse("明天下午3点半给客户回电话", baseMonday, out TodoNaturalDateMatch tmrMatch));
        Assert.NotNull(tmrMatch.DueDate);
        DateTimeOffset tmrLocal = tmrMatch.DueDate!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 5, 12), tmrLocal.Date);
        Assert.True(tmrMatch.HasDueTime);
        Assert.Equal(15, tmrLocal.Hour);
        Assert.Equal(30, tmrLocal.Minute);
        Assert.Equal(TodoRecurrenceMode.None, tmrMatch.RecurrenceMode);

        Assert.True(TodoNaturalDateParser.TryParse("今晚提交周报", baseMonday, out TodoNaturalDateMatch tonightMatch));
        Assert.NotNull(tonightMatch.DueDate);
        DateTimeOffset tonightLocal = tonightMatch.DueDate!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 5, 11), tonightLocal.Date);
        Assert.True(tonightMatch.HasDueTime);
        Assert.Equal(20, tonightLocal.Hour);

        Assert.True(TodoNaturalDateParser.TryParse("下周三 14:30 评审", baseMonday, out TodoNaturalDateMatch nextWedMatch));
        Assert.NotNull(nextWedMatch.DueDate);
        DateTimeOffset nextWedLocal = nextWedMatch.DueDate!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 5, 20), nextWedLocal.Date);
        Assert.True(nextWedMatch.HasDueTime);
        Assert.Equal(14, nextWedLocal.Hour);
        Assert.Equal(30, nextWedLocal.Minute);

        Assert.True(TodoNaturalDateParser.TryParse("每天喝八杯水", baseMonday, out TodoNaturalDateMatch dailyMatch));
        Assert.Equal(TodoRecurrenceMode.Daily, dailyMatch.RecurrenceMode);
        Assert.NotNull(dailyMatch.DueDate);
        Assert.False(dailyMatch.HasDueTime);

        Assert.True(TodoNaturalDateParser.TryParse("工作日晨会 09:30", baseMonday, out TodoNaturalDateMatch weekdayMatch));
        Assert.Equal(TodoRecurrenceMode.Weekdays, weekdayMatch.RecurrenceMode);
        Assert.True(weekdayMatch.HasDueTime);
        Assert.Equal(9, weekdayMatch.DueDate!.Value.ToLocalTime().Hour);
        Assert.Equal(30, weekdayMatch.DueDate!.Value.ToLocalTime().Minute);

        Assert.True(TodoNaturalDateParser.TryParse("每月15号还信用卡", baseMonday, out TodoNaturalDateMatch monthlyMatch));
        Assert.Equal(TodoRecurrenceMode.Monthly, monthlyMatch.RecurrenceMode);
        Assert.Equal(15, monthlyMatch.DueDate!.Value.ToLocalTime().Day);
    }

    [Fact]
    public void TodoNaturalDateParser_IgnoresDatesInsideUrls()
    {
        var now = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.FromHours(8));

        bool matched = TodoNaturalDateParser.TryParse(
            "查看归档链接 https://example.com/archive/2026/05/20/report?time=14:30",
            now,
            out TodoNaturalDateMatch match);

        Assert.False(matched);
        Assert.False(match.HasAny);
    }

    [Fact]
    public void MarkdownListContinuationHelper_ContinuesCheckedTaskAsUnchecked_ExitsOnEmptyPrefix_AndIncrementsNumberedList()
    {
        string checkedLine = "- [x] Ship release";
        Assert.True(MarkdownListContinuationHelper.TryComputeContinuationEdit(
            checkedLine,
            selectionStart: checkedLine.Length,
            selectionLength: 0,
            newline: "\r\n",
            out int replaceStart,
            out int replaceLength,
            out string replacement,
            out int caretIndex));
        Assert.Equal(checkedLine.Length, replaceStart);
        Assert.Equal(0, replaceLength);
        Assert.Equal("\r\n- [ ] ", replacement);
        Assert.Equal(checkedLine.Length + "\r\n- [ ] ".Length, caretIndex);

        string emptyTaskLine = "First line\r\n- [ ] ";
        Assert.True(MarkdownListContinuationHelper.TryComputeContinuationEdit(
            emptyTaskLine,
            selectionStart: emptyTaskLine.Length,
            selectionLength: 0,
            newline: "\r\n",
            out int emptyStart,
            out int emptyLen,
            out string emptyReplacement,
            out int emptyCaret));
        Assert.Equal("First line\r\n".Length, emptyStart);
        Assert.Equal("- [ ] ".Length, emptyLen);
        Assert.Equal(string.Empty, emptyReplacement);
        Assert.Equal("First line\r\n".Length, emptyCaret);

        string numberedLine = "1. First step";
        Assert.True(MarkdownListContinuationHelper.TryComputeContinuationEdit(
            numberedLine,
            selectionStart: numberedLine.Length,
            selectionLength: 0,
            newline: "\n",
            out _,
            out _,
            out string numReplacement,
            out _));
        Assert.Equal("\n2. ", numReplacement);
    }

    [Fact]
    public void MarkdownDocumentService_ToPlainText_RendersTaskCheckboxesClearly()
    {
        string markdown = "- [ ] Buy milk\n- [x] Ship patch\n- Plain item";
        string plain = new MarkdownDocumentService().ToPlainText(markdown);

        Assert.Contains("\u2610 Buy milk", plain, StringComparison.Ordinal);
        Assert.Contains("\u2611 Ship patch", plain, StringComparison.Ordinal);
        Assert.Contains("Plain item", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureTagParser_BidirectionalTodoAndQuickCaptureConversion_PreservesStructure()
    {
        var pureChecklistNote = new QuickCaptureItem
        {
            Body = "- [ ] Alpha\n- [x] Beta\n- [ ] Gamma",
            ContentFormat = TextContentFormat.Markdown
        };

        var splitItems = QuickCaptureTagParser.SplitItemsForTodoConversion(pureChecklistNote, "Untitled");
        Assert.Equal(3, splitItems.Count);
        Assert.Equal(("Alpha", false), (splitItems[0].Title, splitItems[0].IsCompleted));
        Assert.Equal(("Beta", true), (splitItems[1].Title, splitItems[1].IsCompleted));
        Assert.Equal(("Gamma", false), (splitItems[2].Title, splitItems[2].IsCompleted));

        var titledNote = new QuickCaptureItem
        {
            Title = "Sprint Plan",
            Body = "- [ ] Design\n- [x] Code\nContext paragraph",
            ContentFormat = TextContentFormat.Markdown
        };
        var singleSplit = QuickCaptureTagParser.SplitItemsForTodoConversion(titledNote, "Untitled");
        var single = Assert.Single(singleSplit);
        Assert.Equal("Sprint Plan", single.Title);
        Assert.Equal(2, single.Steps.Count);
        Assert.Equal("Context paragraph", single.Notes);

        var todoItem = new TodoItem
        {
            Text = "Launch Checklist",
            IsImportant = true,
            Notes = "Verify installer offline bundle",
            Steps =
            [
                new TodoStep { Text = "Run x64 tests", IsCompleted = true, SortOrder = 0 },
                new TodoStep { Text = "Restart Debug exe", IsCompleted = false, SortOrder = 1 }
            ]
        };

        (string? qcTitle, string qcBody, bool qcPin) = QuickCaptureTagParser.FormatTodoForQuickCapture(todoItem);
        Assert.Null(qcTitle);
        Assert.True(qcPin);
        Assert.Contains("# Launch Checklist", qcBody, StringComparison.Ordinal);
        Assert.Contains("- [x] Run x64 tests", qcBody, StringComparison.Ordinal);
        Assert.Contains("- [ ] Restart Debug exe", qcBody, StringComparison.Ordinal);
        Assert.Contains("Verify installer offline bundle", qcBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickCaptureService_MergeItemsAndRestoreMergedItems_RoundTripsCleanlyAndPreservesTitles()
    {
        var service = new QuickCaptureService(new QuickCaptureStore(_quickCaptureRoot));
        QuickCaptureItem first = await service.AddDetailedItemAsync(
            "Custom Title A",
            "Body A",
            QuickCaptureAppearancePreset.StickyYellow,
            pin: true);
        QuickCaptureItem second = await service.AddDetailedItemAsync(
            "Custom Title B",
            "Body B",
            QuickCaptureAppearancePreset.Mint,
            pin: false);

        QuickCaptureMergeResult mergeResult = await service.MergeItemsAsync([first.Id, second.Id]);
        Assert.NotNull(mergeResult.MergedItem);
        Assert.Equal(2, mergeResult.SourceSnapshots.Count);

        QuickCaptureStoreData afterMerge = await service.GetDataAsync();
        Assert.Single(afterMerge.Items, item => !item.IsDeleted);
        Assert.Equal(mergeResult.MergedItem!.Id, afterMerge.Items.Single(i => !i.IsDeleted).Id);

        bool restored = await service.RestoreMergedItemsAsync(
            mergeResult.MergedItem.Id,
            mergeResult.SourceSnapshots);
        Assert.True(restored);

        QuickCaptureStoreData afterUndo = await service.GetDataAsync();
        QuickCaptureItem[] activeItems = afterUndo.Items.Where(i => !i.IsDeleted).ToArray();
        Assert.Equal(2, activeItems.Length);
        Assert.Contains(activeItems, i => i.Id == first.Id && i.Title == "Custom Title A" && i.IsPinned);
        Assert.Contains(activeItems, i => i.Id == second.Id && i.Title == "Custom Title B" && !i.IsPinned);
    }

    [Fact]
    public async Task TodoWidgetViewModel_CompletionUndoRetractsDeferredRecurringItem_AndBatchPasteSupportsSingleStepUndo()
    {
        var clockNow = new DateTimeOffset(2026, 5, 11, 9, 0, 0, TimeSpan.FromHours(8));
        var vm = CreateTodoViewModel("todo-ux", clock: () => clockNow);
        await vm.InitializeAsync();

        TodoItemViewModel? recurring = await vm.AddItemAsync(
            "Daily standup",
            dueDate: TodoWidgetStore.NormalizeAllDayDueDate(clockNow),
            hasDueTime: false,
            recurrenceMode: TodoRecurrenceMode.Daily);
        Assert.NotNull(recurring);

        Assert.True(await vm.SetCompletedAsync(recurring!.Id, true));
        Assert.True(recurring.IsCompleted);
        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.CanUndoLastAction);

        await vm.UndoLastActionAsync();
        Assert.False(recurring.IsCompleted);
        Assert.Single(vm.Items);

        IReadOnlyList<TodoItemViewModel> pasted = await vm.AddPastedTextItemsWithUndoAsync(
            "- [ ] First pasted\r\n- [x] Second completed\r\n3. Third numbered");
        Assert.Equal(3, pasted.Count);
        Assert.Equal(4, vm.Items.Count);
        Assert.True(vm.Items.Single(i => i.Text == "Second completed").IsCompleted);
        Assert.True(vm.CanUndoLastAction);

        await vm.UndoLastActionAsync();
        Assert.Single(vm.Items);
        Assert.Equal("Daily standup", vm.Items[0].Text);
    }

    [Fact]
    public async Task TodoWidgetViewModel_SearchQueryFiltersAcrossTitleNotesSteps_AndOverdueCollapseWorksInAllAndActiveViews()
    {
        var clockNow = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.FromHours(8));
        var vm = CreateTodoViewModel("todo-search-overdue", clock: () => clockNow);
        await vm.InitializeAsync();

        DateTimeOffset yesterday = TodoWidgetStore.NormalizeAllDayDueDate(clockNow.AddDays(-1));
        DateTimeOffset today = TodoWidgetStore.NormalizeAllDayDueDate(clockNow);

        TodoItemViewModel? overdueItem = await vm.AddItemAsync("Overdue task", dueDate: yesterday, hasDueTime: false);
        TodoItemViewModel? todayItem = await vm.AddItemAsync("Today task", dueDate: today, hasDueTime: false);
        Assert.NotNull(overdueItem);
        Assert.NotNull(todayItem);
        await vm.UpdateNotesAsync(todayItem!.Id, "Contains secret-keyword-notes");
        await vm.AddStepAsync(overdueItem!.Id, "step-needle-token");

        vm.SelectedFilter = TodoFilter.All;
        Assert.True(vm.ShowTodayOverdueSection);
        Assert.Equal(Visibility.Visible, vm.TodayOverdueBarVisibility);

        Assert.Equal(1, await vm.RescheduleOverdueToTodayAsync());
        Assert.False(vm.ShowTodayOverdueSection);
        Assert.True(await vm.UndoLastActionAsync());
        Assert.True(vm.ShowTodayOverdueSection);

        vm.SearchQuery = "secret-keyword-notes";
        Assert.Equal(todayItem.Id, Assert.Single(vm.VisibleItems).Id);

        vm.SearchQuery = "step-needle-token";
        Assert.Equal(overdueItem.Id, Assert.Single(vm.VisibleItems).Id);

        vm.SearchQuery = string.Empty;
        Assert.Equal(2, vm.VisibleItems.Count);
    }

    [Fact]
    public void DeferredOccurrenceWakeupClampsToMidnight_WmTimeChangeMapsToTimeChange_AndDesktopPinnedSuppressesRaise()
    {
        var now = new DateTimeOffset(2026, 5, 11, 14, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset tomorrowDue = TodoWidgetStore.NormalizeAllDayDueDate(now.AddDays(1));

        var deferredTomorrow = new TodoItem
        {
            Id = "deferred-next",
            Text = "Daily habit",
            DueDate = tomorrowDue,
            HasDueTime = false,
            IsGeneratedOccurrence = true,
            Recurrence = new TodoRecurrence { Mode = TodoRecurrenceMode.Daily, AnchorDueDate = tomorrowDue }
        };

        Assert.False(TodoReminderService.ShouldNotify(
            deferredTomorrow,
            now,
            defaultOffsetMinutes: 1440,
            allDayRemindersEnabled: true,
            allDayReminderTimeMinutes: 9 * 60));

        DateTimeOffset? wakeup = TodoReminderService.FindEarliestNextWakeup(
            "w1",
            [deferredTomorrow],
            now,
            defaultOffsetMinutes: 1440,
            notifiedKeys: null,
            allDayRemindersEnabled: true,
            allDayReminderTimeMinutes: 9 * 60);
        Assert.NotNull(wakeup);
        Assert.True(
            wakeup!.Value.ToLocalTime() >= new DateTimeOffset(2026, 5, 12, 0, 0, 0, now.ToLocalTime().Offset),
            $"Expected wakeup at or after tomorrow local midnight, got {wakeup.Value:O}");

        Assert.Equal(
            "time-change",
            AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
                AppLifecycleRecoverySignalClassifier.WmTimeChange,
                UIntPtr.Zero,
                taskbarCreatedMessage: 0xC001));

        string layerSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/WidgetLayerService.cs"));
        Assert.Contains("(hasExpandedLease && !UsesDesktopPinnedMode())", layerSource, StringComparison.Ordinal);

        string boundsSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        Assert.Contains("ApplyDesktopPinnedRestingActivationStyle(HWnd);", boundsSource, StringComparison.Ordinal);
        Assert.Contains("IsOwnerWindowKeyboardFocused", boundsSource, StringComparison.Ordinal);

        foreach (string surfaceFile in Directory.EnumerateFiles(
                     TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents"),
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(surfaceFile);
            if (fileName.StartsWith("Todo", StringComparison.Ordinal) ||
                fileName.StartsWith("QuickCaptureSurfaceContent", StringComparison.Ordinal))
            {
                string code = File.ReadAllText(surfaceFile);
                Assert.DoesNotContain("ActivateWindowAndFocusIsland", code, StringComparison.Ordinal);
            }
        }
    }

    private TodoWidgetViewModel CreateTodoViewModel(string widgetId, Func<DateTimeOffset>? clock = null)
    {
        var config = new WidgetConfig
        {
            Id = widgetId,
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };
        return new TodoWidgetViewModel(
            new TodoWidgetStore(_widgetsDataRoot, widgetId),
            TestServices.CreateLocalizationService(),
            config,
            settingsService: null,
            clock: clock);
    }

    public void Dispose()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
