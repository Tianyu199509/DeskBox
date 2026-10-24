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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<SettingsIpcEnvelope>> _pending = new();
    private long _nextRequestId;
    private volatile bool _disposed;

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
            [SettingsIpcMethods.UpdateCheck] = TimeSpan.FromSeconds(60),
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

                    if (envelope.Type == SettingsIpcEnvelope.TypeResponse &&
                        _pending.TryRemove(envelope.Id, out var completion))
                    {
                        completion.TrySetResult(envelope);
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
        _pending[id] = completion;

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
            await _stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);

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
        foreach ((_, TaskCompletionSource<SettingsIpcEnvelope> completion) in _pending)
        {
            completion.TrySetException(new IOException(reason));
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
