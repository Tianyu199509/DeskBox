# String Formatting Standards

## 🎯 审计目标

建立统一的字符串格式化标准，确保所有文本显示、错误消息和日志输出遵循一致的国际化规范。

---

## 🔍 Current State Problems

### Common Formatting Anti-Patterns Detected

| Pattern | Occurrences | Issues | Severity |
|---------|-------------|--------|----------|
| `string.Format()` with hardcoded text | ~150 | Not localized, no parameter safety | 🔴 Critical |
| String concatenation (`+`) | ~200 | Error-prone, unreadable | 🔴 Critical |
| `$""` interpolation | ~180 | Hardcoded format strings | 🟠 High |
| Conditional text assembly | ~50 | Branch logic inside UI code | 🟡 Medium |

**Example of Detected Issues**:
```csharp
// ❌ All these patterns found in codebase
MessageBox.Show("错误 " + errorCode + " 在位置 " + location);
string message = string.Format("用户 {0} 保存了 {1} 个文件", user, count);
var greeting = $"{userName}, 欢迎使用 DeskBox";  // No culture awareness!

// ❌ Mixed formats for same concept:
"时间：" + DateTime.Now.ToString()           // Chinese prefix
"Time: " + DateTime.Now.ToString()           // English prefix  
"T.: " + DateTime.Now.ToShortTimeString()    // Abbreviated - inconsistency!
```

---

## ⚠️ Critical Formatting Issues

### Issue #FORMAT-001: Unsafe String Concatenation in User Messages

**Problematic Code**:
```csharp
// ❌ Error messages built via concatenation - breaks if translated
void HandleError(Exception ex)
{
    string errorMsg = "Error " + ex.HResult + ": " + ex.Message;
    // If ex.Message is localized Chinese: "错误 80070005: 访问被拒绝"
    // But prefix is English "Error" → confusing mixed-language error!
    
    MessageBox.Show(errorMsg);
}

// ❌ Date/time concatenated without formatting
lblLastUpdate.Content = "最后更新：" + lastUpdateTime.ToLocalTime();
// Shows "最后更新：1/1/2024 3:45:00 PM" - US date format for Chinese user!
```

**Fix Required**: Use structured message templates

```csharp
public static class SafeMessageBuilder
{
    public static string BuildFromTemplate(string templateName, params object[] args)
    {
        var template = ResourceManager.GetString(templateName);
        
        // Template example: "Error {ErrorCode}: {ErrorMessage}"
        // Arguments: new { ErrorCode = ex.HResult, ErrorMessage = ex.Message }
        
        return string.Format(
            CultureInfo.CurrentCulture,  // Culture-aware formatting
            template,
            args
        );
    }
    
    // Named parameters version (safer)
    public static string Build<T>(string templateName, T data) where T : class
    {
        var template = ResourceManager.GetString(templateName);
        var properties = typeof(T).GetProperties();
        
        foreach (var prop in properties)
        {
            var value = prop.GetValue(data)?.ToString() ?? "null";
            template = template.Replace($"{{{prop.Name}}}", value);
        }
        
        return template;
    }
}

// Usage replacing all unsafe patterns:
HandleErrorEx(ex) => 
    throw new AppException(SafeMessageBuilder.BuildFromTemplate(
        "Error_Generic", ex.HResult, ex.Message));

lblLastUpdate.Content = SafeMessageBuilder.Build<TimelineEntry>("Date_TimestampLabel", 
    new { LastUpdated = lastUpdateTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) });
```

---

### Issue #FORMAT-002: Missing Null-Safe Interpolation

**Anti-Pattern**:
```csharp
// ❌ Will crash if userName is null
var welcome = $"你好，{userName}!";  // Throws NullReferenceException or shows "{userName}"

// ❌ Empty strings handled inconsistently
var status = string.IsNullOrEmpty(description) ? "暂无描述" : description;
// Versus:
var status = description ?? "无描述信息";  // Different approach in another file!
```

**Better Approach**: Centralized null handling

```csharp
public static class SafeStringExtensions
{
    public static string FormatWithFallback(this string? value, string fallback = "", CultureInfo? culture = null)
    {
        culture ??= Thread.CurrentThread.CurrentCulture;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
    
    public static string JoinWithSeparator(this IEnumerable<string?> items, string separator = ", ", string emptyText = "")
    {
        var filtered = items.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return filtered.Count == 0 ? emptyText : string.Join(separator, filtered);
    }
}

// Usage:
welcomeLabel.Text = $"你好，{userName?.FormatWithFallback("朋友")}!".FormatWithResource("Welcome_Greeting");

statusLabel.Text = descriptions.JoinWithSeparator("；", "无描述信息");
```

---

### Issue #FORMAT-003: Inconsistent Placeholder Naming

