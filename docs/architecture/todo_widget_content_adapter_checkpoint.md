# Todo Widget Content Adapter Checkpoint

Date: 2026-06-30

## Scope

This checkpoint records the first Todo content adapter step after the Todo store,
view model, and UI control were added.

## Completed

- Added `TodoWidgetContentAdapter`.
- `TodoWidgetContentAdapter` implements `IWidgetContent`.
- The adapter owns a `TodoWidgetViewModel` and lazily creates `TodoWidgetContent`.
- `InitializeAsync` and `RefreshAsync` reload Todo data through the view model.
- Added an explicit `WidgetContentFactory.CreateTodoContent(...)` path.
- Added tests for adapter initialization, refresh, and factory creation.

## Still Intentionally Closed

- `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` remains `false`.
- `WidgetContentDescriptor` for Todo remains `Placeholder` and `Planned`.
- Todo is not shown in create entries.
- Todo is not connected to `WidgetManager` window creation.
- Todo is not connected to settings, tray menus, or widget context menus.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `159/159` tests passed.

## Next Recommendation

The next step should not open the Todo entry immediately. First add a generic,
hidden host path that can accept an `IWidgetContent` from `WidgetContentFactory`
without changing file widget drag/drop, IME rename, tray/F7, z-order, or installer
behavior.
