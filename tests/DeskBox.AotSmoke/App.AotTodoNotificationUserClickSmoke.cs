#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using System.Runtime.CompilerServices;

namespace DeskBox;

public partial class App
{
    private const string AotTodoNotificationUserClickEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_SMOKE";
    private const string AotTodoNotificationUserClickPhaseEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_PHASE";
    private const string AotTodoNotificationUserClickRunIdEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_RUN_ID";
    private const string AotTodoNotificationUserClickScenario =
        "RealWindowsNotificationUserClick";
    private const string AotTodoNotificationUserClickRunningPhase =
        "RunningMatrix";
    private const string AotTodoNotificationUserClickColdSeedPhase =
        "ColdSeed";
    private const string AotTodoNotificationUserClickColdConsumePhase =
        "ColdConsume";
    private const string AotTodoNotificationUserClickPostflightPhase =
        "Postflight";
    private const string AotTodoNotificationUserClickDirectoryName =
        "aot-todo-notification-user-click-smoke";
    private const string AotTodoNotificationUserClickWidgetId =
        "aot-5b4c3b2b2b-todo";
    private const string AotTodoNotificationUserClickBodySuffix = "body";
    private const string AotTodoNotificationUserClickCompleteSuffix = "complete";
    private const string AotTodoNotificationUserClickSnoozeSuffix = "snooze";
    private const string AotTodoNotificationUserClickColdSuffix = "cold";

    private static readonly DateTimeOffset AotTodoNotificationUserClickClock =
        new(2026, 8, 25, 9, 30, 0, TimeSpan.FromHours(8));

    private readonly List<NativeAppNotificationActivation>
        _aotTodoNotificationUserClickActivations = [];
    private readonly List<AotTodoNotificationUserClickRouteObservation>
        _aotTodoNotificationUserClickRoutes = [];

    private static DateTimeOffset? TryGetAotTodoNotificationUserClickClock()
    {
        return IsAotTodoNotificationUserClickRequest()
            ? AotTodoNotificationUserClickClock
            : null;
    }

    private static bool ShouldSuppressAotTodoNotificationUserClickConfirmation()
    {
        return IsAotTodoNotificationUserClickRequest();
    }

    private static bool IsAotTodoNotificationUserClickRequest()
    {
        return string.Equals(
                   Environment.GetEnvironmentVariable(
                       AotTodoNotificationUserClickEnvironmentVariable),
                   AotTodoNotificationUserClickScenario,
                   StringComparison.Ordinal) &&
               IsAotTodoNotificationUserClickPhase(
                   Environment.GetEnvironmentVariable(
                       AotTodoNotificationUserClickPhaseEnvironmentVariable)) &&
               Guid.TryParseExact(
                   Environment.GetEnvironmentVariable(
                       AotTodoNotificationUserClickRunIdEnvironmentVariable),
                   "N",
                   out _);
    }

    private static bool IsAotTodoNotificationUserClickPhase(string? phase)
    {
        return phase is
            AotTodoNotificationUserClickRunningPhase or
            AotTodoNotificationUserClickColdSeedPhase or
            AotTodoNotificationUserClickColdConsumePhase or
            AotTodoNotificationUserClickPostflightPhase;
    }

    partial void OnNativeNotificationActivationObserved(
        NativeAppNotificationActivation activation)
    {
        if (IsAotTodoNotificationUserClickActivation(activation))
        {
            _aotTodoNotificationUserClickActivations.Add(activation);
        }
    }

    partial void OnTodoNotificationActivationRouteObserved(
        NativeAppNotificationActivation? activation,
        TodoNotificationActivationRouteResult result)
    {
        if (activation is not null &&
            IsAotTodoNotificationUserClickActivation(activation))
        {
            _aotTodoNotificationUserClickRoutes.Add(
                new AotTodoNotificationUserClickRouteObservation(
                    activation,
                    result,
                    Environment.ProcessId,
                    DateTimeOffset.UtcNow));
        }
    }