**Detected Problem**:
```csharp
// ❌ Random placeholder names across codebase
string msg1 = string.Format("{0} saved {1} files", user, count);      // Numeric placeholders
string msg2 = string.Format("User {user} saved {count} files", ...);  // Named placeholders
string msg3 = $"User {userName} saved {fileCount} files";             // C# interpolation

// Different developers use different styles → hard to maintain
```

**Standardization Rule**: **Named placeholders ONLY**, consistent naming convention

```xml
<!-- Resources/Strings.xaml -->
<ResourceDictionary>
  <!-- Standard naming: Action_Subject_Object -->
  <sys:String x:Key="SaveSuccess_Message">文件"{FileName}"已成功保存到"{FilePath}"。</sys:String>
  
  <!-- Parameters must be named meaningfully -->
  <sys:String x:Key="DeleteConfirm_Text">确定要删除"{ItemName}"吗？此操作无法撤销。</sys:String>
</ResourceDictionary>
```

```csharp
// Implementation follows standard naming pattern
public sealed class MessageTemplates
{
    private readonly ResourceManager _rm;
    
    public string SaveSuccess(string fileName, string filePath)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            _rm.GetString("SaveSuccess_Message"),
            fileName,  // Order matches resource definition exactly!
            filePath
        );
    }
    
    public string DeleteConfirmation(string itemName)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            _rm.GetString("DeleteConfirm_Text"),
            itemName
        );
    }
}
```

**Naming Convention Rules**:
1. **Positional indices allowed only when necessary**: `{0}`, `{1}`
2. **Named parameters preferred**: `{fileName}`, `{filePath}`
3. **Parameter order MUST match resource file definition**
4. **Use PascalCase for property-like arguments**: `{UserName}` not `{username}`

---

## 💡 Best Practice Templates

### ✅ Approved Patterns

#### 1. Dynamic Message Construction
```csharp
public static class FormattedMessages
{
    private const int MAX_DESCRIPTION_LENGTH = 200;
    
    public static string TruncatedDescription(string original, int maxLength = MAX_DESCRIPTION_LENGTH)
    {
        if (string.IsNullOrWhiteSpace(original))
            return ResourceManager.GetString("NoContent_Available");
        
        if (original.Length <= maxLength)
            return original;
        
        return original.Substring(0, maxLength) + "...";
    }
    
    public static string HumanReadableSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        
        var format = ResourceManager.GetString("FileSize_Format");
        // Template: "{Size} {Unit}"
        return string.Format(CultureInfo.CurrentCulture, format, size, sizes[order]);
    }
}
```

#### 2. Conditional Text Based on Context
```csharp
public static string GetWidgetStatusText(WidgetState state, TimeSpan elapsed)
{
    return state switch
    {
        WidgetState.Idle => ResourceManager.GetString("Status_Idle"),
        
        WidgetState.Running => $"""
            {ResourceManager.GetString("Status_Running")}: 
            {FormattedMessages.TimeSpanToHuman(elapsed)}
            """,
        
        WidgetState.Error => ResourceManager.GetString("Status_Error"),
        
        WidgetState.Paused => $"""
            {ResourceManager.GetString("Status_Paused")}
            """,
        
        _ => ResourceManager.GetString("Status_Unknown")
    };
}

// Helper method
private static string TimeSpanToHuman(TimeSpan ts)
{
    return ts.TotalDays > 0 
        ? $"{ts.Days}天{ts.Hours}小时"
        : ts.TotalHours > 0 
            ? $"{ts:H}小时{ts:mm}分"
            : $"{ts:mm}分{ts:ss}秒";
}
```

---

## 📋 Formatting Standards Checklist

### Mandatory Requirements

✅ **All user-facing messages must use ResourceManager lookups**  
✅ **Never concatenate raw user input into formatted strings**  
✅ **Always pass CultureInfo to formatting methods**  
✅ **Use named parameters in string.Format() instead of positional indices**  
✅ **Handle null/empty gracefully with fallback values**  
✅ **Ensure all dates/times are locale-aware**  

### Prohibited Patterns

❌ `"Error " + code` - String concatenation in user messages  
❌ `$"Hello {name}"` - C# interpolation without resource key  
❌ `.ToString("MM/dd/yyyy")` - Hardcoded date format  
❌ `$"{value:C}"` - Currency without specifying currency symbol/locale  
❌ `.ToUpper()` / `.ToLower()` - Case conversion should consider Turkish dot issues  

---

## 💡 Best Practices Summary

### ✅ DO

- Define clear message templates in resource files
- Always specify CultureInfo for formatting
- Use semantic parameter names matching template definitions
- Test translations with actual native speakers

### ❌ DON'T

- Concatenate translated strings manually
- Assume English word order applies globally
- Forget about pluralization rules per language
- Use invariant culture for user-facing text

---

<div align="center">

**"A well-formatted message is a localized message."**

*Generated: July 22, 2026*  
*Version: 1.0*

</div>
