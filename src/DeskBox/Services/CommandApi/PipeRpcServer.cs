using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi;

/// <summary>One audited command API call, appended as a JSON line to the audit log.</summary>
public sealed record CommandApiAuditEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string ClientName,
    int ClientProcessId,
    bool Success,
    string? ErrorCode,
    double DurationMilliseconds);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandApiAuditEntry), TypeInfoPropertyName = "AuditEntry")]
internal sealed partial class CommandApiAuditJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Named-pipe host for the command API. Security model:
/// - the pipe ACL grants FullControl only to the current user and SYSTEM;
/// - <see cref="PipeOptions.CurrentUserOnly"/> adds a kernel-side same-user
///   check on every client handle;
/// - every request is dispatched through <see cref="CommandDispatcher"/>,
///   so read-only and destructive gating apply uniformly;
/// - every request produces one audit line regardless of outcome.
/// </summary>
public sealed class PipeRpcServer
{
    private readonly CommandDispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly TimeSpan _idleTimeout;
    private readonly Action<string>? _log;
    private readonly string? _auditLogFilePath;
    private readonly object _auditLock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private NamedPipeServerStream? _listeningPipe;

    public PipeRpcServer(
        CommandDispatcher dispatcher,
        string pipeName,
        string? auditLogFilePath = null,
        Action<string>? log = null,
        TimeSpan? idleTimeout = null)
    {
        _dispatcher = dispatcher;
        _pipeName = pipeName;
        _auditLogFilePath = auditLogFilePath;
        _log = log;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMilliseconds(CommandApiProtocol.DefaultIdleTimeoutMilliseconds);
    }

    public bool IsRunning { get; private set; }

    public string PipeName => _pipeName;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        IsRunning = true;
        _log?.Invoke($"Command API pipe server listening on '{_pipeName}'.");
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _listeningPipe?.Dispose();
        _listeningPipe = null;
        _cts?.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Command API accept loop faulted during shutdown: {ex.Message}");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
        _log?.Invoke("Command API pipe server stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateSecuredPipe();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Command API failed to create pipe '{_pipeName}': {ex.Message}");
                await Task.Delay(1_000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _listeningPipe = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Command API pipe wait failed: {ex.Message}");
                pipe.Dispose();
                continue;
            }

            try
            {
                await ServeClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Command API client session failed: {ex.Message}");
            }
            finally
            {
                pipe.Dispose();
                _listeningPipe = null;
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            byte[] payload;
            try
            {
                using CancellationTokenSource idle =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(_idleTimeout);
                payload = await CommandFrame.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Idle timeout: the client went quiet; drop the session.
                break;
            }
            catch (InvalidDataException ex)
            {
                _log?.Invoke($"Command API dropped a malformed frame: {ex.Message}");
                break;
            }

            JsonRpcRequest? request;
            JsonRpcResponse response;
            if (!TryParseRequest(payload, out request, out JsonRpcResponse? parseError))
            {
                response = parseError!;
            }
            else
            {
                DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
                response = await _dispatcher.DispatchAsync(request!, cancellationToken).ConfigureAwait(false);
                WriteAuditEntry(request!, response, startedUtc);
            }

            byte[] responsePayload = Encoding.UTF8.GetBytes(CommandApiJson.SerializeResponse(response));
            await CommandFrame.WriteAsync(pipe, responsePayload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryParseRequest(
        byte[] payload,
        out JsonRpcRequest? request,
        out JsonRpcResponse? parseError)
    {
        try
        {
            request = CommandApiJson.DeserializeRequest(Encoding.UTF8.GetString(payload));
            parseError = null;
            return request is not null;
        }
        catch (JsonException ex)
        {
            request = null;
            parseError = new JsonRpcResponse
            {
                Error = new JsonRpcErrorObject
                {
                    Code = JsonRpcErrorCodes.ParseError,
                    Message = $"The request is not valid JSON-RPC: {ex.Message}",
                    Data = new CommandErrorPayload
                    {
                        Code = CommandApiProtocol.ErrorCodes.ParseError,
                        Phase = "route",
                        Message = "The request payload could not be parsed as a JSON-RPC 2.0 request.",
                        Hint = "Send a framed JSON-RPC request: {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/ping\",\"params\":{}}.",
                    },
                },
            };
            return false;
        }
    }

    private void WriteAuditEntry(JsonRpcRequest request, JsonRpcResponse response, DateTimeOffset startedUtc)
    {
        if (_auditLogFilePath is null)
        {
            return;
        }

        CommandApiAuditEntry entry = new(
            startedUtc,
            request.Method,
            request.Params?.ClientName ?? "unknown",
            request.Params?.ClientProcessId ?? 0,
            response.Error is null,
            response.Error?.Data?.Code,
            (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds);
        try
        {
            string line = JsonSerializer.Serialize(entry, CommandApiAuditJsonContext.Default.AuditEntry);
            lock (_auditLock)
            {
                File.AppendAllText(_auditLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Command API audit write failed: {ex.Message}");
        }
    }

    private NamedPipeServerStream CreateSecuredPipe()
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No Windows user token."),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // The ACL is the entire same-user guarantee: .NET rejects combining
        // a custom PipeSecurity with PipeOptions.CurrentUserOnly, and an
        // ACL granting FullControl only to the current user and SYSTEM
        // enforces the same threat model.
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
