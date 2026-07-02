# Widget UI Migration Plan

Last updated: 2026-07-02

This document is the long-running checklist for moving DeskBox widget UI toward a more native and maintainable Fluent experience.

The product rule is:

1. Prefer Windows Community Toolkit controls when they provide a better standard Fluent control.
2. Use WinUI native controls when Toolkit does not add meaningful value.
3. Keep custom drawing and custom templates only when DeskBox-specific desktop widget behavior requires them.

This plan is intentionally conservative. Widgets contain drag/drop, window dragging, resize handles, tray animation, inline rename, hover actions, compact confirmations, clipboard capture, media sessions, and file-system behavior. These areas should be migrated one control family at a time, with visual checks after every step.

## Current Package Baseline

Already available:

- `CommunityToolkit.Mvvm`
- `CommunityToolkit.WinUI.Controls.ColorPicker`
- `CommunityToolkit.WinUI.Controls.SettingsControls`
- Toolkit packages installed indirectly by ColorPicker, including `CommunityToolkit.WinUI.Controls.Segmented`

Relevant future candidates:

- `CommunityToolkit.WinUI.Controls.Segmented`
  - Candidate for Quick Capture tabs and Todo filters.
  - If the indirect package is not enough for XAML usage, add the explicit package reference.
- `CommunityToolkit.WinUI.Controls.TokenizingTextBox`
  - Candidate only for the future Tags widget.
- `CommunityToolkit.WinUI.Controls.RadialGauge`
  - Candidate only for a future System Monitor widget if it looks compact enough.

Package policy:

- Add the smallest package that owns the control being used.
- Do not add a Toolkit package only for discovery.
- After adding a package, build Debug and later check installer size before release.

## Current Widget Surfaces

| Surface | Main files | Role |
| --- | --- | --- |
| Shared widget shell | `src/DeskBox/Controls/WidgetShell.xaml`, `WidgetShell.xaml.cs` | Title/chrome modes, title actions, overlay name, overlay drag handle, content presenter. |
| File widget | `src/DeskBox/Views/WidgetWindow.xaml`, `WidgetWindow.xaml.cs` | File/folder storage, mapped folders, icon/list view, drag/drop, selection, context menus, rename, resize. |
| Quick Capture widget | `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml`, `QuickCaptureWidgetWindow.xaml.cs` | Clipboard records, pinned/recent tabs, search, edit overlay, delete confirmations, save to file widget. |
| Content widget host | `src/DeskBox/Views/ContentWidgetWindow.xaml`, `ContentWidgetWindow.xaml.cs` | Shared window host for Todo, Music, and future content widgets. |
| Todo content | `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`, `.xaml.cs` | Task input, filters, color markers, due dates, sorting, edit overlay, undo. |
| Music content | `src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml`, `.xaml.cs` | Windows media session display/control, artwork, progress, rhythm visuals, backdrop. |
| Placeholder content | `src/DeskBox/Controls/WidgetContents/PlaceholderWidgetContent.cs` | Future widget placeholder content. |
| Shared menus | `src/DeskBox/Services/WidgetChromeMenuBuilder.cs`, menu builders in widget windows | Title style menu, per-widget more menus, item context menus, confirmation flyouts. |
| Shared animation | `src/DeskBox/Services/WidgetTrayAnimationController.cs` | Tray show/hide animation for widget windows. |

## Audit Matrix

### Shared Widget Shell

Current controls:

- WinUI native: `UserControl`, `Grid`, `Border`, `ContentPresenter`, `StackPanel`, `FontIcon`, `TextBlock`, `Button`.
- Custom behavior: title/chrome modes, right action button fade/translate animation, overlay identity badge, overlay drag handle, manual pointer handling.
- Custom style: `WidgetTitleActionButtonStyle`, background plate, divider, overlay badge, overlay drag handle.

Toolkit candidates:

- None for the shell itself.
- Toolkit does not provide a desktop widget chrome/title shell.

Keep WinUI native:

- `Button`, `FontIcon`, `TextBlock`, `ContentPresenter`, `MenuFlyout` triggers.

Must remain custom:

- Window dragging zones.
- Overlay and hidden title modes.
- Resize/drag interaction coordination with real Win32 windows.
- Hover button visibility rules.

Recommended migration:

- Do not replace `WidgetShell`.
- Extract shared visual styles for title buttons, overlay handle, overlay badge, and title text.
- Keep all shell pointer/window lifecycle logic custom.

Risk:

- High if changed broadly. Shell mistakes affect every widget.

