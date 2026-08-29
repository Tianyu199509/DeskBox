using System.Text.Json;
using DeskBox.Cli;
using DeskBox.Protocol;

// ── Argument parsing ────────────────────────────────────────────────────────
// Hand-rolled to keep the CLI dependency-free: global flags may appear
// anywhere; the first two non-flag tokens select the verb.
string pipeNameOverride = string.Empty;
int timeoutMilliseconds = CommandApiProtocol.DefaultRequestTimeoutMilliseconds;
bool jsonOutput = false;
List<string> positional = [];

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            jsonOutput = true;
            break;
        case "--pipe" when i + 1 < args.Length:
            pipeNameOverride = args[++i];
            break;
        case "--timeout" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out timeoutMilliseconds) || timeoutMilliseconds <= 0)
            {
                return Fail(CliExitCode.UsageError, "--timeout requires a positive integer (milliseconds).");
            }

            break;
        case "--help" or "-h":
            HelpPrinter.Print();
            return (int)CliExitCode.Ok;
        default:
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count == 0)
{
    HelpPrinter.Print();
    return (int)CliExitCode.UsageError;
}

string clientVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
string pipeName = pipeNameOverride;
if (pipeName.Length == 0)
{
    // Default pipe: retail instance scope. The CLI avoids loading DeskBox
    // assemblies, so it resolves the scope the same way the app does for a
    // production root: %LOCALAPPDATA%\DeskBox.
    string productionRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox");
    pipeName = CommandApiProtocol.GetPipeName(
        DeskBoxInstanceScope.Resolve(productionRoot));
}

PipeRpcClient client = new(pipeName, timeoutMilliseconds, trace: null);

// MCP mode: serve a Model Context Protocol stdio server so MCP hosts
// (Claude Desktop, Cursor, ...) can drive DeskBox through native tools.
// It consumes stdin until the host closes the stream.
if (positional[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        await new McpServer(client, clientVersion).RunAsync(CancellationToken.None).ConfigureAwait(false);
        return (int)CliExitCode.Ok;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"mcp server error: {ex.Message}");
        return (int)CliExitCode.UnexpectedError;
    }
}

try
{
    return await CommandRouter.RunAsync(
        positional,
        client,
        clientVersion,
        jsonOutput,
        Console.Out,
        Console.Error).ConfigureAwait(false);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return (int)ex.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"unexpected error: {ex}");
    return (int)CliExitCode.UnexpectedError;
}

static int Fail(CliExitCode code, string message)
{
    Console.Error.WriteLine($"error: {message}");
    return (int)code;
}