    private static bool IsAotTodoNotificationUserClickActivation(
        NativeAppNotificationActivation activation)
    {
        if (!IsAotTodoNotificationUserClickRequest())
        {
            return false;
        }

        string? runId = Environment.GetEnvironmentVariable(
            AotTodoNotificationUserClickRunIdEnvironmentVariable);
        Dictionary<string, string> arguments = ParseNotificationArguments(
            activation.Arguments);
        return arguments.TryGetValue("widgetId", out string? widgetId) &&
               arguments.TryGetValue("itemId", out string? itemId) &&
               string.Equals(
                   widgetId,
                   AotTodoNotificationUserClickWidgetId,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(runId) &&
               itemId.StartsWith(
                   $"aot-click-{runId}-",
                   StringComparison.Ordinal);
    }

    private void StartAotTodoNotificationUserClickSmokeIfRequested()
    {
        if (IsAotTodoNotificationUserClickRequest())
        {
            _ = RunAotTodoNotificationUserClickSmokeAsync();
        }
    }

    private async Task RunAotTodoNotificationUserClickSmokeAsync()
    {
        await Task.Yield();

        string phase = Environment.GetEnvironmentVariable(
            AotTodoNotificationUserClickPhaseEnvironmentVariable)!;
        string runId = Environment.GetEnvironmentVariable(
            AotTodoNotificationUserClickRunIdEnvironmentVariable)!;
        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !IsAotManagedUiPathEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotTodoNotificationUserClick] RefusedNonPreviewRoot: real " +
                "notification clicks require an explicit isolated AOT root.");
            return;
        }

        string evidenceRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationUserClickDirectoryName));
        string phaseRoot = Path.GetFullPath(Path.Combine(
            evidenceRoot,
            phase.ToLowerInvariant()));
        if (!IsAotManagedUiPathEqualOrInside(dataPaths.RootPath, evidenceRoot) ||
            IsAotManagedUiPathEqual(dataPaths.RootPath, evidenceRoot) ||
            !IsAotManagedUiPathEqualOrInside(evidenceRoot, phaseRoot))
        {
            Log(
                $"[AotTodoNotificationUserClick] Refused unsafe evidence root " +
                $"'{phaseRoot}'.");
            return;
        }

        Directory.CreateDirectory(phaseRoot);
        string resultPath = Path.Combine(phaseRoot, "result.json");
        var evidence = new AotTodoNotificationUserClickEvidence
        {
            Stage = "5B-4C3B2B2B",
            Phase = phase,
            RunId = runId,
            FixedClock = AotTodoNotificationUserClickClock,
            WidgetId = AotTodoNotificationUserClickWidgetId,
            ReceivingProcessId = Environment.ProcessId,
            SystemNotificationAttempted = false,
            ExternalWindowsActivationObserved = false,
            UserClickVerified = false,
            NormalShutdownRequested = true
        };
        var result = new AotManagedUiSmokeResult
        {
            SchemaVersion = 1,
            Scenario = AotTodoNotificationUserClickScenario,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            EvidenceRoot = evidenceRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            TodoNotificationUserClick = evidence
        };
        WriteAotManagedUiResult(resultPath, result);

        NativeAppNotificationService? notificationService =
            _nativeNotificationService;
        bool preserveNotificationForColdClick = false;
        try
        {
            RequireAotTodoNotificationUserClick(
                result,
                !RuntimeFeature.IsDynamicCodeSupported,
                "runtime-native-aot",
                "The real notification click fixture did not run as Native AOT.");
            RequireAotTodoNotificationUserClick(
                result,
                WidgetManager is not null &&
                _todoReminderService is not null &&
                notificationService is { IsRegistered: true },
                "product-services-and-notification-registration-ready",
                "The product notification or Todo services were unavailable.");

            switch (phase)
            {
                case AotTodoNotificationUserClickRunningPhase:
                    await ConfigureAotTodoNotificationUserClickFixtureAsync(runId);
                    result.Steps.Add("isolated-fixture-seeded");
                    await ExecuteAotTodoNotificationUserClickCaseAsync(
                        result,
                        notificationService!,
                        runId,
                        AotTodoNotificationUserClickBodySuffix,
                        expectedAction: null,
                        expectedSnooze: null,
                        "请点击第 1/3 条通知的正文。");
                    await ExecuteAotTodoNotificationUserClickCaseAsync(
                        result,
                        notificationService!,
                        runId,
                        AotTodoNotificationUserClickCompleteSuffix,
                        TodoNotificationActivationRouter.ActionComplete,
                        expectedSnooze: null,
                        "请点击第 2/3 条通知中的“标记完成”按钮。");
                    await ExecuteAotTodoNotificationUserClickCaseAsync(
                        result,
                        notificationService!,
                        runId,
                        AotTodoNotificationUserClickSnoozeSuffix,
                        TodoNotificationActivationRouter.ActionSnooze,
                        TodoNotificationActivationRouter.Snooze30Minutes,
                        "请在第 3/3 条通知中选择“30 分钟”，再点击“稍后提醒”。");
                    evidence.UserClickVerified =
                        evidence.Cases.Count == 3 &&
                        evidence.Cases.All(item => item.UserClickVerified);
                    RequireAotTodoNotificationUserClick(
                        result,
                        evidence.UserClickVerified,
                        "running-body-complete-snooze-user-clicks-verified",
                        "The running-process user click matrix was incomplete.");
                    result.Success = true;
                    result.State = "Completed";
                    break;

                case AotTodoNotificationUserClickColdSeedPhase:
                    await ConfigureAotTodoNotificationUserClickFixtureAsync(runId);
                    result.Steps.Add("isolated-fixture-seeded");
                    await ShowAotTodoNotificationUserClickCaseAsync(
                        result,
                        notificationService!,
                        runId,
                        AotTodoNotificationUserClickColdSuffix,
                        "冷启动验证：应用退出后，请点击这条通知的正文。");
                    evidence.CurrentInstruction =
                        "应用将退出。退出后请点击保留在通知中心的冷启动验证通知。";
                    evidence.SystemNotificationAttempted = true;
                    preserveNotificationForColdClick = true;
                    RequireAotTodoNotificationUserClick(
                        result,
                        notificationService!.Unregister() &&
                        !notificationService.IsRegistered,
                        "cold-seed-notification-registration-released",
                        "The cold-seed process did not release notification registration.");
                    result.Success = true;
                    result.State = "ReadyForUserClick";
                    break;

                case AotTodoNotificationUserClickColdConsumePhase:
                    await CompleteAotTodoNotificationUserClickCaseAsync(
                        result,
                        notificationService!,
                        runId,
                        AotTodoNotificationUserClickColdSuffix,
                        expectedAction: null,
                        expectedSnooze: null,
                        routeStartIndex: 0);
                    evidence.UserClickVerified =
                        evidence.Cases.Count == 1 &&
                        evidence.Cases[0].UserClickVerified;
                    RequireAotTodoNotificationUserClick(
                        result,
                        evidence.UserClickVerified,
                        "cold-start-user-click-and-surface-verified",
                        "The cold-start notification click was not verified.");
                    result.Success = true;
                    result.State = "Completed";
                    break;

                case AotTodoNotificationUserClickPostflightPhase:
                    await CleanupAotTodoNotificationUserClickFixtureAsync(
                        notificationService!,
                        runId);
                    result.Steps.Add("notification-history-and-fixture-cleared");
                    result.Success = true;
                    result.State = "Completed";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log($"[AotTodoNotificationUserClick] Phase {phase} failed: {ex}");
            if (!preserveNotificationForColdClick && notificationService is not null)
            {
                await RemoveAotTodoNotificationUserClickGroupBestEffortAsync(
                    notificationService,
                    runId);
            }
        }
        finally
        {
            CompleteAotTodoNotificationUserClickAnimation();
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteAotManagedUiResult(resultPath, result);
            Log(
                $"[AotTodoNotificationUserClick] phase={phase} " +
                $"state={result.State} success={result.Success} " +
                $"result='{resultPath}'");
            await Task.Delay(150);
            await ShutdownApplicationAsync();
        }
    }

    private async Task ExecuteAotTodoNotificationUserClickCaseAsync(
        AotManagedUiSmokeResult result,
        NativeAppNotificationService notificationService,
        string runId,
        string caseName,
        string? expectedAction,
        string? expectedSnooze,
        string instruction)
    {
        int routeStartIndex = _aotTodoNotificationUserClickRoutes.Count;
        result.TodoNotificationUserClick!.CurrentInstruction = instruction;
        await ShowAotTodoNotificationUserClickCaseAsync(
            result,
            notificationService,
            runId,
            caseName,
            instruction);
        await CompleteAotTodoNotificationUserClickCaseAsync(
            result,
            notificationService,
            runId,
            caseName,
            expectedAction,
            expectedSnooze,
            routeStartIndex);
    }

    private async Task ShowAotTodoNotificationUserClickCaseAsync(
        AotManagedUiSmokeResult result,
        NativeAppNotificationService notificationService,
        string runId,
        string caseName,
        string instruction)
    {
        AotTodoNotificationUserClickEvidence evidence =
            result.TodoNotificationUserClick!;
        string itemId = GetAotTodoNotificationUserClickItemId(runId, caseName);
        string group = GetAotTodoNotificationUserClickGroup(runId);
        string tag = GetAotTodoNotificationUserClickTag(runId, caseName);
        IReadOnlyList<NativeAppNotificationSnapshot> before =
            await GetOwnedAotTodoNotificationsAsync(notificationService, group);
        RequireAotTodoNotificationUserClick(
            result,
            before.Count == 0,
            $"{caseName}-owned-history-empty-before-show",
            $"The notification group was not empty before '{caseName}'.");

        evidence.CurrentCase = caseName;
        evidence.CurrentInstruction = instruction;
        evidence.SystemNotificationAttempted = true;
        result.State = $"Awaiting{caseName}UserClick";
        WriteAotManagedUiResult(result.ResultPath, result);

        bool shown = TryShowNativeTodoReminderNotification(
            new TodoReminderNotification(
                $"DeskBox AOT 真人点击验证 · {caseName}",
                instruction,
                1,
                AotTodoNotificationUserClickWidgetId,
                itemId,
                HasTodayDueItem: false),
            new NativeAppNotificationOptions(tag, group));
        RequireAotTodoNotificationUserClick(
            result,
            shown,
            $"{caseName}-real-system-notification-shown",
            $"The product notification path failed to show '{caseName}'.");
        IReadOnlyList<NativeAppNotificationSnapshot> after =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                group,
                expectedCount: 1);
        RequireAotTodoNotificationUserClick(
            result,
            after.Count == 1 &&
            string.Equals(after[0].Tag, tag, StringComparison.Ordinal),
            $"{caseName}-notification-center-history-exact",
            $"Notification Center did not retain the exact '{caseName}' item.");
    }

    private async Task CompleteAotTodoNotificationUserClickCaseAsync(
        AotManagedUiSmokeResult result,
        NativeAppNotificationService notificationService,
        string runId,
        string caseName,
        string? expectedAction,
        string? expectedSnooze,
        int routeStartIndex)
    {
        AotTodoNotificationUserClickEvidence evidence =
            result.TodoNotificationUserClick!;
        string itemId = GetAotTodoNotificationUserClickItemId(runId, caseName);
        AotTodoNotificationUserClickRouteObservation observation =
            await WaitForAotTodoNotificationUserClickRouteAsync(
                itemId,
                routeStartIndex,
                TimeSpan.FromMinutes(10));
        await Task.Delay(350);
        List<AotTodoNotificationUserClickRouteObservation> matchingRoutes =
            _aotTodoNotificationUserClickRoutes
                .Skip(routeStartIndex)
                .Where(candidate => string.Equals(
                    candidate.Result.ItemId,
                    itemId,
                    StringComparison.Ordinal))
                .ToList();
        RequireAotTodoNotificationUserClick(
            result,
            matchingRoutes.Count == 1,
            $"{caseName}-exactly-one-external-route",
            $"Expected one route for '{caseName}', observed {matchingRoutes.Count}.");

        NativeAppNotificationActivation activation = observation.Activation;
        TodoNotificationActivationRouteResult route = observation.Result;
        bool sourceIsExternalWindows = activation.Source is
            NativeAppNotificationActivationSource.NotificationInvokedEvent or
            NativeAppNotificationActivationSource.CurrentAppInstance;
        bool actionMatches = string.IsNullOrWhiteSpace(expectedAction)
            ? string.IsNullOrWhiteSpace(route.Action)
            : string.Equals(route.Action, expectedAction, StringComparison.Ordinal);
        bool snoozeMatches = string.IsNullOrWhiteSpace(expectedSnooze)
            ? true
            : activation.UserInput.TryGetValue(
                  TodoNotificationActivationRouter.SnoozeInputId,
                  out string? actualSnooze) &&
              string.Equals(actualSnooze, expectedSnooze, StringComparison.Ordinal);
        RequireAotTodoNotificationUserClick(
            result,
            sourceIsExternalWindows &&
            activation.CapturedAtUtc != default &&
            activation.SourceProcessId > 0 &&
            route.Succeeded &&
            actionMatches &&
            snoozeMatches,
            $"{caseName}-typed-windows-activation-and-route-exact",
            $"The real Windows activation or route for '{caseName}' was inconsistent.");

        AotTodoNotificationSurfaceHostSnapshot host =
            await CaptureAotTodoNotificationSurfaceHostAsync(
                AotTodoNotificationUserClickWidgetId,
                itemId);
        bool surfaceMatches = caseName switch
        {
            AotTodoNotificationUserClickBodySuffix or
            AotTodoNotificationUserClickColdSuffix =>
                route.TargetPresented &&
                host.Visible &&
                host.HasXamlRoot &&
                host.ItemVisible &&
                host.ItemSelected,
            AotTodoNotificationUserClickCompleteSuffix =>
                route.RefreshCompleted &&
                host.Visible &&
                host.HasXamlRoot &&
                host.ItemVisible &&
                host.IsCompleted,
            AotTodoNotificationUserClickSnoozeSuffix =>
                route.RefreshCompleted &&
                host.Visible &&
                host.HasXamlRoot &&
                host.ItemVisible &&
                route.SnoozedUntil ==
                    AotTodoNotificationUserClickClock.AddMinutes(30) &&
                host.SnoozedUntil == route.SnoozedUntil,
            _ => false
        };
        RequireAotTodoNotificationUserClick(
            result,
            surfaceMatches,
            $"{caseName}-real-todo-surface-state-exact",
            $"The visible Todo surface did not reflect '{caseName}'.");

        var caseEvidence = new AotTodoNotificationUserClickCaseEvidence
        {
            Case = caseName,
            ItemId = itemId,
            ExpectedAction = expectedAction,
            ExpectedSnooze = expectedSnooze,
            ActivationSource = activation.Source.ToString(),
            ActivationCapturedAtUtc = activation.CapturedAtUtc,
            ActivationSourceProcessId = activation.SourceProcessId,
            ReceivingProcessId = observation.ReceivingProcessId,
            EnvelopeId = activation.EnvelopeId,
            ForwardedThroughEnvelope = !string.IsNullOrWhiteSpace(
                activation.EnvelopeId),
            Arguments = activation.Arguments,
            UserInput = activation.UserInput.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            RouteDisposition = route.Disposition,
            RouteSucceeded = route.Succeeded,
            TargetPresented = route.TargetPresented,
            RefreshCompleted = route.RefreshCompleted,
            WindowHandle = host.WindowHandle,
            Visible = host.Visible,
            HasXamlRoot = host.HasXamlRoot,
            ItemVisible = host.ItemVisible,
            ItemSelected = host.ItemSelected,
            IsCompleted = host.IsCompleted,
            SnoozedUntil = host.SnoozedUntil,
            UserClickVerified = true
        };
        evidence.Cases.Add(caseEvidence);
        evidence.ExternalWindowsActivationObserved = true;
        evidence.ActivationCount = _aotTodoNotificationUserClickActivations.Count;
        evidence.RouteCount = _aotTodoNotificationUserClickRoutes.Count;

        string group = GetAotTodoNotificationUserClickGroup(runId);
        string tag = GetAotTodoNotificationUserClickTag(runId, caseName);
        await notificationService.RemoveByTagAndGroupAsync(tag, group);
        IReadOnlyList<NativeAppNotificationSnapshot> remaining =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                group,
                expectedCount: 0);
        RequireAotTodoNotificationUserClick(
            result,
            remaining.Count == 0,
            $"{caseName}-notification-history-cleaned",
            $"The '{caseName}' notification was not cleaned precisely.");
        WriteAotManagedUiResult(result.ResultPath, result);
    }

    private async Task<AotTodoNotificationUserClickRouteObservation>
        WaitForAotTodoNotificationUserClickRouteAsync(
            string itemId,
            int startIndex,
            TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            AotTodoNotificationUserClickRouteObservation? match =
                _aotTodoNotificationUserClickRoutes
                    .Skip(startIndex)
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.Result.ItemId,
                        itemId,
                        StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for a real notification click for '{itemId}'.");
    }

    private async Task ConfigureAotTodoNotificationUserClickFixtureAsync(
        string runId)
    {
        var store = new TodoWidgetStore(AotTodoNotificationUserClickWidgetId);
        await store.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                CreateAotTodoNotificationUserClickItem(runId, AotTodoNotificationUserClickBodySuffix),
                CreateAotTodoNotificationUserClickItem(runId, AotTodoNotificationUserClickCompleteSuffix),
                CreateAotTodoNotificationUserClickItem(runId, AotTodoNotificationUserClickSnoozeSuffix),
                CreateAotTodoNotificationUserClickItem(runId, AotTodoNotificationUserClickColdSuffix)
            ]
        });

        SettingsService.Settings.Widgets.RemoveAll(widget => string.Equals(
            widget.Id,
            AotTodoNotificationUserClickWidgetId,
            StringComparison.Ordinal));
        SettingsService.Settings.DeletedWidgetIds.Remove(
            AotTodoNotificationUserClickWidgetId);
        FeatureWidgetSettings.SetEnabled(
            SettingsService.Settings,
            WidgetKind.Todo,
            true);
        SettingsService.Settings.TodoReminderEnabled = false;
        SettingsService.Settings.TodoShowCompletedTasks = true;
        SettingsService.Settings.TodoDefaultFilter = TodoFilter.All.ToString();
        SettingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Id = AotTodoNotificationUserClickWidgetId,
            Name = "AOT Todo Real Notification Click",
            WidgetKind = WidgetKind.Todo,
            IsVisible = false,
            IsDisabled = false,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 360,
            Height = 480
        });
        await SettingsService.SaveAsync();
    }

    private async Task CleanupAotTodoNotificationUserClickFixtureAsync(
        NativeAppNotificationService notificationService,
        string runId)
    {
        await RemoveAotTodoNotificationUserClickGroupBestEffortAsync(
            notificationService,
            runId);
        var store = new TodoWidgetStore(AotTodoNotificationUserClickWidgetId);
        await store.SaveAsync(new TodoWidgetData());
        SettingsService.Settings.Widgets.RemoveAll(widget => string.Equals(
            widget.Id,
            AotTodoNotificationUserClickWidgetId,
            StringComparison.Ordinal));
        SettingsService.Settings.DeletedWidgetIds.Remove(
            AotTodoNotificationUserClickWidgetId);
        await SettingsService.SaveAsync();

        IReadOnlyList<NativeAppNotificationSnapshot> remaining =
            await GetOwnedAotTodoNotificationsAsync(
                notificationService,
                GetAotTodoNotificationUserClickGroup(runId));
        if (remaining.Count != 0)
        {
            throw new InvalidOperationException(
                "Postflight still found owned notification history.");
        }
    }

    private static async Task RemoveAotTodoNotificationUserClickGroupBestEffortAsync(
        NativeAppNotificationService notificationService,
        string runId)
    {
        string group = GetAotTodoNotificationUserClickGroup(runId);
        foreach (string caseName in new[]
                 {
                     AotTodoNotificationUserClickBodySuffix,
                     AotTodoNotificationUserClickCompleteSuffix,
                     AotTodoNotificationUserClickSnoozeSuffix,
                     AotTodoNotificationUserClickColdSuffix
                 })
        {
            try
            {
                await notificationService.RemoveByTagAndGroupAsync(
                    GetAotTodoNotificationUserClickTag(runId, caseName),
                    group);
            }
            catch
            {
            }
        }
    }

    private void CompleteAotTodoNotificationUserClickAnimation()
    {
        if (WidgetManager?.ContentWidgets.TryGetValue(
                AotTodoNotificationUserClickWidgetId,
                out ContentWidgetWindow? window) == true)
        {
            window.CompleteTrayShowWithoutAnimation();
        }
    }

    private static TodoItem CreateAotTodoNotificationUserClickItem(
        string runId,
        string caseName)
    {
        string itemId = GetAotTodoNotificationUserClickItemId(runId, caseName);
        return new TodoItem
        {
            Id = itemId,
            Text = $"AOT real notification click {caseName}",
            DueDate = AotTodoNotificationUserClickClock.AddHours(2),
            ReminderOffsetMinutes = 5,
            CreatedAt = AotTodoNotificationUserClickClock.AddDays(-1),
            UpdatedAt = AotTodoNotificationUserClickClock.AddDays(-1)
        };
    }

    private static string GetAotTodoNotificationUserClickItemId(
        string runId,
        string caseName) => $"aot-click-{runId}-{caseName}";

    private static string GetAotTodoNotificationUserClickGroup(string runId) =>
        $"db-c3b2b2b-{runId}";

    private static string GetAotTodoNotificationUserClickTag(
        string runId,
        string caseName) => $"{caseName}-{runId}";

    private static void RequireAotTodoNotificationUserClick(
        AotManagedUiSmokeResult result,
        bool condition,
        string step,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{step}: {message}");
        }

        result.Steps.Add(step);
    }
}

