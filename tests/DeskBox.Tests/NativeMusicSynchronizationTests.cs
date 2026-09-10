extern alias MusicPkg;
using System.Collections.Concurrent;
using Context = MusicPkg::DeskBox.MusicPackage.Services.MusicSynchronizationContext;
namespace DeskBox.Tests;

public sealed class NativeMusicSynchronizationTests
{
    [Fact]
    public async Task ConsecutiveAwaitsStayOnDispatcherAndRetainPackageContext()
    {
        using var queue = new BlockingCollection<Action>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new Context(action => { queue.Add(action); return true; });
        var thread = new Thread(() =>
        {
            foreach (Action action in queue.GetConsumingEnumerable()) action();
        });
        thread.Start();
        context.Post(async _ =>
        {
            try
            {
                int id = Environment.CurrentManagedThreadId;
                await Task.Delay(10);
                Assert.Equal(id, Environment.CurrentManagedThreadId);
                Assert.Same(context, SynchronizationContext.Current);
                await Task.Run(() => Thread.Sleep(10));
                Assert.Equal(id, Environment.CurrentManagedThreadId);
                Assert.Same(context, SynchronizationContext.Current);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }, null);
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { queue.CompleteAdding(); thread.Join(TimeSpan.FromSeconds(5)); }
    }
}
