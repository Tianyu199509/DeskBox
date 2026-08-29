namespace DeskBox.Cli;

/// <summary>CLI usage text. Kept in one place and written for both humans and AI agents.</summary>
public static class HelpPrinter
{
    public static void Print()
    {
        Console.Out.WriteLine(
            """
            DeskBox CLI — control a running DeskBox app over the local command API.

            USAGE
              deskbox <command> [arguments] [--json] [--timeout <ms>] [--pipe <name>]

            COMMANDS
              ping                          Check that DeskBox is running and reachable.
              info                          Server version, uptime, capabilities, policy.
              schema                        Full machine-readable command schema (for AI self-discovery).
              widgets list                  List live widget windows (ids, titles, rectangles).
              settings get                  Allowlisted settings snapshot.
              quickcapture list [--limit N] List quick capture notes.
              quickcapture add <body> [--title T] [--pin] [--dry-run]
                                            Add a quick capture note.
              todo list --widget <id> [--limit N]
                                            List one todo widget's items.
              todo add --widget <id> <text> [--important] [--color <marker>] [--dry-run]
                                            Add a todo item to one todo widget.
              mcp                           Run a Model Context Protocol server on stdio
                                            (for Claude Desktop, Cursor, and other MCP hosts).

            GLOBAL FLAGS
              --json          Print the raw protocol response instead of formatted output.
              --timeout <ms>  Per-request timeout (default 10000).
              --pipe <name>   Override the pipe name (dev/test; default follows %LOCALAPPDATA%\DeskBox).

            EXIT CODES
              0 ok   1 unexpected error   2 usage error   3 DeskBox not running   4 timeout   5 server rejected

            SECURITY
              The command API only accepts connections from the current Windows user.
              Mutating commands can be disabled (read-only mode) and destructive
              commands are off by default in DeskBox settings. Every call is audited.
            """);
    }
}
