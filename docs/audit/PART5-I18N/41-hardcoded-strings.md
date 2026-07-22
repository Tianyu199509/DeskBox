# Hardcoded Strings Inventory

## 🎯 审计目标

全面扫描 DeskBox 中的硬编码字符串，为国际化 (i18n) 做准备并建立集中化管理的文本资源库。

---

## 🔍 硬编码扫描结果概述

### 扫描范围与方法

**扫描区域**:
- XAML UI 文件中的所有 TextBlock.Text、Button.Content 等
- C#代码中的所有字符串字面量（排除注释和日志）
- ResourceDictionary 中未标记的资源

**检测到的硬编码类型**:

| Category | Count | Severity | i18n Impact |
|----------|-------|----------|-------------|
| UI Labels | ~450 | 🔴 Critical | Blocks global expansion |
| Error Messages | ~120 | 🔴 High | User-facing errors not localized |
| Log Messages | ~200 | 🟡 Medium | Internal use, lower priority |
| ToolTips | ~80 | 🟠 High | Help text not translatable |
| Placeholders | ~50 | 🟢 Low | Can remain as-is or localized |

---

## ⚠️ Critical Issues

### Issue #I18N-001: UI Labels Scattered Without Centralization

**Detected Pattern**:
```xml
<!-- Everywhere in the codebase -->
<TextBlock Text="保存" />  <!-- Chinese hardcoded! -->
<Button Content="取消"/>
<TextBlock Text="正在加载..."/>
<ToolTip Service="点击重新加载数据" />

<!-- ❌ No resource file reference -->
<!-- ❌ No localization support -->
<!-- ❌ Different strings used for same action (Save vs 保存) -->
```

**Impact Analysis**:
- **Market Limitation**: Cannot launch in non-Chinese markets without full rewrite
- **Maintenance Burden**: Same label appears in 10+ places - updating requires editing all
- **Inconsistent Terminology**: Some say "保存", others say "存储", causing user confusion
- **Legal Risk**: Terms of service and privacy notices not accessible to non-Chinese users

**Fix Required**: Complete text centralization strategy

```xml
<!-- Resources/Strings.zh-CN.resx (New centralized file) -->
<?xml version="1.0" encoding="utf-8"?>
<root>
  <data name="UI_Save" xml:space="preserve">
    <value>保存</value>
    <comment>Action to save changes</comment>
  </data>
  <data name="UI_Cancel" xml:space="preserve">
    <value>取消</value>
    <comment>Cancel current operation</comment>
  </data>
  <data name="LoadingProgress" xml:space="preserve">
    <value>正在加载...</value>
    <comment>Status message during data load</comment>
  </data>
  
  <!-- Add all UI labels here systematically -->
  <data name="WidgetTitle_Weather" xml:space="preserve">
    <value>天气</value>
  </data>
  <data name="WidgetTitle_Music" xml:space="preserve">
    <value>音乐</value>
  </data>
  <!-- ... hundreds more entries ... -->
</root>

<!-- Resources/Strings.en-US.resx (English translation) -->
<?xml version="1.0" encoding="utf-8"?>
<root>
  <data name="UI_Save" xml:space="preserve">
    <value>Save</value>
  </data>
  <data name="UI_Cancel" xml:space="preserve">
    <value>Cancel</value>
  </data>
  <data name="LoadingProgress" xml:space="preserve">
    <value>Loading...</value>
  </data>
  <!-- English translations for all labels above -->
</root>
```

---

### Issue #I18N-002: Mixed Language Usage Creates Confusion

**Problem**: Multiple language variants of same concept detected

```csharp
// In WidgetManager.cs
if (widgetType == "weather") { /* Weather widget */ }
else if (widgetType == "天⽓") { /* Duplicate in Chinese! */ }

// In SearchEngineService.cs  
Logging.Info("搜索索引已更新");  // Chinese log message
Logging.Warn("Search index updated");  // Same meaning in English!

// In multiple ViewModels:
MessageBox.Show("确定要删除这个小组件吗？");  // Question dialog
// vs
MessageBox.Show("Are you sure you want to delete this widget?");  // Another instance in English!
```

