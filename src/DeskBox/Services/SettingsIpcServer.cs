namespace DeskBox.Services;

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;

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
    // Batch R11: see the dispatch loop and SendEventAsync.
    private readonly SemaphoreSlim _dispatchGate = new(16, 16);
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
    /// <param name="expectedClientPidProvider">
    /// Batch R12 security audit: the pipe grants its full method surface
    /// (shutdown, installer launch, snapshot reads) to whoever connects.
    /// The DACL stops other users/sandboxes, but any same-user process can
    /// still connect. When this provider returns a pid, a connecting client
    /// whose process id does not match is dropped before a single request
    /// is served; null disables the gate (loopback tests, dev probes).
    /// </param>
    public static SettingsIpcServer Start(
        string pipeName,
        Func<string, string?, CancellationToken, Task<(bool Ok, string? Payload, string? Error)>> dispatcher,
        Func<uint?>? expectedClientPidProvider = null)
    {
        var server = new SettingsIpcServer(pipeName, dispatcher)
        {
            _expectedClientPidProvider = expectedClientPidProvider,
        };
        server._acceptLoop = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    private Func<uint?>? _expectedClientPidProvider;

    /// <summary>
    /// Drops the connection when the client process is not the settings
    /// host this main process spawned (or the dying one still saying
    /// goodbye). See Start's doc for the threat model.
    /// </summary>
    private bool ClientIdentityMatches(NamedPipeServerStream stream)
    {
        if (_expectedClientPidProvider is not { } provider)
        {
            return true;
        }

        uint expected = provider() ?? 0;
        if (expected == 0)
        {
            // Self-review (batch R12): "no host known" is the boot window
            // before the first spawn — an open door, not a state to allow.
            // The coordinator sets the pid before the spawned host can
            // possibly connect, so rejecting here breaks nothing legitimate.
            App.Log("[SettingsIpc] Rejected pipe client: no settings host is registered");
            return false;
        }

        try
        {
            if (!Win32Helper.GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint clientPid))
            {
                App.Log("[SettingsIpc] Client identity probe failed; dropping connection");
                return false;
            }

            if (clientPid != expected)
            {
                App.Log($"[SettingsIpc] Rejected pipe client pid={clientPid} (expected {expected})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsIpc] Client identity check failed: {ex.Message}");
            return false;
        }
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

            // Identity gate BEFORE the stream becomes the event target
            // (re-review of R12): rejecting after the assignment left a
            // microsecond window where pushed events were written to the
            // rejected client's pipe.
            if (!ClientIdentityMatches(stream))
            {
                await stream.DisposeAsync();
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

            // Batch R12: guard against a runaway sender — a legit envelope
            // (full settings snapshot with layout histories) stays well
            // under 32MB; anything larger is a broken or hostile peer and
            // the connection is dropped rather than fed to the parser.
            if (line.Length > 32 * 1024 * 1024)
            {
                App.Log($"[SettingsIpc] Dropping connection: envelope line {line.Length} chars exceeds the 32MB guard");
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
                // Batch R11: bounded concurrent dispatch. Unbounded Task.Run
                // per request let a chatty host (preview spam + commits +
                // reads) spawn unbounded pool threads, each ending in a
                // locked write — under a wedged pipe that turned into pool
                // starvation. The cap keeps a burst from amplifying; long
                // ops (10min migrate) still run without blocking others.
                // Task.Run keeps the dispatch off the read loop (a sync
                // dispatcher prefix would otherwise head-of-line block
                // reads); the gate bounds how many run at once.
                _ = Task.Run(() => RunBoundedDispatchAsync(envelope, stream));
            }
            // Client→server events have no consumer yet; batch 3d routes them.
        }
    }

    private async Task RunBoundedDispatchAsync(SettingsIpcEnvelope envelope, NamedPipeServerStream origin)
    {
        await _dispatchGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
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

            // Batch R12 (sequence audit, defect D): a long command (10min
            // migrate) outliving its connection must not have its response
            // written to a NEWLY connected host — with id-only correlation
            // that could mis-complete an unrelated in-flight request and
            // flip state. Bind the answer to the requesting stream.
            await WriteAsync(new SettingsIpcEnvelope(
                envelope.Id,
                SettingsIpcEnvelope.TypeResponse,
                envelope.Method,
                ok,
                payload,
                error), origin);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    /// <summary>Pushes an event envelope to the connected host, if any.</summary>
    /// <remarks>
    /// Batch R11: the write is a synchronous stream.Write under the send
    /// lock — when the host stops draining its pipe (suspended or wedged)
    /// the OS buffer fills and the write blocks indefinitely. Pushers
    /// include main-process UI threads (topology, local-persist, tray,
    /// shutdown), so the write now runs on a single dedicated writer task
    /// fed by this queue: callers enqueue and return immediately, event
    /// order is preserved, and even a wedged pipe costs exactly one
    /// blocked writer thread instead of a pile-up.
    /// </remarks>
    public Task SendEventAsync(string method, string? payload)
    {
        // Batch R12: bounded with drop-oldest — a host that stops draining
        // (wedged or suspended) while the main process pushes at high
        // frequency (download progress) must not grow the queue unboundedly;
        // progress-class events tolerate loss, ordering among survivors is
        // preserved.
        _eventQueue.Enqueue(new SettingsIpcEnvelope(
            Interlocked.Decrement(ref _nextEventId),
            SettingsIpcEnvelope.TypeEvent,
            method,
            Ok: true,
            payload,
            Error: null));
        while (_eventQueue.Count > 4096 && _eventQueue.TryDequeue(out _))
        {
            _droppedEventCount++;
        }

        if (Interlocked.CompareExchange(ref _droppedEventLogGate, 0, 0) == 0)
        {
            Interlocked.Exchange(ref _droppedEventLogGate, 1);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Interlocked.Exchange(ref _droppedEventLogGate, 0);
                long dropped = Interlocked.Exchange(ref _droppedEventCount, 0);
                if (dropped > 0)
                {
                    App.Log($"[SettingsIpc] Dropped {dropped} backlog events (host not draining)");
                }
            });
        }

        _eventQueueSignal.Release();
        // Single-writer discipline (batch R11 self-review): two concurrent
        // first-pushes could both see a null writer and race two drain
        // loops onto the same queue — the write lock would still serialize
        // the bytes, but cross-writer interleaving could flip two events'
        // order. The guard keeps exactly one writer alive for FIFO.
        lock (_eventQueue)
        {
            _eventWriter ??= Task.Run(EventWriterLoopAsync);
        }

        return Task.CompletedTask;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<SettingsIpcEnvelope> _eventQueue = new();
    private readonly SemaphoreSlim _eventQueueSignal = new(0);
    private Task? _eventWriter;
    private long _droppedEventCount;
    private int _droppedEventLogGate;

    private async Task EventWriterLoopAsync()
    {
        while (true)
        {
            try
            {
                await _eventQueueSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (_eventQueue.TryDequeue(out SettingsIpcEnvelope? envelope))
            {
                await WriteAsync(envelope).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAsync(SettingsIpcEnvelope envelope, NamedPipeServerStream? origin = null)
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

        // Responses stay bound to their requesting connection (batch R12
        // bleed-through fix); event pushes carry no origin and go to
        // whichever host is connected now.
        if (origin is not null && !ReferenceEquals(stream, origin))
        {
            return;
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
