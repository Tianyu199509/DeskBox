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
| `quickcapture/list` | `quickcapture.read` | no | any | Items (args: `limit` 1-200). |
| `quickcapture/add` | `quickcapture.write` | yes | any | Add text note (args: `body`, `title?`, `pin?`). |
| `todo/list` | `todo.read` | no | any | Items of one widget (args: `widgetId`, `limit?`). |
| `todo/add` | `todo.write` | yes | any | Add item (args: `widgetId`, `text`, `important?`, `colorMarker?`). |
| `widgets/list` | `layout.read` | no | ui-thread | Live widget windows with rects. |

Authoritative argument details live in `server/schema` output.

## 6. CLI

```
deskbox ping | info | schema | settings get | widgets list
deskbox quickcapture list [--limit N] | quickcapture add <body> [--title T] [--pin] [--dry-run]
deskbox todo list --widget <id> | todo add --widget <id> <text> [--important] [--color m] [--dry-run]
deskbox mcp                       # MCP server on stdio
```

Global flags: `--json`, `--timeout <ms>`, `--pipe <name>`.
Exit codes: `0` ok, `1` unexpected, `2` usage, `3` app not running,
`4` timeout, `5` server rejected.

## 7. MCP integration

`deskbox mcp` speaks MCP `2024-11-05` over stdio and exposes coarse-grained
tools: `deskbox_status`, `list_widgets`, `list_quick_capture`,
`add_quick_capture`, `list_todos`, `add_todo`, `get_settings`. Register it
in an MCP host with:

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
