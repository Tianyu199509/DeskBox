# Content Widget Window Factory Checkpoint

Date: 2026-06-30

## Scope

This checkpoint adds the hidden factory layer for lightweight content widget
windows.

## Completed

- Added `ContentWidgetWindowFactory`.
- Added `ContentWidgetWindowPlan`.
- The factory can prepare Todo content window plans using `TodoWidgetContentAdapter`.
- The factory can prepare placeholder plans for Weather, Tags, Music, and
  SystemMonitor.
- File, QuickCapture, and legacy Productivity are rejected by the hidden content
  window path.
- Added unit tests for plan selection and registry safety.

## Still Intentionally Closed

- The factory is not connected to `WidgetManager`.
- `ContentWidgetWindow` is still not used by production restore/create flows.
- Todo remains hidden from create entries.
- Todo registry state remains non-creatable.
- No file widget or quick-capture behavior changed.
- No drag/drop, IME rename, tray/F7 orchestration, sorting, settings, or
  installer behavior changed.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `178/178` tests passed.

## Next Recommendation

The next step can add a private `WidgetManager` method for content windows while
still keeping `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` as `false`.
Do not expose Todo to create menus until manual validation of `ContentWidgetWindow`
is complete.
