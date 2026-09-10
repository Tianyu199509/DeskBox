extern alias GlancePkg;

using Coordinator = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceImageLoadCoordinator;
using Data = GlancePkg::DeskBox.Models.GlanceWidgetData;
using ImageInfo = GlancePkg::DeskBox.Models.GlanceImageInfo;
using Source = GlancePkg::DeskBox.Models.GlanceBackgroundSource;
using Category = GlancePkg::DeskBox.Models.GlanceOnlineImageCategory;

namespace DeskBox.Tests;

public sealed class NativeGlanceImageLoadCoordinatorTests
{
    [Fact]
    public async Task CachedImagesDisplayBeforeNetworkAndFailureKeepsCache()
    {
        var queue = new Queue<Action>();
        var displayed = new List<string>();
        var network = new TaskCompletionSource<IReadOnlyList<ImageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loader = new Coordinator((_, _) => Task.FromResult(Images("cache")),
            (_, _) => network.Task, action => { queue.Enqueue(action); return true; },
            images => displayed.Add(images[0].LocalPath));
        Task loading = loader.RequestAsync(new Data { BackgroundSource = Source.Bing });
        queue.Dequeue()();
        Assert.Equal(["cache"], displayed);
        network.SetException(new IOException("offline"));
        await loading;
        Assert.Empty(queue);
        Assert.Equal(["cache"], displayed);
    }

    [Fact]
    public async Task OldNetworkReplyCannotOverwriteNewSourceEvenWhenItIgnoresCancellation()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var displayed = new List<string>();
        var oldReply = new TaskCompletionSource<IReadOnlyList<ImageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loader = new Coordinator((data, _) => Task.FromResult(Images(data.BackgroundSource.ToString())),
            (_, _) => oldReply.Task, action => { queue.Enqueue(action); return true; },
            images => displayed.Add(images[0].LocalPath));
        Task old = loader.RequestAsync(new Data { BackgroundSource = Source.Bing });
        await loader.RequestAsync(new Data { BackgroundSource = Source.LocalFiles });
        oldReply.SetResult(Images("late-bing-result"));
        await old;
        while (queue.TryDequeue(out var action)) action();
        Assert.Equal(["LocalFiles"], displayed);
    }

    [Fact]
    public async Task RequestCapturesCategoryAndPathsAndDoesNotFetchOnlineForLocalSources()
    {
        Data? observed = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int networkCalls = 0;
        using var loader = new Coordinator(async (snapshot, _) =>
        {
            await gate.Task;
            observed = snapshot;
            return Images("local");
        }, (_, _) => { networkCalls++; return Task.FromResult(Images("network")); }, _ => true, _ => { });
        var data = new Data { BackgroundSource = Source.LocalFiles, OnlineImageCategory = Category.Cities, LocalImagePaths = ["original"] };
        Task loading = loader.RequestAsync(data);
        data.OnlineImageCategory = Category.Animals;
        data.LocalImagePaths[0] = "changed";
        gate.SetResult();
        await loading;
        Assert.Equal(Category.Cities, observed!.OnlineImageCategory);
        Assert.Equal(["original"], observed.LocalImagePaths);
        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public async Task UnloadAndDisposeInvalidateAlreadyQueuedResults()
    {
        var queue = new Queue<Action>();
        int applied = 0;
        using var loader = new Coordinator((_, _) => Task.FromResult(Images("local")),
            (_, _) => Task.FromResult(Images("online")), action => { queue.Enqueue(action); return true; }, _ => applied++);
        var settings = new Data { BackgroundSource = Source.LocalFiles };
        await loader.RequestAsync(settings);
        loader.Cancel();
        queue.Dequeue()();
        Assert.Equal(0, applied);
        await loader.RequestAsync(settings);
        queue.Dequeue()();
        Assert.Equal(1, applied);
        await loader.RequestAsync(settings);
        loader.Dispose();
        queue.Dequeue()();
        await loader.RequestAsync(settings);
        Assert.Empty(queue);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task CancellationSerializesWithAnAcceptedUiCommit()
    {
        var queue = new Queue<Action>();
        using var applyEntered = new ManualResetEventSlim();
        using var allowApply = new ManualResetEventSlim();
        using var loader = new Coordinator((_, _) => Task.FromResult(Images("local")),
            (_, _) => Task.FromResult(Images("online")), action => { queue.Enqueue(action); return true; },
            _ =>
            {
                applyEntered.Set();
                allowApply.Wait();
            });
        await loader.RequestAsync(new Data { BackgroundSource = Source.LocalFiles });

        Task commit = Task.Run(queue.Dequeue());
        Assert.True(applyEntered.Wait(TimeSpan.FromSeconds(5)));
        Task cancel = Task.Run(loader.Cancel);
        await Task.Delay(50);
        Assert.False(cancel.IsCompleted);
        allowApply.Set();
        await Task.WhenAll(commit, cancel);
    }

    private static IReadOnlyList<ImageInfo> Images(string path) => [new() { LocalPath = path }];
}
