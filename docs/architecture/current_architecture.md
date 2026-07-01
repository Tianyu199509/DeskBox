# DeskBox Current Architecture

Last updated: 2026-06-30

This document describes the current architecture after the phase-1 widget foundation work. It is intended as the short, current-state handoff for future maintenance. Historical notes remain in the other architecture documents.

## Current Goal

DeskBox is moving toward a reusable widget foundation without forcing every existing widget into the same implementation immediately.

The current rule is:

- Reuse shared shell, content, animation, menu, settings, and lifecycle helpers where stable.
- Keep high-risk legacy behavior in place until it can be migrated in small steps.
- Do not add new large widget features until the shared foundation is stable enough to avoid copying logic.

## Widget Types

Current widget kinds are represented by `WidgetKind`.

Current production widget categories:

- `File`: file organizer / mapped folder widgets.
- `QuickCapture`: the note and clipboard widget, still using a dedicated window.
- `Todo`: first content-type feature widget using `ContentWidgetWindow`.

Planned placeholder kinds:

- `Weather`
- `Tags`
- `Music`
- `SystemMonitor`

Legacy value:

- `Productivity` is treated as legacy and should not be reintroduced into active creation paths.

## Main Architecture Flow

The intended content-widget path is:

```text
WidgetKind
-> WidgetRegistry
-> WidgetContentDescriptor
-> WidgetContentFactory / IWidgetContentProvider
-> IWidgetContent
-> ContentWidgetWindow
-> WidgetManager
```

Use this path for future content-type widgets whenever possible.

## Core Files

Core widget foundation:

- `src/DeskBox/Models/WidgetKind.cs`
- `src/DeskBox/Models/WidgetConfig.cs`
- `src/DeskBox/Services/WidgetRegistry.cs`
- `src/DeskBox/Services/WidgetContentDescriptor.cs`
- `src/DeskBox/Services/WidgetContentFactory.cs`
- `src/DeskBox/Services/IWidgetContentProvider.cs`
- `src/DeskBox/Services/ContentWidgetWindowFactory.cs`
- `src/DeskBox/Services/WidgetManager.cs`

Window creation routing:

- `WidgetWindowProvider` inside `WidgetManager`: maps a creatable `WidgetKind` to the correct host-window creation path.
- Current providers: File -> `WidgetWindow`, QuickCapture -> `QuickCaptureWidgetWindow`, Todo -> `ContentWidgetWindow`.
- The provider layer is intentionally thin. It centralizes dispatch but does not move host-specific implementation bodies yet.

Shared shell and window helpers:

- `src/DeskBox/Controls/WidgetShell.xaml`
- `src/DeskBox/Controls/WidgetShell.xaml.cs`
- `src/DeskBox/Controls/WidgetShellContentHost.cs`
- `src/DeskBox/Services/WidgetTrayAnimationController.cs`
- `src/DeskBox/Services/WidgetTitleBarMetrics.cs`
- `src/DeskBox/Services/WidgetSessionManager.cs`

Current windows:

- `src/DeskBox/Views/WidgetWindow.xaml.cs`: file widgets.
- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs`: QuickCapture / note widget.
- `src/DeskBox/Views/ContentWidgetWindow.xaml.cs`: Todo and future content widgets.

Current Todo implementation:

- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs`
- `src/DeskBox/ViewModels/TodoWidgetViewModel.cs`
- `src/DeskBox/Services/TodoWidgetStore.cs`
- `src/DeskBox/Services/TodoWidgetContentProvider.cs`

## WidgetRegistry

`WidgetRegistry` answers whether a kind is known, implemented, creatable, and available in the current session.

Current behavior:

- `File`, `QuickCapture`, and `Todo` are creatable/implemented.
- `Weather`, `Tags`, `Music`, and `SystemMonitor` are known but not user-creatable.
- Feature widget availability is checked through `FeatureWidgetSettings`.

Do not use ad hoc `if` checks in new UI entry points when `WidgetRegistry` can answer the question.

## Descriptors

`WidgetContentDescriptor` stores content metadata:

- default title
- glyph
- implementation stage
- availability
- create-entry visibility
- localization keys
- optional settings page metadata

`WidgetContentFactory.GetDescriptors()` and `GetCreateEntryDescriptors()` are the preferred sources for create-entry and future "more widgets" UI.

Current descriptor entry points:

- `GetDescriptors()`: all active non-legacy content descriptors.
- `GetCreateEntryDescriptors()`: user-facing create menu entries, currently only file widgets.
- `GetFeatureWidgetEntryDescriptors()`: Settings > Feature widgets / More widgets entries, including available and planned feature widgets.

## Content Providers

`WidgetContentFactory` now uses provider registration for detached content creation.

Current providers:

- `TodoWidgetContentProvider`: creates real Todo content.
- `PlaceholderWidgetContentProvider`: creates placeholder content for planned kinds.

Current contract:

- `IWidgetContentProvider` owns content creation for one kind.
- `WidgetContentProviderContext` passes shared services and factory helpers.
- `ContentWidgetWindowFactory` asks `WidgetContentFactory.CreateDetachedContent(...)` for content and does not special-case Todo.

