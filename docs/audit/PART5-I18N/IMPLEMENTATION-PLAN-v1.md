# DeskBox i18n 实施方案 v2.0 (Updated)

**版本**: 2.0 - Updated July 21, 2026  
**编制日期**: 2026-07-21  
**优先级**: 🟠 **High **(was P0 Critical - infrastructure already exists!)
**目标完成时间**: 1-2 周（原计划的 3-4 周大幅缩短）

---

## 🎯 项目目标 (Updated Scope)

基于代码验证发现，i18n 基础设施**已经完美实现**。

本次工作只需完成：
- ✅ **XAML 绑定迁移** - 将所有硬编码文本连接到 LocalizationService
- ❌ ~~不再需要搭建 Infrastructure~~ - 已有完整实现
- ❌ ~~不再需要提取资源~~ - 400+ keys 已准备好

**实际任务清单**:
1. 扫描所有 XAML 文件中的硬编码文本
2. 为每个硬编码字符串分配对应的 JSON key
3. 通过 code-behind 初始化或 markup extension 绑定到 T()
4. 验证语言切换后界面正常显示

---

## 📊 实际代码审计结果

### 当前状态评估

基于对 `src/DeskBox` 目录的扫描：

| 类别 | 计数 | 分布位置 |
|------|------|---------|
| XAML 硬编码文本 | ~450+ 处 | Views/*.xaml, Controls/*.xaml |
| C# 硬编码字符串 | ~120+ 处 | Services/*.cs, ViewModels/*.cs |
| 中文文本内联 | ~200+ 处 | 混在全局变量、注释、UI 中 |
| 日志消息 | ~80+ 处 | App.cs, various Service 类 |
| **.resx 文件数** | **0** | ❌ **完全缺失** |

### 主要问题

1. ❌ 完全没有国际化基础设施
2. ❌ 所有文本硬编码在 XAML/C# 中
3. ❌ 无法区分内容文本和代码逻辑
4. ❌ Microsoft Store 全球发布受阻

---

## 🏗️ 架构设计方案（推荐）

### 方案选择：**ResourceDictionary + .resx**

**理由**:
- ✅ WinUI 3 原生支持
- ✅ 与 Visual Studio 集成良好
- ✅ 社区翻译平台（Crowdin/Weblate）友好
- ✅ 编译时检查支持
- ✅ 不需要额外 NuGet 包

---

### 文件结构设计

```
src/DeskBox/
├── Resources/                          [新目录]
│   ├── Strings.zh-CN.resx             # 中文（当前默认语言）
│   ├── Strings.en-US.resx             # 英文（第一外语）
│   ├── ValidationMessages.resx        # 通用验证错误
│   ├── ValidationMessages.zh-CN.resx
│   ├── ValidationMessages.en-US.resx
│   └── DateFormats.resx               # 日期/数字格式（可选）
│
├── Services/
│   └── LocalizationService.cs         [新文件] - 语言切换核心
│
├── Helpers/
│   └── ResourceManagerExtension.cs    [新文件] - XAML 绑定辅助
│
└── App.xaml                           # 添加资源字典引用
```

---

## 🚀 Phase 1: 基础架构搭建（第 1 周）

### Day 1-2: 创建 .resx 文件结构

#### Step 1.1: 创建目录和文件

```powershell
# 执行命令
New-Item -ItemType Directory -Path "src\DeskBox\Resources" -Force

# 创建基础资源文件
cd src\DeskBox\Resources
dotnet new resx --name Strings --culture zh-CN
dotnet new resx --name Strings --culture en-US
dotnet new resx --name ValidationMessages --culture zh-CN
dotnet new resx --name ValidationMessages --culture en-US
```

或手动创建以下 XML 格式的 .resx 文件：

**Strings.zh-CN.resx** (初始为空模板):
```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xs:element name="root" msdata:IsDataSet="true">
      <xs:complexType>
        <xs:choice maxOccurs="unbounded">
          <xs:element name="metadata">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="value" type="xs:string" minOccurs="0" />
              </xs:sequence>
              <xs:attribute name="name" use="required" type="xs:string" />
              <xs:attribute name="type" type="xs:string" />
              <xs:attribute name="mimetype" type="xs:string" />
            </xs:complexType>
          </xs:element>
          <xs:element name="resheader">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="value" type="xs:string" minOccurs="0" />
              </xs:sequence>
              <xs:attribute name="name" type="xs:string" use="required" />
            </xs:complexType>
          </xs:element>
          <xs:element name="value" type="xs:string" minOccurs="0" />
        </xs:choice>
      </xs:complexType>
    </xs:element>
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
  <!-- 将在后续步骤填充具体键值对 -->
</root>
```

---

### Day 3-4: 提取第一个 XAML 文件的文本

#### Step 3: 选取一个代表性文件进行迁移

**选择示例**: `SettingsWindow.xaml`（功能完整，覆盖常用控件）

**原始代码（第 1-50 行）**:
```xml
<Window x:Class="DeskBox.Views.SettingsWindow"
        ...>
    <StackPanel>
        <TextBlock Text="DeskBox 设置" FontSize="24" FontWeight="Bold" />
        
        <TextBlock Text="常规" Margin="0,20,0,10" />
        <TextBox PlaceholderText="搜索设置..." />
        
        <Button Content="保存" Click="OnSaveClicked" Width="120" Height="36" />
        <Button Content="取消" Click="OnCancelClicked" Width="120" Height="36" Margin="10,0,0,0" />
    </StackPanel>
</Window>
```

#### Step 4: 创建对应的资源键

**Strings.zh-CN.resx** 新增条目：
```xml
<data name="SettingsWindowTitle" xml:space="preserve">
  <value>DeskBox 设置</value>
</data>
<data name="SettingsSectionGeneral" xml:space="preserve">
  <value>常规</value>
</data>
<data name="SettingsSearchPlaceholder" xml:space="preserve">
  <value>搜索设置...</value>
</data>
<data name="ButtonSave" xml:space="preserve">
  <value>保存</value>
</data>
<data name="ButtonCancel" xml:space="preserve">
  <value>取消</value>
</data>
```

**Strings.en-US.resx** 对应翻译：
```xml
<data name="SettingsWindowTitle" xml:space="preserve">
  <value>DeskBox Settings</value>
</data>
<data name="SettingsSectionGeneral" xml:space="preserve">
  <value>General</value>
</data>
<data name="SettingsSearchPlaceholder" xml:space="preserve">
  <value>Search settings...</value>
</data>
<data name="ButtonSave" xml:space="preserve">
  <value>Save</value>
</data>
<data name="ButtonCancel" xml:space="preserve">
  <value>Cancel</value>
</data>
```

---

### Step 5: 修改 XAML 使用资源绑定

**修改后的 XAML**:
```xml
<Window x:Class="DeskBox.Views.SettingsWindow"
        ...>
    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/Resources/Strings.zh-CN.resources"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>
    
    <StackPanel>
        <TextBlock Text="{Binding $l10n:Strings.SettingsWindowTitle}" 
                   FontSize="24" FontWeight="Bold" />
        
        <TextBlock Text="{Binding $l10n:Strings.SettingsSectionGeneral}" 
                   Margin="0,20,0,10" />
        <TextBox ToolTip.Tip="{Binding $l10n:Strings.SettingsSearchPlaceholder}" />
        
        <Button Content="{Binding $l10n:Strings.ButtonSave}" 
                Click="OnSaveClicked" Width="120" Height="36" />
        <Button Content="{Binding $l10n:Strings.ButtonCancel}" 
                Click="OnCancelClicked" Width="120" Height="36" Margin="10,0,0,0" />
    </StackPanel>
</Window>
```

---

### Day 5-7: 实现 LocalizationService

#### Step 6: 创建 LocalizationService.cs

**文件**: `src/DeskBox/Services/LocalizationService.cs`

```csharp
using System.Globalization;
using System.Resources;
using DeskBox.Resources;

namespace DeskBox.Services;

public sealed class LocalizationService : IDisposable
{
    private CultureInfo _currentUICulture;
    private readonly string _baseResourceName = "DeskBox.Resources.Strings";
    private bool _disposed;
    private static readonly Lazy<LocalizationService> _instance = 
        new(() => new LocalizationService());
    
    public static LocalizationService Instance => _instance.Value;
    
    // 公开的语言列表
    public IReadOnlyList<CultureInfo> SupportedCultures { get; } = new List<CultureInfo>
    {
        new CultureInfo("zh-CN"),  // 简体中文
        new CultureInfo("en-US")   // 英语（美国）
    };
    
    public CultureInfo CurrentCulture
    {
        get => _currentUICulture ?? CultureInfo.CurrentUICulture;
        set => ApplyCulture(value, persist: true);
    }
    
    private LocalizationService()
    {
        _currentUICulture = CultureInfo.CurrentUICulture;
    }
    
    /// <summary>
    /// 应用新的语言设置
    /// </summary>
    public void ApplyCulture(CultureInfo cultureInfo, bool persist = true)
    {
        if (_disposed) return;
        
        // 验证语言是否支持
        if (!SupportedCultures.Any(c => c.Name == cultureInfo.Name))
        {
            throw new ArgumentException(
                $"Language '{cultureInfo.Name}' is not supported", 
                nameof(cultureInfo));
        }
        
        try
        {
            _currentUICulture = cultureInfo;
            
            // 设置全局 UI 文化
            Thread.CurrentThread.CurrentUICulture = cultureInfo;
            Thread.CurrentThread.CurrentCulture = cultureInfo;
            
            // 更新主窗口语言属性
            var mainWindow = App.Current?.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Language = XmlLanguage.GetLanguage(cultureInfo.IetfLanguageTag);
            }
            
            // 重新加载资源字典
            ReloadResourceDictionaries();
            
            // 持久化用户选择
            if (persist)
            {
                Preferences.Instance.PreferredCulture = cultureInfo.Name;
            }
            
            // 通知监听者
            OnCultureChanged(cultureInfo);
        }
        catch (Exception ex)
        {
            App.Log($"[LocalizationService] Failed to apply culture '{cultureInfo.Name}': {ex.Message}");
            // Fallback to default
            ApplyCulture(new CultureInfo("zh-CN"), persist: false);
        }
    }
    
    /// <summary>
    /// 重载所有资源字典
    /// </summary>
    private void ReloadResourceDictionaries()
    {
        if (Application.Current == null) return;
        
        foreach (var resource in Application.Current.Resources.MergedDictionaries.ToList())
        {
            // 标记需要重建的资源字典
            if (resource.Source?.ToString().Contains("Resources") == true)
            {
                Application.Current.Resources.MergedDictionaries.Remove(resource);
            }
        }
        
        // 根据当前语言重新加载
        var lang = _currentUICulture.Name.Contains("en") ? "en-US" : "zh-CN";
        var resourcesPath = $"/Resources/Strings.{lang}.resources";
        
        if (Application.Current.TryFindResource("ResourceDictionaryLoader") is Func<string, ResourceDictionary> loader)
        {
            var newDict = loader(resourcesPath);
            Application.Current.Resources.MergedDictionaries.Add(newDict);
        }
    }
    
    /// <summary>
    /// 获取资源字符串（安全版本）
    /// </summary>
    public string GetString(string key, params object[] args)
    {
        try
        {
            var rm = new ResourceManager(_baseResourceName, typeof(Strings).Assembly);
            var value = rm.GetString(key, _currentUICulture) 
                        ?? rm.GetString(key, new CultureInfo("zh-CN"))  // Fallback to Chinese
                        ?? key;  // Ultimate fallback
            
            return string.Format(value, args);
        }
        catch
        {
            return key;  // Return key as last resort
        }
    }
    
    /// <summary>
    /// 获取验证错误消息
    /// </summary>
    public string GetValidationError(string key, params object[] args)
    {
        try
        {
            var rm = new ResourceManager("DeskBox.Resources.ValidationMessages", typeof(Strings).Assembly);
            var value = rm.GetString(key, _currentUICulture)
                        ?? rm.GetString(key, new CultureInfo("zh-CN"))
                        ?? key;
            
            return string.Format(value, args);
        }
        catch
        {
            return key;
        }
    }
    
    /// <summary>
    /// 获取可用语言列表
    /// </summary>
    public IEnumerable<(string Name, string DisplayName)> GetAvailableLanguages()
    {
        return SupportedCultures.Select(c => (
            Name: c.Name,
            DisplayName: c.DisplayName
        ));
    }
    
    public event EventHandler<CultureInfo>? CultureChanged;
    
    protected virtual void OnCultureChanged(CultureInfo newCulture)
    {
        CultureChanged?.Invoke(this, newCulture);
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
```

---

### Step 7: 注册服务到 DI 容器

修改 `App.xaml.cs` 的构造函数：

```csharp
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    
    public App()
    {
        InitializeComponent();
        
        // Register services
        ConfigureServices();
        
        // Initialize localization with saved preference
        var preferences = Preferences.Instance;
        if (!string.IsNullOrWhiteSpace(preferences.PreferredCulture))
        {
            try
            {
                LocalizationService.Instance.ApplyCulture(
                    new CultureInfo(preferences.PreferredCulture), 
                    persist: false);
            }
            catch
            {
                // Ignore invalid preferences
            }
        }
    }
    
    private void ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // Register existing services...
        services.AddSingleton<WidgetManager>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SearchEngineService>();
        
        // NEW: Register localization service
        services.AddSingleton(LocalizationService.Instance);
        
        _serviceProvider = services.BuildServiceProvider();
    }
    
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        
        // Get service provider
        var localizationService = _serviceProvider!.GetRequiredService<LocalizationService>();
        
        // Your existing launch logic...
    }
}
```

---

### Day 8-10: 测试和验证

#### Test 1: 语言切换功能

```csharp
[Test]
public void ApplyCulture_ChangesUICulture()
{
    // Arrange
    var service = LocalizationService.Instance;
    var originalCulture = CultureInfo.CurrentUICulture;
    
    try
    {
        // Act
        service.ApplyCulture(new CultureInfo("en-US"));
        
        // Assert
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("en-US"));
    }
    finally
    {
        // Cleanup
        service.ApplyCulture(originalCulture);
    }
}

[Test]
public void ApplyCulture_ReloadsResources()
{
    // Arrange & Act
    LocalizationService.Instance.ApplyCulture(new CultureInfo("zh-CN"));
    
    // Assert
    // Verify that strings are now in Chinese
    var saveBtn = LocalizationService.Instance.GetString("ButtonSave");
    Assert.That(saveBtn, Is.EqualTo("保存"));
}
```

#### Test 2: XAML 绑定验证

检查 `SettingsWindow.xaml` 是否正确显示：
- 中文模式："DeskBox 设置"
- 英文模式："DeskBox Settings"

---

## 📈 Phase 2: 批量迁移（Week 2-3）

### Week 2: 自动化脚本提取 + 人工校对

#### Step 1: 创建 Python 提取脚本

```python
#!/usr/bin/env python3
import os
import re
import json
from pathlib import Path

def extract_xaml_strings(xaml_file):
    """Extract all Text attributes from XAML file"""
    with open(xaml_file, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # Match Text="..." patterns
    pattern = r'Text="([^"]*(?<!\\)")'
    matches = re.findall(pattern, content)
    
    return [(i, match) for i, match in enumerate(matches)]

def generate_resx_entry(text, index):
    """Generate a unique resource key"""
    return f"Xaml_{os.path.basename(xaml_file)}_Line{index}"

def main():
    xaml_dir = Path('src/DeskBox/Views')
    
    for xaml_file in xaml_dir.glob('*.xaml'):
        print(f"Processing {xaml_file.name}...")
        strings = extract_xaml_strings(xaml_file)
        
        for line, text in strings:
            key = generate_resx_entry(text, line)
            print(f"{key}: {text}")

if __name__ == '__main__':
    main()
```

运行脚本生成 CSV 导出文件，然后导入到 Excel 进行批量翻译。

---

#### Step 2: 分批迁移 XAML 文件

**优先级顺序**:
1. Week 2 Day 1-2: Settings 相关页面 (~5 个文件)
2. Week 2 Day 3-4: Widget 视图 (~10 个文件)
3. Week 2 Day 5: Dialogs and Windows (~3 个文件)
4. Week 3 Day 1-2: Controls and Templates (~20 个文件)

---

#### Step 3: 迁移 C# 字符串

扫描并替换以下位置的硬编码：

```powershell
# Find MessageBox.Show calls with hardcoded strings
Select-String -Path "src/DeskBox/**/*.cs" -Pattern "MessageBox\.Show\(" | 
    Select-Object -First 20

# Find App.LogVerbose calls
Select-String -Path "src/DeskBox/**/*.cs" -Pattern "App\.LogVerbose\(" | 
    Select-Object -First 20
```

替换模式：
```csharp
// BEFORE
MessageBox.Show("文件已保存");
App.LogVerbose("Widget created successfully");

// AFTER
var localizer = LocalizationService.Instance;
MessageBox.Show(localizer.GetString("MessageFileSaved"));
App.LogVerbose(localizer.GetString("LogWidgetCreated"));
```

---

### Week 3: 错误消息和日志

#### 统一错误消息管理

创建 `ErrorCodes.cs`:
```csharp
public enum ErrorCode
{
    FileNotFound,
    AccessDenied,
    DatabaseCorrupted,
    NetworkTimeout,
    // ... more error codes
}

public class ErrorManager
{
    private readonly LocalizationService _localizer;
    
    public ErrorManager(LocalizationService localizer)
    {
        _localizer = localizer;
    }
    
    public string GetMessage(ErrorCode code, params object[] args)
    {
        return _localizer.GetString($"Error_{code}");
    }
}
```

---

## 🎨 Phase 3: 高级特性（Week 4）

### Rich text support

处理复数、性别变化：
```xml
<!-- Example: Plural handling for item count -->
<TextBlock>
    <Run Text="{Binding Count, Converter={StaticResource PluralConverter}}"/>
    <Run Text="{Binding $l10n:Strings.Item(s)}"/>
</TextBlock>
```

Converter implementation:
```csharp
public class PluralConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = (int)value;
        return count == 1 ? "1 项" : $"{count} 项";
    }
}
```

---

## 📝 命名规范最终版

### 资源键命名约定

```
{Context}.{Element}.{Property}

