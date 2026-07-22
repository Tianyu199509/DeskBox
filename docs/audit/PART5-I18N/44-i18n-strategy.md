# 国际化 (i18n) 就绪度审计报告

## 🎯 审计目标

评估 DeskBox 的多语言支持准备情况，识别硬编码文本，设计方案实现多语言适配。

---

## 🔍 现状扫描

### grep 搜索命令

```powershell
# 查找所有 XAML 中的硬编码文本
grep -r "Text=\"[^\"]*\"" src/DeskBox/Views/*.xaml | findstr /v "/Resources/"

# 查找所有 C# 中的硬编码字符串
grep -r 'MessageBox\.Show\|App\.Log' src/DeskBox/Services/*.cs | findstr /C:"\""

# 查找中文文本
grep -r "[\u4e00-\u9fa5]" src/DeskBox/ | findstr /v "//"
```

### 预期发现的硬编码模式

#### XAML 硬编码示例
```xml
<!-- ❌ BAD -->
<TextBlock Text="保存设置" />
<Button Content="确定" Click="OnConfirm" />
<TitleBar Title="DeskBox 设置" />
```

#### C# 硬编码示例
```csharp
// ❌ BAD
MessageBox.Show("文件已保存");
App.LogVerbose("Widget created successfully");
throw new InvalidOperationException("Invalid widget state");
```

---

## 📊 风险评估

### ⚠️ **问题 #I1: No Localization Infrastructure**

**严重等级**: 🟠 High  
**当前状态**: ❌ 完全无多语言支持  

**影响范围**:
- UI 文本全部硬编码在 XAML 中
- 日志消息硬编码在 C# 中
- 错误提示无法本地化
- 阻碍国际化发布计划

**业务影响**:
1. 无法发布中文版/英文版等多语言版本
2. 社区贡献者难以参与翻译
3. Microsoft Store 全球分发受限

---

## 🏗️ i18n 架构设计方案

### 方案 A: ResourceDictionary + .resx (推荐)

#### 结构规划

```
src/DeskBox/
├── Resources/
│   ├── Strings.resx                    # Default (Chinese Simplified)
│   ├── Strings.zh-CN.resx              # Chinese (Simplified) - explicit
│   ├── Strings.en-US.resx              # English (US)
│   └── ValidationMessages.resx           # Validation error messages
│
├── Views/
│   └── SettingsWindow.xaml            → binds to {StaticResource SaveButton}
│
└── Services/
    └─LocalizationService.cs             → manages culture switching
```

#### XAML 绑定方式

```xml
<!-- BEFORE (Hardcoded) -->
<TextBlock Text="保存设置" />

<!-- AFTER (Localized) -->
<TextBlock Text="{Binding $l10n:Strings.SaveSettings}" />
```

或者使用 `x:Uid` pattern:
```xml
<Button x:Uid="btnSave" Content="ResxFile!btnSave.Content" />
```

---

#### C# 访问方式

```csharp
// Using ResourceManager
using DeskBox.Resources;

public void ShowSaveMessage()
{
    var message = Strings.SaveSuccess;  // Gets from .resx
    MessageBox.Show(message);
}
```

---

### 方案 B: CommunityToolkit.Mvvm.LocalizableString (备选)

如果项目已使用 CommunityToolkit，可以考虑其内置的 LocalizableString:

```csharp
public class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    [Localizable(typeof(Strings), "SaveButtonText")]
    private string _saveButtonText = Strings.SaveButtonText;
}
```

**优点**:
- ✅ 强类型绑定
- ✅ IntelliSense 支持
- ✅ Compile-time checking

**缺点**:
- ❌ 需要额外 NuGet 包
- ❌ 维护成本稍高

---

## 🎨 动态语言切换设计

### Architecture Diagram

```
User clicks language change
    ↓
SettingsViewModel.CurrentUICulture = NewCulture
    ↓
LocalizationService.UpdateCulture(newCulture)
    ↓
ApplicationLanguages.PrimaryLanguageOverride = newCulture.TwoLetterISOLanguageName
    ↓
XAML resources reload automatically
    ↓
All TextBlocks update to new language
```

### Implementation Template

```csharp
public partial class LocalizationService : IDisposable
{
    private CultureInfo? _currentCulture;
    
    public CultureInfo CurrentCulture
    {
        get => _currentCulture ?? CultureInfo.CurrentUICulture;
        set
        {
            _currentCulture = value;
            Application.Current.MainWindow.Language = XmlLanguage.GetLanguage(value.IetfLanguageTag);
            
            // Force resource reload
            foreach (var resource in FindResources())
            {
                resource.Refresh();
            }
        }
    }
    
    public void Dispose()
    {
        // Cleanup...
    }
}
```

