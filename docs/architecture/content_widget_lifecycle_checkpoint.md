# Content Widget Lifecycle Checkpoint

Date: 2026-06-30

This checkpoint closes one lifecycle gap before any Todo widget entry point is
enabled.

## What Changed

- Added a unified internal enumeration path in `WidgetManager` for loaded desktop
  widget windows:
  - file widgets
  - quick capture widgets
  - hidden content widget windows
- Included hidden content widget windows in generic lifecycle operations:
  - hide all widgets
  - hide widget by id
  - remove widget by id
  - bring all visible widgets to front
  - restore raised widgets to the desktop layer
  - pointer hit testing during raised tray sessions
  - shutdown close-all cleanup
- Included content widget counts in tray lifecycle logs.

## What Did Not Change

- Todo is still not user-visible.
- `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` remains `false`.
- Todo still does not appear in create menus, settings, tray menus, context menus,
  onboarding, or restore flows.
- No file widget drag/drop, rename IME, sorting, installer, tray icon, or
  settings-page behavior was changed.

## Why This Matters

`ContentWidgetWindow` can now be safely exercised through hidden/internal paths
without becoming a lifecycle outlier. When Todo is later enabled, it will already
participate in the same tray/layer/hide/close bookkeeping as existing desktop
widgets.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Current test count: `179/179`.

## Recommended Next Step

Keep Todo hidden and add a narrow debug/internal validation path for creating a
single `ContentWidgetWindow` manually. Use it only to verify window appearance,
dragging, resizing, hide/close behavior, and tray/layer behavior before opening
any public create entry.
