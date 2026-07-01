# Widget Manager Content Window Private Path Checkpoint

Date: 2026-06-30

## Scope

This checkpoint adds a private manager-side holding path for future content
widget windows without opening any user-facing entry.

## Completed

- Added an internal `_contentWidgets` dictionary to `WidgetManager`.
- Added an internal read-only `ContentWidgets` view for tests and diagnostics.
- Added a private `CreateContentWidgetFromConfigAsync(...)` method.
- Content windows are included in generic visibility and appearance-preview
  bookkeeping if they are ever created internally.
- Added a restore test proving Todo configs are still skipped while the registry
  remains closed.

## Still Intentionally Closed

- `CreateContentWidgetFromConfigAsync(...)` is not called by restore, tray, menu,
  settings, or create flows.
- `CreateRegisteredWidgetFromConfigAsync(...)` still supports only File and
  QuickCapture.
- `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` remains `false`.
- Todo descriptor remains `Placeholder` and `Planned`.
- Todo is still hidden from create entries.
- Existing file widget and quick-capture window paths are not migrated.
- No drag/drop, IME rename, tray/F7 behavior, sorting, installer, or settings
  behavior changed.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `179/179` tests passed.

## Next Recommendation

Before enabling Todo, manually validate `ContentWidgetWindow` behind a temporary
debug-only path or add a narrowly scoped internal test hook. Keep the public
registry closed until that visual/manual check confirms title dragging, resizing,
F7/tray behavior, and shell appearance are acceptable.
