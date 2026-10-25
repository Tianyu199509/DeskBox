namespace DeskBox.Services;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

/// <summary>
/// Client half of the settings-host IPC pipe (batch 3b). Runs in the
/// settings process: connects to the main process's pipe with retries,
/// correlates request/response envelopes by id, surfaces server-pushed
/// events, and faults pending requests when the pipe drops.
/// Every await is ConfigureAwait(false): the remote facades call back into
/// this class with synchronous GetResult() blocking on the UI thread, so a
/// continuation captured on the UI synchronization context would deadlock
/// there.
/// </summary>
public sealed class SettingsIpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();

    /// <summary>
    /// An in-flight request: the method rides along so a response is only
    /// matched when both id and method agree (batch R12 bleed-through gate).
    /// </summary>
    private sealed record PendingCall(string Method, TaskCompletionSource<SettingsIpcEnvelope> Completion);
    private long _nextRequestId;
    private volatile bool _disposed;
    // Serializes request writes: the appearance-preview forwarder fires from
    // a pool thread while UI-thread facades call synchronously and the
    // background Everything warmup primes — concurrent Writes on one
    // NamedPipeClientStream interleave envelopes (batch R7).
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private SettingsIpcClient(NamedPipeClientStream stream)
    {
        _stream = stream;
    }

    /// <summary>Raised on the reader thread for every server-pushed event.</summary>
    public event Action<string, string?>? EventReceived;

    /// <summary>Raised when the pipe drops (reader loop exited).</summary>
    public event Action? Disconnected;

    public static async Task<SettingsIpcClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        int attempts = 20)
    {
        for (int attempt = 1; ; attempt++)
        {
            var stream = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
                var client = new SettingsIpcClient(stream);
                client.StartReader();
                return client;
            }
            catch (TimeoutException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                if (attempt >= attempts)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Per-method request ceilings (batch R3): most commands are short, but
    /// downloads/uploads/exports/scans legitimately run for minutes — a flat
    /// 15s ceiling made the host report failure while the main process kept
    /// working. Methods not listed keep the default.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TimeSpan> MethodTimeouts =
        new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            [SettingsIpcMethods.UpdateDownload] = TimeSpan.FromMinutes(30),
            // Deep review R13-2 (D19): the main-side HTTP check itself runs
            // up to 100s — a 60s ceiling turned every slow-network check
            // into a timeout throw while the main process was still working.
            [SettingsIpcMethods.UpdateCheck] = TimeSpan.FromSeconds(110),
            [SettingsIpcMethods.BackupSettingsProbe] = TimeSpan.FromMinutes(2),
            [SettingsIpcMethods.BackupSettingsList] = TimeSpan.FromMinutes(2),
            [SettingsIpcMethods.CloudDownloadSnapshot] = TimeSpan.FromMinutes(30),
            [SettingsIpcMethods.CloudPrepareScopedRestore] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.BackupUploadNow] = TimeSpan.FromMinutes(30),
            [SettingsIpcMethods.BackupCreateSnapshotNow] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.BackupExport] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.AppExportDiagnostics] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.AppUploadDiagnostics] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.RestoreAttachmentHealth] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetOrgUndo] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetOrgAbandon] = TimeSpan.FromMinutes(10),
            // Batch R9: these run whole-tree copies, archive extraction, or
            // network deletes — a flat 15s made the host report failure
            // while the main process kept migrating/restoring (the migrate
            // dialog even lost its progress subscription mid-flight).
            [SettingsIpcMethods.RestorePrepare] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetMigratePreview] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetMigrateRoot] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetRestoreOrphans] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetMoveOrphanToDesktop] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetDeleteOrphan] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.CloudDeleteSnapshot] = TimeSpan.FromMinutes(2),
            [SettingsIpcMethods.DataQuickCaptureCacheCleanup] = TimeSpan.FromMinutes(2),
            // Deep review R13: long-task stragglers the R3/R9 sweeps missed,
            // graded by the same rule as their families. Inventory walks
            // every snapshot zip's manifest; orphan discovery recursively
            // counts directory trees; the quick-capture clears delete all
            // captured data (files included); external-state recovery runs
            // display + Everything connection probes.
            [SettingsIpcMethods.RestoreInventory] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.WidgetGetOrphans] = TimeSpan.FromMinutes(10),
            [SettingsIpcMethods.DataQuickCaptureClear] = TimeSpan.FromMinutes(2),
            [SettingsIpcMethods.DataQuickCaptureClearRecent] = TimeSpan.FromMinutes(2),
            [SettingsIpcMethods.AppForceExternalRecovery] = TimeSpan.FromSeconds(60),
        };

    private static TimeSpan TimeoutFor(string method) =>
        MethodTimeouts.TryGetValue(method, out TimeSpan timeout)
            ? timeout
            : TimeSpan.FromSeconds(15);

    private void StartReader()
    {
        _ = Task.Run(async () =>
        {
            var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    // Batch R12: same 32MB runaway guard as the server side.
                    if (line.Length > 32 * 1024 * 1024)
                    {
                        App.Log($"[SettingsIpc] Main process sent a {line.Length}-char envelope; treating the pipe as broken");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    SettingsIpcEnvelope? envelope;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<SettingsIpcEnvelope>(
                            line,
                            SettingsIpcJsonContext.Default.SettingsIpcEnvelope);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (envelope is null)
                    {
                        continue;
                    }

                    // Batch R12 (sequence audit, defect D): correlate by id
                    // AND method — a stale response bleeding in from another
                    // connection epoch (the server binds responses to their
                    // origin now, this is the second gate) must never
                    // mis-complete an unrelated in-flight request.
                    if (envelope.Type == SettingsIpcEnvelope.TypeResponse &&
                        _pending.TryGetValue(envelope.Id, out var completion) &&
                        string.Equals(completion.Method, envelope.Method, StringComparison.Ordinal))
                    {
                        _pending.Remove(envelope.Id, out _);
                        completion.Completion.TrySetResult(envelope);
                    }
                    else if (envelope.Type == SettingsIpcEnvelope.TypeEvent)
                    {
                        TryRaiseEvent(envelope.Method, envelope.Payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }

            FailPending("pipe disconnected");
            Disconnected?.Invoke();
        });
    }

    private void TryRaiseEvent(string method, string? payload)
    {
        try
        {
            EventReceived?.Invoke(method, payload);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsIpc] Event observer threw: {ex.Message}");
        }
    }

    /// <summary>Sends a request and awaits the correlated response.</summary>
    public async Task<(bool Ok, string? Payload, string? Error)> RequestAsync(
        string method,
        string? payload = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<SettingsIpcEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCall(method, completion);

        var envelope = new SettingsIpcEnvelope(
            id,
            SettingsIpcEnvelope.TypeRequest,
            method,
            Ok: true,
            payload,
            Error: null);
        string line = JsonSerializer.Serialize(
            envelope,
            SettingsIpcJsonContext.Default.SettingsIpcEnvelope);
        byte[] bytes = Encoding.UTF8.GetBytes(line + '\n');

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cts.Token);
        // A main process buried in restore/startup work must not hang the
        // host window forever: the remote facades call this synchronously on
        // the UI thread, so bounded waits turn into visible "unavailable"
        // states instead of a dead settings page. Long-running operations
        // get per-method ceilings (see MethodTimeouts).
        linked.CancelAfter(TimeoutFor(method));
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
                await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            SettingsIpcEnvelope response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return (response.Ok, response.Payload, response.Error);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void FailPending(string reason)
    {
        foreach ((_, PendingCall call) in _pending)
        {
            call.Completion.TrySetException(new IOException(reason));
        }

        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        FailPending("client disposed");
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        _cts.Dispose();
    }
}
