namespace DeskBox.Services;

using DeskBox.Models;

/// <summary>
/// Throttles update-download progress reports (deep review R13, R12 debt ③
/// — originally a batch R9 note): the download loop reports every 128KB
/// chunk, which flooded the IPC push lane and the About page with hundreds
/// of near-identical updates per download. A report passes when it crosses
/// an integer-percent step, or at least 100ms has passed since the last
/// accepted report; the FIRST report and the final one (bytes reaching the
/// total) always pass. Percent math uses the last ACCEPTED report, so the
/// displayed progress never moves backwards.
/// </summary>
internal sealed class AppUpdateDownloadProgressThrottle
{
    private long _lastAcceptedBytes = -1;
    private long _lastAcceptedTicks;

    public bool ShouldPush(AppUpdateDownloadProgress progress, long nowTicks)
    {
        // A lower byte count than the last accepted report would move the
        // displayed progress backwards — drop it outright.
        if (_lastAcceptedBytes >= 0 && progress.BytesReceived < _lastAcceptedBytes)
        {
            return false;
        }

        bool first = _lastAcceptedBytes < 0;
        bool final = progress.TotalBytes is { } total && progress.BytesReceived >= total;
        bool percentStep = progress.TotalBytes is { } sameTotal &&
            _lastAcceptedBytes >= 0 &&
            progress.BytesReceived * 100 / sameTotal != _lastAcceptedBytes * 100 / sameTotal;
        bool timeElapsed = _lastAcceptedTicks == 0 || nowTicks - _lastAcceptedTicks >= 100;

        if (!first && !final && !percentStep && !timeElapsed)
        {
            return false;
        }

        _lastAcceptedBytes = progress.BytesReceived;
        _lastAcceptedTicks = nowTicks;
        return true;
    }
}
