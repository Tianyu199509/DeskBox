#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using System.Runtime.CompilerServices;

namespace DeskBox;

public partial class App
{
    private const string AotTodoNotificationSurfaceSmokeEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_SURFACE_SMOKE";
    private const string AotTodoNotificationSurfaceScenario =
        "TodoNotificationSurfaceRouting";
    private const string AotTodoNotificationSurfaceDirectoryName =
        "aot-todo-notification-surface-smoke";
    private const string AotTodoNotificationSurfaceWidgetId =
        "aot-5b4c3b2b2-todo";
    private const string AotTodoNotificationSurfaceBodyItemId =
        "surface-body-item";
    private const string AotTodoNotificationSurfaceCompleteItemId =
        "surface-complete-item";
    private const string AotTodoNotificationSurfaceSnoozeItemId =
        "surface-snooze-item";

    private static readonly DateTimeOffset AotTodoNotificationSurfaceClock =
        new(2026, 8, 25, 8, 15, 0, TimeSpan.FromHours(8));

    private readonly List<TodoNotificationActivationRouteResult>
        _aotTodoNotificationSurfaceRoutes = [];

    private static DateTimeOffset? TryGetAotTodoNotificationSurfaceClock()
    {
        return IsAotTodoNotificationSurfaceRequest()
            ? AotTodoNotificationSurfaceClock
            : null;
    }

    private static bool ShouldSuppressAotTodoNotificationSurfaceSystemNotification()
    {
        return IsAotTodoNotificationSurfaceRequest();
    }

    private void RecordAotTodoNotificationSurfaceRoute(
        TodoNotificationActivationRouteResult result)
    {
        if (IsAotTodoNotificationSurfaceRequest())
        {
            _aotTodoNotificationSurfaceRoutes.Add(result);
        }
    }