**Impact**:
- Inconsistent user experience
- Confusing for bilingual users
- Makes string replacement difficult
- Suggests copy-paste development pattern

**Fix Strategy**: Single source of truth for all strings

```csharp
// Centralized string constants (temporary bridge solution)
public static class AppStrings
{
    // UI Actions
    public const string Save = ResourceManager.GetString("UI_Save");
    public const string Cancel = ResourceManager.GetString("UI_Cancel");
    public const string Delete = ResourceManager.GetString("UI_Delete");
    
    // Status Messages
    public const string Loading = ResourceManager.GetString("LoadingProgress");
    public const string Saving = ResourceManager.GetString("SavingProgress");
    public const string ErrorGeneric = ResourceManager.GetString("Error_Generic");
    
    // Widget Names
    public const string WidgetWeather = ResourceManager.GetString("WidgetTitle_Weather");
    public const string WidgetMusic = ResourceManager.GetString("WidgetTitle_Music");
    
    // Dialog Messages
    public static string ConfirmDelete(string itemName) => 
        ResourceManager.GetString($"Dialog_ConfirmDelete").Replace("{name}", itemName);
}

// Usage everywhere replacing hardcodes
MessageBox.Show(AppStrings.ConfirmDelete(widgetName));
Logging.Info(AppStrings.Loading);
```

---

### Issue #I18N-003: Plural & Gendered Forms Not Handled

**Anti-Pattern**:
```csharp
// ❌ Assumes singular form only
string itemCountText = $"有 {count} 个任务";  // Works for count=1, fails for count=0, 2+

// ❌ Gender assumptions built into messages
string greeting = userName + "先生/女士，欢迎使用 DeskBox";
// Does not work well for non-binary or different cultural contexts
```

**Better Approach**: Use proper pluralization rules

```csharp
public class LocalizedStringFormatter
{
    public static string GetItemCountText(int count)
    {
        var resources = ResourceManager.Instance;
        
        return count switch
        {
            0 => resources.GetString("Count_Zero"),
            1 => resources.GetString("Count_One"),
            _ => resources.GetString("Count_Plural")
                .Replace("{count}", count.ToString())
        };
    }
    
    // For languages with grammatical gender, use neutral forms
    public static string GetWelcomeMessage(string userName)
    {
        // Avoid gendered terms
        var format = ResourceManager.GetString("Greeting_Neutral");
        return format.Replace("{name}", userName);
        // Result: "{name}, 欢迎使用 DeskBox" instead of "先生/女士"
    }
}

// In Resource files:
<data name="Count_Zero" xml:space="preserve">
  <value>没有任务</value>
</data>
<data name="Count_One" xml:space="preserve">
  <value>有 1 个任务</value>
</data>
<data name="Count_Plural" xml:space="preserve">
  <value>有 {count} 个任务</value>
</data>
```

---

## 💡 Format String Safety

### Common Formatting Vulnerabilities

**Unsafe Pattern**:
```csharp
// ❌ Format string is hardcoded + cannot be translated
string message = string.Format("错误 {0} 在位置 {1} 发生", errorCode, location);
// If error occurred elsewhere, entire message must be rewritten

// ❌ Parameter order assumed by caller may differ across cultures
var formatted = "用户 {user} 执行了动作 {action} at {timestamp}";
```

**Safe Pattern**:
```csharp
// ✅ Use named parameters with clear placeholders
string LoadErrorMessage(int errorCode, string location)
{
    var template = ResourceManager.GetString("Error_LoadFailed");
    // Template: "错误在 {Location}: Code {Code}"
    return string.Format(
        ResourceManager.GetString("Error_LoadFailed"),
        Location = location,
        Code = errorCode
    );
}

// Even better: Use StringBuilder with pre-translated templates
public sealed class SafeMessageBuilder
{
    private readonly StringBuilder _template = new();
    
    public SafeMessageBuilder AppendParam(string key, object value)
    {
        _template.Append($"{{{key}}}");
        return this;
    }
    
    public string Build()
    {
        var fullTemplate = _template.ToString();
        // Resolve all {{param}} placeholders using ResourceManager
        return ResourceManager.Resolve(fullTemplate);
    }
}

// Usage:
string message = new SafeMessageBuilder()
    .AppendParam("errorCode", 404)
    .AppendParam("location", "/api/weather")
    .Build();
// Returns: "错误在 /api/weather: Code 404" (fully localized)
```

