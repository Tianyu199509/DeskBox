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
              widgets list                  List widgets (ids, kinds, names, rectangles, mapped paths).
              widgets create <kind> [--path <folder>]
                                            Create a widget: file|folder|todo|glance|music|weather|search.
              widgets show <id> | hide <id> | rename <id> <name>
                                            Control one widget.
              widgets remove <id> --yes     Remove a widget (folder contents stay on disk).
              search query <text> [--limit N]
                                            Search files (Everything) and DeskBox content.
              groups merge <src> <target> | dissolve <id>
                                            Merge widgets into a group / dissolve it.
              organize plan [--include-slow] | apply <planId> | undo <historyId>
                                            Two-phase desktop organization (plan is preview-only).
              files list <id>               List entries shown in one file widget.
              files add --widget <id> <path> [more...] [--move|--copy]
                                            Import files/folders into a file widget's mapped folder.
              settings get                  Allowlisted settings snapshot.
              settings set <key> <value>    Set theme (System|Light|Dark) or language.
              music status <id>             Read SMTC snapshot (title, artist, state, volume).
              music toggle|next|previous <id>
                                            Toggle play/pause, previous, or next track.
              music volume <id> <0-100>     Set the system volume (0-100).
              weather get [--force]         Fetch weather for the configured location.
              weather set-city <name>       Geocode and switch the weather location.
              glance get <id>               Read a glance widget's settings.
              glance next <id> | toggle-pause <id>
                                            Advance the carousel or toggle auto-rotation pause.
              quickcapture list [--limit N] List quick capture notes.
              quickcapture add <body> [--title T] [--pin] [--dry-run]
                                            Add a quick capture note.
              quickcapture pin <itemId> [--unpin] | update <itemId> <body> | delete <itemId> [more...]
                                            Manage quick capture items (delete is permanent).
              todo list --widget <id> [--limit N]
                                            List one todo widget's items.
              todo add --widget <id> <text> [--important] [--color <marker>] [--dry-run]
                                            Add a todo item to one todo widget.
              todo done|reopen --widget <id> <itemId>
                                            Mark one item completed / not completed.
              todo edit --widget <id> <itemId> <text> | set-due --widget <id> <itemId> [--due <iso>]
                                            Edit text or set/clear a due date.
              todo delete --widget <id> <itemId> [more...] | clear-completed --widget <id>
                                            Delete items or clear completed ones.
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
