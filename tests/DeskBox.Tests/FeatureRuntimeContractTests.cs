using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Features.Search;
using DeskBox.Features.Todo;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Interface-level state-machine contract tests for <see cref="IFeatureRuntime"/>
/// (roadmap §2): idempotent start/dispose, cancellable start, reverse-order
/// rollback of partial starts, and disposal serialized against an in-flight
/// start. The same assertions run against a fully controllable fake (the law
/// itself) and against the three production runtimes through thin case
/// adapters. They complement — never replace — the feature-level tests that
/// pin each runtime's own semantics (TodoReminderRuntimeTests,
/// QuickCaptureClipboardRuntimeTests, the batch-6 search coordinator tests).
/// The production runtimes serialize their transitions on the owning UI
/// thread; that boundary cannot be exercised from a worker test thread and is
/// pinned here through the fake's gated transitions instead.
/// </summary>
public sealed class FeatureRuntimeContractTests
{
    public static TheoryData<string> Cases => new()
    {
        FakeName, TodoName, QuickCaptureName, SearchName
    };

    private const string FakeName = "fake";
    private const string TodoName = "todo-reminders";
    private const string QuickCaptureName = "quick-capture";
    private const string SearchName = "search";

    private static ILeaseCase CreateCase(string name) => name switch
    {
        FakeName => new FakeCase(),
        TodoName => new TodoCase(),
        QuickCaptureName => new QuickCaptureCase(),
        SearchName => new SearchCase(),
        _ => throw new ArgumentException($"Unknown case '{name}'.", nameof(name)),
    };

    // ── Contract laws, shared across every case ───────────────────────

