# Toolkit UI Migration Plan

Last updated: 2026-07-01

This document is the working checklist for moving DeskBox UI toward a more polished WinUI / Fluent style.

The product rule is:

1. Use Windows Community Toolkit controls when they provide a better, standard Fluent control.
2. Use WinUI native controls when Toolkit does not add value.
3. Keep custom controls only for DeskBox-specific behavior or visuals that no standard control covers.

The goal is not to "use Toolkit everywhere" mechanically. The goal is to make the UI feel native, consistent, and maintainable while keeping desktop-widget behavior stable.

## Current Toolkit Status

Already installed:

- `CommunityToolkit.Mvvm`: view-model helpers.
- `CommunityToolkit.WinUI.Controls.ColorPicker`: used for the settings accent color trial.

Installed indirectly by the ColorPicker package:

- `CommunityToolkit.WinUI.Animations`
- `CommunityToolkit.WinUI.Controls.Segmented`
- `CommunityToolkit.WinUI.Converters`

Verified candidate packages:

- `CommunityToolkit.WinUI.Controls.SettingsControls`
  - Provides `SettingsCard`.
  - Provides `SettingsExpander`.
  - This is the correct package for Windows App SDK / WinUI 3.
- `CommunityToolkit.WinUI.Controls.Segmented`
  - Candidate for tabs and filters.
- `CommunityToolkit.WinUI.Controls.RangeSelector`
  - Candidate only if a future setting truly needs a range.
- `CommunityToolkit.WinUI.Controls.RadialGauge`
  - Candidate only if the system monitor widget needs this visual.
- `CommunityToolkit.WinUI.Controls.TokenizingTextBox`
  - Candidate only for future tag editing/search.

Package policy:

- Add the smallest package that owns the control being used.
- Do not install a full bundle just to make discovery easier.
- After adding a package, build Debug and later verify installer size before release.

## Migration Rules

Before each migration:

- Create a local backup under `backups/`.
- Change one control family at a time.
- Keep behavior and persistence stable.
- Avoid broad XAML restyling in the same commit as a behavior fix.

After each migration:

- Build with:
  `dotnet build src\DeskBox\DeskBox.csproj -c Debug -p:UseAppHost=false -p:OutDir=D:\project\wingezi\artifacts\build-check-out\`
- Check light theme, dark theme, Chinese, English, narrow settings window, reset defaults, and existing saved settings.
- Launch and visually inspect. Settings rows especially need text wrapping and control alignment checks.

## Replacement Matrix

| Area | Current pattern | Preferred control | Package | Priority | Notes |
| --- | --- | --- | --- | --- | --- |
| Settings single rows | `Border + Grid + TextBlock + control` | `SettingsCard` | `CommunityToolkit.WinUI.Controls.SettingsControls` | High | Best next migration after ColorPicker. |
| Settings expandable groups | Native `Expander` + custom header + `Border` | `SettingsExpander` with `SettingsCard` items | `CommunityToolkit.WinUI.Controls.SettingsControls` | High | Replace one settings page first. |
| Accent color | Custom button + `ContentDialog` + WinUI `ColorPicker` | `ColorPickerButton` | `CommunityToolkit.WinUI.Controls.ColorPicker` | Done / trial | Keep preset swatches for quick choices. |
| App/page navigation | Native `NavigationView` | Keep WinUI native | none | Keep | Already the right base. Tune icons/spacing only. |
| Breadcrumb | Native `BreadcrumbBar` | Keep WinUI native | none | Keep | Matches Windows settings pattern. |
| Combo boxes | WinUI `ComboBox` | Keep WinUI native | none | Keep | Toolkit does not improve basic dropdowns enough. |
| Toggles | WinUI `ToggleSwitch` | Keep WinUI native | none | Keep | Current app-level style tuning is acceptable. |
| Sliders + number input | WinUI `Slider` + `NumberBox` | Keep WinUI native | none | Keep | Toolkit range controls only if selecting a range. |
| Quick filter tabs | Custom button group | Evaluate `Segmented` | already indirect / or explicit package | Medium | Use after SettingsCard proves stable. |
| Todo filter tabs | Custom button group | Evaluate `Segmented` | already indirect / or explicit package | Medium | Must preserve counts and selected state. |
| Color/tag filters | Custom chips/buttons | `Segmented` or custom chips | `Segmented` if it fits | Later | Tags may need token input later. |
| Menus | WinUI `MenuFlyout` | Keep WinUI native | none | Keep | Native menus are correct; keep font/style tuning. |
| Compact delete confirmations | Custom `MenuFlyout` pattern | Keep WinUI `MenuFlyout` | none | Keep | Better than large `ContentDialog` in small widgets. |
| Large destructive confirmations | `ContentDialog` | WinUI `ContentDialog` | none | Keep | Use only for app-wide destructive actions. |
| Text input | WinUI `TextBox` | Keep WinUI native | none | Keep | Improve styles, not control type. |
| Date selection | WinUI date picker / custom flyout | Prefer WinUI native picker | none | Review | Avoid oversized dialogs inside small widgets. |
| File grid | `GridView` + custom item visuals | Keep custom item template | none | Keep | Core DeskBox file behavior. |
| Widget shell | Custom `WidgetShell` | Keep custom | none | Keep | Owns chrome, drag, resize, overlay/hidden modes. |
| Widget animations | Custom `WidgetTrayAnimationController` | Keep custom | none | Keep | Desktop-window animation behavior is product-specific. |
| Music visualizer | Custom XAML/C# visuals | Keep custom | none | Keep | Toolkit does not cover this product visual. |
| System monitor gauges | Future custom cards | Evaluate `RadialGauge` | `RadialGauge` | Later | Only if it looks compact and premium. |

## Settings Page Plan

The settings window is the biggest visual win. Most hand-composed settings rows should become Toolkit settings controls.

### Target Structure

Use this pattern:

```xml
<toolkit:SettingsCard
    Header="..."
    Description="..."
    HeaderIcon="...">
    <!-- right-side control -->
