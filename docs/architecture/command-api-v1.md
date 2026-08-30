# Command API v1 — Local CLI/MCP Control Contract

Status: active contract for DeskBox 1.4.5+.
Audience: CLI users, AI agent authors, and maintainers.

## 1. Purpose

The command API lets local clients (the DeskBox CLI, MCP hosts such as
Claude Desktop or Cursor, and any script an AI agent writes) inspect and
drive a **running** DeskBox app: read widget/todo/quick-capture state, add
notes and todos, and enumerate live widget windows.

Design principles, inherited from the native ABI contract
(`docs/architecture/shortcut-native-abi-v2.md`):

- **Versioned envelope** — every request carries `protocolVersion`; a
  mismatch is rejected with a stable error code, never guessed around.
- **Capability negotiation** — `server/info` returns the capability list;
  clients must not call a command whose capability is absent.
- **Stable errors with hints** — failures carry `code` / `phase` / `message`
  / `hint`; the hint is written for machine self-correction.
- **Registry is the single source of truth** — `deskbox schema` is served
  from the same `CommandRegistry` that dispatches, so it can never drift
  from what the server implements.

## 2. Transport

- **Named pipe**: `DeskBox_Api_Pipe_<InstanceScope>` where `<InstanceScope>`
  is the same scope used by the single-instance mutex
  (`DeskBoxDataPathService.InstanceScope`). Dev/preview data roots therefore
  never expose or reach the retail instance's API.
- **Framing**: 4-byte little-endian length prefix + UTF-8 JSON payload
  (`DeskBox.Protocol.CommandFrame`). Hard cap: 4 MiB per frame.
- **Idle timeout**: the server drops a connection after 30 s without a
  request; clients simply reconnect per request (CLI) or re-ping (MCP).
- **Security**:
  - pipe ACL grants FullControl to the current user and SYSTEM only —
    handles opened by any other account are rejected by the kernel;
  - every request is dispatched through `CommandDispatcher`, which enforces
    the read-only and destructive policies below;
  - every request appends one JSON line to `%LOCALAPPDATA%\DeskBox\CommandApi.audit.log`.

## 3. Envelope

Request (`params` is the typed `CommandRequest`):

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "todo/add",
  "params": {
    "protocolVersion": 1,
    "clientName": "deskbox-cli",
    "clientVersion": "1.4.5",
    "clientProcessId": 1234,
    "idempotencyKey": null,
    "dryRun": false,
    "arguments": { "widgetId": "3f2a", "text": "buy milk" }
  }
}
```

Success:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": 1,
    "serverVersion": "1.4.5",
    "capabilities": ["server.info", "todo.read", "todo.write"],
    "data": { "widgetId": "3f2a", "itemId": "abc", "itemCount": 3, "saved": true }
  }
}
```

