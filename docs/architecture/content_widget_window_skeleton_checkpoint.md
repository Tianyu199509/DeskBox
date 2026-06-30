# Content Widget Window Skeleton Checkpoint

Date: 2026-06-30

## Scope

This checkpoint adds the first lightweight `ContentWidgetWindow` skeleton for
future non-file feature widgets.

## Completed

- Added `ContentWidgetWindow.xaml`.
- Added `ContentWidgetWindow.xaml.cs`.
- The window hosts `WidgetShell`.
- The window uses `WidgetShellContentHost` for `IWidgetContent` lifecycle.
- The window implements `IDesktopWidgetWindow`.
- Basic bounds, show, hide, title dragging, resize borders, DWM corner, backdrop,
  and shell surface appearance are present.

## Still Intentionally Closed

- `ContentWidgetWindow` is not called by `WidgetManager`.
- Todo is still not creatable.
- Todo descriptor still remains `Placeholder` and `Planned`.
- No create menu, tray menu, settings page, or onboarding entry uses Todo.
- File widget and Quick Capture windows are untouched.
- No file drag/drop, OLE subclass, IME rename, sorting, installer, or settings
  behavior changed.
- Tray animation is intentionally minimal in this skeleton and should be refined
  only after the host is manually validated.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `170/170` tests passed.

## Next Recommendation

The next step should be a small factory around `ContentWidgetWindow`, still
without connecting it to `WidgetManager` restore/create flows. That factory can
validate the descriptor/content pairing and keep Todo hidden until a separate
enablement commit.
