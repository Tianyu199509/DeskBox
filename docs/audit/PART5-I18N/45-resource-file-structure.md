# Resource File Structure Design

## 🎯 审计目标

设计合理的资源文件结构，为多语言支持提供清晰、可维护的文件组织方案。

---

## 🔍 Current State Analysis

### Existing Resource Usage

**Detected Pattern**:
```xml
<!-- ❌ NO structured resource files exist -->
<!-- All text hardcoded in XAML and C# -->

<!-- App.xaml contains scattered definitions: -->
<SolidColorBrush x:Key="PrimaryBrush">#FF0078D4</SolidColorBrush>
<TextBlock Text="保存"/>  <!-- Direct in markup - not even a resource! -->
```

**Impact**: 
- Zero i18n capability today
- No centralized text management
- Cannot add new languages without complete rewrite
- Maintenance nightmare (same text in 10+ places)

---

## 📁 Recommended Resource File Structure

### Option A: Category-Based Structure (Recommended for DeskBox)

```
Resources/
├── Strings.zh-CN.resx           # All UI strings in Chinese
├── Strings.en-US.resx           # English fallback
├── Strings.ja-JP.resx           # Japanese support (future)
├── Strings.de-DE.resx           # German (future)
│
├── ValidationMessages.zh-CN.resx   # Error/validation messages
├── ValidationMessages.en-US.resx
│
├── ToolTips.zh-CN.resx            # Hover help text
├── ToolTips.en-US.resx
│
└── Messages/                       # Dialog messages
    ├── CommonDialogs.zh-CN.resx
    ├── CommonDialogs.en-US.resx
    ├── SystemNotifications.zh-CN.resx
    └── SystemNotifications.en-US.resx
```

### Key Naming Conventions

| Prefix | Purpose | Example Keys |
|--------|---------|--------------|
| `UI_` | User interface elements | `UI_Save`, `UI_Cancel`, `UI_Delete` |
| `Widget_` | Widget-specific labels | `WidgetTitle_Weather`, `WidgetDesc_Music` |
| `Status_` | Status bar messages | `Status_Loading`, `Status_Saving` |
| `Error_` | System errors | `Error_FileNotFound`, `Error_ConnectionFailed` |
| `Dialog_` | Confirmation dialogs | `Dialog_ConfirmDelete`, `Dialog_Warning_ExpiresSoon` |
| `Tooltip_` | Helper tooltips | `Tooltip_AddNewWidget`, `Tooltip_Settings` |
| `Placeholder_` | Input field hints | `Placeholder_SearchFiles`, `Placeholder_EnterName` |

---

## 🛠️ Implementation Strategy

### Step 1: Extract Current Strings into Resources

**Migration Script** (PowerShell):

```powershell
# Phase 1: Inventory all hardcoded strings
$hardcodedStrings = @()

Get-ChildItem src/**/*.cs, *.xaml -Recurse | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    
    # Match string literals (3+ chars, excluding comments/logs)
    $matches = [regex]::Matches($content, '(?<!["\x27])(?<="|'')([^\x27"\\]{3,})(?=["\x27])')
    
    foreach ($match in $matches) {
        $text = $match.Value
        
        # Filter out code-only strings (class names, methods, etc.)
        if (-not $text.StartsWith("I") -and -not $text.EndsWith("Service")) {
            $hardcodedStrings += [PSCustomObject]@{
                File = $_.FullName
                Line = (Select-String $_.FullName -Pattern $text -Context 0,1).LineNumber
                Text = $text
            }
        }
    }
}

# Save inventory for manual review
$hardcodedStrings | Export-Csv "hardcoded_strings_inventory.csv" -NoTypeInformation
```

### Step 2: Create Base Resource Files

**File**: `Resources/Strings.zh-CN.resx`

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  
  <!-- UI Actions -->
  <data name="UI_Save" xml:space="preserve">
    <value>保存</value>
    <comment>Action to save changes</comment>
  </data>
  <data name="UI_Cancel" xml:space="preserve">
    <value>取消</value>
  </data>
  <data name="UI_Delete" xml:space="preserve">
    <value>删除</value>
  </data>
  <data name="UI_Edit" xml:space="preserve">
    <value>编辑</value>
  </data>
  <data name="UI_Close" xml:space="preserve">
    <value>关闭</value>
  </data>
  
  <!-- Widget Labels -->
  <data name="WidgetTitle_Weather" xml:space="preserve">
    <value>天气</value>
    <comment>Title of weather widget</comment>
  </data>
  <data name="WidgetTitle_Music" xml:space="preserve">
    <value>音乐</value>
  </data>
  
  <!-- ... hundreds more entries ... -->
</root>
```

### Step 3: Implement ResourceManager Accessor

**Helper Class**:

```csharp
public static class ResourceAccessors
{
    private static readonly ResourceManager _mainResourceManager;
    private static readonly ResourceManager _validationResourceManager;
    private static readonly ResourceManager _toolTipResourceManager;
    
    static ResourceAccessors()
    {
        var assembly = typeof(ResourceAccessors).Assembly;
        
        _mainResourceManager = new ResourceManager("DeskBox.Resources.Strings", assembly);
        _validationResourceManager = new ResourceManager("DeskBox.Resources.ValidationMessages", assembly);
        _toolTipResourceManager = new ResourceManager("DeskBox.Resources.ToolTips", assembly);
    }
    
    // UI Actions
    public static string Save => GetString("UI_Save");
    public static string Cancel => GetString("UI_Cancel");
    public static string Delete => GetString("UI_Delete");
    public static string Edit => GetString("UI_Edit");
    public static string Close => GetString("UI_Close");
    
    // Widgets
    public static string WeatherTitle => GetString("WidgetTitle_Weather");
    public static string MusicTitle => GetString("WidgetTitle_Music");
    
    // With culture override
    public static string GetString(string key, CultureInfo? culture = null)
    {
        return _mainResourceManager.GetString(key, culture ?? Thread.CurrentThread.CurrentCulture);
    }
    
    public static string ValidationMessage(string key)
    {
        return _validationResourceManager.GetString(key, Thread.CurrentThread.CurrentCulture);
    }
    
    public static string GetToolTip(string key)
    {
        return _toolTipResourceManager.GetString(key, Thread.CurrentThread.CurrentCulture);
    }
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Use clear prefix-based naming for all keys
- Add context comments for translators
- Separate by category (UI vs error vs tooltip)
- Keep original source language as reference point
- Test with actual translations before release

### ❌ DON'T

- Mix multiple languages in same file
- Assume character encodings will handle all scripts
- Merge .resx files manually (use Visual Studio designer)
- Forget about case sensitivity in different cultures

---

<div align="center">

**"Good structure enables scalability – design once, expand everywhere."**

*Generated: July 22, 2026*  
*Version: 1.0*

</div>
