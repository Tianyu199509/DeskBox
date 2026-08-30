using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Two-phase desktop organization. organize/plan scans the desktop and
/// returns a preview plan (planId); organize/apply executes a cached plan;
/// organize/undo rolls a completed run back via its historyId. Plans live
/// in an in-process cache (the planner produces in-memory objects), so
/// apply must happen in the same DeskBox session, within the TTL.
/// </summary>
internal static class OrganizationPlanCache
{
    private const int Capacity = 8;
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, CacheEntry> Plans = new(StringComparer.Ordinal);

    public static void Store(DesktopOrganizationPlan plan)
    {
        Prune();
        Plans[plan.Id] = new CacheEntry(plan, DateTimeOffset.UtcNow);
        while (Plans.Count > Capacity)
        {
            string oldest = Plans
                .OrderBy(pair => pair.Value.CreatedUtc)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            Plans.TryRemove(oldest, out _);
        }
    }

    public static DesktopOrganizationPlan Take(string planId)
    {
        if (!Plans.TryGetValue(planId, out CacheEntry entry))
        {
            throw CommandValidationException.ValidationFailed(
                $"No cached organization plan with id '{planId}'.",
                "Call organize/plan again (plans expire after 10 minutes and live only for this DeskBox session), then apply the returned planId.");
        }

        if (DateTimeOffset.UtcNow - entry.CreatedUtc > TimeToLive)
        {
            Plans.TryRemove(planId, out _);
            throw CommandValidationException.ValidationFailed(
                $"Organization plan '{planId}' expired.",
                "Call organize/plan again, then apply the fresh planId.");
        }

        return entry.Plan;
    }

    private static void Prune()
    {
        foreach ((string key, CacheEntry entry) in Plans)
        {
            if (DateTimeOffset.UtcNow - entry.CreatedUtc > TimeToLive)
            {
                Plans.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct CacheEntry(DesktopOrganizationPlan Plan, DateTimeOffset CreatedUtc);
}

public sealed record OrganizationPlanTarget(
    string SourceBucketId,
    string CategoryId,
    string TargetWidgetId,
    string SuggestedDisplayName,
    string TargetDirectoryPath,
    bool CreatesWidget,
    int ItemCount,
    IReadOnlyList<string> ItemNames);

public sealed record OrganizationPlanResult(
    string PlanId,
    string DesktopPath,
    string StorageRootPath,
    int TargetCount,
    int TotalItemCount,
    IReadOnlyList<OrganizationPlanTarget> Targets,
    IReadOnlyList<string> ExcludedItemNames);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrganizationPlanResult), TypeInfoPropertyName = "OrgPlanResult")]
[JsonSerializable(typeof(OrganizationPlanTarget), TypeInfoPropertyName = "OrgPlanTarget")]
internal sealed partial class OrganizeJsonContext : JsonSerializerContext
{
}

/// <summary>Scans the desktop and returns the categorization plan without
/// moving anything. Headless: scanning runs on the thread pool and the
/// planner is pure in-memory computation.</summary>
public sealed class OrganizePlanHandler : ICommandHandler
{
    private readonly Func<DesktopOrganizationCoordinator?> _coordinator;

    public OrganizePlanHandler(Func<DesktopOrganizationCoordinator?> coordinator)
    {
        _coordinator = coordinator;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "organize/plan",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.OrganizeWrite,
        MutatesState: false,
        Destructive: false,
        Summary: "Scans the desktop and returns a preview plan (categories, target folders/widgets, items). Nothing moves yet.",
        Arguments:
        [
            new CommandArgumentDescriptor("includeSlowItems", "boolean", false,
                "Also include slow-to-inspect desktop items (default false).", "false"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":28,"method":"organize/plan","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{}}}""",
        ExampleResponseJson: """{"result":{"data":{"planId":"p1","targetCount":2,"totalItemCount":5,"targets":[{"categoryId":"Documents","targetDirectoryPath":"…","itemCount":3}]}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        DesktopOrganizationCoordinator? coordinator = _coordinator()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        bool includeSlowItems = CommandArguments.TryGetBool(arguments, "includeSlowItems", out bool slow) && slow;

        DesktopOrganizationPlan plan = await coordinator
            .BuildPlanAsync(includeSlowItems, cancellationToken)
            .ConfigureAwait(false);
        OrganizationPlanCache.Store(plan);

        List<OrganizationPlanTarget> targets = plan.Targets
            .Select(target => new OrganizationPlanTarget(
                target.SourceBucketId,
                target.CategoryId,
                target.TargetWidgetId,
                target.SuggestedDisplayName,
                target.TargetDirectoryPath,
                target.CreatesWidget,
                target.Items.Count,
                target.Items.Select(item => System.IO.Path.GetFileName(item.SourcePath)).Take(8).ToList()))
            .ToList();
        List<string> excluded = plan.ExcludedItems
            .Select(item => System.IO.Path.GetFileName(item.SourcePath))
            .ToList();
        OrganizationPlanResult result = new(
            plan.Id,
            plan.DesktopPath,
            plan.StorageRootPath,
            targets.Count,
            targets.Sum(target => target.ItemCount),
            targets,
            excluded);
        return JsonSerializer.SerializeToElement(result, OrganizeJsonContext.Default.OrgPlanResult);
    }
}

public sealed record OrganizationApplyResult(
    string PlanId,
    string HistoryId,
    int CreatedWidgetCount,
    int RetainedItemCount,
    IReadOnlyList<string> CreatedWidgetIds);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrganizationApplyResult), TypeInfoPropertyName = "OrgApplyResult")]
