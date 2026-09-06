using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class WidgetShellContentHostTests
{
    [Fact]
    public async Task SetContentAsync_InitializesSetsAndAppliesAppearance()
    {
        var calls = new List<string>();
        var content = new TestWidgetContent("first", WidgetKind.Todo, calls);
        var host = new WidgetShellContentHost(setContent: c => calls.Add($"set:{c.WidgetId}"));

        await host.SetContentAsync(content);

        Assert.Equal(content, host.CurrentContent);
        Assert.Equal(
        [
            "initialize:first",
            "set:first",
            "appearance:first"
        ], calls);
    }

    [Fact]
    public async Task SetContentAsync_DeactivatesPreviousContentWhenReplacing()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.File, calls);
        var second = new TestWidgetContent("second", WidgetKind.Todo, calls);
        var host = new WidgetShellContentHost(setContent: c => calls.Add($"set:{c.WidgetId}"));

        await host.SetContentAsync(first);
        await host.SetContentAsync(second);

        Assert.Equal(second, host.CurrentContent);
        Assert.Equal(
        [
            "initialize:first",
            "set:first",
            "appearance:first",
            "initialize:second",
            "deactivate:first",
            "set:second",
            "appearance:second",
            "dispose:first"
        ], calls);
    }

    [Fact]
    public async Task RefreshAndActivationCallbacks_ForwardToCurrentContent()
    {
        var calls = new List<string>();
        var content = new TestWidgetContent("first", WidgetKind.QuickCapture, calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(content);
        calls.Clear();

        await host.RefreshAsync();
        host.OnActivated();
        host.OnDeactivated();
        host.ApplyAppearance();

        Assert.Equal(
        [
            "refresh:first",
            "activate:first",
            "deactivate:first",
            "appearance:first"
        ], calls);
    }

    [Fact]
    public async Task ActivationCallbacks_OnlyForwardActualStateTransitions()
    {
        var calls = new List<string>();
        var content = new TestWidgetContent("first", WidgetKind.File, calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(content);
        calls.Clear();

        // A desktop-layer window may be shown with SW_SHOWNOACTIVATE and receive
        // an initial deactivated notification without ever becoming active.
        host.OnDeactivated();
        host.OnActivated();
        host.OnActivated();
        host.OnDeactivated();
        host.OnDeactivated();

        Assert.Equal(
        [
            "activate:first",
            "deactivate:first"
        ], calls);
    }

    [Fact]
    public async Task RevealLifecycle_ForwardsFirstFrameThenCompletionExactlyOnce()
    {
        var calls = new List<string>();
        var content = new LifecycleWidgetContent("weather", calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(content);
        calls.Clear();

        host.OnWindowVisibilityChanged(true);
        host.OnWindowRevealCompleted();
        host.OnWindowRevealCompleted();

        Assert.Equal(
        [
            "visible:weather:true",
            "reveal:weather"
        ], calls);
    }

    [Fact]
    public async Task RevealLifecycle_HideInvalidatesLateCompletionAndNextShowGetsNewGeneration()
    {
        var calls = new List<string>();
        var content = new LifecycleWidgetContent("music", calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(content);
        int initialGeneration = host.WindowRevealGeneration;
        calls.Clear();

        host.OnWindowVisibilityChanged(true);
        int firstRevealGeneration = host.WindowRevealGeneration;
        host.OnWindowVisibilityChanged(false);
        int hiddenGeneration = host.WindowRevealGeneration;
        host.OnWindowRevealCompleted();
        host.OnWindowVisibilityChanged(true);
        int secondRevealGeneration = host.WindowRevealGeneration;
        host.OnWindowRevealCompleted();

        Assert.True(firstRevealGeneration > initialGeneration);
        Assert.True(hiddenGeneration > firstRevealGeneration);
        Assert.True(secondRevealGeneration > hiddenGeneration);
        Assert.Equal(
        [
            "visible:music:true",
            "visible:music:false",
            "visible:music:true",
            "reveal:music"
        ], calls);
    }

    [Fact]
    public async Task RevealLifecycle_ContentReplacementReceivesCompletedVisibleState()
    {
        var calls = new List<string>();
        var first = new LifecycleWidgetContent("first", calls);
        var second = new LifecycleWidgetContent("second", calls);
        var host = new WidgetShellContentHost(setContent: _ => { });
        await host.SetContentAsync(first);
        host.OnWindowVisibilityChanged(true);
        host.OnWindowRevealCompleted();
        calls.Clear();

        await host.SetContentAsync(second);

        Assert.Contains("visible:second:true", calls);
        Assert.Contains("reveal:second", calls);
        Assert.True(
            calls.IndexOf("visible:second:true") < calls.IndexOf("reveal:second"));
    }

    [Fact]
    public async Task ReusableContent_PreparesProjectionInsteadOfReinitializing()
    {
        var calls = new List<string>();
        var content = new ReusableWidgetContent("cached", calls);
        var host = new WidgetShellContentHost(
            setContent: current => calls.Add($"set:{current.WidgetId}"));

        await host.SetContentAsync(content);

        Assert.Equal(
        [
            "prepare:cached",
            "set:cached",
            "appearance:cached"
        ], calls);
    }

    [Fact]
    public async Task PreparedTransition_KeepsOutgoingContentUntilCompletion()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.Todo, calls);
        var second = new TestWidgetContent("second", WidgetKind.Weather, calls);
        var host = new WidgetShellContentHost(
            setContent: content => calls.Add($"set:{content.WidgetId}"),
            beginTransition: (outgoing, incoming) =>
                calls.Add($"begin:{outgoing.WidgetId}->{incoming.WidgetId}"),
            completeTransition: () => calls.Add("complete"),
            rollbackTransition: content => calls.Add($"rollback:{content.WidgetId}"));
        await host.SetContentAsync(first);
        calls.Clear();

        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Same(first, host.CurrentContent);
        Assert.Equal(["initialize:second"], calls);

        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);

        Assert.NotNull(transition);
        Assert.Same(second, host.CurrentContent);
        Assert.DoesNotContain("dispose:first", calls);
        transition!.Complete();

        Assert.Equal(
        [
            "initialize:second",
            "deactivate:first",
            "begin:first->second",
            "appearance:second",
            "complete",
            "dispose:first"
        ], calls);
    }

    [Fact]
    public async Task PreparedTransition_RollbackRestoresOutgoingAndDisposesCandidate()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.Todo, calls);
        var second = new TestWidgetContent("second", WidgetKind.Music, calls);
        var host = new WidgetShellContentHost(
            setContent: content => calls.Add($"set:{content.WidgetId}"),
            beginTransition: (outgoing, incoming) =>
                calls.Add($"begin:{outgoing.WidgetId}->{incoming.WidgetId}"),
            rollbackTransition: content => calls.Add($"rollback:{content.WidgetId}"));
        await host.SetContentAsync(first);
        calls.Clear();

        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);
        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);
        transition!.Rollback();

        Assert.Same(first, host.CurrentContent);
        Assert.Equal(
        [
            "initialize:second",
            "deactivate:first",
            "begin:first->second",
            "appearance:second",
            "deactivate:second",
            "rollback:first",
            "dispose:second"
        ], calls);
    }

    [Fact]
    public async Task CompletePresenterFailure_RemainsRollbackCapable()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.Todo, calls);
        var second = new TestWidgetContent("second", WidgetKind.Weather, calls);
        var host = new WidgetShellContentHost(
            setContent: content => calls.Add($"set:{content.WidgetId}"),
            beginTransition: (outgoing, incoming) =>
                calls.Add($"begin:{outgoing.WidgetId}->{incoming.WidgetId}"),
            completeTransition: () => throw new InvalidOperationException("presenter failed"),
            rollbackTransition: content => calls.Add($"rollback:{content.WidgetId}"));
        await host.SetContentAsync(first);
        calls.Clear();

        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);
        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);

        Assert.Throws<InvalidOperationException>(() => transition!.Complete());
        transition!.Rollback();

        Assert.Same(first, host.CurrentContent);
        Assert.Contains("rollback:first", calls);
        Assert.Contains("dispose:second", calls);
        Assert.DoesNotContain("dispose:first", calls);
    }

    [Fact]
    public async Task OutgoingDisposeFailure_DoesNotUndoCommittedIncoming()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent(
            "first",
            WidgetKind.Todo,
            calls,
            throwOnDispose: true);
        var second = new TestWidgetContent("second", WidgetKind.Weather, calls);
        var host = new WidgetShellContentHost(
            setContent: content => calls.Add($"set:{content.WidgetId}"),
            beginTransition: (outgoing, incoming) =>
                calls.Add($"begin:{outgoing.WidgetId}->{incoming.WidgetId}"),
            completeTransition: () => calls.Add("complete"),
            rollbackTransition: content => calls.Add($"rollback:{content.WidgetId}"));
        await host.SetContentAsync(first);
        calls.Clear();

        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);
        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);

        transition!.Complete();
        transition.Rollback();

        Assert.Same(second, host.CurrentContent);
        Assert.Contains("complete", calls);
        Assert.Contains("dispose:first", calls);
        Assert.DoesNotContain("rollback:first", calls);
        Assert.DoesNotContain("dispose:second", calls);
    }

    [Fact]
    public async Task CancelledInitialization_DisposesCandidateAfterInitializationSettles()
    {
        var calls = new List<string>();
        var initialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new DeferredWidgetContent(
            "candidate",
            calls,
            initialization.Task);
        var host = new WidgetShellContentHost(setContent: _ => { });
        using var cancellation = new CancellationTokenSource();

        Task<WidgetShellContentHost.WidgetShellPreparedContent?> preparing =
            host.PrepareContentAsync(content, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await preparing);
        Assert.Equal(0, content.DisposeCount);

        initialization.SetResult();
        await WaitUntilAsync(() => content.DisposeCount == 1);

        Assert.Equal(1, content.DisposeCount);
        Assert.Null(host.CurrentContent);
    }

    [Fact]
    public async Task DisposeDuringTransition_ReleasesIncomingAndOutgoingExactlyOnce()
    {
        var calls = new List<string>();
        var first = new DeferredWidgetContent(
            "first",
            calls,
            Task.CompletedTask);
        var second = new DeferredWidgetContent(
            "second",
            calls,
            Task.CompletedTask);
        var host = new WidgetShellContentHost(
            setContent: _ => { },
            beginTransition: (_, _) => { });
        await host.SetContentAsync(first);
        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);
        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);

        host.DisposeContent();
        host.DisposeContent();
        transition!.Rollback();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Null(host.CurrentContent);
    }

    [Fact]
    public async Task BeginTransition_ExposesIncomingBeforeOutgoingCanBeReleased()
    {
        var calls = new List<string>();
        var first = new TestWidgetContent("first", WidgetKind.File, calls);
        var second = new TestWidgetContent("second", WidgetKind.QuickCapture, calls);
        bool outgoingVisible = true;
        bool incomingVisible = false;
        var host = new WidgetShellContentHost(
            setContent: _ => incomingVisible = true,
            beginTransition: (_, _) => incomingVisible = true,
            completeTransition: () => outgoingVisible = false);
        await host.SetContentAsync(first);
        incomingVisible = false;
        using WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await host.PrepareContentAsync(second, CancellationToken.None);
        using WidgetShellContentHost.WidgetShellContentTransition? transition =
            host.CommitPreparedContent(prepared!);

        Assert.True(outgoingVisible || incomingVisible);
        Assert.True(outgoingVisible);
        Assert.True(incomingVisible);

        transition!.Complete();

        Assert.False(outgoingVisible);
        Assert.True(incomingVisible);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class TestWidgetContent : IWidgetContent, IDisposable
    {
        private readonly List<string> _calls;
        private readonly bool _throwOnDispose;

        public TestWidgetContent(
            string id,
            WidgetKind widgetKind,
            List<string> calls,
            bool throwOnDispose = false)
        {
            _calls = calls;
            _throwOnDispose = throwOnDispose;
            Config = new WidgetConfig
            {
                Id = id,
                Name = id,
                WidgetKind = widgetKind
            };
        }

        public WidgetConfig Config { get; }

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View => throw new NotSupportedException("Tests do not instantiate WinUI views.");

        public Task InitializeAsync()
        {
            _calls.Add($"initialize:{WidgetId}");
            return Task.CompletedTask;
        }

        public Task RefreshAsync()
        {
            _calls.Add($"refresh:{WidgetId}");
            return Task.CompletedTask;
        }

        public void ApplyAppearance()
        {
            _calls.Add($"appearance:{WidgetId}");
        }

        public void OnActivated()
        {
            _calls.Add($"activate:{WidgetId}");
        }

        public void OnDeactivated()
        {
            _calls.Add($"deactivate:{WidgetId}");
        }

        public void Dispose()
        {
            _calls.Add($"dispose:{WidgetId}");
            if (_throwOnDispose)
            {
                throw new InvalidOperationException(
                    $"dispose failed for {WidgetId}");
            }
        }
    }

    private sealed class LifecycleWidgetContent(
        string id,
        List<string> calls) : IWidgetContent
    {
        public WidgetConfig Config { get; } = new()
        {
            Id = id,
            Name = id,
            WidgetKind = WidgetKind.Weather
        };

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View =>
            throw new NotSupportedException("Tests do not instantiate WinUI views.");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task RefreshAsync() => Task.CompletedTask;

        public void ApplyAppearance()
        {
        }

        public void OnActivated()
        {
        }

        public void OnDeactivated()
        {
        }

        public void OnWindowVisibilityChanged(bool visible)
        {
            calls.Add($"visible:{id}:{visible.ToString().ToLowerInvariant()}");
        }

        public void OnWindowRevealCompleted()
        {
            calls.Add($"reveal:{id}");
        }
    }

    private sealed class DeferredWidgetContent(
        string id,
        List<string> calls,
        Task initialization) : IWidgetContent, IDisposable
    {
        public int DisposeCount { get; private set; }

        public WidgetConfig Config { get; } = new()
        {
            Id = id,
            Name = id,
            WidgetKind = WidgetKind.Todo
        };

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View =>
            throw new NotSupportedException(
                "Tests do not instantiate WinUI views.");

        public async Task InitializeAsync()
        {
            calls.Add($"initialize:{id}");
            await initialization;
        }

        public Task RefreshAsync() => Task.CompletedTask;

        public void ApplyAppearance()
        {
        }

        public void OnActivated()
        {
        }

        public void OnDeactivated()
        {
        }

        public void Dispose()
        {
            DisposeCount++;
            calls.Add($"dispose:{id}");
        }
    }

    private sealed class ReusableWidgetContent(
        string id,
        List<string> calls) :
        IWidgetContent,
        IWidgetGroupContentCacheable
    {
        public bool IsReadyForReuse => true;

        public WidgetConfig Config { get; } = new()
        {
            Id = id,
            Name = id,
            WidgetKind = WidgetKind.File
        };

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View =>
            throw new NotSupportedException(
                "Tests do not instantiate WinUI views.");

        public Task InitializeAsync()
        {
            calls.Add($"initialize:{id}");
            return Task.CompletedTask;
        }

        public Task RefreshAsync() => Task.CompletedTask;

        public void PrepareForReuse()
        {
            calls.Add($"prepare:{id}");
        }

        public void ApplyAppearance()
        {
            calls.Add($"appearance:{id}");
        }

        public void OnActivated()
        {
        }

        public void OnDeactivated()
        {
        }

        public void Dispose()
        {
            calls.Add($"dispose:{id}");
        }
    }
}
