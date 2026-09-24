using DeskBox.Contracts;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal sealed class DispatcherBackupTimer(DispatcherQueue dispatcher) : IBackupTimer
{
    private DispatcherQueueTimer? _timer;
    public event Action? Tick;

    public void Start()
    {
        if (_timer is not null) return;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => Tick?.Invoke();

    public void Stop()
    {
        if (_timer is null) return;
        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer = null;
    }

    public void Dispose() => Stop();
}