### File Widget

Current controls:

- WinUI native: `Window`, `GridView`, `ListView`, `ProgressRing`, `TextBox`, `Button`, `FontIcon`, `Image`, `ToolTip`, `Canvas`, `MenuFlyout`.
- Custom templates: `WidgetGridViewItemStyle`, `WidgetListViewItemStyle`, item `Border` surfaces, custom tooltip content, selection rectangle on `Canvas`.
- Custom behavior: external drag/drop, internal drag sorting, multi-select, box selection, shell context actions, inline item rename, mapped folder actions, delete confirmations, folder migration overlay.

Toolkit candidates:

- None for the main file grid.
- Toolkit controls should not replace `GridView`/`ListView` unless a specific control clearly improves file-grid behavior.

Keep WinUI native:

- `GridView` and `ListView` for item hosting.
- `MenuFlyout` and `ToggleMenuFlyoutItem` for context menus.
- `TextBox` for inline rename.
- `ProgressRing` for loading/migration state.
- `ToolTip` for paths.

Must remain custom:

- File item visual template.
- Drag/drop surface and item slot sizing.
- Selection rectangle.
- Inline rename positioning.
- File-system context menu decisions.
- Delete confirmation flyout content.

Recommended migration:

- Unify file item hover background with Quick Capture and Todo item hover style.
- Replace duplicate title action button definitions with the shared `WidgetShell` style only.
- Keep custom item templates, but move shared constants such as row padding, radius, icon action size, hover brush, and tooltip style into shared resources.
- Review whether the list view row can use a common `WidgetListRow` style without changing selection and drag behavior.

Risk:

- Very high for drag/drop, selection, and rename.
- Medium for visual-only style extraction.

### Quick Capture Widget

Current controls:

- WinUI native: `Window`, `ListView`, `TextBox`, `Button`, `FontIcon`, `Image`, `Border`, `MenuFlyout`, `ContentDialog`.
- Custom templates: tab buttons, item row, image preview card, hover action host, edit overlay, status toast.
- Custom behavior: clipboard record state, recent/pinned filtering, search mode, image loading, delete confirmation flyout, save to file widget, title menu, tray animation.

Toolkit candidates:

- `Segmented` for top tabs: Records / Pinned / Recent.
- Possibly Toolkit animations later, only if they simplify list transition without hurting performance.

Keep WinUI native:

- `ListView` for record list.
- `TextBox` for input/search/edit.
- `MenuFlyout` for item and title menus.
- `Flyout` or `MenuFlyout` for compact confirmations.

Must remain custom:

- Clipboard item row layout.
- Image preview and text preview row.
- Save/pin/delete hover action host.
- Edit overlay, unless a future shared editor component replaces it.
- Clipboard and file-widget integration.

Recommended migration:

- First remove duplicate tab button styling by creating a shared widget segmented/filter abstraction.
- Then evaluate Toolkit `Segmented` with Quick Capture after Todo proves the pattern.
- Keep list item templates custom but share hover/action-host styles with Todo.
- Avoid large `ContentDialog` inside the widget; use shared compact flyout confirmations.

Risk:

- Medium for tabs.
- High for list virtualization/performance if item template is changed too much.
- High for edit/search interactions because this widget already had tab-switch performance issues.

### Todo Widget

Current controls:

- WinUI native: `UserControl`, `TextBox`, `Button`, `ListView`, `MenuFlyout`, `MenuFlyoutSubItem`, `Flyout`, `CalendarDatePicker`.
- Custom templates: filter buttons, color filter dots, completion button, item row, hover action host, edit overlay, undo toast.
- Custom behavior: input, default filter, drag reorder, completed visibility, color markers, due presets, custom due date flyout, undo, clear completed, clear all.

Toolkit candidates:

- `Segmented` for top filters: All / Today / Important / Completed.
- Maybe `Segmented` or a custom chip group for color filters. Use only if single-select color filtering remains visually clear.

Keep WinUI native:

- `ListView` with `CanDragItems` and `CanReorderItems`.
- `CalendarDatePicker` for custom due date.
- `MenuFlyout` for item actions and compact confirmations.
- `TextBox` for add/edit.

Must remain custom:

- Task row layout because it combines completion state, marker color, multiline text, due text, hover actions, and drag reorder.
- Completion button visual if the default checkbox does not match DeskBox compact style.
- Undo toast and compact delete confirmations, unless extracted to shared widgets.

Recommended migration:

