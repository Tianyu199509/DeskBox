# Content Widget Menu Checkpoint

Date: 2026-06-30

This checkpoint gives `ContentWidgetWindow` the minimum shell-owned menu
capabilities expected by every future non-file widget.

## What Changed

- The content widget title bar now opens the same menu on right click.
- The `...` menu now includes:
  - lock position
  - lock size
  - rename
  - delete widget
- Lock state is stored directly on `WidgetConfig` through `SettingsService`.
- Rename uses `WidgetManager.RenameWidgetAsync`, so validation and persistence stay
  centralized.
- The content widget title view model now raises `DisplayName` change
  notifications after rename.

## Why This Matters

Todo, Weather, Music, Tags, and System Monitor should not each implement their
own basic window menu. The shell window owns physical window behavior; content
widgets own only their body content.

## What Stayed Out Of Scope

- File-widget specific folder actions remain in `WidgetWindow`.
- Quick Capture still has its own specialized menu.
- Delete confirmation for content widgets is still minimal and can be improved
  later if needed.
- The menu-building code is not yet extracted into a shared service; this step
  keeps the change local to `ContentWidgetWindow`.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Expected test count at this checkpoint: `179/179`.

## Manual Test Checklist

- Create a Todo widget.
- Open the menu from the `...` button.
- Open the same menu from title-bar right click.
- Toggle lock position and verify dragging is blocked/unblocked.
- Toggle lock size and verify resizing is blocked/unblocked.
- Rename the Todo widget and confirm the title updates immediately.
- Restart DeskBox and confirm the renamed title and lock states persist.
- Delete the Todo widget and confirm it does not return after restart.
