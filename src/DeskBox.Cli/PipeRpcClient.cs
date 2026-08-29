using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskBox.Protocol;

namespace DeskBox.Cli;

/// <summary>Exit codes documented in the CLI help and the command API reference.</summary>
public enum CliExitCode
{
    Ok = 0,
    UnexpectedError = 1,
    UsageError = 2,
    AppNotRunning = 3,
    Timeout = 4,
    ServerRejected = 5,
}

/// <summary>Client-side failure categories mapped to <see cref="CliExitCode"/>.</summary>
public sealed class CliException : Exception
{
    public CliExitCode ExitCode { get; }

    public CliException(CliExitCode exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }
}

/// <summary>
/// JSON-RPC client for the DeskBox command API. Opens a short-lived framed
/// connection per request; retries briefly while the server swaps pipe
/// instances between clients.
/// </summary>
public sealed class PipeRpcClient
{
    private readonly string _pipeName;
    private readonly int _timeoutMilliseconds;
    private readonly TextWriter? _trace;

    public PipeRpcClient(string pipeName, int timeoutMilliseconds, TextWriter? trace = null)
    {
        _pipeName = pipeName;
        _timeoutMilliseconds = timeoutMilliseconds <= 0
            ? CommandApiProtocol.DefaultRequestTimeoutMilliseconds
            : timeoutMilliseconds;
        _trace = trace;
    }

    /// <summary>True when a server currently listens on the pipe.</summary>
    public static bool IsServerReachable(string pipeName)
    {
        try
        {
            using NamedPipeClientStream probe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            probe.Connect(500);
            return probe.IsConnected;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe exists but this user may not talk to it.
            return true;
        }
    }

    /// <summary>
    /// Sends one request and waits for the matching response. Throws
    /// <see cref="CliException"/> for transport failures; server-side command
    /// errors are returned as a populated <see cref="JsonRpcResponse.Error"/>.
    /// </summary>
    public async Task<JsonRpcResponse> SendAsync(
        string method,
        JsonElement arguments,
        string clientVersion,
        string? idempotencyKey = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMilliseconds);

        NamedPipeClientStream pipe;
        try
        {
            pipe = await ConnectWithRetryAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException(
                CliExitCode.Timeout,
                $"Could not connect to the DeskBox command API within {_timeoutMilliseconds} ms. The DeskBox app may be starting or busy; retry with --timeout.");
        }
        try
        {
            long id = Random.Shared.NextInt64(1, long.MaxValue);
            JsonRpcRequest request = new()
            {
                Id = JsonSerializer.SerializeToElement(id),
                Method = method,
                Params = new CommandRequest
                {
                    ProtocolVersion = CommandApiProtocol.ProtocolVersion,
                    ClientName = "deskbox-cli",
                    ClientVersion = clientVersion,
                    ClientProcessId = Environment.ProcessId,
                    IdempotencyKey = idempotencyKey,
                    DryRun = dryRun,
                    Arguments = arguments,
                },
            };

            byte[] requestPayload = Encoding.UTF8.GetBytes(CommandApiJson.SerializeRequest(request));
            await CommandFrame.WriteAsync(pipe, requestPayload, timeoutCts.Token).ConfigureAwait(false);

            byte[] responsePayload = await CommandFrame.ReadAsync(pipe, timeoutCts.Token).ConfigureAwait(false);
            JsonRpcResponse? response = CommandApiJson.DeserializeResponse(responsePayload);
            if (response is null)
            {
                throw new CliException(
                    CliExitCode.UnexpectedError,
                    "The server returned an empty or unparseable response frame.");
            }

            _trace?.WriteLine($"<- {CommandApiJson.SerializeResponse(response)}");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException(
                CliExitCode.Timeout,
                $"Request '{method}' timed out after {_timeoutMilliseconds} ms. The DeskBox app may be busy; retry with --timeout.");
        }
        catch (IOException ex)
        {
            throw new CliException(
                CliExitCode.AppNotRunning,
                $"The connection to the DeskBox command API was lost: {ex.Message}");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<NamedPipeClientStream> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            NamedPipeClientStream pipe = new(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1_500, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                lastError = ex;
                // The server serves one connection at a time and recreates
                // its listening instance between clients; a short retry
                // bridges that window when another client just connected.
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                lastError = ex;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new CliException(
            CliExitCode.AppNotRunning,
            $"Could not connect to the DeskBox command API pipe '{_pipeName}' after {maxAttempts} attempts. " +
            "Is DeskBox running with the command API enabled? " +
            $"Last error: {lastError?.Message}");
    }
}
