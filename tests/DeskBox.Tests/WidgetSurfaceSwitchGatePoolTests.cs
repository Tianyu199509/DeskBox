using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSurfaceSwitchGatePoolTests
{
    [Fact]
    public void SameSurface_ReusesGateAcrossHostLifetimes()
    {
        var pool = new WidgetSurfaceSwitchGatePool();

        Assert.Same(pool.Get("surface"), pool.Get("surface"));
    }

    [Fact]
    public async Task DifferentSurfaces_CanEnterConcurrently()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        SemaphoreSlim first = pool.Get("surface-1");
        SemaphoreSlim second = pool.Get("surface-2");

        await first.WaitAsync();
        bool secondEntered = await second.WaitAsync(TimeSpan.FromMilliseconds(50));

        Assert.NotSame(first, second);
        Assert.True(secondEntered);
        first.Release();
        second.Release();
    }

    [Fact]
    public void RetiredSurface_RemovesItsStableGateWithoutDisposingActiveLease()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        SemaphoreSlim retired = pool.Get("retired-surface");
        retired.Wait();

        Assert.True(pool.Remove("retired-surface"));
        Assert.Equal(0, pool.Count);

        retired.Release();
        SemaphoreSlim replacement = pool.Get("retired-surface");
        Assert.NotSame(retired, replacement);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task AcquireMany_HoldsEachSurfaceUntilLeaseIsDisposed()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        IDisposable first = await pool.AcquireManyAsync(
            ["surface-b", "surface-a", "surface-a"]);
        Task<IDisposable> waiting = pool.AcquireManyAsync(
            ["surface-a", "surface-b"]);

        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using IDisposable second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(await pool.Get("surface-a").WaitAsync(TimeSpan.Zero));
        Assert.False(await pool.Get("surface-b").WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task AcquireMany_CancellationReleasesPreviouslyAcquiredSurface()
    {
        var pool = new WidgetSurfaceSwitchGatePool();
        SemaphoreSlim blocked = pool.Get("surface-b");
        await blocked.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> pending = pool.AcquireManyAsync(
            ["surface-a", "surface-b"],
            cancellation.Token);

        for (int attempt = 0;
             attempt < 100 && pool.Get("surface-a").CurrentCount != 0;
             attempt++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(0, pool.Get("surface-a").CurrentCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(await pool.Get("surface-a").WaitAsync(
            TimeSpan.FromMilliseconds(100)));
        pool.Get("surface-a").Release();
        blocked.Release();
    }
}