- Use Todo as the first `Segmented` trial because its filter state is simpler than Quick Capture.
- Keep row template custom.
- Extract row hover/action-host style into shared resources before touching Quick Capture.
- Keep due date picker native; only reduce flyout visual custom code if it can still avoid clipping in small widgets.

Risk:

- Low to medium for top filters.
- Medium for color filters.
- High for row rewrite because it may break drag reorder and text alignment.

### Music Widget

Current controls:

- WinUI native: `UserControl`, `Grid`, `Border`, `Image`, `ItemsControl`, `Button`, `TextBlock`, `PathIcon`, `FontIcon`, `Ellipse`, `Rectangle`.
- Custom visuals: artwork backdrop gradient, rhythm bars/dots/line spectrum, album art shadow/gloss, custom progress track/thumb, custom play/pause glyph.
- Custom behavior: Windows media session integration, album art extraction, progress seeking, cover hover motion, rhythm state transitions.

Toolkit candidates:

- None for core music UI.
- Do not make Music look like a `SettingsCard` surface.

Keep WinUI native:

- `Button` for previous/play/next.
- `Image` for artwork.
- `Slider` should be evaluated for progress seeking only if it can meet DeskBox visual needs.
- `ItemsControl` for lightweight visualizer bars.

Must remain custom:

- Media widget layout.
- Artwork surface.
- Visualizer styles.
- Artwork color backdrop.
- Cover hover motion.

Recommended migration:

- Evaluate replacing custom progress track with WinUI `Slider` in a small branch. Accept it only if:
  - thumb can be visually hidden until hover,
  - click-to-seek does not bounce back,
  - it does not look heavier than the current control,
  - pointer capture works reliably.
- Keep visualizer custom, but extract it as a named component/style so future variants are isolated.
- Keep play/pause custom glyph only if built-in icons are too hard or too thin.

Risk:

- Medium for progress control.
- Low for visual style extraction.
- High for media session behavior if progress and play/pause code changes together.

### Content Widget Host

Current controls:

- WinUI native: `Window`, `Grid`, `Border`.
- Custom behavior: wraps `WidgetShell`, creates content through providers, manages title rename, resize, backdrop, menus, feature-widget close behavior.

Toolkit candidates:

- None for the host window.

Keep WinUI native:

- `MenuFlyout`, `ToggleMenuFlyoutItem`, `ContentDialog` for title menus and rare large confirmations.

Must remain custom:

- Host lifecycle.
- Provider bridge.
- Feature-widget enable/disable close behavior.
- Window placement, resize, animation.

Recommended migration:

- Do not redesign the host.
- Continue moving all reusable chrome/menu behavior into services/builders.
- Make future widgets plug into this host unless they need a dedicated legacy window like Quick Capture currently does.

Risk:

- High if changed broadly.
- Low for menu builder extraction.

## Cross-Cutting Replacement Matrix

| Area | Current pattern | Preferred target | Toolkit? | Priority | Risk |
| --- | --- | --- | --- | --- | --- |
| Widget title/chrome shell | Custom `WidgetShell` | Keep custom shell with shared styles | No | Keep | High |
| Title action buttons | Repeated `Button + FontIcon` styles | Shared WinUI `Button` style | No | High | Low |
| Overlay drag handle/name | Custom `Border` visuals | Keep custom, centralize style | No | Medium | Medium |
| Widget tabs/filters | Custom `Button` groups | Toolkit `Segmented` trial | Yes | High | Medium |
| Color filter chips | Custom circular `Button + Ellipse` | Evaluate Toolkit `Segmented`, otherwise custom shared chip style | Maybe | Medium | Medium |
| File item grid/list | `GridView/ListView` custom templates | Keep WinUI native hosting and custom item templates | No | Keep | Very high |
| Quick Capture list | `ListView` custom rows | Keep WinUI native hosting and custom rows | No | Keep | High |
| Todo list | `ListView` custom rows | Keep WinUI native hosting and custom rows | No | Keep | High |
| Music progress | Custom `Border/Ellipse` progress | Evaluate WinUI `Slider`; fallback custom | No | Medium | Medium |
| Music visualizer | Custom `ItemsControl` visuals | Keep custom, isolate component | No | Low | Low |
| Toast/undo messages | Custom `Border` overlays | Shared custom widget toast component | No | High | Low |
| Compact delete confirmations | `MenuFlyout` pattern repeated | Shared WinUI `MenuFlyout` builder | No | High | Low |
| Large confirmations | `ContentDialog` | Keep WinUI native | No | Keep | Low |
| Date picking | `CalendarDatePicker` in `Flyout` | Keep WinUI native, reduce custom wrapper if possible | No | Medium | Medium |
| Menus | `MenuFlyout` and `ToggleMenuFlyoutItem` | Keep WinUI native, centralize styling/builders | No | High | Low |
| Empty states | Custom `StackPanel + FontIcon + TextBlock` | Shared empty-state component/style | No | Medium | Low |
| Inline rename | `TextBox` positioned manually | Keep WinUI `TextBox`, improve style/placement | No | Medium | Medium |
| Window animation | Custom `WidgetTrayAnimationController` | Keep custom | No | Keep | High |

