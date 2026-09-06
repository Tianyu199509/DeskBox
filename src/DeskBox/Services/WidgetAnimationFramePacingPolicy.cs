namespace DeskBox.Services;

/// <summary>
/// Schedules native window submissions by elapsed time. A late frame consumes
/// the latest state immediately; it is never divided again by a nominal Hz.
/// Only measured submission work reduces the cadence, with hysteresis allowing
/// it to recover when that work becomes cheaper. Keep one instance per owner.
/// </summary>
internal sealed class WidgetAnimationFramePacingPolicy
{
    private const double WorkBudgetFraction = 0.7;
    private const int OverloadSamples = 3;
    private const int RecoverySamples = 6;
    private double _frameBudgetMilliseconds;
    private double _nextSubmissionMilliseconds;
    private double _averageWorkMilliseconds;
    private bool _hasWorkSample;
    private int _overloadSamples;
    private int _recoverySamples;

    public double TargetIntervalMilliseconds { get; private set; }

    public void Reset(double timestampMs, double frameBudgetMs)
    {
        _frameBudgetMilliseconds = NormalizeBudget(frameBudgetMs);
        TargetIntervalMilliseconds = _frameBudgetMilliseconds;
        _nextSubmissionMilliseconds = timestampMs;
        _averageWorkMilliseconds = 0;
        _hasWorkSample = false;
        _overloadSamples = 0;
        _recoverySamples = 0;
    }

    public bool ShouldSubmit(double timestampMs, double frameBudgetMs, bool force = false)
    {
        double budget = NormalizeBudget(frameBudgetMs);
        if (Math.Abs(budget - _frameBudgetMilliseconds) > 0.01)
        {
            // A mode/monitor change must not inherit an old display's cadence.
            // Retain measured work, but reconsider its cost against this budget.
            _frameBudgetMilliseconds = budget;
            TargetIntervalMilliseconds = Math.Max(budget,
                _hasWorkSample ? _averageWorkMilliseconds / WorkBudgetFraction : budget);
            _nextSubmissionMilliseconds = Math.Min(_nextSubmissionMilliseconds,
                timestampMs + TargetIntervalMilliseconds);
            _overloadSamples = 0;
            _recoverySamples = 0;
        }

        // Small dispatch jitter should not turn a native-rate stream into half
        // rate. This is an early tolerance, never a timer or a fixed FPS limit.
        double tolerance = Math.Min(0.5, budget * 0.1);
        return force || timestampMs + tolerance >= _nextSubmissionMilliseconds;
    }

    /// <param name="timestampMs">
    /// The frame/work start time used for ShouldSubmit, not the completion time.
    /// Scheduling from completion would add the work duration a second time.
    /// </param>
    public void RecordSubmission(double timestampMs, double workMilliseconds)
    {
        if (double.IsFinite(workMilliseconds) && workMilliseconds >= 0)
        {
            _averageWorkMilliseconds = _hasWorkSample
                ? _averageWorkMilliseconds * 0.75 + workMilliseconds * 0.25
                : workMilliseconds;
            _hasWorkSample = true;
            double desired = Math.Max(_frameBudgetMilliseconds,
                _averageWorkMilliseconds / WorkBudgetFraction);
            if (desired > TargetIntervalMilliseconds * 1.15)
            {
                _recoverySamples = 0;
                if (++_overloadSamples >= OverloadSamples)
                {
                    TargetIntervalMilliseconds = desired;
                    _overloadSamples = 0;
                }
            }
            else if (desired < TargetIntervalMilliseconds * 0.85 ||
                (desired <= _frameBudgetMilliseconds &&
                 TargetIntervalMilliseconds > _frameBudgetMilliseconds))
            {
                _overloadSamples = 0;
                if (++_recoverySamples >= RecoverySamples)
                {
                    TargetIntervalMilliseconds = Math.Max(desired,
                        TargetIntervalMilliseconds * 0.75);
                    _recoverySamples = 0;
                }
            }
            else
            {
                _overloadSamples = 0;
                _recoverySamples = 0;
            }
        }

        double interval = Math.Max(_frameBudgetMilliseconds, TargetIntervalMilliseconds);
        // Preserve phase when a faster shared clock serves a slower display.
        // If the clock has stalled, discard missed deadlines instead of issuing
        // a burst of catch-up updates or multiplying the already slow cadence.
        _nextSubmissionMilliseconds = timestampMs - _nextSubmissionMilliseconds >= interval
            ? timestampMs + interval
            : _nextSubmissionMilliseconds + interval;
    }

    private static double NormalizeBudget(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1000d / 60;
}
