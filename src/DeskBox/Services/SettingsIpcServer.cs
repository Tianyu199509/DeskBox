namespace DeskBox.Services;

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>
/// Server half of the settings-host IPC pipe (batch 3b). Runs in the main
/// process: accepts one settings-host connection at a time (re-accepting
/// after a host restart), dispatches request envelopes to the registered
/// handler on thread-pool threads, and pushes event envelopes to whatever
/// client is currently connected. The pipe is ACL'd to the current user.
/// </summary>
public sealed class SettingsIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, string?, CancellationToken, Task<(bool Ok, string? Payload, string? Error)>> _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private NamedPipeServerStream? _current;
    private readonly object _writeLock = new();
    private long _nextEventId = -1;
    private volatile bool _disposed;

    private SettingsIpcServer(
        string pipeName,
        Func<string, string?, CancellationToken, Task<(bool Ok, string? Payload, string? Error)>> dispatcher)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
    }

    /// <param name="dispatcher">
    /// Method dispatcher: returns (ok, payload, error) for a request. Runs on
    /// a pool thread — marshal to the UI thread where needed.
    /// </param>
    public static SettingsIpcServer Start(
        string pipeName,
        Func<string, string?, CancellationToken, Task<(bool Ok, string? Payload, string? Error)>> dispatcher)
    {
        var server = new SettingsIpcServer(pipeName, dispatcher);
        server._acceptLoop = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream stream;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                stream = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 64 * 1024,
                    outBufferSize: 64 * 1024,
                    security);
                await stream.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsIpc] Accept failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(300), _cts.Token).ContinueWith(_ => { });
                continue;
            }

            lock (_writeLock)
            {
                _current = stream;
            }

            try
            {
                await ServeConnectionAsync(stream);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsIpc] Connection ended: {ex.Message}");
            }
            finally
            {
                lock (_writeLock)
                {
                    if (ReferenceEquals(_current, stream))
                    {
                        _current = null;
                    }
                }
                await stream.DisposeAsync();
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream stream)
    {
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (!_cts.IsCancellationRequested && stream.IsConnected)
        {
            string? line = await reader.ReadLineAsync(_cts.Token);
            if (line is null)
            {
                return;
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
            catch (JsonException ex)
            {
                App.Log($"[SettingsIpc] Malformed envelope dropped: {ex.Message}");
                continue;
            }

            if (envelope is null)
            {
                continue;
            }

            if (envelope.Type == SettingsIpcEnvelope.TypeRequest)
            {
                _ = Task.Run(async () =>
                {
                    bool ok;
                    string? payload;
                    string? error;
                    try
                    {
                        (ok, payload, error) = await _dispatcher(envelope.Method, envelope.Payload, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        payload = null;
                        error = ex.Message;
                    }

                    await WriteAsync(new SettingsIpcEnvelope(
                        envelope.Id,
                        SettingsIpcEnvelope.TypeResponse,
                        envelope.Method,
                        ok,
                        payload,
                        error));
                });
            }
            // Client→server events have no consumer yet; batch 3d routes them.
        }
    }

    /// <summary>Pushes an event envelope to the connected host, if any.</summary>
    public async Task SendEventAsync(string method, string? payload)
    {
        await WriteAsync(new SettingsIpcEnvelope(
            Interlocked.Decrement(ref _nextEventId),
            SettingsIpcEnvelope.TypeEvent,
            method,
            Ok: true,
            payload,
            Error: null));
    }

    private async Task WriteAsync(SettingsIpcEnvelope envelope)
    {
        if (_disposed)
        {
            return;
        }

        NamedPipeServerStream? stream;
        lock (_writeLock)
        {
            stream = _current;
        }

        if (stream is null || !stream.IsConnected)
        {
            return;
        }

        string line = JsonSerializer.Serialize(
            envelope,
            SettingsIpcJsonContext.Default.SettingsIpcEnvelope);
        byte[] bytes = Encoding.UTF8.GetBytes(line + '\n');
        try
        {
            lock (_writeLock)
            {
                if (!ReferenceEquals(_current, stream) || !stream.IsConnected)
                {
                    return;
                }
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsIpc] Event write failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        NamedPipeServerStream? stream;
        lock (_writeLock)
        {
            stream = _current;
            _current = null;
        }
        if (stream is not null)
        {
            await stream.DisposeAsync();
        }
        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop;
            }
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
