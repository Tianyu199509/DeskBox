# DeskBox Todo Widget Design

Version: draft v1  
Date: 2026-06-30  
Scope: first real feature widget after Phase 1 architecture groundwork.

## 1. Decision

Todo should be the first real feature widget built on the new widget architecture.

Reason:

- It is local-first and does not require network, location permission, API keys, or external services.
- It validates `WidgetKind + WidgetRegistry + WidgetContentFactory + IWidgetContent` with a real content surface.
- It has lower risk than Weather, Music, Tags, and SystemMonitor.
- It can remain independent from Quick Capture and File widgets.

Todo must be an independent widget. It should not be implemented as a tab inside Quick Capture.

## 2. Product Scope

### 2.1 v1 Includes

- Create a Todo widget.
- Add todo items.
- Edit todo item text inline.
- Mark todo items completed / active.
- Delete todo items.
- Clear completed items.
- Basic filtering inside the widget:
  - All
  - Active
  - Completed
- Persist data locally.
- Respect existing widget shell appearance:
  - title bar
  - background
  - corner radius
  - transparency
  - menu font
  - WinUI native controls

### 2.2 v1 Does Not Include

- Due dates.
- Reminders.
- Recurring tasks.
- Priority.
- Subtasks.
- Tags.
- Search.
- Cloud sync.
- Microsoft To Do integration.
- Windows notification integration.
- Dragging files into todo items.
- Dragging todo items between widgets.
- Todo inside Quick Capture.
- Todo shown in the file widget.
- Widget merge / tab integration.

These can be considered later after the first widget path is stable.

## 3. Architecture Placement

### 3.1 Existing Pieces To Reuse

- `WidgetKind.Todo`
- `WidgetConfig.Metadata`
- `WidgetRegistry`
- `WidgetContentFactory`
- `IWidgetContent`
- `WidgetShell`
- Existing multi-window host model

### 3.2 New Pieces

Suggested files:

```text
src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml
src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs
src/DeskBox/ViewModels/TodoWidgetViewModel.cs
src/DeskBox/Models/TodoItem.cs
src/DeskBox/Models/TodoWidgetData.cs
src/DeskBox/Services/TodoWidgetStore.cs
tests/DeskBox.Tests/TodoWidgetStoreTests.cs
tests/DeskBox.Tests/TodoWidgetViewModelTests.cs
```

The first implementation may use a simple `UserControl` plus view model. Do not create a new window class for Todo unless the existing host path cannot support it.

## 4. Data Model

### 4.1 TodoItem

```csharp
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int SortOrder { get; set; }
}
```

### 4.2 TodoWidgetData

```csharp
public sealed class TodoWidgetData
{
    public int SchemaVersion { get; set; } = 1;
    public List<TodoItem> Items { get; set; } = [];
}
```

Keep v1 data small and explicit. Avoid adding future fields until the feature needs them.

## 5. Storage

Todo data should not be stored directly in `WidgetConfig.Metadata` except for small display preferences.

Recommended path:

```text
%LocalAppData%/DeskBox/data/widgets/{widgetId}/todo.json
```

`WidgetConfig.Metadata` may store:

```text
todo.filter = all | active | completed
todo.schema = 1
```

Do not store the todo item list in `settings.json`. It would make settings larger, noisier, and riskier to migrate.

## 6. Registry And Creation

### 6.1 Before Todo Is User-Visible

Keep:

```text
WidgetRegistry.CanCreateWindow(Todo) = false
WidgetContentDescriptor.CanShowInCreateEntry = false
WidgetContentAvailability = Planned
WidgetContentStage = Placeholder
```

### 6.2 When Todo v1 Is Ready To Test

Only after `TodoWidgetContent`, store, and tests are ready:

```text
WidgetRegistry.CanCreateWindow(Todo) = true
WidgetRegistry.IsImplemented(Todo) = true
WidgetContentDescriptor.ContentStage = Implemented
WidgetContentDescriptor.Availability = Available
WidgetContentDescriptor.CanShowInCreateEntry = true
```

This should be one explicit commit, with tests proving the change.

## 7. Window Hosting

Preferred path:

1. Build `TodoWidgetContent` as `IWidgetContent`.
2. Add a generic host path only if needed.
3. Reuse `WidgetShell`.
4. Keep F7/tray/layer behavior inside existing manager/window code.

Avoid adding `TodoWidgetWindow` unless there is a clear blocker. The goal is to validate that future widgets can be content-first instead of window-first.

## 8. UI Guidelines

Use WinUI-native controls:

- `TextBox` for adding/editing items.
- `Button` with `FontIcon` for add/delete/clear.
- `CheckBox` for completion.
- `ListView` for item list.
- `MenuFlyout` for overflow actions.
- `InfoBar` or existing toast style only if needed.

Visual tone:

- Quiet and dense, closer to Windows Settings / Microsoft To Do than a marketing card UI.
- No decorative gradients, blobs, or oversized hero layout.
- Keep item rows compact.
- Preserve keyboard navigation.
- Make empty state useful but short.

Suggested layout:

```text
WidgetShell
  TodoWidgetContent
    Add row
    Filter segmented buttons
    ListView items
    Footer count / clear completed
```

## 9. Interaction Details

### Add Item

- User types into add box.
- Press `Enter` or click add button.
- Empty or whitespace-only text is ignored.
- New item appears at the top or bottom. Choose one and keep it consistent.

Recommended v1: new items appear at the top.

### Edit Item

- Double-click item text or use an edit button.
- `Enter` saves.
- `Esc` cancels.
- Empty text after edit deletes nothing; keep previous value and exit edit mode.

### Complete Item

- CheckBox toggles `IsCompleted`.
- Completed items remain visible based on current filter.

### Delete Item

- Delete button or context menu.
- No confirmation for single item delete in v1.
- Clear completed can ask for confirmation if many items will be removed.

## 10. Session And Window Behavior

Todo v1 must not change:

- tray/F7 behavior
- temporary topmost behavior
- desktop-layer restore
- mouse hook behavior
- file drag/drop
- IME handling in file rename

If Todo edit mode needs session recording, use `WidgetSessionManager` only as a recorder:

```text
MarkInteractionActive(widget, "todo-edit")
MarkInteractionEnded(widget, "todo-edit")
```

Do not let Todo decide z-order.

## 11. Localization

Do not hardcode user-visible strings in the final implementation.

Suggested localization keys:

```text
Todo.Title
Todo.AddPlaceholder
Todo.Filter.All
Todo.Filter.Active
Todo.Filter.Completed
Todo.Empty.All
Todo.Empty.Active
Todo.Empty.Completed
Todo.ClearCompleted
Todo.ItemsLeft
Todo.Delete
Todo.Edit
Todo.Save
Todo.Cancel
```

Descriptor keys should also eventually resolve:

```text
WidgetContent.Todo.StatusLabel
WidgetContent.Todo.StatusDescription
```

## 12. Tests

### Store Tests

- Creates missing `todo.json`.
- Loads empty data.
- Saves and reloads items.
- Handles invalid JSON without crashing.
- Preserves item order.
- Uses per-widget storage path.

### ViewModel Tests

- Add item trims text.
- Add empty item is ignored.
- Toggle completion updates item.
- Edit item updates `UpdatedAt`.
- Delete item removes item.
- Clear completed only removes completed items.
- Filter returns expected items.

### Coverage Tests

- Todo remains non-creatable until implementation is explicitly enabled.
- When Todo is enabled, registry/content descriptor tests must be updated in the same commit.

## 13. Manual Validation Matrix

Before enabling Todo creation:

- Existing File widgets still restore.
- Existing Quick Capture still restores.
- Future Todo config in settings does not crash and is skipped.
- Build/test pass.

After enabling Todo creation:

- Create Todo widget.
- Restart app and confirm Todo widget restores.
- Add/edit/delete/complete items.
- Filter all/active/completed.
- F7 show/hide works while Todo is visible.
- Tray show/hide works while Todo is visible.
- Click outside restores desktop layer normally.
- File widget drag/drop still works.
- File rename IME still works.
- Quick Capture tabs still work.
- Settings page still opens.

## 14. Rollout Plan

Recommended commits:

1. Add Todo data model and store tests.
2. Add Todo view model tests.
3. Add Todo content UI behind no entry point.
4. Add generic content host path if needed.
5. Enable Todo registry/descriptor as creatable.
6. Add creation entry and localized strings.
7. Manual test and fix only Todo-specific issues.

Do not combine all steps into one large commit.

## 15. Open Questions

Need product decision before implementation:

- Should new items appear at top or bottom?
- Should completed items automatically move below active items?
- Should clear completed require confirmation?
- Should Todo widget have a default title of `Todo` or localized `待办`?
- Should the first Todo widget be created by onboarding in the future, or only manually?

Default recommendation:

- New items at top.
- Completed items stay in current order.
- Clear completed confirms only when removing more than five items.
- Default title uses localized `Todo.Title`.
- No onboarding auto-create in v1.