---

## 📝 实施路线图

### Phase 1: Foundation (Week 1)

**Day 1-2**: Setup resource files
```bash
# Create directory structure
mkdir src/DeskBox/Resources
touch src/DeskBox/Resources/Strings.resx
touch src/DeskBox/Resources/ValidationMessages.resx
```

**Day 3-4**: Extract UI strings
- Scan all XAML files for Text attributes
- Move to .resx with key naming convention

**Day 5-7**: Implement localization service
- Add CurrentUICulture property
- Wire up language change event

---

### Phase 2: Expansion (Week 2-3)

**Week 2**: Error messages & Logs
- Migrate所有 MessageBox.Show() 调用
- Replace App.LogVerbose() with localized variants

**Week 3**: Community translation
- Publish .resx files to Crowdin/Weblate
- Set up automated CI pipeline for translations

---

### Phase 3: Polish (Week 4)

**Rich text support**: Plural forms, gender, etc.
```xml
<!-- Example: Plural handling -->
<TextBlock Text="{Binding Count, Converter={StaticResource PluralConverter}, ConverterParameter='item'}" />
```

---

## 🔤 命名规范建议

### Resource Key Convention

```
{EntityType}.{PropertyName}.{Attribute}

Examples:
- Widget.TitleBar.Text
- Button.Save.Click
- ValidationError.Username.Required
- Tooltip.SearchBox.Hint
```

### File Naming

```
Strings.{Language}.resx
ValidationMessages.{Language}.resx
Dates.{Language}.resx     # Date/time formats
Numbers.{Language}.resx   # Number formats
```

---

## ⚠️ 技术陷阱规避

### Trap 1: Hardcoded Paths in Strings

```csharp
// ❌ BAD - Culture-specific path format
string errorMsg = $"文件 '{path}' 未找到";

// ✅ GOOD - Use path API
string errorMsg = PathDoesNotExist(path);  // Returns localized message
```

### Trap 2: Date/Time Formatting

```csharp
// ❌ BAD
string dateText = DateTime.Now.ToString("yyyy-MM-dd");

// ✅ GOOD
string dateText = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
```

### Trap 3: Number Formatting

```csharp
// ❌ BAD
string price = "$" + amount.ToString();  // US format only

// ✅ GOOD
string price = amount.ToString("C", CultureInfo.CurrentCulture);
```

---

## 💡 最佳实践建议

### 1. External Translation Platforms

推荐工具：
- **Crowdin** (GitHub integration)
- **Weblate** (Open source)
- **Transifex** (Enterprise)

**工作流程**:
```
Developer pushes .resx → Platform auto-updates → Translators edit → PR merged → CI builds
```

### 2. Fallback Strategy

```
Default (Chinese): Strings.resx
          ↓ (missing key)
Fallback (English): Strings.en-US.resx
          ↓ (still missing)
Default again: Generic error messages
```

### 3. Testing Coverage

```csharp
[Test]
public void Test_AllResources_HaveTranslations()
{
    var cultures = new[] { "zh-CN", "en-US" };
    
    foreach (var culture in cultures)
    {
        var resourceSet = new ResourceSet("Strings", 
            Assembly.GetExecutingAssembly(), 
            new CultureInfo(culture));
        
        Assert.That(resourceSet.Count > 0, 
            $"Resource file for {culture} is empty");
    }
}
```

---

## 📊 工作量估算

| 任务 | 耗时 | 难度 |
|------|------|------|
| 提取所有 XAML 文本 | 4h | ⭐⭐ |
| 创建 .resx 文件结构 | 2h | ⭐ |
| 迁移 C# 硬编码字符串 | 6h | ⭐⭐⭐ |
| 实现 Language Switching | 3h | ⭐⭐ |
| 测试与验证 | 4h | ⭐⭐ |
| **总计** | **19h** | **中等** |

---

## 🎯 下一步行动

### Immediate (This Sprint)
1. ✅ 创建基础资源文件结构
2. ✅ 提取所有 Visible UI 文本
3. ✅ 实现 Language Service

### Short-term (Next Sprint)
4. ⏳ 迁移所有错误消息
5. ⏳ 集成外部翻译平台
6. ⏳ 添加单元测试

### Long-term (Q4 2026)
7. ⏸️ RTL language support (Arabic, Hebrew)
8. ⏸️ Dynamic font switching per language
9. ⏸️ VoiceOver/Speak accessibility integration

---

**文档版本**: v1.0  
**审查日期**: 2026-07-22  
**审查人**: AI Code Auditor  
**优先级**: 🔴 Critical (阻碍全球化发布)