    [Theory, MemberData(nameof(Cases))]
    public async Task StartAsync_IsIdempotent_RepeatedCallsNeverBuildASecondLease(string caseName)
    {
        using ILeaseCase lease = CreateCase(caseName);
        await lease.Runtime.StartAsync();
        await lease.Runtime.StartAsync();
        Assert.True(lease.LeaseHeld);
        Assert.Equal(1, lease.Acquisitions);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task StartAsync_WithCancelledToken_FailsBeforeAcquiringAnything(string caseName)
    {
        using ILeaseCase lease = CreateCase(caseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lease.Runtime.StartAsync(new CancellationToken(canceled: true)));
        Assert.False(lease.LeaseHeld);
        Assert.Equal(0, lease.Acquisitions);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task DisposeAsync_ReleasesTheLease_AndRepeatedDisposalIsANoOp(string caseName)
    {
        using ILeaseCase lease = CreateCase(caseName);
        await lease.Runtime.StartAsync();
        await lease.Runtime.DisposeAsync();
        await lease.Runtime.DisposeAsync();
        Assert.False(lease.LeaseHeld);
        Assert.Equal(1, lease.CompletedReleases);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task PartialStartFailure_LeavesNoLease_AndAllowsRetry(string caseName)
    {
        using ILeaseCase lease = CreateCase(caseName);
        lease.FailNextAcquisition();
        await Assert.ThrowsAnyAsync<Exception>(() => lease.Runtime.StartAsync());
        Assert.False(lease.LeaseHeld);

        await lease.Runtime.StartAsync();
        Assert.True(lease.LeaseHeld);
    }

    [Fact]
    public async Task FakePartialStart_ReleasesAcquiredResourcesInReverseAcquisitionOrder()
    {
        using FakeCase lease = new() { FailDuringResource = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.Runtime.StartAsync());
        Assert.Equal(new[] { "r2", "r1" }, lease.ReleaseOrder);
        Assert.Empty(lease.HeldResources);
    }

    [Fact]
    public async Task DisposeAgainstInFlightStart_WaitsForStartToEnd_BeforeDisposing()
    {
        using FakeCase lease = new();
        TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.StartGate = startGate;
        Task start = lease.Runtime.StartAsync();
        await UntilAsync(() => lease.Transitions.Contains("start:begin"));

        Task dispose = lease.Runtime.DisposeAsync().AsTask();
        await UntilAsync(() => lease.DisposeWaiting);
        Assert.DoesNotContain("dispose:begin", lease.Transitions);

        startGate.SetResult();
        await Task.WhenAll(start, dispose);
        Assert.Equal(
            new[] { "start:begin", "start:end", "dispose:begin", "dispose:end" },
            lease.Transitions);
    }

    [Fact]
    public async Task DisposeAgainstCancelledStart_FollowsTheCancelledStart()
    {
        using FakeCase lease = new();
        TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.StartGate = startGate;
        using CancellationTokenSource cancellation = new();
        Task start = lease.Runtime.StartAsync(cancellation.Token);
        await UntilAsync(() => lease.Transitions.Contains("start:begin"));

        Task dispose = lease.Runtime.DisposeAsync().AsTask();
        await UntilAsync(() => lease.DisposeWaiting);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await dispose;
        Assert.Equal(new[] { "start:begin", "dispose:begin", "dispose:end" }, lease.Transitions);
    }

    [Fact]
    public async Task SearchRuntime_SupportsTheEnableDisableCycle_AfterRelease()
    {
        // Search's lease is a cycle, not terminal like the shutdown disposal
        // of the Todo/QuickCapture runtimes: the settings coordinator
        // re-enables through StartAsync after a disable disposal.
        using SearchCase lease = new();
        await lease.Runtime.StartAsync();
        await lease.Runtime.DisposeAsync();
        await lease.Runtime.StartAsync();
        Assert.True(lease.LeaseHeld);
        Assert.Equal(2, lease.Acquisitions);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "The expected transition never happened.");
    }

    // ── Shared case surface ───────────────────────────────────────────

    private interface ILeaseCase : IDisposable
    {
        string Name { get; }
        IFeatureRuntime Runtime { get; }
        bool LeaseHeld { get; }
        int Acquisitions { get; }
        int CompletedReleases { get; }
        void FailNextAcquisition();
    }

    // ── Fake case: the law itself, with gated transitions ─────────────

    private sealed class FakeCase : ILeaseCase
    {
        private readonly FakeRuntime _runtime = new();

        public string Name => FakeName;
        public IFeatureRuntime Runtime => _runtime;
        public bool LeaseHeld => _runtime.HeldResources.Count > 0;
        public int Acquisitions => _runtime.Acquisitions;
        public int CompletedReleases => _runtime.LeasesReleased;
        public IReadOnlyList<string> HeldResources => _runtime.HeldResources;
        public IReadOnlyList<string> ReleaseOrder => _runtime.ReleaseOrder;
        public IReadOnlyList<string> Transitions => _runtime.Transitions;
        public bool DisposeWaiting => _runtime.DisposeWaiting;

        public int FailDuringResource
        {
            get => _runtime.FailDuringResource;
            init => _runtime.FailDuringResource = value;
        }

        public TaskCompletionSource? StartGate
        {
            get => _runtime.StartGate;
            set => _runtime.StartGate = value;
        }

        public void FailNextAcquisition() => _runtime.FailNextStart = true;
        public void Dispose() { }
    }

    private sealed class FakeRuntime : IFeatureRuntime
    {
        private readonly SemaphoreSlim _transition = new(1, 1);
        private readonly List<string> _held = [];
        private readonly List<string> _releaseOrder = [];
        private readonly List<string> _transitions = [];

        public int Acquisitions { get; private set; }
        public int LeasesReleased { get; private set; }
        public int FailDuringResource { get; set; }
        public bool FailNextStart { get; set; }
        public TaskCompletionSource? StartGate { get; set; }
        public bool DisposeWaiting { get; private set; }
        public IReadOnlyList<string> HeldResources => _held;
        public IReadOnlyList<string> ReleaseOrder => _releaseOrder;
        public IReadOnlyList<string> Transitions => _transitions;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _transition.WaitAsync(cancellationToken);
            try
            {
                _transitions.Add("start:begin");
                cancellationToken.ThrowIfCancellationRequested();
                if (_held.Count > 0)
                {
                    // Idempotent: the lease is already held, this is a no-op.
                    return;
                }
                if (StartGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                bool failFirst = FailNextStart;
                FailNextStart = false;
                try
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _held.Add($"r{i}");
                        if (_held.Count == 1) Acquisitions++;
                        if (failFirst && i == 1)
                            throw new InvalidOperationException("first resource failed");
                        if (FailDuringResource == i)
                            throw new InvalidOperationException($"acquiring r{i} failed");
                    }
                    _transitions.Add("start:end");
                }
                catch
                {
                    RollBackHeld();
                    throw;
                }
            }
            finally
            {
                _transition.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeWaiting = true;
            await _transition.WaitAsync();
            try
            {
                DisposeWaiting = false;
                _transitions.Add("dispose:begin");
                if (_held.Count > 0) LeasesReleased++;
                RollBackHeld();
                _transitions.Add("dispose:end");
            }
            finally
            {
                _transition.Release();
            }
        }

        private void RollBackHeld()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                _releaseOrder.Add(_held[i]);
                _held.RemoveAt(i);
            }
        }
    }

    // ── Production runtime cases (thin adapters, no redesign) ─────────

    private sealed class TodoCase : ILeaseCase
    {
        private static readonly TodoReminderSettings Active = new(true, true, 5);
        private readonly List<Session> _sessions = [];
        private readonly TodoReminderRuntime _runtime;
        private bool _failNext;

        public TodoCase()
        {
            _runtime = new TodoReminderRuntime(
                () =>
                {
                    Session session = new() { FailStart = _failNext };
                    _failNext = false;
                    _sessions.Add(session);
                    return session;
                },
                () => Active);
        }

        public string Name => TodoName;
        public IFeatureRuntime Runtime => _runtime;
        public bool LeaseHeld => _runtime.Current is not null;
        public int Acquisitions => _runtime.Current is null ? 0 : 1;
        public int CompletedReleases => _sessions.Count(session => session.Disposals > 0);
        public void FailNextAcquisition() => _failNext = true;
        public void Dispose() { }

        private sealed class Session : ITodoReminderSession
        {
            public bool FailStart { get; init; }
            public int Starts { get; private set; }
            public int Refreshes { get; private set; }
            public int Disposals { get; private set; }
            public void Start()
            {
                Starts++;
                if (FailStart) throw new InvalidOperationException("timer registration failed");
            }
            public void Refresh() => Refreshes++;
            public Task<int> CheckNowAsync(DateTimeOffset now) => Task.FromResult(0);
            public ValueTask DisposeAsync()
            {
                Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class QuickCaptureCase : ILeaseCase
    {
        private readonly List<Session> _sessions = [];
        private readonly QuickCaptureClipboardRuntime _runtime;
        private bool _failNext;

        public QuickCaptureCase()
        {
            _runtime = new QuickCaptureClipboardRuntime(
                () => true,
                () =>
                {
                    Session session = new() { FailRefresh = _failNext };
                    _failNext = false;
                    _sessions.Add(session);
                    return session;
                },
                _ => { });
        }

        public string Name => QuickCaptureName;
        public IFeatureRuntime Runtime => _runtime;
        public bool LeaseHeld => _runtime.Current is not null;
        public int Acquisitions => _sessions.Count;
        public int CompletedReleases => _sessions.Count(session => session.Disposed);
        public void FailNextAcquisition() => _failNext = true;
        public void Dispose() { }

        private sealed class Session : IQuickCaptureClipboardSession
        {
            public bool FailRefresh { get; init; }

#pragma warning disable CS0067 // The runtime subscribes/unsubscribes; cases never raise it.
            public event Action? DiagnosticsChanged;
#pragma warning restore CS0067
            public bool Listening { get; private set; }
            public bool Disposed { get; private set; }

            public void Refresh()
            {
                if (FailRefresh)
                    throw new InvalidOperationException("listener wiring failed after subscription");
                Listening = true;
            }

            public void CaptureCurrent() { }

            public QuickCaptureClipboardDiagnostics GetDiagnostics() =>
                new(true, Listening, null, Listening ? "enabled" : "disabled", null);

            public Task StopAsync()
            {
                Listening = false;
                return Task.CompletedTask;
            }

            public void Dispose() => Disposed = true;
        }
    }

    private sealed class SearchCase : ILeaseCase
    {
        private readonly SearchFeatureRuntime _runtime;
        private bool _chainLive;
        private int _acquisitions;
        private int _releases;
        private bool _failNext;

        public SearchCase()
        {
            _runtime = new SearchFeatureRuntime(
                () =>
                {
                    bool fail = _failNext;
                    _failNext = false;
                    if (fail) throw new InvalidOperationException("search chain failed mid-initialization");
                    _chainLive = true;
                    _acquisitions++;
                },
                () =>
                {
                    _chainLive = false;
                    _releases++;
                });
        }

        public string Name => SearchName;
        public IFeatureRuntime Runtime => _runtime;
        public bool LeaseHeld => _chainLive;
        public int Acquisitions => _acquisitions;
        public int CompletedReleases => _releases;
        public void FailNextAcquisition() => _failNext = true;
        public void Dispose() { }
    }
}
