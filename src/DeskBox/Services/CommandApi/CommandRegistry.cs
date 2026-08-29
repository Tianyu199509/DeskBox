using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi;

/// <summary>
/// Registration table for command API methods. Mirrors the
/// <c>WidgetRegistry</c> pattern: handlers are registered explicitly at
/// composition time, unknown methods are rejected, and the registry is the
/// single source for both dispatch and the machine-readable schema exposed
/// to AI clients.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<CommandRegistration> _registrations;

    public CommandRegistry(IEnumerable<ICommandHandler> handlers)
    {
        List<CommandRegistration> registrations = [];
        foreach (ICommandHandler handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Registration.Method, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate command API method registration: '{handler.Registration.Method}'.");
            }

            registrations.Add(handler.Registration);
        }

        _registrations = registrations;
    }

    public bool IsKnown(string method) => _handlers.ContainsKey(method);

    public ICommandHandler? Resolve(string method) => _handlers.GetValueOrDefault(method);

    public int Count => _handlers.Count;

    public IReadOnlyList<CommandRegistration> Registrations => _registrations;

    /// <summary>
    /// Capability list derived from registrations. Ordering is stable so
    /// schema snapshots and golden-file contract tests stay deterministic.
    /// </summary>
    public IReadOnlyList<string> GetCapabilities()
        => _registrations
            .Select(registration => registration.Capability)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

    public CommandApiSchema BuildSchema(string serverVersion)
        => new(
            CommandApiProtocol.ProtocolVersion,
            serverVersion,
            GetCapabilities(),
            _registrations
                .Select(registration => registration.ToDescriptor())
                .OrderBy(descriptor => descriptor.Method, StringComparer.Ordinal)
                .ToList());
}
