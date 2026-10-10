namespace DeskBox.Tests;

using DeskBox.Helpers;

/// <summary>
/// Watchdog semantics for STA Shell operations (feedback 455): an operation
/// that keeps showing Shell progress must never be abandoned no matter how
/// long it runs; only total silence for the whole inactivity window may be
/// declared dead, after which the unabortable thread leaks by design while
/// the worker slot is released (rebuilt) and the caller gets a retryable
/// failure.
/// </summary>
public sealed class StaOperationWatchdogTests
{
    [Fact]
    public void ShouldAbandon_FalseBelowThreshold_TrueAtAndBeyondIt()
    {
        long clock = 1_000;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(500),
            () => Volatile.Read(ref clock));

        Assert.False(
            watchdog.ShouldAbandon(clock + 499, workerThread: null, out _));
        Assert.True(
            watchdog.ShouldAbandon(clock + 500, workerThread: null, out TimeSpan idleAtThreshold));
        Assert.Equal(
            500,
            idleAtThreshold.TotalMilliseconds);
        Assert.True(
            watchdog.ShouldAbandon(clock + 5_000, workerThread: null, out TimeSpan idleBeyond));
        Assert.Equal(
            5_000,
            idleBeyond.TotalMilliseconds);
    }

    [Fact]
    public void Pulse_RenewsTheInactivityDeadline()
    {
        long clock = 0;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(500),
            () => Volatile.Read(ref clock));

        clock = 400;
        watchdog.Pulse();
        clock = 700;
        Assert.False(
            watchdog.ShouldAbandon(clock, workerThread: null, out _));

        clock = 901;
        Assert.True(
            watchdog.ShouldAbandon(clock, workerThread: null, out _));
    }

    [Fact]
    public void ShouldAbandon_StaysFalseWhileProgressKeepsArriving()
    {
        // Simulates a legitimate minute-level transfer: 30 renewal cycles,
        // each bringing the idle time right up to (but never across) the
        // threshold. Total runtime 27 fake seconds; a wall-clock timeout
        // would have killed it, a progress-renewed watchdog must not.
        long clock = 0;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(1_000),
            () => Volatile.Read(ref clock));

        for (int step = 1; step <= 30; step++)
        {
            clock = step * 900;
            watchdog.Pulse();
            clock += 899;
            Assert.False(
                watchdog.ShouldAbandon(clock, workerThread: null, out _));
        }

        clock += 200;
        Assert.True(
            watchdog.ShouldAbandon(clock, workerThread: null, out TimeSpan idle));
        Assert.Equal(1099, idle.TotalMilliseconds);
    }

    [Fact]
    public async Task ShouldAbandon_RunningWorkerThreadDoesNotSuppressTimeout()
    {
        // Only an administratively suspended thread suppresses the verdict;
        // a normally running (but silent) worker must still be abandoned.
        long clock = 0;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(100),
            () => Volatile.Read(ref clock));
        var running = new ManualResetEventSlim(false);
        var stop = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            running.Set();
            stop.Wait();
        });
        thread.Start();
        try
        {
            Assert.True(running.Wait(5_000));
            clock = 5_000;
            Assert.True(
                watchdog.ShouldAbandon(clock, thread, out _));
        }
        finally
        {
            stop.Set();
            await Task.Run(thread.Join);
        }
    }

    [Fact]
    public void PollInterval_IsDerivedFromTheInactivityTimeout()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            new StaOperationWatchdog(
                TimeSpan.FromMilliseconds(200),
                () => 0L).PollInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(150),
            new StaOperationWatchdog(
                TimeSpan.FromMilliseconds(600),
                () => 0L).PollInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            new StaOperationWatchdog(
                TimeSpan.FromSeconds(20),
                () => 0L).PollInterval);
    }

    [Fact]
    public async Task RunAsync_WithWatchdog_ReturnsFastOperationValue()
    {
        var runner = new BoundedStaOperationRunner(
            maxConcurrency: 1,
            maxQueued: 2,
            queueTimeout: TimeSpan.FromSeconds(5));
        long clock = 0;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(1_000),
            () => Volatile.Read(ref clock));

        StaOperationResult<int> result = await runner.RunAsync(
            () => 42,
            CancellationToken.None,
            watchdog);

        Assert.True(result.Started);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task RunAsync_WithoutWatchdog_StillCompletesNormally()
    {
        var runner = new BoundedStaOperationRunner(
            maxConcurrency: 1,
            maxQueued: 2,
            queueTimeout: TimeSpan.FromSeconds(5));

        StaOperationResult<string> result = await runner.RunAsync(
            () => "ok",
            CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public async Task RunAsync_Watchdog_AbandonsSilentOperation_RebuildsWorker_AndCountsAbandonment()
    {
        var runner = new BoundedStaOperationRunner(
            maxConcurrency: 1,
            maxQueued: 0,
            queueTimeout: TimeSpan.FromSeconds(5),
            threadName: "test silent STA");
        long clock = 0;
        var watchdog = new StaOperationWatchdog(
            TimeSpan.FromMilliseconds(300),
            () => Volatile.Read(ref clock),
            pollInterval: TimeSpan.FromMilliseconds(10));
        var release = new ManualResetEventSlim(false);
        long abandonmentsBefore = BoundedStaOperationRunner.TotalAbandonedOperations;

        Task<StaOperationResult<int>> silent = runner.RunAsync(
            () =>
            {
                release.Wait();
                return 0;
            },
            CancellationToken.None,
            watchdog);

        try
        {
            // Advance fake time in steps so the start pulse — whenever the
            // STA thread happens to run — can never keep the idle window
            // below the threshold forever.
            for (int i = 0; i < 50 && !silent.IsCompleted; i++)
            {
                Interlocked.Add(ref clock, 100);
                await Task.Delay(15);
            }
        }
        finally
        {
            release.Set();
        }

        StaOperationAbandonedException exception =
            await Assert.ThrowsAsync<StaOperationAbandonedException>(
                () => silent);
        Assert.Equal(
            abandonmentsBefore + 1,
            BoundedStaOperationRunner.TotalAbandonedOperations);
        Assert.Equal(
            abandonmentsBefore + 1,
            exception.ProcessAbandonmentCount);
        Assert.True(exception.IdleDuration >= watchdog.InactivityTimeout);
        Assert.Equal("test silent STA", exception.WorkerName);

        // The abandoned worker's slot was released: with maxConcurrency 1 a
        // fresh call must start immediately on a rebuilt worker.
        StaOperationResult<int> rebuilt = await runner.RunAsync(
            () => 7,
            CancellationToken.None);
        Assert.True(rebuilt.Started);
        Assert.Equal(7, rebuilt.Value);
    }

    /// <summary>
    /// Source contract: every abandonment must be diagnosable from the log —
    /// the warning line carries the no-progress duration, the threshold and
    /// the process-wide abandonment counter.
    /// </summary>
    [Fact]
    public void AbandonmentPath_LogsDurationThresholdAndCounter()
    {
        string runner = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Helpers/BoundedStaOperationRunner.cs"));
        string transfer = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Services/FileService.ShellTransfer.cs"));

        Assert.Contains("processAbandonments=", runner, StringComparison.Ordinal);
        Assert.Contains("leaks by design", runner, StringComparison.Ordinal);
        // The shell transfer bridges liveness through its progress sink.
        Assert.Contains("_watchdog?.Pulse();", transfer, StringComparison.Ordinal);
        Assert.Contains(
            "InteractiveShellTransferInactivityTimeout",
            transfer,
            StringComparison.Ordinal);
    }

    private static string GetRepoFile(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(
                directory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file: {relativePath}");
    }
}
