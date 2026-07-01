# Create Entry Descriptor Checkpoint

Date: 2026-06-30

This checkpoint moves user-facing widget creation menus away from hand-written
File/Todo menu branches and onto `WidgetContentFactory.GetCreateEntryDescriptors`.

## What Changed

- `WidgetContentDescriptor` now has an optional `CreateEntryTextKey`.
- File and Todo descriptors provide their create-entry localization keys:
  - File: `Common.NewWidget`
  - Todo: `Todo.NewWidget`
- Tray create entries are generated from `GetCreateEntryDescriptors`.
- File-widget create entries are generated from `GetCreateEntryDescriptors`.
- `WidgetManager.CreateWidgetOfKindAsync` is the single UI-facing creation
  dispatch method for descriptor-driven menus.

## Why This Matters

Future widget kinds should only need registry/content descriptor changes when
they become creatable. Menus should not need one-off branches for every new
widget type.

## What Stayed Out Of Scope

- Folder mapping remains a special action, not a descriptor-driven content
  widget entry.
- Quick Capture remains excluded from create entries because it is controlled by
  settings.
- Content widget rename/lock/menu parity is still a separate follow-up.
- No new Weather, Tags, Music, or System Monitor behavior was opened.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Expected test count at this checkpoint: `179/179`.

## Manual Test Checklist

- Tray menu still shows file widget, Todo widget, and folder mapping entries.
- File-widget title/content menu still shows file widget, Todo widget, and folder
  mapping entries.
- Creating File from each menu creates a managed file widget.
- Creating Todo from each menu creates a Todo content widget.
- Switching language updates tray create-entry text.
