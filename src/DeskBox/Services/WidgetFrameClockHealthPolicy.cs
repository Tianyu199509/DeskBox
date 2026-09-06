namespace DeskBox.Services;

/// <summary>Detects a DWM hint consistently slower than the participating display budget.</summary>
internal sealed class WidgetFrameClockHealthPolicy
{
    private int _consecutiveSlowWaits;

    internal bool RecordWait(double waitMilliseconds, double targetMilliseconds)
    {
        if (!double.IsFinite(targetMilliseconds) || targetMilliseconds <= 0 ||
            !double.IsFinite(waitMilliseconds) || waitMilliseconds < targetMilliseconds * 1.5)
        {
            _consecutiveSlowWaits = 0;
            return false;
        }
        return ++_consecutiveSlowWaits >= 8;
    }
}