## Prioritized Migration Phases

### Phase 0: Audit And Baseline

Goal:

- Keep this document accurate.
- Establish visual and behavioral baselines before changing controls.

Tasks:

- Capture screenshots of File, Quick Capture, Todo, Music in light/dark theme.
- Capture standard, compact, overlay, hidden title modes.
- Record current keyboard/pointer behaviors:
  - file drag/drop,
  - file internal drag,
  - Todo reorder,
  - Quick Capture tab switch/search/edit,
  - Music play/pause/progress seek,
  - title rename and context menus.

Acceptance:

- No code migration yet.
- Baseline screenshots and behavior notes exist.
- `dotnet build src\DeskBox\DeskBox.csproj -c Debug -p:UseAppHost=false -p:OutDir=D:\project\wingezi\artifacts\build-check-out\` passes.

### Phase 1: Shared Widget Visual Tokens

Goal:

- Reduce duplicated styling without changing control families.

Tasks:

- Create shared styles/resources for:
  - widget icon action buttons,
  - hover row background,
  - hover action host,
  - compact toast,
  - compact confirmation menu titles,
  - empty-state text/icon sizing,
  - widget tooltip content.
- Apply only to Todo first, then Quick Capture, then File widget.

Keep:

- Existing WinUI controls.
- Existing item templates and behavior.

Acceptance:

- Visual consistency improves.
- No behavior changes.
- Todo add/edit/delete/reorder still works.
- Quick Capture add/search/pin/recent/edit/delete still works.
- File drag/drop/rename/right-click still works.

Risk:

- Low if scoped to styles.

### Phase 2: Shared Confirmation And Toast Builders

Goal:

- Stop duplicating compact delete/clear confirmations and undo toast UI across widgets.

Tasks:

- Add a small helper for compact `MenuFlyout` confirmation.
- Add a small helper or control for widget toast/undo message.
- Migrate Todo first.
- Migrate Quick Capture second.
- Migrate File delete confirmations last.

Keep:

- WinUI `MenuFlyout`.
- WinUI `Flyout` where non-menu content is required.

Acceptance:

- Confirmations look consistent.
- No large dialogs appear inside small widgets for routine item deletion.
- Long text truncates correctly.
- Keyboard focus is not trapped.

Risk:

- Low to medium.

### Phase 3: Toolkit Segmented Trial On Todo

Goal:

- Validate Toolkit `Segmented` for widget filter tabs.

Tasks:

- Add explicit `CommunityToolkit.WinUI.Controls.Segmented` package only if needed.
- Replace Todo top filter `Button` group with Toolkit segmented control.
- Preserve counts in labels.
- Preserve exactly one selected filter.
- Keep color filters unchanged in this phase.

Acceptance:

- Default selected filter is correct.
- Single selection only.
- Mouse and keyboard interaction are correct.
- Counts update live.
- Text fits Chinese and English.
- Todo tab switching remains smooth.

Risk:

- Medium.

Rollback:

- Revert only Todo filter XAML/code-behind.
- Keep shared styles from earlier phases if stable.

### Phase 4: Toolkit Segmented Trial On Quick Capture

Goal:

- Apply the proven segmented pattern to Quick Capture.

Tasks:

- Replace Records/Pinned/Recent tab buttons.
- Preserve search mode.
- Preserve recent capture status.
- Verify tab switch performance.

Acceptance:

- Records/Pinned/Recent are single-select.
- Switching tabs is at least as smooth as before.
- Search open/close still works.
- Recent empty state and enable action still work.
- Pinned reorder controls still work.

Risk:

- Medium to high because Quick Capture has more states than Todo.

### Phase 5: File Widget Visual Cleanup

Goal:

- Improve file widget consistency while preserving file behavior.

Tasks:

- Apply shared hover/action styles.
- Normalize tooltip styling.
- Normalize empty state.
- Review title action buttons after `WidgetShell` style cleanup.
- Do not rewrite item drag/drop or selection.

Acceptance:

- External desktop drag/drop works.
- Internal drag and shortcut drag still work.
- Multi-select and box selection work.
- Rename works.
- Right-click menus work.
- Icon and list views both look consistent.

Risk:

- Medium if visual-only.
- Very high if item container templates or pointer handlers are rewritten.

### Phase 6: Music Progress And Visual Component Isolation

Goal:

- Make Music more native where possible, while keeping the premium custom media surface.

Tasks:

- Extract visualizer styles into a dedicated component or resource section.
- Evaluate WinUI `Slider` for progress.
- Keep custom progress if WinUI `Slider` cannot meet visual/interaction requirements.
- Keep artwork, backdrop, and visualizer custom.

Acceptance:

- Play/pause works reliably.
- Previous/next works.
- Progress click and drag do not bounce back.
- Thumb visibility follows hover rules.
- Artwork and backdrop render correctly.
- Playback-to-paused visualizer transition remains smooth.

Risk:

- Medium.

### Phase 7: Quick Capture Host Unification Review

Goal:

- Decide whether Quick Capture should remain a dedicated window or move into `ContentWidgetWindow`.

Tasks:

- Compare current Quick Capture dedicated lifecycle with Todo/Music content host lifecycle.
- Identify blockers:
  - clipboard service,
  - title menus,
  - save-to-file-widget actions,
  - tray animation,
  - input/search focus.

Acceptance:

- Decision document update only, unless blockers are small.
- No forced migration.

Risk:

- High if implemented too early.

## Per-Widget Acceptance Checklist

### File Widget

- Opens at saved position and size.
- Standard/compact/overlay/hidden title modes behave correctly.
- External drag from desktop into file widget works.
- Internal drag/reorder works, including shortcuts.
- Right-click item menu opens and actions work.
- Right-click blank area menu opens and actions work.
- Rename item and rename widget work.
- Icon view and list view both render correctly.
- Image-as-icon setting still works.
- Multi-select and delete confirmation work.

### Quick Capture

- Widget enable/disable follows settings.
- Records/Pinned/Recent tabs show correct counts and items.
- Search opens, filters, closes, and restores previous state.
- Add text works.
- Clipboard recent capture state works after restart.
- Image preview loads.
- Pin/unpin works.
- Delete confirmation uses compact UI.
- Edit overlay works.
- Save to file widget works.
- Title menu opens Quick Capture settings directly.

### Todo

- Widget enable/disable follows settings.
- Add task works.
- Filters are single-select.
- Counts update correctly.
- Color filters are selectable and clearable.
- Completion button aligns with single-line and multi-line text.
- Drag reorder works.
- Due presets and custom due date work.
- Delete and clear completed confirmations use compact UI.
- Undo works.
- Edit overlay works.
- Title menu opens Todo settings directly.

### Music

- Widget enable/disable follows settings.
- Displays current Windows media session.
- Play/pause/previous/next work.
- Progress updates.
- Progress click and drag work.
- No duplicate shell/window layer is created.
- Artwork displays or placeholder displays.
- Backdrop and visualizer follow settings.
- Paused state transitions smoothly.
- Resizing remains responsive.

## Development Rules

- Before each phase, create a local backup under `backups/`.
- Keep migrations small and reversible.
- Do not mix behavior fixes and visual control migration unless the behavior fix is required to make the migrated control work.
- Prefer one widget as a trial before applying a pattern to all widgets.
- Do not replace custom controls just for the sake of using Toolkit.
- Every replacement must improve at least one of:
  - native Fluent feel,
  - consistency,
  - accessibility,
  - maintainability,
  - performance.

## Validation Commands

Build without touching a running Debug exe:

```powershell
dotnet build src\DeskBox\DeskBox.csproj -c Debug -p:UseAppHost=false -p:OutDir=D:\project\wingezi\artifacts\build-check-out\
```

Run normal Debug build only when DeskBox is closed:

```powershell
dotnet build src\DeskBox\DeskBox.csproj -c Debug
```

Check Git cleanliness:

```powershell
git status -sb
```

Check whitespace before commit:

```powershell
git diff --check
```

## Initial Recommendation

Start with Phase 1, not Toolkit `Segmented`.

Reason:

- The most visible inconsistency across widgets is not only the tab control. It is the repeated custom row hover/action/toast/button styling.
- Phase 1 is low risk and will make later Toolkit trials easier.
- After shared visual tokens are stable, Todo can be the first Toolkit `Segmented` trial.

