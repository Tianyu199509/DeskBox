using DeskBox.Contracts;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Ownership-ledger behavior of <see cref="FeatureRuntimeRegistry"/>: the
/// registry owns the runtime instances, disposes them in reverse registration
/// order (matching the contract's reverse-acquisition reclamation), and
/// quarantines a faulting runtime for leak isolation instead of letting it
/// block the sweep.
/// </summary>
public sealed class FeatureRuntimeRegistryTests
{
    [Fact]
    public async Task Register_ReturnsTheRuntime_ForWiring_AndTryGetResolvesIt()
    {
        FeatureRuntimeRegistry registry = new();
        RecordingRuntime first = registry.Register("search", new RecordingRuntime());
        RecordingRuntime second = registry.Register("quick-capture", new RecordingRuntime());

        IFeatureRuntime? resolved = registry.TryGet("search");
        Assert.NotNull(resolved);
        Assert.Same(first, resolved);
        await resolved.StartAsync();
        Assert.Equal(1, first.Starts);

        Assert.Same(second, registry.TryGet("quick-capture"));
        Assert.Null(registry.TryGet("todo-reminders"));
        Assert.Equal(new[] { "search", "quick-capture" }, registry.RegisteredFeatures);

        Assert.Throws<ArgumentException>(() => registry.Register("search", new RecordingRuntime()));
    }

    [Fact]
    public async Task DisposeAsync_ByFeatureId_DisposesOnlyThatRuntime()
    {
        FeatureRuntimeRegistry registry = new();
        RecordingRuntime search = registry.Register("search", new RecordingRuntime());
        RecordingRuntime quickCapture = registry.Register("quick-capture", new RecordingRuntime());

        await registry.DisposeAsync("search");
        Assert.Equal(1, search.Disposals);
        Assert.Equal(0, quickCapture.Disposals);

        // The registry does not deduplicate: repeat disposal of the same id
        // relies on the runtime's own idempotent disposal contract.
        await registry.DisposeAsync("search");
        Assert.Equal(2, search.Disposals);
    }

    [Fact]
    public async Task DisposeAllAsync_DisposesInReverseRegistrationOrder_AndIsolatesFaults()
    {
        List<string> order = [];
        RecordingRuntime first = new() { DisposeAction = () => order.Add("first") };
        RecordingRuntime faulting = new()
        {
            DisposeAction = () =>
            {
                order.Add("faulting:throw");
                throw new IOException("hung watcher");
            }
        };
        RecordingRuntime middle = new() { DisposeAction = () => order.Add("middle") };
        RecordingRuntime last = new() { DisposeAction = () => order.Add("last") };

        List<string> reports = [];
        FeatureRuntimeRegistry registry = new(reports.Add);
        registry.Register("first", first);
        registry.Register("faulting", faulting);
        registry.Register("middle", middle);
        registry.Register("last", last);

        await registry.DisposeAllAsync();

        // Reverse registration order; the faulting runtime is reported and
        // quarantined, so the runtimes registered before it still release.
        Assert.Equal(new[] { "last", "middle", "faulting:throw", "first" }, order);
        Assert.Single(reports);
        Assert.Contains("faulting", reports[0], StringComparison.Ordinal);
        Assert.Contains("quarantined for leak isolation", reports[0], StringComparison.Ordinal);

        // Quarantine keeps the entry observable for leak isolation.
        Assert.Equal(4, registry.RegisteredFeatures.Count);
        Assert.NotNull(registry.TryGet("faulting"));
    }

    [Fact]
    public async Task DisposeAllAsync_WithASingleFaultingRuntime_ReportsAndCompletes()
    {
        List<string> reports = [];
        FeatureRuntimeRegistry registry = new(reports.Add);
        RecordingRuntime runtime = new()
        {
            DisposeAction = () => throw new InvalidOperationException("dispose failed")
        };
        registry.Register("todo-reminders", runtime);

        await registry.DisposeAllAsync();
        Assert.Equal(1, runtime.Disposals);
        Assert.Single(reports);
    }

    private sealed class RecordingRuntime : IFeatureRuntime
    {
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public Action? DisposeAction { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            DisposeAction?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
