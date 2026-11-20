#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DeskBox.Services;
using Microsoft.Windows.AppNotifications;

namespace DeskBox;

public partial class App
{
    private const string AotTodoNotificationSmokeEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_SMOKE";
    private const string AotTodoNotificationPhaseEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_PHASE";
    private const string AotTodoNotificationRunIdEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_RUN_ID";
    private const string AotTodoNotificationScenario = "RealDisplayAndCleanup";
    private const string AotTodoNotificationShowPhase = "ShowAndInspect";
    private const string AotTodoNotificationCleanupPhase = "Cleanup";
    private const string AotTodoNotificationPostflightPhase = "Postflight";
    private const string AotTodoNotificationSmokeDirectoryName =
        "aot-todo-notification-smoke";

    private void StartAotTodoNotificationLifecycleSmokeIfRequested()
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotTodoNotificationSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        string? phase = Environment.GetEnvironmentVariable(
            AotTodoNotificationPhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            AotTodoNotificationRunIdEnvironmentVariable);
        if (!string.Equals(
                scenario.Trim(),
                AotTodoNotificationScenario,
                StringComparison.Ordinal) ||
            !IsAotTodoNotificationPhase(phase) ||
            !Guid.TryParseExact(runId, "N", out _))
        {
            Log(
                $"[AotTodoNotificationSmoke] Refused unsupported request " +
                $"scenario='{scenario}' phase='{phase}' runId='{runId}'.");
            return;
        }

