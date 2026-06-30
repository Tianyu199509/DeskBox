# Widget Content Detached Factory Checkpoint

Date: 2026-06-30

## Scope

This checkpoint adds a hidden content-selection path for future widget kinds.
It is not a user-facing creation path.

## Completed

- Added `WidgetContentFactory.CreateDetachedContent(...)`.
- Added `WidgetContentFactory.CanCreateDetachedContent(...)`.
- Todo detached content returns `TodoWidgetContentAdapter`.
- Weather, Tags, Music, and SystemMonitor detached content return placeholder content.
- File and QuickCapture are rejected because their content is still owned by
  existing production windows.
- Added tests proving detached content does not change create-entry or window
  creation availability.

## Still Intentionally Closed

- `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` remains `false`.
- Todo descriptor remains `Placeholder` and `Planned`.
- Todo is not shown in create entries.
- Detached content is not connected to `WidgetManager`.
- No real window creation path uses detached content yet.
- No file widget XAML, drag/drop, IME rename, tray/F7, z-order, animation,
  sorting, settings, or installer behavior changed.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `170/170` tests passed.

## Next Recommendation

The next meaningful step is to decide whether to introduce a generic
`ContentWidgetWindow` host for non-file feature widgets, or to adapt the existing
`WidgetWindow` carefully. Given the file widget's drag/drop and IME risk, a small
new content-only host is likely safer than forcing Todo through `WidgetWindow`.
