#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotTodoNotificationForwardingSmokeEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_SMOKE";
    private const string AotTodoNotificationForwardingPhaseEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_PHASE";
    private const string AotTodoNotificationForwardingRunIdEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_FORWARDING_RUN_ID";
    private const string AotTodoNotificationForwardingScenario =
        "EnvelopeAndSingleInstance";
    private const string AotTodoNotificationForwardingSeedPhase = "SeedColdStart";
    private const string AotTodoNotificationForwardingColdPhase = "ColdStartConsume";
    private const string AotTodoNotificationForwardingPrimaryPhase = "PrimaryAwait";
    private const string AotTodoNotificationForwardingSecondaryPhase = "SecondaryForward";
    private const string AotTodoNotificationForwardingPostflightPhase = "Postflight";
    private const string AotTodoNotificationForwardingSmokeDirectoryName =
        "aot-todo-notification-forwarding-smoke";
    private const string AotTodoNotificationForwardingWidgetId = "aot-5b4c3b2b1-todo";
    private const string AotTodoNotificationForwardingColdItemId = "cold-start-snooze";
    private const string AotTodoNotificationForwardingLiveItemId = "live-forward-snooze";
    private const string AotTodoNotificationForwardingColdEnvelopeId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTimeOffset AotTodoNotificationForwardingClock =
        new(2026, 8, 25, 8, 15, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo AotTodoNotificationForwardingTimeZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "DeskBox.Aot.Forwarding.UTC+08",
            TimeSpan.FromHours(8),
            "DeskBox AOT Forwarding UTC+08",
            "DeskBox AOT Forwarding UTC+08");

    private readonly List<NativeNotificationActivationEnvelope>
        _aotTodoNotificationForwardingConsumed = [];
    private readonly List<string> _aotTodoNotificationForwardingRejections = [];

    private static NativeAppNotificationActivation?
        TryGetAotTodoNotificationForwardingActivation()
    {
        if (!IsAotTodoNotificationForwardingRequest(
                requiredPhase: AotTodoNotificationForwardingSecondaryPhase))
        {
            return null;
        }

        return new NativeAppNotificationActivation(
            CreateAotTodoNotificationForwardingArguments(
                AotTodoNotificationForwardingLiveItemId),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TodoNotificationActivationRouter.SnoozeInputId] =
                    TodoNotificationActivationRouter.SnoozeTomorrow
            });
    }

    private static DateTimeOffset? TryGetAotTodoNotificationForwardingClock()
    {
        return IsAotTodoNotificationForwardingRequest()
            ? AotTodoNotificationForwardingClock
            : null;
    }

    private static TimeZoneInfo? TryGetAotTodoNotificationForwardingTimeZone()
    {
        return IsAotTodoNotificationForwardingRequest()
            ? AotTodoNotificationForwardingTimeZone
            : null;
    }

    private static bool ShouldSuppressAotTodoNotificationForwardingSystemNotification()
    {
        return IsAotTodoNotificationForwardingRequest();
    }

    partial void OnPendingNativeNotificationActivationConsumed(
        NativeNotificationActivationEnvelope envelope)
    {
        if (IsAotTodoNotificationForwardingRequest())
        {
            _aotTodoNotificationForwardingConsumed.Add(envelope);
        }
    }

    partial void OnPendingNativeNotificationActivationRejected(
        string? path,
        string? error)
    {
        if (IsAotTodoNotificationForwardingRequest())
        {
            _aotTodoNotificationForwardingRejections.Add(
                $"{path ?? "none"}|{error ?? "unknown"}");
        }
    }

    private void StartAotTodoNotificationForwardingSmokeIfRequested()
    {
        if (!IsAotTodoNotificationForwardingRequest())
        {
            return;
        }

        string phase = Environment.GetEnvironmentVariable(
            AotTodoNotificationForwardingPhaseEnvironmentVariable)!;
        if (string.Equals(
                phase,
                AotTodoNotificationForwardingSecondaryPhase,
                StringComparison.Ordinal))
        {
            Log(
                "[AotTodoNotificationForwarding] Secondary unexpectedly reached OnLaunched.");
            return;
        }

        _ = RunAotTodoNotificationForwardingSmokeAsync(
            phase,
            Environment.GetEnvironmentVariable(
                AotTodoNotificationForwardingRunIdEnvironmentVariable)!);
    }

    private async Task RunAotTodoNotificationForwardingSmokeAsync(
        string phase,
        string runId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string resultDirectory = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationForwardingSmokeDirectoryName,
            phase.ToLowerInvariant()));
        string resultPath = Path.Combine(resultDirectory, "result.json");
        var result = new AotTodoNotificationForwardingSmokeResult
        {
            SchemaVersion = 1,
            Stage = "5B-4C3B2B1",
            Scenario = AotTodoNotificationForwardingScenario,
            Phase = phase,
            RunId = runId,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            PreviewDataRoot = dataPaths.RootPath,
            ResultPath = resultPath,
            FixedClock = AotTodoNotificationForwardingClock,
            TimeZoneId = AotTodoNotificationForwardingTimeZone.Id,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            SystemNotificationAttempted = false,
            ExternalWindowsActivationAttempted = false,
            Steps = []
        };

        try
        {
            RequireAotTodoNotificationForwarding(
                result,
                dataPaths.IsDevelopmentRoot &&
                IsAotTodoNotificationForwardingPathEqualOrInside(
                    dataPaths.RootPath,
                    resultDirectory),
                "isolated-preview-root",
                "The forwarding smoke requires an isolated AOT preview root.");
            Directory.CreateDirectory(resultDirectory);
            result.PendingBefore = PendingNativeNotificationActivationStore.PendingFileCount;

            switch (phase)
            {
                case AotTodoNotificationForwardingSeedPhase:
                    await SeedAotTodoNotificationForwardingAsync(result);
                    break;
                case AotTodoNotificationForwardingColdPhase:
                    await VerifyAotTodoNotificationColdStartAsync(result);
                    break;
                case AotTodoNotificationForwardingPrimaryPhase:
                    result.State = "Ready";
                    WriteAotTodoNotificationForwardingResult(resultPath, result);
                    await VerifyAotTodoNotificationLiveForwardAsync(result);
                    break;
                case AotTodoNotificationForwardingPostflightPhase:
                    await VerifyAotTodoNotificationForwardingPostflightAsync(result);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported forwarding smoke phase '{phase}'.");
            }

            result.PendingAfter = PendingNativeNotificationActivationStore.PendingFileCount;
            result.ConsumedEnvelopeIds = _aotTodoNotificationForwardingConsumed
                .Select(envelope => envelope.EnvelopeId)
                .ToList();
            result.ConsumedSourceProcessIds = _aotTodoNotificationForwardingConsumed
                .Select(envelope => envelope.SourceProcessId)
                .ToList();
            result.ConsumedUserInput = _aotTodoNotificationForwardingConsumed
                .SelectMany(envelope => envelope.UserInput)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            result.RejectedEnvelopeCount = _aotTodoNotificationForwardingRejections.Count;
            result.ExecutableSha256 = ComputeAotTodoNotificationForwardingSha256(
                result.ExecutablePath);
            RequireAotTodoNotificationForwarding(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "The forwarding scenario did not run inside Native AOT.");
            RequireAotTodoNotificationForwarding(
                result,
                !result.SystemNotificationAttempted &&
                !result.ExternalWindowsActivationAttempted,
                "no-system-notification-or-windows-activation",
                "The controlled forwarding fixture crossed a deferred Windows boundary.");
            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log($"[AotTodoNotificationForwarding] Phase {phase} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.NormalShutdownRequested = true;
            if (Directory.Exists(resultDirectory))
            {
                WriteAotTodoNotificationForwardingResult(resultPath, result);
            }

            Log(
                $"[AotTodoNotificationForwarding] phase={phase} " +
                $"state={result.State} success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task SeedAotTodoNotificationForwardingAsync(
        AotTodoNotificationForwardingSmokeResult result)
    {
        ConfigureAotTodoNotificationForwardingSettings(SettingsService.Settings);
        await SettingsService.SaveAsync(notifySubscribers: false);
        var todoStore = new TodoWidgetStore(AotTodoNotificationForwardingWidgetId);
        await todoStore.SaveAsync(CreateAotTodoNotificationForwardingData());
        RequireAotTodoNotificationForwarding(
            result,
            await HasAotTodoNotificationForwardingItemsAsync(todoStore),
            "fixture-seeded",
            "The two forwarding Todo targets were not persisted.");

        Directory.CreateDirectory(PendingNativeNotificationActivationStore.SpoolPath);
        string corruptPath = Path.Combine(
            PendingNativeNotificationActivationStore.SpoolPath,
            "0000000000000000000-ffffffffffffffffffffffffffffffff.json");
        File.WriteAllText(corruptPath, "{corrupt-envelope");

        var envelope = new NativeNotificationActivationEnvelope
        {
            EnvelopeId = AotTodoNotificationForwardingColdEnvelopeId,
            CreatedAtUtc = AotTodoNotificationForwardingClock.ToUniversalTime(),
            SourceProcessId = Environment.ProcessId,
            Arguments = CreateAotTodoNotificationForwardingArguments(
                AotTodoNotificationForwardingColdItemId),
            UserInput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TodoNotificationActivationRouter.SnoozeInputId] =
                    TodoNotificationActivationRouter.Snooze30Minutes
            }
        };
        NativeNotificationActivationEnvelopeWriteResult stored =
            PendingNativeNotificationActivationStore.Store(envelope);
        NativeNotificationActivationEnvelopeWriteResult duplicate =
            PendingNativeNotificationActivationStore.Store(envelope);
        result.StoredDisposition = stored.Disposition.ToString();
        result.DuplicateDisposition = duplicate.Disposition.ToString();
        result.SeededEnvelopeId = envelope.EnvelopeId;
        RequireAotTodoNotificationForwarding(
            result,
            stored.Disposition ==
                NativeNotificationActivationEnvelopeWriteDisposition.Stored &&
            duplicate.Disposition ==
                NativeNotificationActivationEnvelopeWriteDisposition.Duplicate &&
            PendingNativeNotificationActivationStore.PendingFileCount == 2,
            "atomic-store-duplicate-and-corrupt-seeded",
            "The typed envelope, duplicate guard, and corrupt predecessor were not seeded.");
    }

    private async Task VerifyAotTodoNotificationColdStartAsync(
        AotTodoNotificationForwardingSmokeResult result)
    {
        NativeNotificationActivationEnvelope consumed =
            _aotTodoNotificationForwardingConsumed.Single();
        RequireAotTodoNotificationForwarding(
            result,
            _aotTodoNotificationForwardingRejections.Count == 1 &&
            consumed.EnvelopeId == AotTodoNotificationForwardingColdEnvelopeId &&
            consumed.UserInput.TryGetValue(
                TodoNotificationActivationRouter.SnoozeInputId,
                out string? selection) &&
            selection == TodoNotificationActivationRouter.Snooze30Minutes,
            "cold-start-drain-preserved-user-input",
            "Cold-start drain did not reject corruption and preserve the typed selection.");

        var todoStore = new TodoWidgetStore(AotTodoNotificationForwardingWidgetId);
        DateTimeOffset expected = AotTodoNotificationForwardingClock.AddMinutes(30);
        TodoItem item = await WaitForAotTodoNotificationForwardingItemAsync(
            todoStore,
            AotTodoNotificationForwardingColdItemId,
            expected);
        result.ColdSnoozedUntil = item.SnoozedUntil;
        RequireAotTodoNotificationForwarding(
            result,
            item.SnoozedUntil == expected &&
            item.ReminderDismissedForDueDate == item.DueDate &&
            PendingNativeNotificationActivationStore.PendingFileCount == 0,
            "cold-start-mutation-persisted",
            "Cold-start forwarding did not persist the exact 30-minute snooze.");
    }

    private async Task VerifyAotTodoNotificationLiveForwardAsync(
        AotTodoNotificationForwardingSmokeResult result)
    {
        DateTimeOffset expected = new(
            2026,
            8,
            26,
            9,
            0,
            0,
            TimeSpan.FromHours(8));
        var todoStore = new TodoWidgetStore(AotTodoNotificationForwardingWidgetId);
        TodoItem item = await WaitForAotTodoNotificationForwardingItemAsync(
            todoStore,
            AotTodoNotificationForwardingLiveItemId,
            expected);
        NativeNotificationActivationEnvelope consumed =
            _aotTodoNotificationForwardingConsumed.Single();
        result.LiveSnoozedUntil = item.SnoozedUntil;
        result.SecondaryProcessId = consumed.SourceProcessId;
        result.SingleInstanceForwardingObserved = consumed.SourceProcessId > 0 &&
            consumed.SourceProcessId != Environment.ProcessId;
        RequireAotTodoNotificationForwarding(
            result,
            result.SingleInstanceForwardingObserved &&
            consumed.UserInput.TryGetValue(
                TodoNotificationActivationRouter.SnoozeInputId,
                out string? selection) &&
            selection == TodoNotificationActivationRouter.SnoozeTomorrow &&
            item.SnoozedUntil == expected &&
            item.ReminderDismissedForDueDate == item.DueDate &&
            PendingNativeNotificationActivationStore.PendingFileCount == 0,
            "live-second-instance-forwarding-persisted",
            "The real secondary process did not forward and persist Tomorrow.");

        // Keep the primary alive long enough for start-aot-preview.ps1 to
        // observe that the secondary exited while the original process
        // survived the single-instance hand-off.
        await Task.Delay(2500);
    }

    private async Task VerifyAotTodoNotificationForwardingPostflightAsync(
        AotTodoNotificationForwardingSmokeResult result)
    {
        var todoStore = new TodoWidgetStore(AotTodoNotificationForwardingWidgetId);
        TodoWidgetData data = await todoStore.LoadAsync();
        TodoItem cold = data.Items.Single(item =>
            item.Id == AotTodoNotificationForwardingColdItemId);
        TodoItem live = data.Items.Single(item =>
            item.Id == AotTodoNotificationForwardingLiveItemId);
        DateTimeOffset expectedTomorrow = new(
            2026,
            8,
            26,
            9,
            0,
            0,
            TimeSpan.FromHours(8));
        result.ColdSnoozedUntil = cold.SnoozedUntil;
        result.LiveSnoozedUntil = live.SnoozedUntil;
        RequireAotTodoNotificationForwarding(
            result,
            cold.SnoozedUntil == AotTodoNotificationForwardingClock.AddMinutes(30) &&
            live.SnoozedUntil == expectedTomorrow &&
            PendingNativeNotificationActivationStore.PendingFileCount == 0,
            "postflight-state-reloaded-and-spool-empty",
            "Postflight did not reload both exact snoozes with an empty spool.");

        await todoStore.ClearAsync();
        result.StoreCleared = (await todoStore.LoadAsync()).Items.Count == 0;
        RequireAotTodoNotificationForwarding(
            result,
            result.StoreCleared,
            "fixture-store-cleared",
            "The forwarding Todo fixture was not cleared.");
    }

    private static async Task<TodoItem> WaitForAotTodoNotificationForwardingItemAsync(
        TodoWidgetStore store,
        string itemId,
        DateTimeOffset expectedSnoozedUntil)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TodoItem? item = (await store.LoadAsync()).Items.FirstOrDefault(candidate =>
                candidate.Id == itemId &&
                candidate.SnoozedUntil == expectedSnoozedUntil);
            if (item is not null)
            {
                return item;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for Todo forwarding mutation '{itemId}'.");
    }

    private static async Task<bool> HasAotTodoNotificationForwardingItemsAsync(
        TodoWidgetStore store)
    {
        TodoWidgetData data = await store.LoadAsync();
        return data.Items.Select(item => item.Id).Order().SequenceEqual(
            new[]
            {
                AotTodoNotificationForwardingColdItemId,
                AotTodoNotificationForwardingLiveItemId
            }.Order());
    }

    private static TodoWidgetData CreateAotTodoNotificationForwardingData()
    {
        return new TodoWidgetData
        {
            Version = 3,
            Items =
            [
                CreateAotTodoNotificationForwardingItem(
                    AotTodoNotificationForwardingColdItemId,
                    sortOrder: 0),
                CreateAotTodoNotificationForwardingItem(
                    AotTodoNotificationForwardingLiveItemId,
                    sortOrder: 1)
            ]
        };
    }

    private static TodoItem CreateAotTodoNotificationForwardingItem(
        string id,
        int sortOrder)
    {
        return new TodoItem
        {
            Id = id,
            Text = id,
            DueDate = AotTodoNotificationForwardingClock.AddYears(10),
            ReminderOffsetMinutes = 5,
            CreatedAt = AotTodoNotificationForwardingClock.AddDays(-1),
            UpdatedAt = AotTodoNotificationForwardingClock.AddDays(-1),
            SortOrder = sortOrder
        };
    }

    private static void ConfigureAotTodoNotificationForwardingSettings(
        AppSettings settings)
    {
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = 5;
        settings.DeletedWidgetIds = [];
        settings.Widgets =
        [
            new WidgetConfig
            {
                Id = AotTodoNotificationForwardingWidgetId,
                Name = "DeskBox Todo forwarding AOT fixture",
                WidgetKind = WidgetKind.Todo,
                IsDisabled = false
            }
        ];
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
    }

    private static string CreateAotTodoNotificationForwardingArguments(string itemId)
    {
        return
            $"source={TodoNotificationActivationRouter.SourceValue};" +
            $"action={TodoNotificationActivationRouter.ActionSnooze};" +
            $"widgetId={AotTodoNotificationForwardingWidgetId};" +
            $"itemId={itemId}";
    }

    private static bool IsAotTodoNotificationForwardingRequest(
        string? requiredPhase = null)
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotTodoNotificationForwardingSmokeEnvironmentVariable);
        string? phase = Environment.GetEnvironmentVariable(
            AotTodoNotificationForwardingPhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            AotTodoNotificationForwardingRunIdEnvironmentVariable);
        if (!string.Equals(
                scenario,
                AotTodoNotificationForwardingScenario,
                StringComparison.Ordinal) ||
            !Guid.TryParseExact(runId, "N", out _) ||
            phase is not (
                AotTodoNotificationForwardingSeedPhase or
                AotTodoNotificationForwardingColdPhase or
                AotTodoNotificationForwardingPrimaryPhase or
                AotTodoNotificationForwardingSecondaryPhase or
                AotTodoNotificationForwardingPostflightPhase))
        {
            return false;
        }

        return requiredPhase is null ||
            string.Equals(phase, requiredPhase, StringComparison.Ordinal);
    }

    private static bool IsAotTodoNotificationForwardingPathEqualOrInside(
        string root,
        string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd('\\', '/');
        return string.Equals(
                   normalizedRoot,
                   normalizedCandidate,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeAotTodoNotificationForwardingSha256(string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void RequireAotTodoNotificationForwarding(
        AotTodoNotificationForwardingSmokeResult result,
        bool condition,
        string step,
        string error)
    {
        if (!condition)
        {
            throw new InvalidOperationException(error);
        }

        result.Steps.Add(step);
    }

    private static void WriteAotTodoNotificationForwardingResult(
        string resultPath,
        AotTodoNotificationForwardingSmokeResult result)
    {
        string json = JsonSerializer.Serialize(
            result,
            AotTodoNotificationForwardingJsonContext.Default.SmokeResult);
        string tempPath = $"{resultPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, resultPath, overwrite: true);
    }
}

internal sealed class AotTodoNotificationForwardingSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public int SecondaryProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public DateTimeOffset FixedClock { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public bool SystemNotificationAttempted { get; set; }
    public bool ExternalWindowsActivationAttempted { get; set; }
    public bool SingleInstanceForwardingObserved { get; set; }
    public bool StoreCleared { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public int PendingBefore { get; set; }
    public int PendingAfter { get; set; }
    public int RejectedEnvelopeCount { get; set; }
    public string? StoredDisposition { get; set; }
    public string? DuplicateDisposition { get; set; }
    public string? SeededEnvelopeId { get; set; }
    public DateTimeOffset? ColdSnoozedUntil { get; set; }
    public DateTimeOffset? LiveSnoozedUntil { get; set; }
    public List<string> ConsumedEnvelopeIds { get; set; } = [];
    public List<int> ConsumedSourceProcessIds { get; set; } = [];
    public Dictionary<string, string> ConsumedUserInput { get; set; } = [];
    public List<string> Steps { get; set; } = [];
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotTodoNotificationForwardingSmokeResult),
    TypeInfoPropertyName = "SmokeResult")]
internal partial class AotTodoNotificationForwardingJsonContext :
    JsonSerializerContext
{
}
#endif