internal sealed partial class OrganizeApplyJsonContext : JsonSerializerContext
{
}

/// <summary>Executes a previously returned plan: files move into category
/// folders (existing file widgets are filled first, missing categories
/// create new widgets). Every move is recorded for organize/undo.</summary>
public sealed class OrganizeApplyHandler : ICommandHandler
{
    private readonly Func<DesktopOrganizationCoordinator?> _coordinator;

    public OrganizeApplyHandler(Func<DesktopOrganizationCoordinator?> coordinator)
    {
        _coordinator = coordinator;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "organize/apply",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.OrganizeWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Executes a cached organize/plan (files move into managed category folders; undoable via organize/undo).",
        Arguments:
        [
            new CommandArgumentDescriptor("planId", "string", true, "Plan id from organize/plan.", "\"p1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":29,"method":"organize/apply","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"planId":"p1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"planId":"p1","historyId":"h1","createdWidgetCount":1,"retainedItemCount":0,"createdWidgetIds":["abc"]}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        DesktopOrganizationCoordinator? coordinator = _coordinator()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        if (!CommandArguments.TryGetString(arguments, "planId", out string planId)
            || string.IsNullOrWhiteSpace(planId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'planId' argument is required.",
                "Call organize/plan first and pass the returned planId.");
        }

        DesktopOrganizationPlan previewPlan = OrganizationPlanCache.Take(planId);

        // Apply every target bucket with the default dynamic destination —
        // the same as pressing "organize" with all suggestions selected in
        // the settings UI.
        List<DesktopOrganizationTargetSelection> selections = previewPlan.Targets
            .Select(target => new DesktopOrganizationTargetSelection
            {
                SourceBucketId = target.SourceBucketId,
                IsSelected = true,
            })
            .ToList();
        DesktopOrganizationPlan executionPlan =
            coordinator.CreateExecutionPlan(previewPlan, selections);
        OrganizationPlanCache.Store(executionPlan);

        DesktopOrganizationExecutionResult execution = await coordinator
            .ExecuteAsync(executionPlan, cancellationToken)
            .ConfigureAwait(true);

        OrganizationApplyResult result = new(
            planId,
            execution.History.Id,
            execution.CreatedWidgets.Count,
            execution.RetainedItems.Count,
            execution.CreatedWidgets.Select(widget => widget.Id).ToList());
        return JsonSerializer.SerializeToElement(result, OrganizeApplyJsonContext.Default.OrgApplyResult);
    }
}

public sealed record OrganizationUndoResult(bool Undone, string HistoryId);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrganizationUndoResult), TypeInfoPropertyName = "OrgUndoResult")]
internal sealed partial class OrganizeUndoJsonContext : JsonSerializerContext
{
}

/// <summary>Rolls a completed organization run back: files return to their
/// original desktop paths and widgets created by that run are removed.</summary>
public sealed class OrganizeUndoHandler : ICommandHandler
{
    private readonly Func<DesktopOrganizationCoordinator?> _coordinator;

    public OrganizeUndoHandler(Func<DesktopOrganizationCoordinator?> coordinator)
    {
        _coordinator = coordinator;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "organize/undo",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.OrganizeWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Undoes one completed organization run by historyId (from organize/apply).",
        Arguments:
        [
            new CommandArgumentDescriptor("historyId", "string", true, "History id returned by organize/apply.", "\"h1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":30,"method":"organize/undo","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"historyId":"h1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"undone":true,"historyId":"h1"}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        DesktopOrganizationCoordinator? coordinator = _coordinator()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        if (!CommandArguments.TryGetString(arguments, "historyId", out string historyId)
            || string.IsNullOrWhiteSpace(historyId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'historyId' argument is required.",
                "Pass the historyId returned by organize/apply.");
        }

        await coordinator.UndoAsync(historyId).ConfigureAwait(true);
        OrganizationUndoResult result = new(true, historyId);
        return JsonSerializer.SerializeToElement(result, OrganizeUndoJsonContext.Default.OrgUndoResult);
    }
}
