using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Fault-injection tests for the startup pipeline
/// (docs/architecture/module-boundary-roadmap-20260918.md, cut 1): an optional
/// step's failure must degrade — recorded with a diagnostic, never fatal —
/// while a critical step's failure is recorded and rethrown into the fatal
/// path. The pipeline is exercised directly so a regression cannot hide
/// inside App.xaml.cs.
/// </summary>
public sealed class StartupPipelineTests
{
    [Fact]
    public void OptionalStep_FailureDegradesAndDoesNotThrow()
    {
        var pipeline = CreatePipeline(out _, out _);

        pipeline.RunOptional("broken-feature", () => throw new InvalidOperationException("boom"));

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal("broken-feature", result.Name);
        Assert.Equal(StartupCriticality.Optional, result.Criticality);
        Assert.Equal(StartupStepOutcome.Degraded, result.Outcome);
        Assert.Contains("boom", result.Diagnostic);
    }

    [Fact]
    public async Task OptionalAsyncStep_FailureDegradesAndDoesNotThrow()
    {
        var pipeline = CreatePipeline(out _, out _);

        await pipeline.RunOptionalAsync("broken-async", () => throw new IOException("disk gone"));

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal(StartupStepOutcome.Degraded, result.Outcome);
        Assert.Contains("disk gone", result.Diagnostic);
    }

    [Fact]
    public void CriticalStep_FailureIsRecordedAndRethrown()
    {
        var pipeline = CreatePipeline(out _, out _);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => pipeline.RunCritical("tray-icon", () => throw new InvalidOperationException("E_NOTIMPL")));

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal("tray-icon", result.Name);
        Assert.Equal(StartupCriticality.Critical, result.Criticality);
        Assert.Equal(StartupStepOutcome.Failed, result.Outcome);
        Assert.Contains("E_NOTIMPL", result.Diagnostic);
    }

    [Fact]
    public async Task CriticalAsyncStep_FailureIsRecordedAndRethrown()
    {
        var pipeline = CreatePipeline(out _, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.RunCriticalAsync("settings-load", () => throw new InvalidOperationException("corrupt")));

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal(StartupCriticality.Critical, result.Criticality);
        Assert.Equal(StartupStepOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void StepsAfterAnOptionalFailure_StillRun()
    {
        var pipeline = CreatePipeline(out _, out _);
        bool laterStepRan = false;

        pipeline.RunOptional("fails", () => throw new InvalidOperationException());
        pipeline.RunOptional("still-runs", () => laterStepRan = true);

        Assert.True(laterStepRan, "An optional failure must not short-circuit the rest of startup.");
        Assert.Equal(2, pipeline.Snapshot().Count);
    }

    [Fact]
    public void Results_CaptureCriticalityOutcomeDurationAndDiagnostic()
    {
        var pipeline = CreatePipeline(out _, out _);

        pipeline.RunOptional("sync-ok", () => { });
        pipeline.RunCritical("critical-ok", () => { });

        IReadOnlyList<StartupStepResult> results = pipeline.Snapshot();
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
            Assert.True(result.DurationMs >= 0);
            Assert.Null(result.Diagnostic);
        });
        Assert.Equal(StartupCriticality.Optional, results[0].Criticality);
        Assert.Equal(StartupCriticality.Critical, results[1].Criticality);
    }

    [Fact]
    public void MarkProgress_FiresBeforeEveryStep()
    {
        var pipeline = CreatePipeline(out _, out Func<int> progressMarks);

        pipeline.RunOptional("a", () => { });
        pipeline.RunCritical("b", () => { });
        pipeline.RunOptional("fails", () => throw new InvalidOperationException());

        Assert.Equal(3, progressMarks());
    }

    [Fact]
    public void WriteSummary_ReportsDegradedAndFailedSteps()
    {
        var pipeline = CreatePipeline(out List<string> log, out _);
        pipeline.RunOptional("ok", () => { });
        pipeline.RunOptional("degraded-step", () => throw new InvalidOperationException("why"));
        try
        {
            pipeline.RunCritical("failed-step", () => throw new InvalidOperationException("fatal"));
        }
        catch (InvalidOperationException)
        {
        }

        pipeline.WriteSummary();

        string summary = Assert.Single(log, line => line.Contains("Pipeline:", StringComparison.Ordinal));
        Assert.Contains("1 degraded", summary, StringComparison.Ordinal);
        Assert.Contains("1 failed", summary, StringComparison.Ordinal);
        Assert.Contains(log, line => line.Contains("degraded-step", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("failed-step", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("Step 'ok'", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordDegraded_CapturesBespokePathFailures()
    {
        // The widget-restore path keeps its own catch because it must unwind
        // the desktop-layer deferral; it still contributes a structured record.
        var pipeline = CreatePipeline(out _, out _);

        pipeline.RecordDegraded("restore-widgets", "simulated restore failure");

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal("restore-widgets", result.Name);
        Assert.Equal(StartupStepOutcome.Degraded, result.Outcome);
        Assert.Equal("simulated restore failure", result.Diagnostic);
    }

    [Fact]
    public void SlowStep_IsLoggedButNotAborted()
    {
        var pipeline = CreatePipeline(out List<string> log, out _);
        pipeline.SlowStepWarnMs = 1;

        pipeline.RunOptional("slow-but-fine", () => Thread.Sleep(20));

        StartupStepResult result = Assert.Single(pipeline.Snapshot());
        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Contains(log, line =>
            line.Contains("slow-but-fine", StringComparison.Ordinal) &&
            line.Contains("soft budget", StringComparison.Ordinal));
    }

    private static StartupPipeline CreatePipeline(out List<string> log, out Func<int> progressMarks)
    {
        var lines = new List<string>();
        int marks = 0;
        log = lines;
        progressMarks = () => marks;
        return new StartupPipeline(lines.Add, () => marks++);
    }
}
