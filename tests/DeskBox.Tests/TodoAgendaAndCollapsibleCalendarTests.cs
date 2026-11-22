using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Covers the V2-Alpha <c>deskbox.todo-agenda</c> official template, bidirectional
/// <c>todo.update</c> / <c>todo.rolloverOverdue</c> host mutations, collapsible calendar
/// holiday badges + P1/emerald dot priority distinction, multi-locale NLP parsing,
/// Pomodoro focus linkage, and Capsule direct action execution.
/// </summary>
public sealed class TodoAgendaAndCollapsibleCalendarTests
{
    [Fact]
    public async Task TodoHostBridge_UpdateAndRolloverOverdue_MutateStoreAndPreserveNonTargets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-todo-v2-{Guid.NewGuid():N}");
        string widgetsRoot = Path.Combine(tempRoot, "widgets");
        Directory.CreateDirectory(widgetsRoot);

        try
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly yesterday = today.AddDays(-2);
            DateOnly tomorrow = today.AddDays(1);
            DateOnly nextWeek = today.AddDays(5);

            var store = new TodoWidgetStore(widgetsRoot, CustomWidgetHostSnapshotService.FallbackTodoWidgetId);
            var seedData = new TodoWidgetData
            {
                Items =
                [
                    new TodoItem
                    {
                        Id = "overdue-open",
                        Text = "Submit expense report",
                        DueDate = new DateTimeOffset(yesterday.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(yesterday.ToDateTime(TimeOnly.MinValue))),
                        HasDueTime = false,
                        IsCompleted = false,
                        SortOrder = 0
                    },
                    new TodoItem
                    {
                        Id = "overdue-done",
                        Text = "Already finished past task",
                        DueDate = new DateTimeOffset(yesterday.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(yesterday.ToDateTime(TimeOnly.MinValue))),
                        HasDueTime = false,
                        IsCompleted = true,
                        SortOrder = 1
                    },
                    new TodoItem
                    {
                        Id = "future-open",
                        Text = "Prepare roadmap",
                        DueDate = new DateTimeOffset(nextWeek.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(nextWeek.ToDateTime(TimeOnly.MinValue))),
                        HasDueTime = false,
                        IsCompleted = false,
                        SortOrder = 2
                    }
                ]
            };
            await store.SaveAsync(seedData);

            // 1. Rollover overdue moves only incomplete past-due tasks to today.
            int rolled = await CustomWidgetHostSnapshotService.RolloverOverdueTodosFromCustomWidgetAsync(
                settingsService: null,
                targetDueIso: "today",
                widgetsDataRootOverride: widgetsRoot);
            Assert.Equal(1, rolled);

