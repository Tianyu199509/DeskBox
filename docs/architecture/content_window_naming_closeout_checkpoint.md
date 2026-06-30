# Content Window Naming Closeout Checkpoint

Date: 2026-06-30

This checkpoint removes transitional naming and debug-only Todo creation paths
after Todo became a normal creatable content widget.

## What Changed

- `ContentWidgetWindowFactory` now uses production-oriented method names:
  - `CanCreateContentWindow`
  - `CreateContentWindow`
  - `CreateContentWindowPlan`
- `WidgetManager` now calls the production content-window factory names.
- The debug-only `DESKBOX_DEBUG_TODO_WIDGET` startup path was removed.
- The fixed `debug-todo-content-window` id and session-candidate exception were
  removed.

## Why This Matters

Todo is no longer hidden or debug-only. Keeping hidden/debug naming in the active
code path would make future Weather, Tags, Music, and System Monitor work harder
to reason about.

## What Stayed Out Of Scope

- Historical checkpoint documents still mention the old debug path because they
  record earlier migration steps.
- No behavior changed for File widgets, Quick Capture, drag/drop, rename IME,
  sorting, installer, or settings.
- No new feature widget was opened.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Expected test count at this checkpoint: `179/179`.