        _ = RunAotTodoNotificationLifecycleSmokeAsync(phase!, runId!);
    }

    private async Task RunAotTodoNotificationLifecycleSmokeAsync(
        string phase,
        string runId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !AotTodoNotificationPathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotTodoNotificationSmoke] RefusedNonPreviewRoot: real notification " +
                "display requires an explicit isolated Native AOT preview root.");
            return;
        }

        string smokeRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationSmokeDirectoryName));
        string phaseRoot = Path.GetFullPath(Path.Combine(
            smokeRoot,
            phase.ToLowerInvariant()));
        if (!AotTodoNotificationIsPathEqualOrInside(dataPaths.RootPath, smokeRoot) ||
            !AotTodoNotificationIsPathEqualOrInside(smokeRoot, phaseRoot))
        {
            Log(
                $"[AotTodoNotificationSmoke] Refused unsafe result root '{phaseRoot}'.");
            return;
        }

        Directory.CreateDirectory(phaseRoot);
        string resultPath = Path.Combine(phaseRoot, "result.json");
        string group = $"db-c3b1-{runId}";
        string singleTag = $"single-{runId}";
        string aggregateTag = $"aggregate-{runId}";
        var result = new AotTodoNotificationSmokeResult
        {
            SchemaVersion = 1,
            Stage = "5B-4C3B1",
            Scenario = AotTodoNotificationScenario,
            Phase = phase,
            RunId = runId,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            PreviewDataRoot = dataPaths.RootPath,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Group = group,
            SingleTag = singleTag,
            AggregateTag = aggregateTag,
            Steps = []
        };
        WriteAotTodoNotificationResult(resultPath, result);

        NativeAppNotificationService? notificationService = _nativeNotificationService;
        try
        {
            RequireAotTodoNotification(
                result,
                notificationService is not null,
                "native-notification-service-created",
                "The product native notification service was not created.");
            result.RegisteredAtStart = notificationService!.IsRegistered;
            RequireAotTodoNotification(
                result,
                result.RegisteredAtStart,
                "native-notification-registered",
                "The product native notification service was not registered.");

            AppNotificationSetting setting = AppNotificationManager.Default.Setting;
            result.NotificationSetting = setting.ToString();
            RequireAotTodoNotification(
                result,
                setting == AppNotificationSetting.Enabled,
                "system-notifications-enabled",
                $"Windows app notifications are not enabled for DeskBox: {setting}.");

            await CaptureAotTodoNotificationPhaseAsync(result, notificationService);
            result.ExecutableSha256 = ComputeAotTodoNotificationSha256(
                result.ExecutablePath);
            RequireAotTodoNotification(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Todo notification smoke did not run inside Native AOT.");
            result.UnregisterSucceeded = notificationService.Unregister();
            result.RegisteredAfterUnregister = notificationService.IsRegistered;
            RequireAotTodoNotification(
                result,
                result.UnregisterSucceeded && !result.RegisteredAfterUnregister,
                "native-notification-unregistered",
                "The native notification registration was not released cleanly.");
            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log($"[AotTodoNotificationSmoke] Phase {phase} failed: {ex}");
            if (notificationService is not null)
            {
                await CleanupAotTodoNotificationsBestEffortAsync(
                    result,
                    notificationService);
                result.UnregisterSucceeded = notificationService.Unregister();
                result.RegisteredAfterUnregister = notificationService.IsRegistered;
            }
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.NormalShutdownRequested = true;
            WriteAotTodoNotificationResult(resultPath, result);
            Log(
                $"[AotTodoNotificationSmoke] phase={phase} state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task CaptureAotTodoNotificationPhaseAsync(
        AotTodoNotificationSmokeResult result,
        NativeAppNotificationService notificationService)
    {
        switch (result.Phase)
        {
            case AotTodoNotificationShowPhase:
                await ShowAndInspectAotTodoNotificationsAsync(result, notificationService);
                break;
            case AotTodoNotificationCleanupPhase:
                await CleanupAotTodoNotificationsAsync(result, notificationService);
                break;
            case AotTodoNotificationPostflightPhase:
                await VerifyAotTodoNotificationPostflightAsync(result, notificationService);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo notification phase '{result.Phase}'.");
        }
    }

    private async Task ShowAndInspectAotTodoNotificationsAsync(
        AotTodoNotificationSmokeResult result,
        NativeAppNotificationService notificationService)
    {
        IReadOnlyList<NativeAppNotificationSnapshot> before =
            await GetOwnedAotTodoNotificationsAsync(notificationService, result.Group);
        result.NotificationCountBefore = before.Count;
        RequireAotTodoNotification(
            result,
            before.Count == 0,
            "owned-history-empty-before-show",
            "The unique run group was not empty before notification display.");

        string widgetId = $"aot-c3b1-widget-{result.RunId}";
        string singleItemId = $"aot-c3b1-single-{result.RunId}";
        string aggregateItemId = $"aot-c3b1-aggregate-{result.RunId}";
        const string singleTitle = "DeskBox Todo AOT 验证（单项）";
        const string singleMessage = "临时测试通知，将在几秒内自动清理。";
        const string aggregateTitle = "DeskBox Todo AOT 验证（聚合）";
        const string aggregateMessage = "3 个临时提醒，仅用于验证通知结构。";

        result.SystemNotificationAttempted = true;
        result.SingleShowSucceeded = TryShowNativeTodoReminderNotification(
            new TodoReminderNotification(
                singleTitle,
                singleMessage,
                1,
                widgetId,
                singleItemId,
                HasTodayDueItem: true),
            new NativeAppNotificationOptions(result.SingleTag, result.Group));
        RequireAotTodoNotification(
            result,
            result.SingleShowSucceeded,
            "single-notification-show-returned-success",
            "The product single Todo notification path did not report success.");

        result.AggregateShowSucceeded = TryShowNativeTodoReminderNotification(
            new TodoReminderNotification(
                aggregateTitle,
                aggregateMessage,
                3,
                widgetId,
                aggregateItemId,
                HasTodayDueItem: false),
            new NativeAppNotificationOptions(result.AggregateTag, result.Group));
        RequireAotTodoNotification(
            result,
            result.AggregateShowSucceeded,
            "aggregate-notification-show-returned-success",
            "The product aggregate Todo notification path did not report success.");

        IReadOnlyList<NativeAppNotificationSnapshot> shown =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                result.Group,
                expectedCount: 2);
        result.NotificationCountAfter = shown.Count;
        result.Notifications = shown
            .OrderBy(notification => notification.Tag, StringComparer.Ordinal)
            .Select(CaptureAotTodoNotificationSnapshot)
            .ToList();
        RequireAotTodoNotification(
            result,
            shown.Count == 2,
            "notification-center-shows-two-owned-items",
            "Notification Center did not retain both owned notifications.");

        NativeAppNotificationSnapshot single = shown.Single(notification =>
            string.Equals(notification.Tag, result.SingleTag, StringComparison.Ordinal));
        NativeAppNotificationSnapshot aggregate = shown.Single(notification =>
            string.Equals(notification.Tag, result.AggregateTag, StringComparison.Ordinal));
        result.SinglePayload = InspectAotTodoNotificationPayload(single);
        result.AggregatePayload = InspectAotTodoNotificationPayload(aggregate);

        RequireAotTodoNotification(
            result,
            ValidateSingleAotTodoPayload(
                result.SinglePayload,
                result.Group,
                result.SingleTag,
                widgetId,
                singleItemId,
                singleTitle,
                singleMessage),
            "single-payload-actions-and-snooze-options-exact",
            "The single Todo notification payload did not contain the expected launch, actions, input, and four snooze options.");
        RequireAotTodoNotification(
            result,
            ValidateAggregateAotTodoPayload(
                result.AggregatePayload,
                result.Group,
                result.AggregateTag,
                widgetId,
                aggregateItemId,
                aggregateTitle,
                aggregateMessage),
            "aggregate-payload-has-no-actions",
            "The aggregate Todo notification payload did not carry the exact view action or had unexpected launch data.");
        RequireAotTodoNotification(
            result,
            result.SystemNotificationAttempted &&
            result.SingleShowSucceeded &&
            result.AggregateShowSucceeded,
            "real-system-notification-display-proved",
            "The real notification display boundary was not entered successfully.");
    }

    private static async Task CleanupAotTodoNotificationsAsync(
        AotTodoNotificationSmokeResult result,
        NativeAppNotificationService notificationService)
    {
        IReadOnlyList<NativeAppNotificationSnapshot> before =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                result.Group,
                expectedCount: 2);
        result.NotificationCountBefore = before.Count;
        result.Notifications = before
            .OrderBy(notification => notification.Tag, StringComparer.Ordinal)
            .Select(CaptureAotTodoNotificationSnapshot)
            .ToList();
        RequireAotTodoNotification(
            result,
            before.Count == 2 &&
            before.Any(notification => notification.Tag == result.SingleTag) &&
            before.Any(notification => notification.Tag == result.AggregateTag),
            "cross-process-history-reloaded",
            "The second process did not reload both owned notification records.");

        await notificationService.RemoveByTagAndGroupAsync(
            result.SingleTag,
            result.Group);
        result.SingleCleanupSucceeded = true;
        IReadOnlyList<NativeAppNotificationSnapshot> afterSingle =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                result.Group,
                expectedCount: 1);
        RequireAotTodoNotification(
            result,
            afterSingle.Count == 1 &&
            string.Equals(
                afterSingle[0].Tag,
                result.AggregateTag,
                StringComparison.Ordinal),
            "single-tag-group-cleanup-exact",
            "Exact single tag/group cleanup removed the wrong notification set.");

        await notificationService.RemoveByTagAndGroupAsync(
            result.AggregateTag,
            result.Group);
        result.AggregateCleanupSucceeded = true;
        IReadOnlyList<NativeAppNotificationSnapshot> after =
            await WaitForOwnedAotTodoNotificationCountAsync(
                notificationService,
                result.Group,
                expectedCount: 0);
        result.NotificationCountAfter = after.Count;
        RequireAotTodoNotification(
            result,
            after.Count == 0,
            "aggregate-tag-group-cleanup-exact",
            "Exact aggregate tag/group cleanup left an owned notification behind.");
        RequireAotTodoNotification(
            result,
            !result.SystemNotificationAttempted,
            "cleanup-process-did-not-display",
            "The cleanup process unexpectedly attempted to show a notification.");
    }

    private static async Task VerifyAotTodoNotificationPostflightAsync(
        AotTodoNotificationSmokeResult result,
        NativeAppNotificationService notificationService)
    {
        IReadOnlyList<NativeAppNotificationSnapshot> remaining =
            await GetOwnedAotTodoNotificationsAsync(notificationService, result.Group);
        result.NotificationCountBefore = remaining.Count;
        result.NotificationCountAfter = remaining.Count;
        RequireAotTodoNotification(
            result,
            remaining.Count == 0,
            "new-process-postflight-empty",
            "A fresh process still found an owned notification in Notification Center.");
        RequireAotTodoNotification(
            result,
            !result.SystemNotificationAttempted,
            "postflight-process-did-not-display",
            "The postflight process unexpectedly attempted to show a notification.");
    }

    private static async Task<IReadOnlyList<NativeAppNotificationSnapshot>>
        WaitForOwnedAotTodoNotificationCountAsync(
            NativeAppNotificationService notificationService,
            string group,
            int expectedCount)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        IReadOnlyList<NativeAppNotificationSnapshot> current = [];
        do
        {
            current = await GetOwnedAotTodoNotificationsAsync(
                notificationService,
                group);
            if (current.Count == expectedCount)
            {
                return current;
            }

            await Task.Delay(150);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return current;
    }

    private static async Task<IReadOnlyList<NativeAppNotificationSnapshot>>
        GetOwnedAotTodoNotificationsAsync(
            NativeAppNotificationService notificationService,
            string group)
    {
        IReadOnlyList<NativeAppNotificationSnapshot> notifications =
            await notificationService.GetAllAsync();
        return notifications
            .Where(notification => string.Equals(
                notification.Group,
                group,
                StringComparison.Ordinal))
            .ToArray();
    }

    private static AotTodoNativeNotificationEvidence CaptureAotTodoNotificationSnapshot(
        NativeAppNotificationSnapshot snapshot) =>
        new()
        {
            Id = snapshot.Id,
            Tag = snapshot.Tag,
            Group = snapshot.Group,
            Payload = snapshot.Payload
        };

    private static AotTodoNativeNotificationPayloadEvidence InspectAotTodoNotificationPayload(
        NativeAppNotificationSnapshot snapshot)
    {
        XDocument document = XDocument.Parse(snapshot.Payload, LoadOptions.None);
        XElement toast = document.Root ??
            throw new InvalidOperationException("The app notification payload has no root element.");
        string launch = toast.Attribute("launch")?.Value ?? string.Empty;
        List<XElement> actions = toast
            .Descendants()
            .Where(element => element.Name.LocalName == "action")
            .ToList();
        XElement? input = toast
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "input");
        return new AotTodoNativeNotificationPayloadEvidence
        {
            Tag = snapshot.Tag,
            Group = snapshot.Group,
            RootName = toast.Name.LocalName,
            LaunchArguments = ParseAotTodoNotificationArguments(launch),
            Texts = toast
                .Descendants()
                .Where(element => element.Name.LocalName == "text")
                .Select(element => element.Value)
                .ToList(),
            InputId = input?.Attribute("id")?.Value,
            InputType = input?.Attribute("type")?.Value,
            InputDefaultSelectionId = input?.Attribute("defaultInput")?.Value,
            InputSelectionIds = input?
                .Elements()
                .Where(element => element.Name.LocalName == "selection")
                .Select(element => element.Attribute("id")?.Value ?? string.Empty)
                .ToList() ?? [],
            Actions = actions
                .Select(action => new AotTodoNativeNotificationActionEvidence
                {
                    Content = action.Attribute("content")?.Value ?? string.Empty,
                    InputId = action.Attribute("hint-inputId")?.Value,
                    Arguments = ParseAotTodoNotificationArguments(
                        action.Attribute("arguments")?.Value ?? string.Empty)
                })
                .ToList()
        };
    }

    private static bool ValidateSingleAotTodoPayload(
        AotTodoNativeNotificationPayloadEvidence payload,
        string group,
        string tag,
        string widgetId,
        string itemId,
        string title,
        string message)
    {
        if (payload.RootName != "toast" ||
            payload.Tag != tag ||
            payload.Group != group ||
            !payload.Texts.SequenceEqual([title, message]) ||
            payload.InputId != TodoReminderSnoozeInputId ||
            payload.InputType != "selection" ||
            payload.InputDefaultSelectionId != TodoReminderSnooze10Minutes ||
            !payload.InputSelectionIds
                .Order(StringComparer.Ordinal)
                .SequenceEqual(new[]
                {
                    TodoReminderSnooze10Minutes,
                    TodoReminderSnooze30Minutes,
                    TodoReminderSnooze1Hour,
                    TodoReminderSnoozeTomorrow
                }.Order(StringComparer.Ordinal)) ||
            !HasAotTodoNotificationArguments(
                payload.LaunchArguments,
                widgetId,
                itemId,
                "today",
                action: null) ||
            payload.Actions.Count != 2)
        {
            return false;
        }

        AotTodoNativeNotificationActionEvidence? complete = payload.Actions
            .SingleOrDefault(action => action.Arguments.TryGetValue(
                "action",
                out string? value) && value == TodoReminderActionComplete);
        AotTodoNativeNotificationActionEvidence? snooze = payload.Actions
            .SingleOrDefault(action => action.Arguments.TryGetValue(
                "action",
                out string? value) && value == TodoReminderActionSnooze);
        return complete is not null &&
            string.IsNullOrWhiteSpace(complete.InputId) &&
            HasAotTodoNotificationArguments(
                complete.Arguments,
                widgetId,
                itemId,
                view: null,
                TodoReminderActionComplete) &&
            snooze is not null &&
            snooze.InputId == TodoReminderSnoozeInputId &&
            HasAotTodoNotificationArguments(
                snooze.Arguments,
                widgetId,
                itemId,
                view: null,
                TodoReminderActionSnooze);
    }

    private static bool ValidateAggregateAotTodoPayload(
        AotTodoNativeNotificationPayloadEvidence payload,
        string group,
        string tag,
        string widgetId,
        string itemId,
        string title,
        string message)
    {
        if (payload.RootName != "toast" ||
            payload.Tag != tag ||
            payload.Group != group ||
            !payload.Texts.SequenceEqual([title, message]) ||
            // Aggregates deliberately carry no selection input: there is no
            // single completable target, so the product keeps the plain toast
            // lifecycle with one view action reusing the body arguments.
            !string.IsNullOrWhiteSpace(payload.InputId) ||
            payload.InputSelectionIds.Count != 0 ||
            payload.Actions.Count != 1 ||
            !HasAotTodoNotificationArguments(
                payload.LaunchArguments,
                widgetId,
                itemId,
                "all",
                action: null))
        {
            return false;
        }

        AotTodoNativeNotificationActionEvidence view = payload.Actions.Single();
        return string.IsNullOrWhiteSpace(view.InputId) &&
            HasAotTodoNotificationArguments(
                view.Arguments,
                widgetId,
                itemId,
                "all",
                action: null);
    }

    private static bool HasAotTodoNotificationArguments(
        IReadOnlyDictionary<string, string> arguments,
        string widgetId,
        string itemId,
        string? view,
        string? action)
    {
        if (!arguments.TryGetValue("source", out string? source) ||
            source != TodoReminderSourceValue ||
            !arguments.TryGetValue("widgetId", out string? actualWidgetId) ||
            actualWidgetId != widgetId ||
            !arguments.TryGetValue("itemId", out string? actualItemId) ||
            actualItemId != itemId)
        {
            return false;
        }

        bool viewMatches = view is null
            ? !arguments.ContainsKey("view")
            : arguments.TryGetValue("view", out string? actualView) &&
              actualView == view;
        bool actionMatches = action is null
            ? !arguments.ContainsKey("action")
            : arguments.TryGetValue("action", out string? actualAction) &&
              actualAction == action;
        return viewMatches && actionMatches;
    }

    private static Dictionary<string, string> ParseAotTodoNotificationArguments(
        string arguments)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in arguments.Split(
                     ['&', ';'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            int separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separatorIndex]);
            string value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                parsed[key] = value;
            }
        }

        return parsed;
    }

    private static async Task CleanupAotTodoNotificationsBestEffortAsync(
        AotTodoNotificationSmokeResult result,
        NativeAppNotificationService notificationService)
    {
        result.CompensationAttempted = true;
        try
        {
            await notificationService.RemoveByTagAndGroupAsync(
                result.SingleTag,
                result.Group);
            await notificationService.RemoveByTagAndGroupAsync(
                result.AggregateTag,
                result.Group);
            result.CompensationSucceeded =
                (await GetOwnedAotTodoNotificationsAsync(
                    notificationService,
                    result.Group)).Count == 0;
        }
        catch (Exception cleanupException)
        {
            result.CompensationError = cleanupException.ToString();
            Log(
                $"[AotTodoNotificationSmoke] Best-effort cleanup failed: " +
                cleanupException);
        }
    }

    private static void RequireAotTodoNotification(
        AotTodoNotificationSmokeResult result,
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

    private static bool IsAotTodoNotificationPhase(string? phase) =>
        phase is AotTodoNotificationShowPhase or
            AotTodoNotificationCleanupPhase or
            AotTodoNotificationPostflightPhase;

    private static void WriteAotTodoNotificationResult(
        string resultPath,
        AotTodoNotificationSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotTodoNotificationJsonContext.Default.AotTodoNotificationSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string ComputeAotTodoNotificationSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool AotTodoNotificationPathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool AotTodoNotificationIsPathEqualOrInside(
        string root,
        string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return AotTodoNotificationPathsEqual(normalizedRoot, normalizedCandidate) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AotTodoNotificationSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public string NotificationSetting { get; set; } = string.Empty;
    public bool RegisteredAtStart { get; set; }
    public bool UnregisterSucceeded { get; set; }
    public bool RegisteredAfterUnregister { get; set; }
    public bool SystemNotificationAttempted { get; set; }
    public bool SingleShowSucceeded { get; set; }
    public bool AggregateShowSucceeded { get; set; }
    public string Group { get; set; } = string.Empty;
    public string SingleTag { get; set; } = string.Empty;
    public string AggregateTag { get; set; } = string.Empty;
    public int NotificationCountBefore { get; set; }
    public int NotificationCountAfter { get; set; }
    public bool SingleCleanupSucceeded { get; set; }
    public bool AggregateCleanupSucceeded { get; set; }
    public bool CompensationAttempted { get; set; }
    public bool CompensationSucceeded { get; set; }
    public string? CompensationError { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public List<AotTodoNativeNotificationEvidence> Notifications { get; set; } = [];
    public AotTodoNativeNotificationPayloadEvidence? SinglePayload { get; set; }
    public AotTodoNativeNotificationPayloadEvidence? AggregatePayload { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

internal sealed class AotTodoNativeNotificationEvidence
{
    public uint Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

internal sealed class AotTodoNativeNotificationPayloadEvidence
{
    public string Tag { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string RootName { get; set; } = string.Empty;
    public Dictionary<string, string> LaunchArguments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> Texts { get; set; } = [];
    public string? InputId { get; set; }
    public string? InputType { get; set; }
    public string? InputDefaultSelectionId { get; set; }
    public List<string> InputSelectionIds { get; set; } = [];
    public List<AotTodoNativeNotificationActionEvidence> Actions { get; set; } = [];
}

internal sealed class AotTodoNativeNotificationActionEvidence
{
    public string Content { get; set; } = string.Empty;
    public string? InputId { get; set; }
    public Dictionary<string, string> Arguments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotTodoNotificationSmokeResult),
    TypeInfoPropertyName = "AotTodoNotificationSmokeResult")]
internal partial class AotTodoNotificationJsonContext : JsonSerializerContext
{
}
#endif