            CustomWidgetTodoSnapshot afterRoll = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: null,
                maxItems: 50,
                widgetsDataRootOverride: widgetsRoot);
            Assert.Equal(today.ToString("yyyy-MM-dd"), Assert.Single(afterRoll.Records, r => r.Id == "overdue-open").GetField("dueIso"));
            Assert.Equal(yesterday.ToString("yyyy-MM-dd"), Assert.Single(afterRoll.Records, r => r.Id == "overdue-done").GetField("dueIso"));
            Assert.Equal(nextWeek.ToString("yyyy-MM-dd"), Assert.Single(afterRoll.Records, r => r.Id == "future-open").GetField("dueIso"));

            // 2. todo.update rejects malformed dueIso without mutating stored title/priority.
            bool invalidResult = await CustomWidgetHostSnapshotService.UpdateTodoFromCustomWidgetAsync(
                settingsService: null,
                itemId: "future-open",
                title: "Should not apply",
                dueIso: "not-a-date",
                priority: "high",
                widgetsDataRootOverride: widgetsRoot);
            Assert.False(invalidResult);

            // 3. todo.update mutates title, relative dueIso ("tomorrow"), and priority ("high").
            bool updated = await CustomWidgetHostSnapshotService.UpdateTodoFromCustomWidgetAsync(
                settingsService: null,
                itemId: "future-open",
                title: "[Design] Prepare V2 roadmap",
                dueIso: "tomorrow",
                priority: "high",
                widgetsDataRootOverride: widgetsRoot);
            Assert.True(updated);

            CustomWidgetTodoSnapshot afterUpdate = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: null,
                maxItems: 50,
                widgetsDataRootOverride: widgetsRoot);
            CustomWidgetRecord updatedRec = Assert.Single(afterUpdate.Records, r => r.Id == "future-open");
            Assert.Equal("[Design] Prepare V2 roadmap", updatedRec.GetField("title"));
            Assert.Equal(tomorrow.ToString("yyyy-MM-dd"), updatedRec.GetField("dueIso"));
            Assert.Equal("high", updatedRec.GetField("priority"));

            // 4. todo.update "clear" clears DueDate cleanly while keeping title.
            bool clearedDue = await CustomWidgetHostSnapshotService.UpdateTodoFromCustomWidgetAsync(
                settingsService: null,
                itemId: "future-open",
                title: null,
                dueIso: "clear",
                priority: null,
                widgetsDataRootOverride: widgetsRoot);
            Assert.True(clearedDue);

            CustomWidgetTodoSnapshot afterClear = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: null,
                maxItems: 50,
                widgetsDataRootOverride: widgetsRoot);
            Assert.Equal(string.Empty, Assert.Single(afterClear.Records, r => r.Id == "future-open").GetField("dueIso"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void CalendarEngine_MarksHolidayBadges_PriorityDots_SkipsCompleted_AndComputesHolidayCountdown()
    {
        var febMonth = new DateOnly(2026, 2, 1);
        var today = new DateOnly(2026, 2, 10);
        var selected = new DateOnly(2026, 2, 15);

        var records = new List<CustomWidgetRecord>
        {
            new()
            {
                Id = "t-p1",
                Fields =
                {
                    ["title"] = "Release readiness check",
                    ["dueIso"] = "2026-02-15",
                    ["priority"] = "high",
                    ["done"] = "false"
                }
            },
            new()
            {
                Id = "t-normal",
                Fields =
                {
                    ["title"] = "Weekly sync",
                    ["dueIso"] = "2026-02-16",
                    ["priority"] = "normal",
                    ["done"] = "false"
                }
            },
            new()
            {
                Id = "t-done",
                Fields =
                {
                    ["title"] = "Completed archival",
                    ["dueIso"] = "2026-02-17",
                    ["priority"] = "high",
                    ["done"] = "true"
                }
            }
        };

        CustomWidgetCalendarMonthView view = CustomWidgetCalendarEngine.BuildMonthView(
            febMonth,
            today,
            selected,
            "zh-CN",
            records,
            "dueIso");

        CustomWidgetCalendarCell swapCell = Assert.Single(view.Cells, c => c.Date == new DateOnly(2026, 2, 14));
        Assert.Equal("swapWork", swapCell.CornerBadgeKind);

        CustomWidgetCalendarCell holidayP1Cell = Assert.Single(view.Cells, c => c.Date == new DateOnly(2026, 2, 15));
        Assert.Equal("holiday", holidayP1Cell.CornerBadgeKind);
        Assert.Equal(1, holidayP1Cell.EventCount);
        Assert.True(holidayP1Cell.HasHighPriorityEvent);

        CustomWidgetCalendarCell normalCell = Assert.Single(view.Cells, c => c.Date == new DateOnly(2026, 2, 16));
        Assert.Equal(1, normalCell.EventCount);
        Assert.False(normalCell.HasHighPriorityEvent);

        CustomWidgetCalendarCell completedOnlyCell = Assert.Single(view.Cells, c => c.Date == new DateOnly(2026, 2, 17));
        Assert.Equal(0, completedOnlyCell.EventCount);
        Assert.False(completedOnlyCell.HasHighPriorityEvent);

        bool hasUpcoming = CustomWidgetCalendarEngine.TryGetNextStatutoryHolidayCountdown(
            today,
            "zh-CN",
            out string holidayLabel,
            out int daysUntil);
        Assert.True(hasUpcoming);
        Assert.False(string.IsNullOrWhiteSpace(holidayLabel));
        Assert.Equal(5, daysUntil);
    }

    [Fact]
    public async Task TodoAgendaTemplate_FrPreviewSeeds_MultiLocaleNlpDraft_Filters_Focus_AndCapsulePrimaryAction()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-todo-agenda-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            localization.SetLanguage("fr-FR");

            // 1. fr-FR preview seeds 3 French sample tasks and respects canonical locale structure.
            CustomWidgetTemplateBundle frBundle =
                CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.TodoAgendaTemplateId, "fr-FR");
            var frStore = new CustomWidgetDataStore("todo-agenda-fr-preview", Path.Combine(tempDir, "fr-widgets"));
            await frStore.WriteWorkspaceBundleAsync(frBundle.ManifestJson, frBundle.LogicJs, locales: frBundle.Locales);
            using (var frVm = new CustomWidgetViewModel(
                       new WidgetConfig { Id = "todo-agenda-fr-preview", Name = "Todo", WidgetKind = WidgetKind.Custom },
                       localization,
                       store: frStore,
                       dispatcherQueue: null,
                       notificationServiceProvider: null,
                       isPreviewOnly: true,
                       settingsService: settingsService))
            {
                await frVm.InitializeAsync();
                Assert.Equal(3, frVm.Collections["tasks"].Count);
                Assert.Equal(frBundle.Locales!.Locales["fr-FR"]["seedTask1"], frVm.Collections["tasks"][0].Fields["title"]);
            }

            // 2. zh-CN preview runtime tests NLP draft parsing, token clearing, submit, filtering, focus & capsule primary action.
            localization.SetLanguage("zh-CN");
            CustomWidgetTemplateBundle zhBundle =
                CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.TodoAgendaTemplateId, "zh-CN");
            var zhStore = new CustomWidgetDataStore("todo-agenda-zh-preview", Path.Combine(tempDir, "zh-widgets"));
            await zhStore.WriteWorkspaceBundleAsync(zhBundle.ManifestJson, zhBundle.LogicJs, locales: zhBundle.Locales);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "todo-agenda-zh-preview", Name = "日程待办台", WidgetKind = WidgetKind.Custom },
                localization,
                store: zhStore,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: true,
                settingsService: settingsService);

            await vm.InitializeAsync();

            // Live NLP while typing
            vm.SetStateValueWithoutRebuild("state.draftTitle", "明天 下午评审 !p1 #产品", notifyLiveBindings: true);
            CustomWidgetEvaluationContext nlpCtx = vm.CreateEvaluationContext();
            Assert.Equal("true", nlpCtx.Computed["hasParsedTokens"].GetString());
            Assert.Equal("true", nlpCtx.Computed["hasParsedDate"].GetString());
            Assert.Equal("true", nlpCtx.Computed["hasParsedPriority"].GetString());
            Assert.Equal("true", nlpCtx.Computed["hasParsedTag"].GetString());
            Assert.Equal("#产品", nlpCtx.Computed["parsedTagChip"].GetString());
            Assert.Equal("P1", nlpCtx.Computed["draftPriorityCode"].GetString());

            // Clear tag chip and submit remaining draft ("明天 下午评审 !p1") -> resolves tag if kept or clean submit
            await vm.ExecuteActionsAsync([
                new CustomWidgetActionSpec("script.run", Fn: "clearParsedToken", Args: new Dictionary<string, string> { ["kind"] = "tag" })
            ]);
            Assert.Equal("false", vm.CreateEvaluationContext().Computed["hasParsedTag"].GetString());

            vm.SetStateValueWithoutRebuild("state.draftTitle", "明天 下午评审 !p1 #研发", notifyLiveBindings: true);
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "submitDraft")]);
            Assert.Equal(string.Empty, vm.State["draftTitle"]);

            await vm.ExecuteActionsAsync([
                new CustomWidgetActionSpec("script.run", Fn: "setFilter", Args: new Dictionary<string, string> { ["filter"] = "all" })
            ]);
            Assert.Equal("all", vm.State["filter"]);

            Assert.True(CustomWidgetNativeRenderer.TryResolveComputedRecords(
                "${$computed.pendingTasks}",
                vm.CreateEvaluationContext(),
                out IReadOnlyList<CustomWidgetRecord> allPending));
            CustomWidgetRecord created = Assert.Single(allPending, r => r.GetField("title") == "下午评审");
            Assert.Equal("研发", created.GetField("tagText"));
            Assert.Equal("true", created.GetField("hasTag"));
            Assert.Equal("#F43F5E", created.GetField("railColor"));

            // Selecting calendar date switches triage filter back to 'day'
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "onCalendarDatePicked")]);
            Assert.Equal("day", vm.State["filter"]);

            // Starting task focus sets focusTaskId, currentTask, and activates Pomodoro running state
            await vm.ExecuteActionsAsync([
                new CustomWidgetActionSpec(
                    "script.run",
                    Fn: "startTaskFocus",
                    Args: new Dictionary<string, string>
                    {
                        ["id"] = created.Id,
                        ["title"] = created.GetField("title")
                    })
            ]);
            CustomWidgetEvaluationContext focusCtx = vm.CreateEvaluationContext();
            Assert.Equal("true", focusCtx.Computed["focusActive"].GetString());
            Assert.Equal("true", vm.State["pomo.running"]);
            Assert.Equal("下午评审", vm.State["currentTask"]);

            // Capsule primaryAction pauses active Pomodoro focus first
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "onCapsulePrimaryAction")]);
            Assert.Equal("false", vm.State["pomo.running"]);

            // Stopping focus clears focus state; next capsule primaryAction completes top pending task
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "stopTaskFocus")]);
            Assert.Equal("false", vm.CreateEvaluationContext().Computed["focusActive"].GetString());

            int pendingBeforeCapsuleDone = vm.Collections["tasks"].Count(r => r.GetField("done") != "true");
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "onCapsulePrimaryAction")]);
            int pendingAfterCapsuleDone = vm.Collections["tasks"].Count(r => r.GetField("done") != "true");
            Assert.Equal(pendingBeforeCapsuleDone - 1, pendingAfterCapsuleDone);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