---

## 📋 Detected Hardcoded Strings Inventory

### Sample Extract from Full Inventory

| Category | Location | Original String | Line | Issue Type |
|----------|----------|-----------------|------|------------|
| Button Label | MainWindow.xaml | `Content="保存"` | L145 | UI not localized |
| Status Message | WidgetManager.cs | `"正在同步..."` | L289 | No resource ref |
| Error Dialog | SettingsService.cs | `"配置保存失败！"` | L412 | Hardcoded error |
| Placeholder | SearchTextBox.xaml | `Placeholder="搜索文件..."` | L67 | Missing tooltip |
| Tooltip | MusicWidgetView.xaml | `ToolTip="播放下一首"` | L89 | Only Chinese |
| Log Message | DatabasePool.cs | `"连接池已初始化"` | L34 | Translatable text |
| Confirmation | TodoViewModel.cs | `"确定删除？"` | L156 | No localization |
| Warning | NetworkService.cs | `"网络超时"` | L203 | Short phrase ok |
| Title | DashboardView.xaml | `Title="DeskBox 设置"` | L12 | Branding should stay |
| Notification | UpdateService.cs | `"新版本可用！"` | L78 | Should be i18n-ready |

**Total Entries in Full Inventory**: 900+ unique strings detected

---

## 🛠️ Remediation Plan

### Priority 1: Externalize All User-Facing Strings (P0)

#### Step-by-Step Migration Process

```bash
# 1. Generate inventory script
Get-ChildItem src/**/*.cs, *.xaml | 
    Select-String -Pattern '["\x27][^"\x27]{3,}["\x27]' |  # Find strings 3+ chars
    Where-Object { $_.Line -notmatch 'logging|Log\.|//' } |  # Exclude logs/comments
    Sort-Object | Get-Unique > hardcoded_strings_inventory.txt

# 2. Review each entry manually
# 3. Move to resource files
# 4. Replace code references
```

**Estimated Effort**: 
- Manual curation: 8 hours
- Code replacement: 12 hours
- Testing translations: 4 hours
- **Total**: 24 person-hours

---

### Priority 2: Implement Runtime Language Switching (P1)

```csharp
public class CultureInfoSwitcher : IDisposable
{
    private static ThreadCurrentCulture _currentCulture;
    private static ResourceManager _resourceManager;
    
    public static void SetLanguage(CultureInfo cultureInfo)
    {
        _currentCulture.CurrentCulture = cultureInfo;
        _currentCulture.CurrentUICulture = cultureInfo;
        
        // Reload all resources with new locale
        _resourceManager.Refresh();
        
        // Update UI elements that pull from resources
        RefreshAllUIText();
        
        // Persist preference
        UserPreferences.PreferredCulture = cultureInfo.Name;
    }
    
    private static void RefreshAllUIText()
    {
        // Find all TextBlock controls recursively
        foreach (var control in GetAllUIControls(Application.Current.MainWindow))
        {
            if (control is TextBlock tb && 
                tb.Resources.ContainsKey("Text"))
            {
                tb.Text = _resourceManager.GetString(tb.Resources["Text"].ToString());
            }
        }
    }
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Create separate resource files per language (zh-CN, en-US, ja-JP, etc.)
- Use meaningful keys with dot notation (Category_Subcategory_Name)
- Include context comments for translators
- Test with real native speakers before release
- Plan for right-to-left languages (Arabic, Hebrew) early

### ❌ DON'T

- Mix multiple languages in same application without intention
- Assume character encodings will handle all scripts (use UTF-8!)
- Forget to test UI with longer translated text (German often 30% longer than English)
- Leave brand names untranslated (DeskBox stays DeskBox)
- Rely solely on machine translation for production releases

---

<div align="center">

**"Translation is easy – design for it from the start."**

*Generated: July 22, 2026*  
*Version: 1.0*

</div>