    private static bool IsAotTodoNotificationSurfaceRequest()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(
                AotTodoNotificationSurfaceSmokeEnvironmentVariable),
            AotTodoNotificationSurfaceScenario,
            StringComparison.Ordinal);
    }

    private void StartAotTodoNotificationSurfaceSmokeIfRequested()
    {
        if (IsAotTodoNotificationSurfaceRequest())
        {
            _ = RunAotTodoNotificationSurfaceSmokeAsync();
        }
    }

    private async Task RunAotTodoNotificationSurfaceSmokeAsync()
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !IsAotManagedUiPathEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotTodoNotificationSurface] RefusedNonPreviewRoot: the surface " +
                "matrix requires an explicit isolated Native AOT preview root.");
            return;
        }

        string evidenceRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationSurfaceDirectoryName));
        if (!IsAotManagedUiPathEqualOrInside(dataPaths.RootPath, evidenceRoot) ||
            IsAotManagedUiPathEqual(dataPaths.RootPath, evidenceRoot))
        {
            Log(
                $"[AotTodoNotificationSurface] Refused unsafe evidence root " +
                $"'{evidenceRoot}'.");
            return;
        }

        Directory.CreateDirectory(evidenceRoot);
        string resultPath = Path.Combine(evidenceRoot, "result.json");
        var evidence = new AotTodoNotificationSurfaceEvidence
        {
            Stage = "5B-4C3B2B2A",
            FixedClock = AotTodoNotificationSurfaceClock,
            WidgetId = AotTodoNotificationSurfaceWidgetId,
            BodyItemId = AotTodoNotificationSurfaceBodyItemId,
            CompleteItemId = AotTodoNotificationSurfaceCompleteItemId,
            SnoozeItemId = AotTodoNotificationSurfaceSnoozeItemId,
            SnoozeSelection = TodoNotificationActivationRouter.Snooze30Minutes,
            SystemNotificationAttempted = false,
            ExternalWindowsActivationAttempted = false,
            UserClickVerified = false,
            NormalShutdownRequested = true
        };
        var result = new AotManagedUiSmokeResult
        {
            SchemaVersion = 1,
            Scenario = AotTodoNotificationSurfaceScenario,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            EvidenceRoot = evidenceRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            TodoNotificationSurface = evidence
        };
        WriteAotManagedUiResult(resultPath, result);

        try
        {
            RequireAotTodoNotificationSurface(
                result,
                !RuntimeFeature.IsDynamicCodeSupported,
                "runtime-native-aot",
                "The Todo notification surface fixture did not run as Native AOT.");
            RequireAotTodoNotificationSurface(
                result,
                WidgetManager is not null && _todoReminderService is not null,
                "product-services-ready",
                "WidgetManager or TodoReminderService was unavailable.");

            await ConfigureAotTodoNotificationSurfaceFixtureAsync();
            RequireAotTodoNotificationSurface(
                result,
                true,
                "isolated-fixture-seeded",
                "The isolated Todo surface fixture could not be seeded.");

            TodoNotificationActivationRouteResult? bodyRoute =
                await RouteTodoNotificationActivationAsync(
                    CreateAotTodoNotificationSurfaceArguments(
                        AotTodoNotificationSurfaceBodyItemId),
                    new Dictionary<string, string>());
            RequireAotTodoNotificationSurface(
                result,
                bodyRoute is
                {
                    Succeeded: true,
                    TargetRequested: true,
                    TargetPresented: true
                } &&
                bodyRoute.Disposition ==
                    TodoNotificationActivationRouter.DispositionOpened,
                "body-route-target-presented",
                "The body activation did not present its exact Todo target.");
            TodoNotificationActivationRouteResult completedBodyRoute =
                bodyRoute ?? throw new InvalidOperationException(
                    "The body activation did not return a route result.");

            AotTodoNotificationSurfaceHostSnapshot bodySnapshot =
                await CaptureAotTodoNotificationSurfaceHostAsync(
                    AotTodoNotificationSurfaceBodyItemId);
            evidence.WindowHandle = bodySnapshot.WindowHandle;
            evidence.Visible = bodySnapshot.Visible;
            evidence.HasXamlRoot = bodySnapshot.HasXamlRoot;
            evidence.BodyRouteSucceeded = completedBodyRoute.Succeeded;
            evidence.BodyTargetRequested = completedBodyRoute.TargetRequested;
            evidence.BodyTargetPresented = completedBodyRoute.TargetPresented;
            evidence.BodyItemVisible = bodySnapshot.ItemVisible;
            evidence.BodyItemSelected = bodySnapshot.ItemSelected;
            evidence.BodySelectedFilter = bodySnapshot.SelectedFilter;
            RequireAotTodoNotificationSurface(
                result,
                bodySnapshot.WindowHandle != 0 &&
                bodySnapshot.Visible &&
                bodySnapshot.HasXamlRoot &&
                bodySnapshot.ItemVisible &&
                bodySnapshot.ItemSelected,
                "body-visible-item-located",
                "The body target was not visible and selected on the real Todo surface.");

            TodoNotificationActivationRouteResult? completeRoute =
                await RouteTodoNotificationActivationAsync(
                    CreateAotTodoNotificationSurfaceArguments(
                        AotTodoNotificationSurfaceCompleteItemId,
                        TodoNotificationActivationRouter.ActionComplete),
                    new Dictionary<string, string>());
            AotTodoNotificationSurfaceHostSnapshot completeSnapshot =
                await CaptureAotTodoNotificationSurfaceHostAsync(
                    AotTodoNotificationSurfaceCompleteItemId);
            evidence.CompleteRouteSucceeded = completeRoute?.Succeeded == true;
            evidence.CompleteRefreshRequested =
                completeRoute?.RefreshRequested == true;
            evidence.CompleteRefreshCompleted =
                completeRoute?.RefreshCompleted == true;
            evidence.CompleteVisibleState = completeSnapshot.IsCompleted;
            RequireAotTodoNotificationSurface(
                result,
                completeRoute is
                {
                    Succeeded: true,
                    RefreshRequested: true,
                    RefreshCompleted: true
                } &&
                completeSnapshot.ItemVisible &&
                completeSnapshot.IsCompleted,
                "complete-visible-refresh-proved",
                "Complete persisted but was not reflected on the visible Todo surface.");

            TodoNotificationActivationRouteResult? snoozeRoute =
                await RouteTodoNotificationActivationAsync(
                    CreateAotTodoNotificationSurfaceArguments(
                        AotTodoNotificationSurfaceSnoozeItemId,
                        TodoNotificationActivationRouter.ActionSnooze),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [TodoNotificationActivationRouter.SnoozeInputId] =
                            TodoNotificationActivationRouter.Snooze30Minutes
                    });
            AotTodoNotificationSurfaceHostSnapshot snoozeSnapshot =
                await CaptureAotTodoNotificationSurfaceHostAsync(
                    AotTodoNotificationSurfaceSnoozeItemId);
            DateTimeOffset expectedSnoozedUntil =
                AotTodoNotificationSurfaceClock.AddMinutes(30);
            evidence.SnoozeRouteSucceeded = snoozeRoute?.Succeeded == true;
            evidence.SnoozeRefreshRequested = snoozeRoute?.RefreshRequested == true;
            evidence.SnoozeRefreshCompleted = snoozeRoute?.RefreshCompleted == true;
            evidence.SnoozedUntil = snoozeRoute?.SnoozedUntil;
            evidence.VisibleSnoozedUntil = snoozeSnapshot.SnoozedUntil;
            evidence.RouteCount = _aotTodoNotificationSurfaceRoutes.Count;
            RequireAotTodoNotificationSurface(
                result,
                snoozeRoute is
                {
                    Succeeded: true,
                    RefreshRequested: true,
                    RefreshCompleted: true
                } &&
                snoozeRoute.SnoozeSelection ==
                    TodoNotificationActivationRouter.Snooze30Minutes &&
                snoozeRoute.SnoozedUntil == expectedSnoozedUntil &&
                snoozeSnapshot.ItemVisible &&
                snoozeSnapshot.SnoozedUntil == expectedSnoozedUntil,
                "snooze-user-input-visible-refresh-proved",
                "Snooze UserInput was not reflected on the visible Todo surface.");
            RequireAotTodoNotificationSurface(
                result,
                _aotTodoNotificationSurfaceRoutes.Count == 3,
                "exact-three-routes-observed",
                "The fixture did not observe exactly body, Complete and Snooze routes.");
            RequireAotTodoNotificationSurface(
                result,
                !evidence.SystemNotificationAttempted &&
                !evidence.ExternalWindowsActivationAttempted &&
                !evidence.UserClickVerified,
                "controlled-input-not-mislabeled-as-real-click",
                "The automated surface evidence was mislabeled as a real Windows click.");

            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log($"[AotTodoNotificationSurface] Failed: {ex}");
        }
        finally
        {
            CompleteAotTodoNotificationSurfaceAnimation();
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteAotManagedUiResult(resultPath, result);
            Log(
                $"[AotTodoNotificationSurface] state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task ConfigureAotTodoNotificationSurfaceFixtureAsync()
    {
        var store = new TodoWidgetStore(AotTodoNotificationSurfaceWidgetId);
        await store.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                CreateAotTodoNotificationSurfaceItem(
                    AotTodoNotificationSurfaceBodyItemId),
                CreateAotTodoNotificationSurfaceItem(
                    AotTodoNotificationSurfaceCompleteItemId),
                CreateAotTodoNotificationSurfaceItem(
                    AotTodoNotificationSurfaceSnoozeItemId)
            ]
        });

        SettingsService.Settings.Widgets.RemoveAll(widget =>
            string.Equals(
                widget.Id,
                AotTodoNotificationSurfaceWidgetId,
                StringComparison.Ordinal));
        SettingsService.Settings.DeletedWidgetIds.Remove(
            AotTodoNotificationSurfaceWidgetId);
        FeatureWidgetSettings.SetEnabled(
            SettingsService.Settings,
            WidgetKind.Todo,
            true);
        SettingsService.Settings.TodoShowCompletedTasks = true;
        SettingsService.Settings.TodoDefaultFilter = TodoFilter.All.ToString();
        SettingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Id = AotTodoNotificationSurfaceWidgetId,
            Name = "AOT Todo Notification Surface",
            WidgetKind = WidgetKind.Todo,
            IsVisible = false,
            IsDisabled = false,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 360,
            Height = 480
        });
        await SettingsService.SaveAsync();
    }

    private void CompleteAotTodoNotificationSurfaceAnimation()
    {
        if (WidgetManager?.ContentWidgets.TryGetValue(
                AotTodoNotificationSurfaceWidgetId,
                out ContentWidgetWindow? window) == true)
        {
            window.CompleteTrayShowWithoutAnimation();
        }
    }

    private static TodoItem CreateAotTodoNotificationSurfaceItem(string itemId)
    {
        return new TodoItem
        {
            Id = itemId,
            Text = itemId,
            DueDate = AotTodoNotificationSurfaceClock.AddHours(2),
            ReminderOffsetMinutes = 5,
            CreatedAt = AotTodoNotificationSurfaceClock.AddDays(-1),
            UpdatedAt = AotTodoNotificationSurfaceClock.AddDays(-1)
        };
    }

    private static Dictionary<string, string>
        CreateAotTodoNotificationSurfaceArguments(
            string itemId,
            string? action = null)
    {
        var arguments = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = TodoNotificationActivationRouter.SourceValue,
            ["widgetId"] = AotTodoNotificationSurfaceWidgetId,
            ["itemId"] = itemId,
            ["view"] = "all"
        };
        if (!string.IsNullOrWhiteSpace(action))
        {
            arguments["action"] = action;
        }

        return arguments;
    }

    private async Task<AotTodoNotificationSurfaceHostSnapshot>
        CaptureAotTodoNotificationSurfaceHostAsync(string itemId)
    {
        return await CaptureAotTodoNotificationSurfaceHostAsync(
            AotTodoNotificationSurfaceWidgetId,
            itemId);
    }

    private async Task<AotTodoNotificationSurfaceHostSnapshot>
        CaptureAotTodoNotificationSurfaceHostAsync(
            string widgetId,
            string itemId)
    {
        if (WidgetManager is null ||
            !WidgetManager.ContentWidgets.TryGetValue(
                widgetId,
                out ContentWidgetWindow? window))
        {
            throw new InvalidOperationException(
                "The owned Todo notification surface window was not loaded.");
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is not TodoWidgetContentAdapter adapter ||
            adapter.View is not TodoWidgetContent)
        {
            throw new InvalidOperationException(
                "The owned Todo notification surface has the wrong content.");
        }

        TodoItemViewModel item = adapter.ViewModel.Items.Single(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        return new AotTodoNotificationSurfaceHostSnapshot(
            window.WindowHandle.ToInt64(),
            window.Visible,
            window.WindowContentRoot?.XamlRoot is not null,
            adapter.ViewModel.VisibleItems.Contains(item),
            item.IsCopySelected,
            adapter.ViewModel.SelectedFilter.ToString(),
            item.IsCompleted,
            item.SnoozedUntil);
    }

    private static void RequireAotTodoNotificationSurface(
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

internal sealed record AotTodoNotificationSurfaceHostSnapshot(
    long WindowHandle,
    bool Visible,
    bool HasXamlRoot,
    bool ItemVisible,
    bool ItemSelected,
    string SelectedFilter,
    bool IsCompleted,
    DateTimeOffset? SnoozedUntil);

internal sealed class AotTodoNotificationSurfaceEvidence
{
    public string Stage { get; set; } = string.Empty;
    public DateTimeOffset FixedClock { get; set; }
    public string WidgetId { get; set; } = string.Empty;
    public string BodyItemId { get; set; } = string.Empty;
    public string CompleteItemId { get; set; } = string.Empty;
    public string SnoozeItemId { get; set; } = string.Empty;
    public string SnoozeSelection { get; set; } = string.Empty;
    public long WindowHandle { get; set; }
    public bool Visible { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool BodyRouteSucceeded { get; set; }
    public bool BodyTargetRequested { get; set; }
    public bool BodyTargetPresented { get; set; }
    public bool BodyItemVisible { get; set; }
    public bool BodyItemSelected { get; set; }
    public string BodySelectedFilter { get; set; } = string.Empty;
    public bool CompleteRouteSucceeded { get; set; }
    public bool CompleteRefreshRequested { get; set; }
    public bool CompleteRefreshCompleted { get; set; }
    public bool CompleteVisibleState { get; set; }
    public bool SnoozeRouteSucceeded { get; set; }
    public bool SnoozeRefreshRequested { get; set; }
    public bool SnoozeRefreshCompleted { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset? VisibleSnoozedUntil { get; set; }
    public int RouteCount { get; set; }
    public bool SystemNotificationAttempted { get; set; }
    public bool ExternalWindowsActivationAttempted { get; set; }
    public bool UserClickVerified { get; set; }
    public bool NormalShutdownRequested { get; set; }
}
#endif