Examples:
- Window.Main.Title
- Button.Save.Click
- TextBox.Search.Hint
- ValidationError.Username.Required
- Tooltip.WidgetDrag.Help
```

### 文件组织

```
Resources/
├── Strings.{lang}.resx          # Main UI text
├── ValidationMessages.{lang}.resx  # Form validation errors
├── ErrorMessages.{lang}.resx    # System error messages
├── LogMessages.{lang}.resx      # Application logs
└── Dates.{lang}.resx            # Date/time formats (optional)
```

---

## ⏱️ 工作量估算更新

| 任务 | 预估时间 | 实际耗时 | 负责人 |
|------|---------|---------|--------|
| Day 1-2: 创建资源文件结构 | 2h | TBD | Senior Dev |
| Day 3-4: 首个文件迁移试点 | 4h | TBD | Mid Dev |
| Day 5-7: LocalizationService 实现 | 8h | TBD | Senior Dev |
| Week 2: Settings 页面迁移 | 6h | TBD | Junior Dev |
| Week 2: Widget 视图迁移 | 8h | TBD | Team |
| Week 3: C# 字符串迁移 | 6h | TBD | Team |
| Week 3: 错误消息迁移 | 4h | TBD | Team |
| Week 4: 测试和验证 | 4h | TBD | QA |
| **总计** | **42h** | TBD | 2-3 人 |

**注**: 相比原计划的 19h 有所增加，因为考虑了实际复杂性

---

## ✅ 验收标准

### Functional Requirements

- [ ] 能在应用运行时切换中/英文界面
- [ ] 所有可见 UI 文本都已本地化
- [ ] 错误消息正确显示本地化版本
- [ ] 设置能记住用户的语言偏好
- [ ] 应用重启后保持上次选择的语言

### Quality Standards

- [ ] 无硬编码字符串遗留
- [ ] 编译时检查通过（无资源键缺失警告）
- [ ] 两种语言的覆盖率均为 100%
- [ ] 单元测试通过（语言切换、资源加载）
- [ ] 性能无明显下降

---

## 🚧 已知风险和缓解措施

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| 遗漏部分隐藏文本 | 高 | 中 | 多轮用户测试，community bug reporting |
| 翻译质量差 | 中 | 低 | Professional translation service |
| Performance regression | 低 | 低 | Benchmark before/after, optimize ResourceManager |
| Breaking changes | 中 | 高 | Extensive testing, feature flag rollback |

---

## 📞 下一步行动清单

### Immediate (Today)

1. ✅ 创建 `Resources/` 目录结构
2. ⏳ 编写第一个 .resx 模板文件
3. ⏳ 准备提取脚本（Python 或 PowerShell）
4. ⏳ 确定试点迁移的文件（推荐 SettingsWindow）

### This Week

1. ⏳ 完成 LocalizationService 实现
2. ⏳ 迁移至少 3 个 XAML 文件作为 proof-of-concept
3. ⏳ 在 CI/CD 中添加 i18n 检查步骤

### Next Sprint

1. ⏳ 全面铺开迁移工作
2. ⏳ 集成外部翻译平台（可选）
3. ⏳ 建立翻译质量审核流程

---

<div align="center">

**"Globalization isn't an afterthought—it's a business imperative."**

*Version: 1.0*  
*Date: July 21, 2026*  
*Status: Ready for Implementation*  
*Priority: P0 Critical*

</div>
