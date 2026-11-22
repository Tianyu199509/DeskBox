using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

/// <summary>
/// Owns all <see cref="ICustomWidgetCapabilityProvider"/> instances for a <see cref="CustomWidgetViewModel"/>,
/// eliminating per-source timer and snapshot sprawl from the main view model.
/// </summary>
internal sealed class CustomWidgetCapabilityHost : IDisposable
{
    internal CustomWidgetCapabilityHost(CustomWidgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        SystemMetrics = new SystemMetricsCapabilityProvider(vm);
        Pomodoro = new PomodoroCapabilityProvider(vm);
        Countdown = new CountdownCapabilityProvider(vm);
        Calendar = new CalendarCapabilityProvider(vm);
        Weather = new WeatherCapabilityProvider(vm);
        DeviceState = new DeviceStateCapabilityProvider(vm);
        Clipboard = new ClipboardCapabilityProvider(vm);
        StorageDrives = new StorageDrivesCapabilityProvider(vm);
        Music = new MusicCapabilityProvider(vm);
        Todo = new TodoCapabilityProvider(vm);
        Clock = new ClockCapabilityProvider(vm);
        AiMonitor = new AiMonitorCapabilityProvider(vm);
        Network = new NetworkCapabilityProvider(vm);
        FileText = new FileTextCapabilityProvider(vm);
        Command = new CommandCapabilityProvider(vm);

        Providers =
        [
            SystemMetrics,
            Pomodoro,
            Countdown,
            Calendar,
            Weather,
            DeviceState,
            Clipboard,
            StorageDrives,
            Music,
            Todo,
            Clock,
            AiMonitor,
            Network,
            FileText,
            Command
        ];
    }

    internal IReadOnlyList<ICustomWidgetCapabilityProvider> Providers { get; }

    internal SystemMetricsCapabilityProvider SystemMetrics { get; }
    internal PomodoroCapabilityProvider Pomodoro { get; }
    internal CountdownCapabilityProvider Countdown { get; }
    internal CalendarCapabilityProvider Calendar { get; }
    internal WeatherCapabilityProvider Weather { get; }
    internal DeviceStateCapabilityProvider DeviceState { get; }
    internal ClipboardCapabilityProvider Clipboard { get; }
    internal StorageDrivesCapabilityProvider StorageDrives { get; }
    internal MusicCapabilityProvider Music { get; }
    internal TodoCapabilityProvider Todo { get; }
    internal ClockCapabilityProvider Clock { get; }
    internal AiMonitorCapabilityProvider AiMonitor { get; }
    internal NetworkCapabilityProvider Network { get; }
    internal FileTextCapabilityProvider FileText { get; }
    internal CommandCapabilityProvider Command { get; }

    internal CustomWidgetSystemMetricsSnapshot SystemMetricsSnapshot => SystemMetrics.Snapshot;
    internal CustomWidgetAiMonitorSnapshot AiMonitorSnapshot => AiMonitor.Snapshot;
    internal CustomWidgetWeatherSnapshot WeatherSnapshot => Weather.Snapshot;
    internal CustomWidgetDeviceStateSnapshot DeviceStateSnapshot
    {
        get => DeviceState.Snapshot;
        set => DeviceState.Snapshot = value;
    }
    internal CustomWidgetClipboardSnapshot ClipboardSnapshot
    {
        get => Clipboard.Snapshot;
        set => Clipboard.Snapshot = value;
    }
    internal CustomWidgetStorageDrivesSnapshot StorageDrivesSnapshot => StorageDrives.Snapshot;
    internal CustomWidgetMusicSnapshot MusicSnapshot => Music.Snapshot;
    internal CustomWidgetTodoSnapshot TodoSnapshot => Todo.Snapshot;

    internal void UpdateAllSubscriptions()
    {
        for (int i = 0; i < Providers.Count; i++)
        {
            Providers[i].UpdateSubscription();
        }
    }

    internal void StopAll()
    {
        for (int i = 0; i < Providers.Count; i++)
        {
            Providers[i].Stop();
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < Providers.Count; i++)
        {
            Providers[i].Dispose();
        }
    }
}
