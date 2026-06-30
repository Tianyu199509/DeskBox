# Content Widget Window Plan

Date: 2026-06-30

## Decision

Use a new lightweight `ContentWidgetWindow` for future non-file feature widgets
such as Todo, Weather, Music, and SystemMonitor.

Do not force Todo through the existing `WidgetWindow`.

Reason:

- `WidgetWindow` owns high-risk file behavior: shell drag/drop, internal item
  drag, shortcut handling, rename IME, selection rectangle, mapped-folder logic,
  sorting, and file context menus.
- Todo does not need those file-specific behaviors.
- Reusing `WidgetWindow` would make every feature widget carry file-widget
  assumptions and would increase the chance of regressions in file drag/drop or
  IME rename.
- `QuickCaptureWidgetWindow` is closer to feature widgets, but it has its own
  clipboard, tab, edit overlay, and quick-capture-specific behavior.

The safer long-term shape is:

```text
ContentWidgetWindow
  WidgetShell
    IWidgetContent
```

`ContentWidgetWindow` should own only the generic physical window concerns.
Each feature should live in its own `IWidgetContent` implementation.

## First Version Scope

The first `ContentWidgetWindow` version should support only the minimum needed
to host Todo safely:

- AppWindow creation and saved bounds.
- `WidgetShell` title bar.
- `WidgetShellContentHost` content lifecycle.
- DWM corner, backdrop, theme, and background appearance.
- Dragging the title bar.
- Resizing through simple resize borders.
- Hide/show from tray/F7 using the same `IDesktopWidgetWindow` contract.
- Basic right-click title menu entry point, initially minimal.
- Close behavior that persists `WidgetConfig.IsVisible = false`.

It should not include:

- File drag/drop.
- OLE file drop subclass.
- File item rename TextBox.
- File item GridView/ListView selection logic.
- Mapping folders.
- Managed storage.
- File sorting.
- Quick Capture clipboard monitoring.
- Quick Capture tabs.
- Quick Capture edit overlay.
- Any custom fullscreen desktop layer.

## Reused Pieces

- `WidgetConfig`
- `WidgetKind`
- `IWidgetContent`
- `WidgetShell`
- `WidgetShellContentHost`
- `WidgetWindowDiagnostics`
- `WidgetContentFactory.CreateDetachedContent(...)`
- `IDesktopWidgetWindow`
- Existing `WidgetManager` tray/F7 orchestration

## New Pieces

Suggested files:

```text
src/DeskBox/Views/ContentWidgetWindow.xaml
src/DeskBox/Views/ContentWidgetWindow.xaml.cs
src/DeskBox/Services/ContentWidgetWindowFactory.cs
tests/DeskBox.Tests/ContentWidgetWindowFactoryTests.cs
```

Avoid introducing a `BaseWidgetWindow : Window` in this phase. The current
file and quick-capture windows have enough behavioral differences that a shared
base class would likely become a fragile inheritance trap.

## Manager Integration Sequence

Do not enable Todo in one large commit. Use this sequence:

1. Add `ContentWidgetWindow` skeleton behind no production call.
2. Add a small factory that can create `ContentWidgetWindow` for Todo when given
   an already-created `IWidgetContent`.
3. Add tests for factory gating and lifecycle where possible.
4. Add a private manager method that can create content windows, but keep
   `WidgetRegistry.CanCreateWindow(WidgetKind.Todo) = false`.
5. Manually instantiate Todo only through a temporary debug path if needed, not
   through user-facing creation.
6. After manual validation, enable Todo registry/descriptor and create entry in
   one explicit commit.

## Risk Guardrails

Every commit in this sequence must leave these unchanged:

- File widget drag/drop from Explorer.
- File widget internal drag.
- `.lnk` shortcut drag.
- File rename Chinese IME.
- ESC cancel rename behavior.
- Tray/F7 show/hide behavior for existing widgets.
- Desktop-layer restore behavior.
- Installer behavior.
- Settings page behavior unless explicitly scoped.

If any of those regress, revert the current content-window commit first instead
of patching across multiple architecture layers.

## Validation Matrix Before Enabling Todo

- Build passes.
- Test suite passes.
- Existing file widgets restore.
- Existing quick-capture widget restores.
- Tray/F7 still works.
- File widget drag/drop still works.
- File rename IME still works.
- Future Todo config in settings is skipped while registry remains closed.

## Validation Matrix After Enabling Todo

- Create Todo widget.
- Restart app and confirm Todo restores.
- Add, complete, delete, and clear completed tasks.
- Filter All / Active / Completed.
- Tray/F7 show/hide with Todo visible.
- Click outside restores desktop layer.
- Existing file widget drag/drop still works.
- Existing file rename IME still works.
- Existing quick-capture tabs still work.
