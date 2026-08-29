using System.Text.Json;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi;

/// <summary>
/// Validates and routes command API requests. All gatekeeping lives here so
/// handlers stay thin and the policy (protocol version, read-only mode,
/// destructive gating, UI-thread affinity, timeouts) is testable without a
/// real pipe or WinUI runtime.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly Func<bool> _isReadOnlyMode;
    private readonly Func<bool> _allowsDestructive;
    private readonly ICommandUiDispatcher _uiDispatcher;
    private readonly string _serverVersion;
    private readonly TimeSpan _uiDispatchTimeout;

    public CommandDispatcher(
        CommandRegistry registry,
        Func<bool> isReadOnlyMode,
        Func<bool> allowsDestructive,
        ICommandUiDispatcher uiDispatcher,
        string serverVersion,
        TimeSpan? uiDispatchTimeout = null)
    {
        _registry = registry;
        _isReadOnlyMode = isReadOnlyMode;
        _allowsDestructive = allowsDestructive;
        _uiDispatcher = uiDispatcher;
        _serverVersion = serverVersion;
        _uiDispatchTimeout = uiDispatchTimeout ?? TimeSpan.FromSeconds(5);
    }

    public CommandRegistry Registry => _registry;

    public async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        JsonElement? requestId = request.Id;
        CommandRequest? command = request.Params;

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return Error(requestId, JsonRpcErrorCodes.InvalidRequest, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InvalidRequest,
                Phase = "route",
                Message = "The JSON-RPC method name is empty.",
                Hint = "Send a request of the form {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/ping\",\"params\":{...}}.",
            });
        }

        if (command is null)
        {
            return Error(requestId, JsonRpcErrorCodes.InvalidRequest, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InvalidRequest,
                Phase = "route",
                Message = $"Method '{request.Method}' requires a params object.",
                Hint = "Provide params with a protocolVersion field matching the server protocol version.",
            });
        }

        if (command.ProtocolVersion != CommandApiProtocol.ProtocolVersion)
        {
            return Error(requestId, JsonRpcErrorCodes.InvalidRequest, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.ProtocolVersionMismatch,
                Phase = "route",
                Message = $"Client protocol version {command.ProtocolVersion} is not compatible with server version {CommandApiProtocol.ProtocolVersion}.",
                Hint = "Upgrade the DeskBox CLI, or call server/info to negotiate the supported protocol version.",
            });
        }

        ICommandHandler? handler = _registry.Resolve(request.Method);
        if (handler is null)
        {
            return Error(requestId, JsonRpcErrorCodes.MethodNotFound, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.MethodNotFound,
                Phase = "route",
                Message = $"Unknown command API method '{request.Method}'.",
                Hint = "Run 'deskbox schema' (or call server/info) to list the methods implemented by this server.",
            });
        }

        CommandRegistration registration = handler.Registration;
        if (registration.MutatesState && _isReadOnlyMode())
        {
            return Error(requestId, JsonRpcErrorCodes.ServerError, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.ReadOnlyMode,
                Phase = "auth",
                Message = $"Method '{request.Method}' mutates state, but the command API is running in read-only mode.",
                Hint = "Enable mutating commands in DeskBox settings (Command API section), or use read-only commands only.",
            });
        }

        if (registration.Destructive && !_allowsDestructive())
        {
            return Error(requestId, JsonRpcErrorCodes.ServerError, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.DestructiveDisabled,
                Phase = "auth",
                Message = $"Method '{request.Method}' is destructive and destructive commands are disabled.",
                Hint = "Enable destructive commands in DeskBox settings first; CLI callers must also pass --yes.",
            });
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The short UI-dispatch timeout only guards UI-thread marshaling: a
        // busy UI thread must yield a fast UiBusy error instead of holding
        // the pipe open. Headless commands rely on the client-side timeout
        // and the connection idle timeout instead, so slow disk I/O on a
        // legitimate write is not misreported as a busy UI. Handler
        // invocation stays inside the try so a synchronous throw (validation
        // failures throw before the first await) still maps to a structured
        // error instead of faulting the pipe session.
        try
        {
            Task<JsonElement> execution = registration.ThreadAffinity == CommandThreadAffinity.UiThread
                ? ExecuteOnUiThreadAsync(handler, command, cancellationToken)
                : handler.ExecuteAsync(
                    command.Arguments.GetValueOrDefault(),
                    CreateContext(command, cancellationToken),
                    cancellationToken);

            JsonElement data = registration.ThreadAffinity == CommandThreadAffinity.UiThread
                ? await execution.WaitAsync(_uiDispatchTimeout, cancellationToken).ConfigureAwait(false)
                : await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Success(requestId, data);
        }
        catch (TimeoutException)
        {
            return Error(requestId, JsonRpcErrorCodes.ServerError, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.UiBusy,
                Phase = "execute",
                Message = $"Method '{request.Method}' did not complete within {_uiDispatchTimeout.TotalSeconds:0.#}s.",
                Hint = "The UI thread is busy; retry after a short delay. Do not retry writes without an idempotencyKey.",
            });
        }
        catch (CommandValidationException ex)
        {
            return Error(requestId, JsonRpcErrorCodes.InvalidParams, ex.Payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(requestId, JsonRpcErrorCodes.InternalError, new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InternalError,
                Phase = "execute",
                Message = $"Method '{request.Method}' failed: {ex.Message}",
                Hint = "Inspect the DeskBox log for the matching [CommandApi] entry.",
            });
        }
    }

    private CommandExecutionContext CreateContext(CommandRequest command, CancellationToken cancellationToken)
        => new(
            // The in-app handlers close over their dependencies at
            // construction time; the service provider slot exists for future
            // handlers that resolve optional services per call.
            Services: new ServiceDescriptorServiceProvider(),
            DryRun: command.DryRun,
            IdempotencyKey: command.IdempotencyKey,
            CancellationToken: cancellationToken);

    private Task<JsonElement> ExecuteOnUiThreadAsync(
        ICommandHandler handler,
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonElement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandExecutionContext context = CreateContext(command, cancellationToken);

        bool posted = _uiDispatcher.TryPost(() => _ = RunOnUiThreadAsync(handler, command.Arguments.GetValueOrDefault(), context, completion));
        if (!posted)
        {
            return Task.FromException<JsonElement>(new CommandUiShutdownException());
        }

        return completion.Task;
    }

    private static async Task RunOnUiThreadAsync(
        ICommandHandler handler,
        JsonElement arguments,
        CommandExecutionContext context,
        TaskCompletionSource<JsonElement> completion)
    {
        try
        {
            JsonElement result = await handler.ExecuteAsync(arguments, context, context.CancellationToken).ConfigureAwait(true);
            completion.TrySetResult(result);
        }
        catch (CommandValidationException ex)
        {
            completion.TrySetException(ex);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private JsonRpcResponse Success(JsonElement? id, JsonElement data)
        => new()
        {
            Id = id,
            Result = new CommandResult
            {
                ProtocolVersion = CommandApiProtocol.ProtocolVersion,
                ServerVersion = _serverVersion,
                Capabilities = _registry.GetCapabilities(),
                Data = data,
            },
        };

    private static JsonRpcResponse Error(JsonElement? id, int jsonRpcCode, CommandErrorPayload payload)
        => new()
        {
            Id = id,
            Error = new JsonRpcErrorObject
            {
                Code = jsonRpcCode,
                Message = payload.Message,
                Data = payload,
            },
        };
}

/// <summary>Raised by handlers to return a structured validation failure.</summary>
public sealed class CommandValidationException : Exception
{
    public CommandErrorPayload Payload { get; }

    public CommandValidationException(CommandErrorPayload payload)
        : base(payload.Message)
    {
        Payload = payload;
    }

    public static CommandValidationException ValidationFailed(string message, string hint)
        => new(CommandArguments.ValidationFailed(message, hint));
}

/// <summary>Thrown when the UI dispatcher rejects work because the app is shutting down.</summary>
public sealed class CommandUiShutdownException : Exception
{
    public CommandUiShutdownException()
        : base("The application UI dispatcher is shutting down.")
    {
    }
}

/// <summary>
/// Placeholder service provider: handlers close over their dependencies at
/// registration time. Exists so the execution context contract is stable if
/// per-call service resolution is introduced later.
/// </summary>
internal sealed class ServiceDescriptorServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
