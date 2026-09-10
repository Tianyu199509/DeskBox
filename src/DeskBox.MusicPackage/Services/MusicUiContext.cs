using Microsoft.UI.Dispatching;
namespace DeskBox.MusicPackage.Services;

// Each NativeAOT DLL has its own managed runtime. A UI thread entering from
// the host does not inherit that runtime's SynchronizationContext.
internal static class MusicUiContext
{
    internal static IDisposable Enter()
    {
        var queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Music UI entry requires a DispatcherQueue.");
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new MusicSynchronizationContext(action => queue.TryEnqueue(() => action())));
        return new Scope(previous);
    }
    internal static void Invoke(Action action)
    {
        using var scope = Enter();
        action();
    }
    internal static bool TryEnqueue(DispatcherQueue queue, Action action) =>
        queue.TryEnqueue(() => Invoke(action));
    private sealed class Scope(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }
}

// Re-enter the package context on EVERY posted continuation. The SDK queue
// context can dispatch the first await to UI without retaining a context for
// the next await when the callback enters an independent NativeAOT runtime.
internal sealed class MusicSynchronizationContext(Func<Action, bool> enqueue) : SynchronizationContext
{
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!enqueue(() =>
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        })) throw new InvalidOperationException("Music dispatcher no longer accepts work.");
    }
}
