// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Services;

/// <summary>
/// Whether a startup step may terminate the launch when it fails.
/// </summary>
internal enum StartupCriticality
{
    /// <summary>Failure means the process has no usable surface; startup is fatal.</summary>
    Critical,

    /// <summary>Failure degrades one feature; the app must keep starting.</summary>
    Optional
}

internal enum StartupStepOutcome
{
    Succeeded,
    /// <summary>Optional step failed; the feature is absent but the app is usable.</summary>
    Degraded,
    /// <summary>Critical step failed; the launch is being torn down.</summary>
    Failed
}

/// <summary>
/// One startup step's record: name, declared criticality, outcome, wall-clock
/// duration, and a diagnostic line when it did not succeed.
/// </summary>
internal sealed record StartupStepResult(
    string Name,
    StartupCriticality Criticality,
    StartupStepOutcome Outcome,
    long DurationMs,
    string? Diagnostic);

/// <summary>
/// Runs the startup sequence as an explicit pipeline instead of scattered
/// try/catch wrappers. Every step carries criticality, a duration measurement,
/// an outcome, and a diagnostic, so an optional failure is a *recorded*
/// degradation rather than a silent one. Slow steps are logged past a soft
/// budget but never aborted — legitimately long phases (multi-gigabyte data
/// restore) are covered by the stall watchdog, not per-step timeouts.
/// Critical failures are recorded, then rethrown so the caller's fatal path
/// keeps its existing semantics.
/// </summary>
internal sealed class StartupPipeline
{
    private readonly object _gate = new();
    private readonly List<StartupStepResult> _results = new();
    private readonly Action<string> _log;
    private readonly Action _markProgress;

    public StartupPipeline(Action<string> log, Action markProgress)
    {
        _log = log;
        _markProgress = markProgress;
    }

    /// <summary>Warn threshold; exceeding it logs a line but never aborts the step.</summary>
    public long SlowStepWarnMs { get; set; } = 20_000;

    public void RunOptional(string name, Action step)
    {
        _markProgress();
        long started = Environment.TickCount64;
        try
        {
            step();
            Record(name, StartupCriticality.Optional, StartupStepOutcome.Succeeded, ElapsedMs(started), null);
        }
        catch (Exception ex)
        {
            _log($"[Startup] Optional step '{name}' failed: {ex}");
            Record(name, StartupCriticality.Optional, StartupStepOutcome.Degraded, ElapsedMs(started), ex.ToString());
        }
    }

    public async Task RunOptionalAsync(string name, Func<Task> step)
    {
        _markProgress();
        long started = Environment.TickCount64;
        try
        {
            await step();
            Record(name, StartupCriticality.Optional, StartupStepOutcome.Succeeded, ElapsedMs(started), null);
        }
        catch (Exception ex)
        {
            _log($"[Startup] Optional step '{name}' failed: {ex}");
            Record(name, StartupCriticality.Optional, StartupStepOutcome.Degraded, ElapsedMs(started), ex.ToString());
        }
    }

    public void RunCritical(string name, Action step)
    {
        _markProgress();
        long started = Environment.TickCount64;
        try
        {
            step();
            Record(name, StartupCriticality.Critical, StartupStepOutcome.Succeeded, ElapsedMs(started), null);
        }
        catch (Exception ex)
        {
            Record(name, StartupCriticality.Critical, StartupStepOutcome.Failed, ElapsedMs(started), ex.ToString());
            throw;
        }
    }

    public async Task RunCriticalAsync(string name, Func<Task> step)
    {
        _markProgress();
        long started = Environment.TickCount64;
        try
        {
            await step();
            Record(name, StartupCriticality.Critical, StartupStepOutcome.Succeeded, ElapsedMs(started), null);
        }
        catch (Exception ex)
        {
            Record(name, StartupCriticality.Critical, StartupStepOutcome.Failed, ElapsedMs(started), ex.ToString());
            throw;
        }
    }

    /// <summary>
    /// Records a degradation detected by a bespoke recovery path that cannot
    /// route through a step call (it needs its own cleanup, e.g. the widget
    /// restore deferral). The path still logs its own line; this adds the
    /// structured record.
    /// </summary>
    public void RecordDegraded(string name, string? diagnostic) =>
        Record(name, StartupCriticality.Optional, StartupStepOutcome.Degraded, durationMs: 0, diagnostic);

    public IReadOnlyList<StartupStepResult> Snapshot()
    {
        lock (_gate)
        {
            return _results.ToArray();
        }
    }

    /// <summary>
    /// Writes the structured end-of-startup report: totals plus one line per
    /// step that did not succeed. Must never throw — it runs while startup is
    /// wrapping up and a logging failure must not take the launch down.
    /// </summary>
    public void WriteSummary()
    {
        try
        {
            IReadOnlyList<StartupStepResult> results = Snapshot();
            int critical = results.Count(r => r.Criticality == StartupCriticality.Critical);
            int degraded = results.Count(r => r.Outcome == StartupStepOutcome.Degraded);
            int failed = results.Count(r => r.Outcome == StartupStepOutcome.Failed);
            StartupStepResult? slowest = results.Count == 0
                ? null
                : results.MaxBy(r => r.DurationMs);

            _log(
                $"[Startup] Pipeline: {results.Count} steps ({critical} critical), " +
                $"{degraded} degraded, {failed} failed" +
                (slowest is null ? string.Empty : $"; slowest '{slowest.Name}' {slowest.DurationMs}ms"));

            foreach (StartupStepResult result in results.Where(r => r.Outcome != StartupStepOutcome.Succeeded))
            {
                string firstDiagnosticLine = result.Diagnostic?.Split('\n')[0] ?? string.Empty;
                _log(
                    $"[Startup] Step '{result.Name}' {result.Outcome.ToString().ToUpperInvariant()} " +
                    $"({result.Criticality.ToString().ToLowerInvariant()}) after {result.DurationMs}ms" +
                    (firstDiagnosticLine.Length == 0 ? string.Empty : $": {firstDiagnosticLine}"));
            }
        }
        catch (Exception ex)
        {
            try
            {
                _log($"[Startup] Pipeline summary failed: {ex.Message}");
            }
            catch
            {
                // The log sink itself is failing; nothing more can be reported.
            }
        }
    }

    private void Record(
        string name,
        StartupCriticality criticality,
        StartupStepOutcome outcome,
        long durationMs,
        string? diagnostic)
    {
        if (durationMs > SlowStepWarnMs && outcome == StartupStepOutcome.Succeeded)
        {
            _log($"[Startup] Step '{name}' took {durationMs}ms (soft budget {SlowStepWarnMs}ms)");
        }

        lock (_gate)
        {
            _results.Add(new StartupStepResult(name, criticality, outcome, durationMs, diagnostic));
        }
    }

    private static long ElapsedMs(long startedTickCount64) =>
        Math.Max(0, Environment.TickCount64 - startedTickCount64);
}