internal sealed record AotTodoNotificationUserClickRouteObservation(
    NativeAppNotificationActivation Activation,
    TodoNotificationActivationRouteResult Result,
    int ReceivingProcessId,
    DateTimeOffset RoutedAtUtc);

internal sealed class AotTodoNotificationUserClickEvidence
{
    public string Stage { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset FixedClock { get; set; }
    public string WidgetId { get; set; } = string.Empty;
    public int ReceivingProcessId { get; set; }
    public string? CurrentCase { get; set; }
    public string? CurrentInstruction { get; set; }
    public bool SystemNotificationAttempted { get; set; }
    public bool ExternalWindowsActivationObserved { get; set; }
    public bool UserClickVerified { get; set; }
    public int ActivationCount { get; set; }
    public int RouteCount { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public List<AotTodoNotificationUserClickCaseEvidence> Cases { get; set; } = [];
}

internal sealed class AotTodoNotificationUserClickCaseEvidence
{
    public string Case { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string? ExpectedAction { get; set; }
    public string? ExpectedSnooze { get; set; }
    public string ActivationSource { get; set; } = string.Empty;
    public DateTimeOffset ActivationCapturedAtUtc { get; set; }
    public int ActivationSourceProcessId { get; set; }
    public int ReceivingProcessId { get; set; }
    public string? EnvelopeId { get; set; }
    public bool ForwardedThroughEnvelope { get; set; }
    public string Arguments { get; set; } = string.Empty;
    public Dictionary<string, string> UserInput { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string RouteDisposition { get; set; } = string.Empty;
    public bool RouteSucceeded { get; set; }
    public bool TargetPresented { get; set; }
    public bool RefreshCompleted { get; set; }
    public long WindowHandle { get; set; }
    public bool Visible { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool ItemVisible { get; set; }
    public bool ItemSelected { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public bool UserClickVerified { get; set; }
}
#endif
