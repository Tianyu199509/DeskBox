# Debug Todo Content Window Checkpoint

Date: 2026-06-30

This checkpoint adds a manual validation path for `ContentWidgetWindow` without
opening any user-facing Todo entry.

## What Changed

- Added a Debug-only environment switch:
  - `DESKBOX_DEBUG_TODO_WIDGET=1`
- When the switch is enabled in a Debug build, app startup creates or shows one
  fixed Todo content window:
  - widget id: `debug-todo-content-window`
  - widget kind: `Todo`
- The debug Todo window uses the hidden `ContentWidgetWindow` path.
- The fixed debug Todo window is included in tray/F7 session preparation while
  the switch is enabled, so its show/hide and desktop-layer behavior can be
  manually validated.

## What Did Not Change

- Release builds do not compile this debug entry.
- `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` remains `false`.
- Todo is still absent from create menus, settings, tray menus, context menus,
  onboarding, and normal restore flows.
- File widgets and quick capture remain on their existing production paths.

## How To Validate Manually

Use a Debug build and set the environment variable before launching DeskBox:

```powershell
$env:DESKBOX_DEBUG_TODO_WIDGET='1'
.\src\DeskBox\bin\x64\Debug\net8.0-windows10.0.22621.0\DeskBox.exe
```

Suggested checks:

- Todo content window appears.
- Window uses the shared widget shell.
- Title drag works.
- Resize borders work.
- Close hides the window.
- Tray/F7 show and hide includes the debug Todo window.
- Clicking outside after a tray raise restores the desktop layer.
- Existing file widget drag/drop still works.
- Existing file rename IME still works.

To stop validating, launch without the environment variable. If the debug config
needs to be removed from local settings, delete the widget with id
`debug-todo-content-window` from the local settings file.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet build .\DeskBox.sln -c Release -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`
- Manual Debug launch with `DESKBOX_DEBUG_TODO_WIDGET=1` reached the content
  window path and logged `Content PushToBottom` / `Content HoldTemporaryTopMost`.
- `App.IsDeskBoxWindow(...)` now recognizes content widget windows, matching the
  file and quick-capture window identity checks.

Current test count: `179/179`.
