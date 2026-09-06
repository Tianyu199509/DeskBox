using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class WidgetShellContentHostAppearanceTests
{
    [Fact]
    public async Task WarmMemberRoundTrips_ApplyUnchangedAppearanceOnlyOncePerContent()
    {
        var host = CreateHost();
        var first = new Content("first");
        var second = new Content("second");
        for (int cycle = 0; cycle < 150; cycle++)
        {
            await host.SetContentAsync(first);
            host.ApplyAppearance(invalidate: false);
            await host.SetContentAsync(second);
            host.ApplyAppearance(invalidate: false);
        }

        Assert.Equal(1, first.AppearanceCalls);
        Assert.Equal(1, second.AppearanceCalls);
        Assert.Equal(150, first.PrepareCalls);
        Assert.Equal(150, second.PrepareCalls);
        Assert.Equal(0, first.DisposeCalls);
        Assert.Equal(0, second.DisposeCalls);
    }

    [Fact]
    public async Task AppearanceChange_RefreshesCurrentAndCachedMembers()
    {
        var host = CreateHost();
        var first = new Content("first");
        var second = new Content("second");
        await host.SetContentAsync(first);
        await host.SetContentAsync(second);

        host.ApplyAppearance();
        await host.SetContentAsync(first);
        host.ApplyAppearance(invalidate: false);
        await host.SetContentAsync(second);

        Assert.Equal(2, first.AppearanceCalls);
        Assert.Equal(2, second.AppearanceCalls);
    }

    [Fact]
    public async Task ReplacementWithSameId_StillGetsItsFirstAppearance()
    {
        var host = CreateHost();
        var first = new Content("same-id");
        var replacement = new Content("same-id");
        await host.SetContentAsync(first);
        await host.SetContentAsync(replacement);

        Assert.Equal(1, first.AppearanceCalls);
        Assert.Equal(1, replacement.AppearanceCalls);
    }

    [Fact]
    public async Task RollbackAfterAppearanceChange_RefreshesRestoredMember()
    {
        var host = CreateHost();
        var first = new Content("first");
        var second = new Content("second");
        await host.SetContentAsync(first);
        using var prepared = await host.PrepareContentAsync(second, CancellationToken.None);
        using var transition = host.CommitPreparedContent(prepared!);

        host.ApplyAppearance();
        transition!.Rollback();
        host.ApplyAppearance(invalidate: false);

        Assert.Same(first, host.CurrentContent);
        Assert.Equal(2, first.AppearanceCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task FailedAppearance_CanRetryTheSameVersion()
    {
        var host = CreateHost();
        var content = new Content("first");
        await host.SetContentAsync(content);
        content.ThrowOnNextAppearance = true;

        Assert.Throws<InvalidOperationException>(() => host.ApplyAppearance());
        host.ApplyAppearance(invalidate: false);
        host.ApplyAppearance(invalidate: false);

        Assert.Equal(3, content.AppearanceCalls);
    }

    private static WidgetShellContentHost CreateHost() => new(
        setContent: _ => { }, retainContent: _ => true);

    private sealed class Content(string id) : IWidgetContent, IWidgetGroupContentCacheable
    {
        public WidgetConfig Config { get; } = new() { Id = id, WidgetKind = WidgetKind.File };
        public string WidgetId => id;
        public WidgetKind WidgetKind => WidgetKind.File;
        public FrameworkElement View => throw new NotSupportedException();
        public bool IsReadyForReuse => true;
        public int AppearanceCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool ThrowOnNextAppearance { get; set; }
        public Task InitializeAsync() => throw new InvalidOperationException("Warm content must not reinitialize.");
        public Task RefreshAsync() => Task.CompletedTask;
        public void PrepareForReuse() => PrepareCalls++;
        public void ApplyAppearance()
        {
            AppearanceCalls++;
            if (ThrowOnNextAppearance)
            {
                ThrowOnNextAppearance = false;
                throw new InvalidOperationException("Simulated appearance failure.");
            }
        }
        public void OnActivated() { }
        public void OnDeactivated() { }
        public void Dispose() => DisposeCalls++;
    }
}