</toolkit:SettingsCard>
```

For advanced groups:

```xml
<toolkit:SettingsExpander
    Header="..."
    Description="..."
    HeaderIcon="..."
    IsExpanded="False">
    <toolkit:SettingsCard Header="..." Description="...">
        <!-- control -->
    </toolkit:SettingsCard>
</toolkit:SettingsExpander>
```

Keep direct rows for high-frequency settings. Put secondary settings into `SettingsExpander`.

### Page Priority

1. `About`
   - Low risk.
   - Good place to validate `SettingsCard` spacing and icon alignment.
2. `Appearance`
   - Highest visual payoff.
   - Contains theme, accent, text size, hover buttons, widget title styles.
   - Keep common controls visible; advanced window visuals/animation in expanders.
3. `File Widget`
   - Many sliders and toggles.
   - Convert after Appearance so slider/card alignment is already known.
4. `Feature Widgets`
   - Dynamic list with reset buttons and nested settings links.
   - Convert after static pages because dynamic rows are more fragile.
5. `Quick Capture`, `Todo`, `Music` nested settings
   - Convert after main pages.
   - Use `SettingsExpander` for secondary behavior groups.
6. `Advanced`, `Diagnostics`, `Maintenance`
   - Convert last.
   - These can use expanders heavily because they are lower-frequency.

### Keep Visible Versus Expander

Appearance visible:

- App theme.
- Tray icon style.
- Use Windows accent color.
- Accent color.
- Text size.
- Show hover buttons.
- Display widget title style.
- Content widget title style.

Appearance expanders:

- Window appearance details: corner, default width/height, opacity.
- Animation details: effect, speed, direction, easing.

File widget visible:

- Icon size.
- Show image files as icons.
- Show file extensions.
- Hide shortcut extension when showing file extensions.

File widget expanders:

- Layout density.
- Horizontal spacing.
- Vertical spacing.
- File name width.
- Managed storage location/actions.
- Shortcut/advanced storage operations.

Feature widgets visible:

- Quick Capture.
- Todo.
- Music.
- Reset button per feature widget.
- Open settings per feature widget.

Feature widgets expanders:

- Coming soon widgets can be grouped below implemented widgets.
- Do not show "implemented" labels; show only "(coming soon)" in descriptions.

## Widget Content Plan

### Quick Capture

Current:

- Dedicated `QuickCaptureWidgetWindow`.
- Custom tabs, list rows, hover actions, edit panel.

Possible Toolkit:

- `Segmented` for top tabs if it looks better and remains fast.
- Keep list item template custom.
- Keep compact delete confirmation as `MenuFlyout`.
- Keep edit surface custom or WinUI native `TextBox`.

Do not:

- Replace the whole window before it is migrated to the shared content-window path.

### Todo

Current:

- `TodoWidgetContent` inside shared content widget window.
- Custom tab/filter row, color markers, edit panel, delete flyout.

Possible Toolkit:

- `Segmented` for filter tabs.
- WinUI native date picker/flyout for due date.
- Keep task rows custom because checkbox, marker, multi-line text, hover actions, drag sorting, and undo behavior are product-specific.

Do not:

- Use a generic list control if it weakens row hover/action polish.

### Music

Current:

- `MusicWidgetContent` with custom cover, controls, progress, rhythm visuals, backdrop.

Possible Toolkit:

- Almost none for core visuals.
- Keep WinUI native `Button`, `Slider`, `Image`, `Border`.

Do not:

- Replace music layout with Toolkit cards. It should feel like a compact media surface, not a settings page.

### File Widgets

Current:

- Dedicated `WidgetWindow`.
- `GridView` item templates with file/folder/shortcut/image behavior.

Possible Toolkit:

- No Toolkit replacement for the main grid.
- Keep WinUI `GridView`.
- Continue tuning item template, context menus, hover buttons, drag/drop.

Do not:

- Replace file grid with a generic Toolkit list/grid unless there is a concrete performance or accessibility gain.

## Dialog, Flyout, And Menu Policy

Use `MenuFlyout` for:

- Context menus.
- Small destructive confirmations inside widgets.
- "Delete this task?" style confirmations.
- Quick action menus where the anchor matters.

Use `ContentDialog` for:

- App-wide destructive actions.
- Reset all settings.
- Storage cleanup with important explanation.
- Errors that must block user flow.

Avoid `ContentDialog` inside small widgets when:

- It can be clipped by widget bounds.
- It feels visually too heavy.
- The action can be represented as a small anchored flyout.

Toolkit currently does not replace this policy. Native WinUI flyouts and dialogs are the right base.

## Input And Picker Policy

Use Toolkit:

- `ColorPickerButton` for color selection.
- Possibly `TokenizingTextBox` for future tags if it feels native and package cost is acceptable.

Use WinUI native:

- `TextBox`
- `NumberBox`
- `ComboBox`
- `ToggleSwitch`
- `Slider`
- `CalendarDatePicker` or `DatePicker`
- `MenuFlyout`
- `ContentDialog`
- `BreadcrumbBar`
- `NavigationView`

Use custom only when:

- The control is part of widget chrome.
- It must support desktop-window drag, resize, overlay, hidden mode, or special z-order behavior.
- It is a product-specific visual such as music rhythm bars.
- It is a dense file/task/note row with custom hover actions.

## Near-Term Sequence

### Step 1: Finish ColorPickerButton Trial

Status: active.

Done:

- Added `CommunityToolkit.WinUI.Controls.ColorPicker`.
- Replaced the accent color dialog entry with `ColorPickerButton`.
- Bound `SelectedColor` to `SettingsViewModel.SelectedAccentColor`.
- Removed the right-side hex display from the row.

Still inspect:

- Flyout width and color-spectrum aspect ratio.
- Whether the selected color changes too eagerly while dragging.
- Light/dark style.
- System accent mode disabled state.

### Step 2: Convert One Safe Settings Section

Recommended first section:

- `About`

Reason:

- Low behavior risk.
- Mostly buttons, links, text.
- Good place to validate `SettingsCard` visual style.

Package to add:

- `CommunityToolkit.WinUI.Controls.SettingsControls`

Validation:

- Page width.
- Chinese/English wrapping.
- Icon size.
- Card hover state.
- Buttons on right side.

### Step 3: Convert Appearance Page

Reason:

- Highest visual payoff.
- User sees this page often.

Approach:

- Convert visible rows to `SettingsCard`.
- Convert advanced groups to `SettingsExpander`.
- Keep current `ComboBox`, `ToggleSwitch`, `Slider`, `NumberBox`, `ColorPickerButton` as row content.
- Do not change setting values or reset defaults during this step.

### Step 4: Convert File Widget Page

Reason:

- Many repeated row patterns.
- Good follow-up once `SettingsCard` styles are settled.

Approach:

- Use `SettingsCard` for common file display toggles.
- Use `SettingsExpander` for layout sliders and managed-storage operations.

### Step 5: Evaluate Segmented Tabs

Targets:

- Quick Capture top tabs.
- Todo filter tabs.

Validation:

- Selected state is always single.
- Counts align visually.
- Switching remains fast.
- Hover/pressed states match theme.
- Chinese/English labels fit.

## Files Most Likely To Change

Settings migration:

- `src/DeskBox/Views/SettingsWindow.xaml`
- `src/DeskBox/Views/SettingsWindow.xaml.cs`
- `src/DeskBox/ViewModels/SettingsViewModel.cs`
- `src/DeskBox/Services/LocalizationService.cs`
- `src/DeskBox/DeskBox.csproj`

Widget tab migration:

- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml`
- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs`

Do not change unless the task specifically needs it:

- `src/DeskBox/Controls/WidgetShell.xaml`
- `src/DeskBox/Controls/WidgetShell.xaml.cs`
- `src/DeskBox/Services/WidgetManager.cs`
- `src/DeskBox/Services/WidgetTrayAnimationController.cs`
- `src/DeskBox/Views/WidgetWindow.xaml.cs` drag/drop paths

## Current Backups

Toolkit color picker trial backups:

- `backups/toolkit-colorpicker-trial-20260701-232650`
- `backups/toolkit-colorpicker-before-xaml-20260701-233125`
- `backups/toolkit-colorpicker-polish-20260701-234310`
