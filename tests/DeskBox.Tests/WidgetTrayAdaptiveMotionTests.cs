using System.Diagnostics;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTrayAdaptiveMotionTests
{
    [Fact]
    public void HidePreparationFailure_FinalizesVisibilityBeforeReturningNoAnimation()
    {
        bool nativeVisible = true;
        bool inputSuppressed = true;
        var failure = new System.ComponentModel.Win32Exception("SetWindowPos failed");
        Exception? reported = null;

        bool prepared = WidgetTrayAnimationPreparation.TryPrepare(
            () => throw failure,
            () => { nativeVisible = false; inputSuppressed = false; },
            ex => reported = ex);

        Assert.False(prepared);
        Assert.Same(failure, reported);
        Assert.False(nativeVisible);
        Assert.False(inputSuppressed);
    }

    [Fact]
    public void ShowPreparationAndRestoreFailure_StillUncloaksAndResumesContent()
    {
        bool cloaked = false;
        bool contentResumed = false;
        var failures = new List<Exception>();

        bool prepared = WidgetTrayAnimationPreparation.TryPrepare(
            () =>
            {
                cloaked = true;
                throw new System.ComponentModel.Win32Exception("Preparing off-screen position failed");
            },
            () => WidgetTrayAnimationPreparation.CompleteShow(
                () => throw new System.ComponentModel.Win32Exception("Restoring position failed"),
                () => cloaked = false,
                () => contentResumed = true,
                failures.Add),
            failures.Add);

        Assert.False(prepared);
        Assert.False(cloaked);
        Assert.True(contentResumed);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void ShowCleanup_RevealFailureDoesNotSkipContentLifecycle()
    {
        var actions = new List<string>();
        var failures = new List<Exception>();
        WidgetTrayAnimationPreparation.CompleteShow(
            () => actions.Add("restore-position"),
            () => throw new InvalidOperationException("DWM reveal failed"),
            () => actions.Add("resume-content"),
            failures.Add);

        Assert.Equal(new[] { "restore-position", "resume-content" }, actions);
        Assert.Single(failures);
    }

    [Fact]
    public void SuccessfulPreparation_DoesNotCompleteHostAheadOfAnimation()
    {
        bool completed = false;
        bool reported = false;
        bool prepared = WidgetTrayAnimationPreparation.TryPrepare(
            () => { },
            () => completed = true,
            _ => reported = true);
        Assert.True(prepared);
        Assert.False(completed);
        Assert.False(reported);
    }

    [Fact]
    public void NativeTransaction_UsesNewestHandleAndDoesNotRepeatSuccessfulBatch()
    {
        var api = new FakeNativeApi();
        bool[] results = WidgetTrayWindowPositionCommitter.Commit(Positions, api);

        Assert.All(results, result => Assert.True(result));
        Assert.Equal(new IntPtr(12), Assert.Single(api.Ended));
        Assert.Empty(api.SetPositions);
    }

    [Fact]
    public void FailedDefer_DoesNotEndInvalidTransaction_AndRetriesEveryWindow()
    {
        var api = new FakeNativeApi { FailDeferAt = 2, FailedSetWindow = new IntPtr(2) };
        bool[] results = WidgetTrayWindowPositionCommitter.Commit(Positions, api);

        Assert.Empty(api.Ended);
        Assert.Equal(Positions, api.SetPositions);
        Assert.Equal(new[] { true, false }, results);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedBeginOrEnd_RetriesAllWindowPositions(bool failBegin)
    {
        var api = new FakeNativeApi { FailBegin = failBegin, FailEnd = !failBegin };
        bool[] results = WidgetTrayWindowPositionCommitter.Commit(Positions, api);

        Assert.All(results, result => Assert.True(result));
        Assert.Equal(Positions, api.SetPositions);
        Assert.Equal(failBegin ? 0 : 1, api.Ended.Count);
    }

    [Fact]
    public async Task FinalPositionFailure_FaultsIdleAndNeverReportsCompletion()
    {
        var run = new FakeAnimationRun { CommitSucceeds = false };
        int completed = 0;
        int failed = 0;
        run.Driver.Start([Entry(1, completed: () => completed++, failed: () => failed++)],
            1, SettingsService.WidgetAnimationEasingNone, true, 0);
        Task idle = run.Driver.WaitForIdleAsync();

        run.Tick(0);
        run.Tick(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => idle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Driver.WaitForIdleAsync());
        Assert.Equal(0, completed);
        Assert.Equal(1, failed);
        Assert.False(run.Driver.IsRunning);
        Assert.True(run.RegistrationDisposed);
    }

    [Fact]
    public async Task Cancellation_ReleasesRegistrationWithoutCompletingOldGeneration()
    {
        var run = new FakeAnimationRun();
        int completed = 0;
        run.Driver.Start([Entry(1, completed: () => completed++)],
            100, SettingsService.WidgetAnimationEasingNone, true, 0);
        Task idle = run.Driver.WaitForIdleAsync();
        run.Tick(0);
        run.Driver.Cancel();
        await idle;

        Assert.True(run.RegistrationDisposed);
        Assert.Equal(0, completed);
        Assert.Empty(run.CommittedPositions);
    }

    [Fact]
    public async Task UnchangedRoundedPosition_IsSkippedUntilForcedFinalFrame()
    {
        var run = new FakeAnimationRun();
        run.Driver.Start([Entry(1, destination: 0.4)],
            100, SettingsService.WidgetAnimationEasingNone, true, 0);
        Task idle = run.Driver.WaitForIdleAsync();
        for (int elapsed = 0; elapsed < 100; elapsed += 10)
        {
            run.Tick(elapsed);
        }
        Assert.Empty(run.CommittedPositions);

        run.Tick(100);
        await idle;
        Assert.Equal(new WidgetTrayWindowPosition(new IntPtr(1), 0, 0),
            Assert.Single(Assert.Single(run.CommittedPositions)));
    }

    [Fact]
    public void MixedDisplayBatch_SubmitsByEachDisplayBudget()
    {
        var run = new FakeAnimationRun();
        run.Budgets[new IntPtr(1)] = 1000d / 60;
        run.Budgets[new IntPtr(2)] = 1000d / 144;
        run.Driver.Start([Entry(1, destination: 10000), Entry(2, destination: 10000)],
            2000, SettingsService.WidgetAnimationEasingNone, true, 0);

        for (int tick = 0; tick <= 144; tick++)
        {
            run.Tick(tick * 1000d / 144);
        }
        int slowSubmissions = run.CommittedPositions.Sum(batch => batch.Count(position => position.WindowHandle == new IntPtr(1)));
        int fastSubmissions = run.CommittedPositions.Sum(batch => batch.Count(position => position.WindowHandle == new IntPtr(2)));
        Assert.InRange(slowSubmissions, 58, 62);
        Assert.InRange(fastSubmissions, 142, 144);
        run.Driver.Cancel();
    }

    [Fact]
    public void Tracker_DistinguishesCpuCallbacksFromSuccessfulNativeCommits()
    {
        long start = Stopwatch.GetTimestamp();
        var tracker = new WidgetTrayAnimationFrameTracker(start, [60, 144, 144]);
        for (int tick = 1; tick <= 10; tick++)
        {
            tracker.RecordFrame(start + Ticks(tick * 7));
        }
        tracker.RecordPositionSubmission(start + Ticks(14), 144);
        tracker.RecordPositionSubmission(start + Ticks(14), 144);
        tracker.RecordPositionSubmission(start + Ticks(35), 144);
        tracker.RecordPositionSubmission(start + Ticks(35), 60);
        var results = tracker.Complete(start + Ticks(70));

        var fast = Assert.Single(results, summary => summary.RefreshRateHz == 144);
        var slow = Assert.Single(results, summary => summary.RefreshRateHz == 60);
        Assert.Equal(10, fast.FrameCount);
        Assert.Equal(3, fast.PositionSubmissionCount);
        Assert.Equal(2, fast.PositionSubmissionTickCount);
        Assert.Equal(21, fast.MaximumPositionSubmissionIntervalMilliseconds, 3);
        Assert.Equal(1, slow.PositionSubmissionCount);
    }

    private static readonly WidgetTrayWindowPosition[] Positions =
    [new(new IntPtr(1), 100, 200), new(new IntPtr(2), 300, 400)];

    private static long Ticks(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);

    private static WidgetTrayBatchAnimationEntry Entry(int handle, double destination = 100,
        Action? completed = null, Action? failed = null) => new()
    {
        WindowHandle = new IntPtr(handle), BaseX = 0, BaseY = 0,
        FromOffsetX = 0, FromOffsetY = 0, ToOffsetX = destination, ToOffsetY = 0,
        RefreshRateHz = 60, IsValid = () => true,
        Completed = completed ?? (() => { }), Failed = failed
    };

    private sealed class FakeNativeApi : WidgetTrayWindowPositionCommitter.INativeApi
    {
        private int _deferCount;
        public bool FailBegin { get; init; }
        public bool FailEnd { get; init; }
        public int FailDeferAt { get; init; }
        public IntPtr FailedSetWindow { get; init; }
        public List<IntPtr> Ended { get; } = [];
        public List<WidgetTrayWindowPosition> SetPositions { get; } = [];
        public IntPtr Begin(int count) => FailBegin ? IntPtr.Zero : new IntPtr(10);
        public IntPtr Defer(IntPtr transaction, WidgetTrayWindowPosition position) =>
            ++_deferCount == FailDeferAt ? IntPtr.Zero : new IntPtr(10 + _deferCount);
        public bool End(IntPtr transaction)
        {
            Ended.Add(transaction);
            return !FailEnd;
        }
        public bool Set(WidgetTrayWindowPosition position)
        {
            SetPositions.Add(position);
            return position.WindowHandle != FailedSetWindow;
        }
    }

    private sealed class FakeAnimationRun
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private long _timestamp;
        private Action? _callback;
        public bool CommitSucceeds { get; init; } = true;
        public bool RegistrationDisposed { get; private set; }
        public Dictionary<IntPtr, double> Budgets { get; } = [];
        public List<WidgetTrayWindowPosition[]> CommittedPositions { get; } = [];
        public WidgetTrayBatchAnimationDriver Driver { get; }

        public FakeAnimationRun()
        {
            Driver = new WidgetTrayBatchAnimationDriver(
                (callback, _) =>
                {
                    _callback = callback;
                    return new CallbackRegistration(() => RegistrationDisposed = true);
                },
                entry => Budgets.GetValueOrDefault(entry.WindowHandle, 1000d / 60),
                positions =>
                {
                    CommittedPositions.Add(positions.ToArray());
                    return Enumerable.Repeat(CommitSucceeds, positions.Count).ToArray();
                },
                () => _timestamp);
        }

        public void Tick(double elapsedMs)
        {
            _timestamp = _start + Ticks(elapsedMs);
            _callback?.Invoke();
        }
    }

    private sealed class CallbackRegistration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