Failure:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "Method 'todo/add' mutates state, but the command API is running in read-only mode.",
    "data": {
      "code": "read_only_mode",
      "phase": "auth",
      "message": "…",
      "hint": "Enable mutating commands in DeskBox settings (Command API section)."
    }
  }
}
```

Stable `error.data.code` values (`CommandApiProtocol.ErrorCodes`):
`parse_error`, `invalid_request`, `method_not_found`,
`protocol_version_mismatch`, `read_only_mode`, `destructive_disabled`,
`validation_failed`, `ui_busy`, `timeout`, `internal_error`.

## 4. Policy gates (in enforcement order)

1. protocol version → 2. route (unknown/empty method) → 3. read-only mode
   (mutating commands rejected) → 4. destructive gate → 5. handler
   validation → 6. execution (UI-thread marshaling with a 5 s busy timeout
   for `ui-thread` affinity commands).

Settings that control the gates (AppSettings):

| Setting | Default | Effect |
|---|---|---|
| `EnableCommandApi` | `true` | Pipe server listens at all. |
| `CommandApiReadOnly` | `false` | Rejects every `MutatesState` command. |
| `AllowDestructiveCommands` | `false` | Rejects commands flagged destructive. |

## 5. Commands (v1)

| Method | Capability | Mutates | Affinity | Purpose |
|---|---|---|---|---|
| `server/ping` | `server.info` | no | any | Liveness probe. |
| `server/info` | `server.info` | no | any | Version, uptime, capabilities, policy. |
| `server/schema` | `server.info` | no | any | Full machine-readable schema. |
| `settings/get` | `settings.read` | no | any | Allowlisted settings snapshot. |
| `settings/set` | `settings.write` | yes | ui-thread | Set an allowlisted setting: theme (System\|Light\|Dark) or language. |
| `quickcapture/list` | `quickcapture.read` | no | any | Items (args: `limit` 1-200). |
| `quickcapture/add` | `quickcapture.write` | yes | any | Add text note (args: `body`, `title?`, `pin?`). |
| `quickcapture/pin` | `quickcapture.write` | yes | any | Pin/unpin one item (args: `itemId`, `pinned`). |
| `quickcapture/update` | `quickcapture.write` | yes | any | Replace body text (args: `itemId`, `body`). |
| `quickcapture/delete` | `quickcapture.write` | yes | any | Permanently delete items (args: `itemIds[]`). |
| `todo/list` | `todo.read` | no | any | Items of one widget (args: `widgetId`, `limit?`). |
| `todo/add` | `todo.write` | yes | any | Add item (args: `widgetId`, `text`, `important?`, `colorMarker?`). |
| `todo/set-completed` | `todo.write` | yes | ui-thread | Complete/reopen one item (args: `widgetId`, `itemId`, `isCompleted`). |
| `todo/edit` | `todo.write` | yes | ui-thread | Replace item text (args: `widgetId`, `itemId`, `text`). |
| `todo/set-due` | `todo.write` | yes | ui-thread | Set/clear due date (args: `widgetId`, `itemId`, `dueDate?` ISO 8601). |
| `todo/delete` | `todo.write` | yes | ui-thread | Delete items (args: `widgetId`, `itemIds[]`). |
| `todo/clear-completed` | `todo.write` | yes | ui-thread | Delete all completed items (args: `widgetId`). |
| `widgets/list` | `layout.read` | no | ui-thread | Config snapshot: id, kind, name, rect, visibility, mapped path. |
| `widgets/create` | `widgets.write` | yes | ui-thread | Create widget (args: `kind`, `path?` for folder). |
| `widgets/remove` | `widgets.write` | yes | ui-thread | **Destructive.** Remove widget (args: `widgetId`); folder contents stay on disk. |
| `widgets/show` | `widgets.write` | yes | ui-thread | Show widget, enabling feature widgets and lazily creating windows. |
| `widgets/hide` | `widgets.write` | yes | ui-thread | Hide a loaded widget (args: `widgetId`). |
| `widgets/rename` | `widgets.write` | yes | ui-thread | Rename widget (args: `widgetId`, `name`). |
| `files/list` | `files.read` | no | ui-thread | Entries shown in one file widget (args: `widgetId`). |
| `files/add` | `files.write` | yes | ui-thread | Import files/folders (args: `widgetId`, `paths[]`, `move?`). |
| `search/query` | `search.read` | no | ui-thread | Search files (via Everything) and DeskBox content (args: `query`, `limit?`). |
| `groups/merge` | `widgets.write` | yes | any | Merge one widget into another, forming (or joining) a widget group. |
| `groups/dissolve` | `widgets.write` | yes | any | Dissolve a widget group; members become standalone. |
| `organize/plan` | `organize.write` | no | any | Scan the desktop and return a preview plan; nothing moves. |
| `organize/apply` | `organize.write` | yes | ui-thread | Execute a cached plan (files move into managed folders; undoable). |
| `organize/undo` | `organize.write` | yes | ui-thread | Undo one completed organization run by `historyId`. |
| `music/status` | `music.read` | no | ui-thread | Music widget's SMTC snapshot (title, artist, state, volume). |
| `music/toggle` | `music.write` | yes | ui-thread | Toggle play/pause on the current SMTC media session. |
| `music/next` | `music.write` | yes | ui-thread | Next track on the current SMTC media session. |
| `music/previous` | `music.write` | yes | ui-thread | Previous track on the current SMTC media session. |
| `music/volume` | `music.write` | yes | ui-thread | Set system master volume (args: `widgetId`, `volume` 0-100). |
| `weather/get` | `weather.read` | no | any | Current weather for the configured location (args: `forceRefresh?`). |
| `weather/set-city` | `weather.write` | yes | ui-thread | Geocode and persist a new weather location (args: `city`). |
| `glance/get` | `glance.read` | no | any | One glance widget's persisted settings (layout, transition, rotation). |
| `glance/next` | `glance.write` | yes | ui-thread | Advance the glance widget to its next image. |
| `glance/toggle-pause` | `glance.write` | yes | ui-thread | Toggle the glance widget's auto-rotation pause. |

Authoritative argument details live in `server/schema` output.

Mutating todo/file-widget commands run on the UI thread through the live
view models, so recurrence generation, undo history, and the open widget's
display stay consistent; they fail with `widget_not_loaded` (hint: call
`widgets/show` first) when the target widget's window is not loaded.
QuickCapture mutations run headless — the service serializes operations and
raises `Changed`, refreshing the open widget automatically.

## 6. CLI

```
deskbox ping | info | schema | settings get | settings set <key> <value>
deskbox widgets list | create <kind> [--path <folder>] | show <id> | hide <id> | rename <id> <name> | remove <id> --yes
deskbox quickcapture list [--limit N] | add <body> [--title T] [--pin] [--dry-run] | pin <itemId> | update <itemId> <body> | delete <itemId> [more...]
deskbox todo list --widget <id> | add --widget <id> <text> | done|reopen --widget <id> <itemId> | edit | set-due | delete | clear-completed
deskbox files list <id> | add --widget <id> <path> [more...] [--move|--copy]
deskbox search query <text> [--limit N]
deskbox groups merge <src> <target> | dissolve <id>
deskbox organize plan [--include-slow] | apply <planId> | undo <historyId>
deskbox music status <id> | toggle|next|previous <id> | volume <id> <0-100>
deskbox weather get [--force] | set-city <name>
deskbox glance get <id> | next <id> | toggle-pause <id>
deskbox mcp                       # MCP server on stdio
```

Run `deskbox --help` for the full, up-to-date surface; the CLI mirrors
`server/schema` and never drifts from it.

Global flags: `--json`, `--timeout <ms>`, `--pipe <name>`.
Exit codes: `0` ok, `1` unexpected, `2` usage, `3` app not running,
`4` timeout, `5` server rejected.

## 7. MCP integration

`deskbox mcp` speaks MCP `2024-11-05` over stdio and exposes 19
coarse-grained tools: `deskbox_status`, `list_widgets`, `list_quick_capture`,
`add_quick_capture`, `list_todos`, `add_todo`, `complete_todo`,
`delete_todos`, `list_widget_files`, `add_files_to_widget`, `create_widget`,
`search_desktop`, `organize_desktop`, `set_appearance`, `music_control`,
`get_weather`, `set_weather_city`, `glance_control`, `get_settings`. Register
it in an MCP host with:

```json
{
  "mcpServers": {
    "deskbox": { "command": "C:\\Path\\To\\DeskBox.Cli.exe", "args": ["mcp"] }
  }
}
```

## 8. Testing and evolution rules

- `CommandApiProtocolTests` pins the frame codec and camelCase envelope.
- `CommandDispatcherTests` pins every policy gate; new gates need a new test.
- `PipeRpcServerIntegrationTests` runs the real pipe server through the real
  CLI client.
- Adding a command: implement `ICommandHandler`, register it in
  `App.CommandApi.cs`, and add dispatcher tests for any new gate. The schema
  updates automatically — do not hand-edit schema output.
- Breaking envelope/semantic changes bump `CommandApiProtocol.ProtocolVersion`
  and require a note here plus golden-schema review.
