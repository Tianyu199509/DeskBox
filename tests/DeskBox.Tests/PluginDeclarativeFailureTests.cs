using System.Net;
using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

public sealed partial class PluginDeclarativeExecutorTests
{
    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class WaitingStream : Stream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private static PluginDeclarativeExecutor WithHandler(HttpMessageHandler handler, TimeSpan? timeout = null,
        PluginCapabilityGate? gate = null) => new(gate ?? new PluginCapabilityGate(FullGrants, FullGrants),
            _ => new HttpClient(handler), timeout);

    [Fact]
    public async Task SourceDeadline_CoversResponseBodyAndPreservesFallback()
    {
        var stream = new WaitingStream();
        var executor = WithHandler(new CallbackHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })), TimeSpan.FromMilliseconds(250));
        var state = await executor.EvaluateAsync(CreateLivePackage(), FullGrants).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.Started.Task.IsCompletedSuccessfully);
        Assert.True(stream.WasDisposed);
        Assert.Contains("timed out", state.DataSourceErrors["github-repo"], StringComparison.Ordinal);
        Assert.Equal("…", state.Contributions["live-stars"].EffectiveFields["value"]);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndDisposesBody()
    {
        var stream = new WaitingStream();
        var executor = WithHandler(new CallbackHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancellation = new CancellationTokenSource();
        Task<PluginWidgetStateResult> run = executor.EvaluateAsync(CreateLivePackage(), FullGrants, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task HttpClientTimeout_IsADataFailure()
    {
        var executor = new PluginDeclarativeExecutor(new PluginCapabilityGate(FullGrants, FullGrants),
            _ => new HttpClient(new CallbackHandler(async (_, token) =>
            {
                await Task.Delay(5000, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })) { Timeout = TimeSpan.FromMilliseconds(100) });
        var state = await executor.EvaluateAsync(CreateLivePackage(), FullGrants);
        Assert.Contains("timed out", state.DataSourceErrors["github-repo"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsHaveUserAgent_AndHostPolicyIsEnforced()
    {
        bool reached = false;
        var handler = new CallbackHandler((request, _) =>
        {
            reached = true;
            Assert.Contains("DeskBox-Plugins/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"stargazers_count":42}""") });
        });
        var state = await WithHandler(handler).EvaluateAsync(CreateLivePackage(), FullGrants);
        Assert.True(reached);
        Assert.Equal("42", state.Contributions["live-stars"].EffectiveFields["value"]);
        Assert.Equal(42, state.Contributions["live-stars"].EffectivePayload["value"].GetInt32());

        reached = false;
        var denied = new PluginCapabilityGate(FullGrants, new Dictionary<string, IReadOnlyList<string>>());
        await WithHandler(new CallbackHandler((_, _) => { reached = true; throw new InvalidOperationException(); }),
            gate: denied).EvaluateAsync(CreateLivePackage(), FullGrants);
        Assert.False(reached);
    }

    [Theory]
    [InlineData("$.items[999999999999999999999999].name")]
    [InlineData("$.items[]")]
    [InlineData("$.items..name")]
    [InlineData("$.")]
    public void InvalidJsonPaths_ReturnMissingWithoutThrowing(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"items":[{"name":"one"}]}""");
        Assert.Null(PluginDeclarativeExecutor.TryEvaluateJsonPath(document.RootElement, path));
        Assert.False(PluginJsonPath.IsValid(path));
    }

    [Theory]
    [InlineData("ff02::1")]
    [InlineData("100.64.0.1")]
    [InlineData("::ffff:100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    public void NonPublicAddresses_AreDeniedByBothNetworkBoundaries(string address)
    {
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse(address)));
        string host = address.Contains(':') ? "[" + address + "]" : address;
        var scope = new Dictionary<string, IReadOnlyList<string>> { ["network.fetch"] = [host] };
        Assert.Throws<PluginCapabilityRefusedException>(() => new PluginCapabilityGate(scope, scope)
            .RequireAllowed(new Uri("https://" + host + "/"), "network.fetch"));
    }
}

