# Todo Content Widget Enablement Checkpoint

Date: 2026-06-30

This checkpoint opens Todo as the first user-visible widget kind hosted by the
new content-widget path.

## What Changed

- `WidgetKind.Todo` is now registered as implemented and window-creatable.
- `WidgetContentFactory` now marks Todo content as implemented, available, and
  visible in create-entry descriptors.
- `WidgetManager.CreateTodoWidgetAsync` creates a Todo `WidgetConfig` and hosts
  it through `ContentWidgetWindow`.
- `WidgetManager.ShowWidgetAsync` now routes Todo widgets through
  `ContentWidgetWindow` instead of the file-widget window path.
- Tray and file-widget create menus now expose `Todo.NewWidget`.
- `ContentWidgetWindow` has a minimal more menu with a delete action, so Todo
  windows can be removed through the normal widget manager lifecycle.
- Todo list item templates use regular `Binding` instead of `x:Bind` to avoid
  WinUI XAML compiler fragility around `x:DataType`.

## What Stayed Out Of Scope

- No full-screen desktop layer.
- No Todo-specific window class.
- No changes to file-widget drag/drop, sorting, rename IME, or mapped-shortcut
  behavior.
- No Todo onboarding entry.
- No Todo settings page.
- No rename, lock, or advanced menu items for content widgets yet.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Expected test count at this checkpoint: `179/179`.

## Manual Test Checklist

- Create Todo from the tray menu.
- Create Todo from a file-widget title/content menu.
- Add, complete, delete, and clear completed Todo items.
- Hide/show widgets through tray/F7 while a Todo widget exists.
- Restart DeskBox and confirm visible Todo widgets restore.
- Delete a Todo widget from its `...` menu and confirm it does not return after
  restart.
- Recheck at least one existing file widget for external drag/drop and internal
  drag behavior.

## Next Safe Step

Keep the next step small: either add content-widget rename/lock menu parity, or
add a settings/create surface that uses `WidgetContentFactory.GetCreateEntryDescriptors`.
Avoid starting Weather, Tags, or widget merge until Todo has been manually tested
through normal user flows.
