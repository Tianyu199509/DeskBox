# Widget Shell Content Host Checkpoint

Date: 2026-06-30

## Scope

This checkpoint adds a narrow lifecycle bridge between `WidgetShell` and
`IWidgetContent`.

## Completed

- Added `WidgetShellContentHost`.
- The host initializes content before assigning it to the shell.
- The host forwards refresh, appearance, activation, and deactivation callbacks.
- Replacing content deactivates the previous content.
- Added unit tests for lifecycle order and callback forwarding.

## Still Intentionally Closed

- The host is not connected to `WidgetManager` window creation.
- The host is not used by `WidgetWindow` or `QuickCaptureWidgetWindow` yet.
- Todo remains hidden from create entries.
- No file widget XAML was moved.
- No drag/drop, IME rename, tray/F7, z-order, animation, sorting, or installer logic changed.

## Validation

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

Result: `162/162` tests passed.

## Next Recommendation

Before enabling Todo, add a hidden generic window-host planning step or a
non-user-visible factory path that can select content by `WidgetKind` while still
keeping `WidgetRegistry.CanCreateWindow(WidgetKind.Todo)` set to `false`.
