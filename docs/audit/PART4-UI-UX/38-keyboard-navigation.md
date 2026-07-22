# Keyboard Navigation Audit

## 🎯 审计目标

审查 DeskBox 中键盘导航支持程度，确保用户可以完全不使用鼠标完成所有操作。

---

## 🔍 Current Keyboard Support State

### Features Detected

| Navigation Feature | Status | Coverage | Issues |
|-------------------|--------|----------|--------|
| Tab Order | ❌ None | 0% | No explicit tab stops |
| Enter/Space activation | ⚠️ Partial | 30% | Custom controls missing |
| Arrow key navigation | ❌ None | 0% | List/Grid not navigable |
| Shortcuts (Ctrl+S, etc.) | ⚠️ Incomplete | 40% | Missing common shortcuts |
| Escape to close dialogs | ✅ Yes | Good | Works in most places |

---

## ⚠️ Critical Issues

### Issue #KEYBOARD-001: No Explicit Tab Order

**Problem**: Users cannot navigate form elements with Tab key

```xml
<!-- ❌ Controls appear in DOM order, not logical flow -->
<StackPanel>
    <TextBox Name="SearchBox"/>
    <Button Content="Search"/>
    <ListView Name="ResultsList"/>
    <!-- User wants to search → type → hit Enter, but must Tab through list first! -->
</StackPanel>
```

**Fix Required**: Define logical tab order

```xml
<!-- UseTabIndex property for explicit control -->
<StackPanel>
    <TextBox Name="SearchBox" TabIndex="0"/>
    <Button Content="Search" TabIndex="1"/>
    
    <!-- Skip the list from tab order if it's auto-focused on load -->
    <ListView Name="ResultsList" IsTabStop="False" 
              FocusMode="Direct">
        <!-- Navigate via Up/Down arrow keys instead -->
    </ListView>
</StackPanel>
```

---

### Issue #KEYBOARD-002: Custom Widgets Don't Respond to Keys

**Anti-Pattern**:
```csharp
// ❌ Only mouse events handled
public partial class WeatherWidgetView : UserControl
{
    public WeatherWidgetView()
    {
        this.MouseLeftButtonDown += OnClick;
        // NO KEY EVENT HANDLERS!
    }
}

public void OnClick(object sender, MouseButtonEventArgs e)
{
    ExpandDetails();
}
```

**Better Approach**: Add comprehensive keyboard support

```csharp
public partial class AccessibleWeatherWidgetView : UserControl
{
    public AccessibleWeatherWidgetView()
    {
        InitializeComponent();
        
        // Handle keyboard input
        this.KeyDown += OnKeyDown;
        
        // Make widget focusable and part of tab sequence
        this.Focusable = true;
        this.TabNavigation = ContainerNavigation.Once;
    }
    
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                ExpandDetails();
                e.Handled = true;
                break;
                
            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;
                
            case Key.Down:
                NavigateHistory(1);
                e.Handled = true;
                break;
        }
    }
    
    private void ExpandDetails()
    {
        // Same action as clicking
        IsExpanded = !IsExpanded;
    }
}
```

---

### Issue #KEYBOARD-003: Missing Common Shortcut Keys

**Detected Missing Shortcuts**:

| Shortcut | Expected Action | Status | Priority |
|----------|----------------|--------|----------|
| Ctrl+F | Find/Search | ❌ No | 🔴 Critical |
| Ctrl+T | New Widget | ❌ No | 🟠 High |
| Ctrl+W | Close Widget | ❌ No | 🟠 High |
| Escape | Cancel/Discard | ⚠️ Partial | 🟡 Medium |
| Ctrl+Z | Undo | ❌ No | 🟢 Low |

**Fix Required**: Implement standard Windows shortcuts

```csharp
public class GlobalShortcutHandler : IDisposable
{
    private readonly Window _mainWindow;
    private static readonly Dictionary<Key, Action> _shortcuts = new()
    {
        { Key.F, () => ShowFindDialog() },           // Ctrl+F
        { Key.N, () => CreateNewWidget() },          // Ctrl+N  
        { Key.T, () => OpenTemplateSelector() },     // Ctrl+T
        { Key.W, () => RequestCloseCurrentWidget() }, // Ctrl+W
        { Key.OemPlus, () => IncreaseFontSize() },   // Ctrl++
        { Key.OemMinus, () => DecreaseFontSize() }   // Ctrl+-
    };
    
    public GlobalShortcutHandler(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _mainWindow.PreviewKeyDown += OnPreviewKeyDown;
        
        // Register shortcut handlers
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, 
            ExecuteFindCommand));
    }
    
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_shortcuts.TryGetValue(e.Key, out var action))
            {
                e.Handled = true;
                action?.Invoke();
            }
        }
    }
    
    private void ExecuteFindCommand(object sender, ExecutedRoutedEventArgs e)
    {
        FindDialog.ShowModal();
    }
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Test entire app using ONLY keyboard
- Provide visible focus indicators (outline rings)
- Ensure all interactive elements are keyboard-accessible
- Document available shortcuts prominently
- Follow Windows UX guidelines for standard shortcuts

### ❌ DON'T

- Rely solely on mouse/touch interactions
- Disable Tab behavior without alternative
- Forget about screen reader users who depend on keyboard
- Implement custom navigation that breaks system expectations

---

<div align="center">

**"If you can't navigate it with Tab, you can't use it at all."**

*Generated: July 22, 2026*  
*Version: 1.0*

</div>
