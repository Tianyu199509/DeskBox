namespace DeskBox.Protocol;

/// <summary>
/// Version, naming, and limit constants for the DeskBox local command API.
/// The command API intentionally mirrors the native ABI contract style: an
/// explicit protocol version plus a capability set negotiated per connection.
/// </summary>
public static class CommandApiProtocol
{
    /// <summary>
    /// Bump for any breaking change to the envelope, error semantics, or
    /// command argument contract. Additive changes (new commands, new
    /// optional arguments, new capability strings) keep this value.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Pipe name prefix. The full name is this prefix plus the instance
    /// scope already used by <c>DeskBoxDataPathService</c> for the mutex and
    /// activation event, so development, preview, and retail data roots
    /// never expose or reach each other's command API.
    /// </summary>
    public const string PipeNamePrefix = "DeskBox_Api_Pipe_";

    /// <summary>Default per-request timeout applied by clients.</summary>
    public const int DefaultRequestTimeoutMilliseconds = 10_000;

    /// <summary>Idle timeout applied per connection by the pipe server.</summary>
    public const int DefaultIdleTimeoutMilliseconds = 30_000;

    /// <summary>Hard ceiling for one framed message (request or response).</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Stable, string-typed error codes surfaced to clients inside <c>error.data.code</c>.</summary>
    public static class ErrorCodes
    {
        public const string ParseError = "parse_error";
        public const string InvalidRequest = "invalid_request";
        public const string MethodNotFound = "method_not_found";
        public const string ProtocolVersionMismatch = "protocol_version_mismatch";
        public const string ReadOnlyMode = "read_only_mode";
        public const string DestructiveDisabled = "destructive_disabled";
        public const string ValidationFailed = "validation_failed";
        public const string UiBusy = "ui_busy";
        public const string WidgetNotLoaded = "widget_not_loaded";
        public const string Timeout = "timeout";
        public const string InternalError = "internal_error";
    }

    /// <summary>
    /// Capability identifiers reported by <c>server/info</c>. Presence means
    /// the command is implemented; absence means the client must not call it
    /// (mirrors the native ABI rule that an export existing does not imply
    /// the capability is implemented).
    /// </summary>
    public static class Capabilities
    {
        public const string ServerInfo = "server.info";
        public const string SettingsRead = "settings.read";
        public const string QuickCaptureRead = "quickcapture.read";
        public const string QuickCaptureWrite = "quickcapture.write";
        public const string TodoRead = "todo.read";
        public const string TodoWrite = "todo.write";
        public const string LayoutRead = "layout.read";
        public const string WidgetsWrite = "widgets.write";
        public const string FilesRead = "files.read";
        public const string FilesWrite = "files.write";
        public const string SearchRead = "search.read";
        public const string OrganizeWrite = "organize.write";
        public const string SettingsWrite = "settings.write";
    }

    /// <summary>Builds the full pipe name for the given instance scope.</summary>
    public static string GetPipeName(string instanceScope) => PipeNamePrefix + instanceScope;
}
