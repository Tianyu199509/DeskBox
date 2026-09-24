using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class QuickCaptureClipboardRuntimeTests
{
    [Fact]
    public async Task ReconcileOwnsOneListener_AndStopDrainsRetiredCapture()
    {
        bool enabled = false;
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<FakeSession>();
        var runtime = new QuickCaptureClipboardRuntime(
            () => enabled,
            () =>
            {
                var session = new FakeSession(created.Count == 0 ? pendingStop.Task : Task.CompletedTask);
                created.Add(session);
                return session;
            },
            exception => throw new InvalidOperationException("Unexpected retirement error", exception));

        runtime.Refresh();
        Assert.Empty(created);
        enabled = true;
        runtime.Refresh();
        runtime.Refresh();
        Assert.Single(created);
        Assert.Equal(2, created[0].RefreshCount);
        runtime.Refresh(captureCurrent: true);
        Assert.Equal(1, created[0].CaptureCount);

        enabled = false;
        runtime.Refresh();
        Assert.Null(runtime.Current);
        Assert.False(created[0].Listening);
        enabled = true;
        runtime.Refresh();
        Assert.Equal(2, created.Count);
        Assert.True(created[1].Listening);
        Task stopping = runtime.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.False(created[1].Listening);
        pendingStop.SetResult();
        await stopping;
        Assert.All(created, session => Assert.True(session.Disposed));
        runtime.Refresh();
        Assert.Equal(2, created.Count);
    }

    [Fact]
    public async Task FailedFactoryCanRetry_AndOnlyCurrentSessionReportsDiagnostics()
    {
        int creates = 0;
        var session = new FakeSession(Task.CompletedTask);
        var runtime = new QuickCaptureClipboardRuntime(
            () => true,
            () => ++creates == 1 ? throw new IOException("unavailable") : session,
            _ => { });
        Assert.Throws<IOException>(() => runtime.Refresh());
        Assert.Null(runtime.Current);
        int notifications = 0;
        runtime.DiagnosticsChanged += () => notifications++;
        runtime.Refresh();
        int before = notifications;
        session.RaiseDiagnostics();
        Assert.Equal(before + 1, notifications);
        await runtime.StopAsync();
        before = notifications;
        session.RaiseDiagnostics();
        Assert.Equal(before, notifications);
        Assert.Equal(2, creates);
    }

    private sealed class FakeSession(Task stop) : IQuickCaptureClipboardSession
    {
        public event Action? DiagnosticsChanged;
        public bool Listening { get; private set; }
        public bool Disposed { get; private set; }
        public int RefreshCount { get; private set; }
        public int CaptureCount { get; private set; }
        public void Refresh() { Listening = true; RefreshCount++; }
        public void CaptureCurrent() => CaptureCount++;
        public QuickCaptureClipboardDiagnostics GetDiagnostics() =>
            new(true, Listening, null, Listening ? "enabled" : "disabled", null);
        public Task StopAsync() { Listening = false; return stop; }
        public void Dispose() => Disposed = true;
        public void RaiseDiagnostics() => DiagnosticsChanged?.Invoke();
    }
}