Future content widgets should add a provider instead of expanding a switch in `WidgetContentFactory`.

## Windows

### File Widgets

File widgets still use `WidgetWindow`.

This is intentional. File widgets contain many high-risk behaviors:

- external drag/drop
- internal drag sorting
- shortcut handling
- rename and IME handling
- file operations
- mapped-folder behavior
- right-click menus

Do not move this whole window into `WidgetShell` in one pass.

Current shared pieces used by file widgets:

- `WidgetShell` hosts the outer shell.
- `WidgetTitleBarMetrics` controls title/button visual sizing.
- `WidgetTrayAnimationController` controls tray/F7 animation execution.
- shared menu font and spacing resources from `App.xaml`.

### QuickCapture

QuickCapture still uses `QuickCaptureWidgetWindow`.

This is intentional because it has special behavior:

- clipboard monitoring
- note input
- tabs for records/pinned/recent
- image/text/file capture
- QuickCapture-specific menus and editing behavior

Current shared pieces used by QuickCapture:

- `WidgetShell`
- `WidgetTitleBarMetrics`
- `WidgetTrayAnimationController`
- shared menu resources

### ContentWidgetWindow

`ContentWidgetWindow` is the host for content-type feature widgets.

Current production user:

- Todo

Future likely users:

- Weather
- SystemMonitor
- Music
- Tags, if it is implemented as a content widget

Content widgets should prefer this host instead of adding a new dedicated window.

## WidgetShell

`WidgetShell` is the reusable shell for title bar, background, divider, hover buttons, and content slot.

Current capabilities:

- `ShellContent`
- `TitleGlyph`
- `TitleBarContent` for transitional custom title bars
- `ShowHoverButtons`
- `ShowAddButton`
- `IsTitleEditable`
- `TitleEditorContent`
- `AddRequested`
- `MoreRequested`
- `CloseRequested`
- title pointer and right-click events

Current policy:

- New simple content widgets should use the default shell title bar.
- File widgets may continue using `TitleBarContent` until title editing and drag behavior are safely migrated.
- Avoid adding widget-specific visual hacks inside `WidgetShell`.

## Title Bar Metrics

`WidgetTitleBarMetricsCalculator` centralizes title bar visual sizing.

It controls:

- title icon size
- title text size
- action button size
- action icon size
- row height
- transitional inner title padding

Current users:

- `WidgetWindow`
- `QuickCaptureWidgetWindow`
- `ContentWidgetWindow`

File widgets intentionally use zero inner title padding to avoid double padding with `WidgetShell`. QuickCapture keeps inner padding because its current title layout uses it as part of the custom title content.

## Animation

`WidgetTrayAnimationController` centralizes tray/F7 animation execution.

It controls:

- animation profile creation
- window offset movement
- opacity
- scale
- timer progression
- easing
- generation checks
- restoration of visual/window state
- offset override for grouped tray animation

Current users:

- `WidgetWindow`
- `QuickCaptureWidgetWindow`
- `ContentWidgetWindow`

The controller only owns animation mechanics. Host windows still own:

- layer behavior
- backdrop suppression/restoration
- whether hide means close or only hide
- feature switch synchronization
- file item transition restoration

Do not move business semantics into the animation controller.

## Feature Widget Settings

`FeatureWidgetSettings` is the compatibility layer for singleton feature widget enabled states.

Current feature kinds:

- `QuickCapture`
- `Todo`

Settings are stored in:

- generic `FeatureWidgetEnabledStates`
- legacy mirrored fields: `QuickCaptureEnabled`, `TodoEnabled`

The legacy fields are kept for compatibility. Do not add new standalone fields such as `WeatherEnabled` or `MusicEnabled`; use the feature state bag instead.

## WidgetManager

`WidgetManager` owns lifecycle, restoration, F7/tray coordination, z-order, widget creation, deletion, and settings synchronization.

Current feature widget dispatch:

- `FeatureWidgetHandler` maps a feature kind to create/show, enable/disable, and hide-loaded behavior.
- `CreateOrShowFeatureWidgetAsync(...)` uses the handler registry.
- `SetFeatureWidgetEnabledAsync(...)` uses the handler registry.
- `WidgetWindowProvider` maps creatable widget kinds to host-window creation paths.
- `CreateRegisteredWidgetFromConfigAsync(...)` uses the window provider registry instead of a `WidgetKind` switch.

Current handlers:

- QuickCapture: dedicated window path.
- Todo: content window path.

Still intentionally present:

- `CreateWidgetOfKindAsync(...)` still has creation-specific behavior for File and Todo.
- `ShowWidgetAsync(...)` and tray batch preparation still have host-specific existing-window handling because each host stores loaded windows in different collections and has different reveal behavior.

Do not force loaded-window lookup, reveal semantics, or file-widget business behavior into a generic provider until the host boundaries are clearer.

## Settings UI

Current state:

- The feature widget list is generated from `SettingsViewModel.FeatureWidgetEntries`.
- Feature entries are derived from `WidgetContentFactory.GetFeatureWidgetEntryDescriptors()`.
- Available feature widgets, such as QuickCapture and Todo, show toggles.
- Planned feature widgets, such as Weather, Tags, Music, and SystemMonitor, are shown as descriptor-driven read-only rows with status text instead of disabled hand-written UI.
- Toggle state flows through `FeatureWidgetSettings` and `WidgetManager.SetFeatureWidgetEnabledAsync(...)`.

Global appearance settings should contain settings shared by all widgets:

- default width
- default height
- background opacity
- text size
- show hover buttons
- animation effect
- animation speed
- theme/material/corner settings

File-widget display settings should contain only file-widget display details:

- icon size
- horizontal spacing
- vertical spacing
- file name width
- extension display
- list details

Do not place Todo, Weather, Tags, Music, or SystemMonitor business settings inside file-widget display settings.

## Menus

Menu font and spacing are centralized in `App.xaml`.

Relevant resources include:

- `DeskBoxMenuFontFamily`
- `DeskBoxMenuPresenterPadding`
- `DeskBoxMenuItemPadding`
- `DeskBoxMenuItemMinHeight`
- `DeskBoxMenuItemFontSize`

Tray menu, widget title menus, and content menus should use these shared resources. Do not hard-code local menu font or padding unless there is a specific WinUI limitation.

## Data Storage

Main settings:

- `%LocalAppData%/DeskBox/settings.json`

Widget-specific data:

- `%LocalAppData%/DeskBox/data/widgets/{widgetId}/...`

Todo data:

- `%LocalAppData%/DeskBox/data/widgets/{widgetId}/todo.json`

Uninstalling the app may remove binaries but should not be assumed to remove `%LocalAppData%/DeskBox`. This is user data.

## Adding A New Content Widget

Recommended sequence:

1. Confirm `WidgetKind`.
2. Add or update `WidgetContentDescriptor`.
3. Keep `WidgetRegistry` as not creatable until content, storage, and tests are ready.
4. Implement `XxxWidgetContent`.
5. Implement `XxxWidgetViewModel`.
6. Implement `XxxWidgetStore` if persistence is needed.
7. Implement `IWidgetContent` adapter if the view does not directly implement it.
8. Add `XxxWidgetContentProvider`.
9. Register provider in `WidgetContentFactory`.
10. Add localization keys.
11. Add a `WidgetWindowProvider` registration in `WidgetManager` only when the kind is ready to create a real window.
12. Add tests for descriptor, provider, store, view model, settings entry, and registry coverage.
13. Only then make the kind available/creatable in `WidgetRegistry`.
14. Manually test F7, tray, close, delete, restart restore, theme, opacity, text size, and settings sync.

## Suggested Feature Order

Lowest-risk next feature widget:

- `SystemMonitor` with CPU, memory, and network only.

Reasons:

- no location permission
- no account or external API
- no file index complexity
- good test for realtime refresh content widgets

Then:

- `Weather`: location permission plus manual city selection.
- `Tags`: internal DeskBox index only, no file metadata writes.
- `Music`: Windows system media session only.

Last:

- widget merging, because it changes window/data/drag/tab behavior and should have a separate design document.

## High-Risk Areas

Be careful with these areas:

- Win10/Win11 drag and drop.
- UAC / integrity level drag behavior.
- file rename IME behavior.
- Esc cancel rename.
- F7/tray z-order restore.
- settings window topmost behavior.
- tray/menu font fallback on Windows.
- file shortcut `.lnk` drag sorting.
- installer data retention and install path migration.

Touch these only with focused changes and manual regression.

## Current Verification

Most recent verification:

```powershell
dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore
dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build
```

Result:

```text
187/187 passed
```

## Manual Regression Checklist

After changes to Shell, windows, manager, settings, or menus, test at least:

- App starts and restores File, QuickCapture, and Todo widgets.
- F7 shows/hides all expected widgets.
- Tray left-click behavior is correct.
- Tray right-click menu font is correct.
- Widget title/content right-click menu font is correct.
- Clicking another app restores widgets to desktop layer.
- Settings window is not covered by widgets.
- Feature widget toggles sync with desktop deletion/close.
- File widget external drag-in works.
- File widget internal sorting works.
- `.lnk` files can be dragged and sorted.
- File rename supports Chinese IME.
- Esc cancels rename.
- QuickCapture recent list scrolls.
- Clipboard recording setting persists after restart.
- Theme, opacity, text size, hover buttons, and animation settings apply to all widget families.

## Maintenance Rules

- Prefer WinUI / Windows native controls.
- Prefer `WidgetShell` for title and action surfaces.
- Prefer `ContentWidgetWindow` for new content widgets.
- Prefer descriptors and providers over scattered switches.
- Keep legacy compatibility fields until migration is proven safe.
- Make local backups before structural changes.
- Keep changes narrow around drag/drop, IME, F7/z-order, installer, and data migration.
