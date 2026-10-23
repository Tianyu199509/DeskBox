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
                await stream.ConnectAsync((int)timeout.TotalMilliseconds);
                var client = new SettingsIpcClient(stream);
                client.StartReader();
                return client;
            }
            catch (TimeoutException)
            {
                await stream.DisposeAsync();
                if (attempt >= attempts)
                {
                    throw;
                }
            }
        }
    }

    private void StartReader()
    {
        _ = Task.Run(async () =>
        {
            var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token);
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
        try
        {
            await _stream.WriteAsync(bytes, linked.Token);
            await _stream.FlushAsync(linked.Token);

            SettingsIpcEnvelope response = await completion.Task.WaitAsync(linked.Token);
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
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
        }
        _cts.Dispose();
    }
}
